using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RestaurantOS.Api.Modules.Menu;
using RestaurantOS.Api.Modules.Orders;
using RestaurantOS.Api.Realtime;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Integrations;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Integrations;

public sealed record ExternalCustomer(string? Name, string? Phone, string? Address);

public sealed record ExternalOrderRequest(
    string Provider,
    string ExternalId,
    OrderType Type,
    ExternalCustomer? Customer,
    string? Notes,
    List<LineSelection> Lines,
    bool Prepaid = false,
    decimal? ExpectedTotal = null);

public sealed record ExternalOrderResult(
    Guid OrderId,
    long OrderNumber,
    string Provider,
    string ExternalId,
    string State,
    decimal GrandTotal,
    bool TotalMatches,
    string? CancelReason,
    bool Replayed);

/// <summary>
/// Ingests orders from authorised external channels (aggregators, websites). Idempotent per (provider, external id):
/// the order's client id is derived from that pair, and an <see cref="InboundEvent"/> records the delivery.
/// </summary>
public sealed partial class ExternalOrderService(AppDbContext db, ITenantContext tenant, OrderService orders, IRealtimeNotifier notifier)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex ProviderPattern();

    public async Task<ExternalOrderResult> IngestAsync(Guid outletId, ExternalOrderRequest req, CancellationToken ct)
    {
        var provider = (req.Provider ?? "").Trim().ToLowerInvariant();
        if (!ProviderPattern().IsMatch(provider))
            throw new DomainException("validation", "Provider must be 2-40 lowercase letters, digits or dashes.");
        var externalId = (req.ExternalId ?? "").Trim();
        if (externalId.Length is 0 or > 100) throw new DomainException("validation", "External id is required (max 100 characters).");
        if (req.Type is not (OrderType.Delivery or OrderType.Takeaway))
            throw new DomainException("validation", "External orders must be Delivery or Takeaway.");
        if (req.Lines is null || req.Lines.Count == 0) throw new DomainException("order_empty", "Add at least one item.");
        if (req.Lines.Count > 100) throw new DomainException("validation", "Too many items in one order.");

        var previous = await db.InboundEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Provider == provider && e.ExternalId == externalId && e.EventType == "order.created", ct);
        if (previous?.ResultEntityId is { } existingId)
        {
            var existing = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == existingId, ct)
                           ?? throw new NotFoundException("Order");
            if (existing.OutletId != outletId)
                throw new ConflictException("external_id_reused", "This external order was already delivered to another outlet.");
            return Result(existing, provider, externalId, req.ExpectedTotal, replayed: true);
        }

        var notes = string.Join(" · ", new[] { req.Prepaid ? "Prepaid online" : "Pay on delivery", req.Notes }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var (order, created) = await orders.CreateAsync(outletId, new CreateOrderRequest(
                ClientOrderIdFor(tenant.TenantId!.Value, provider, externalId), req.Type, null, null, req.Customer?.Phone,
                req.Customer?.Name, req.Type == OrderType.Delivery ? req.Customer?.Address : null, notes, req.Lines),
            new ExternalOrderContext(OrderSource.Aggregator, OrderStatus.AwaitingAcceptance, $"{provider}:{externalId}"), ct);

        if (created)
        {
            db.InboundEvents.Add(new InboundEvent
            {
                Provider = provider, ExternalId = externalId, EventType = "order.created", ResultEntityId = order.Id,
                Payload = JsonSerializer.Serialize(new { req.Type, lines = req.Lines.Count, req.Prepaid, req.ExpectedTotal }),
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }

            await notifier.PublishAsync(outletId, RealtimeEvents.ExternalOrderPlaced,
                new { orderId = order.Id, order.OrderNumber, provider, order.GrandTotal });
        }

        return Result(order, provider, externalId, req.ExpectedTotal, replayed: !created);
    }

    public async Task<ExternalOrderResult> GetAsync(Guid outletId, string provider, string externalId, CancellationToken ct)
    {
        var reference = $"{provider.ToLowerInvariant()}:{externalId}";
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OutletId == outletId && o.ExternalRef == reference, ct)
                    ?? throw new NotFoundException("Order");
        return Result(order, provider.ToLowerInvariant(), externalId, null, replayed: true);
    }

    /// <summary>Deterministic per tenant, provider and external id, so concurrent duplicate deliveries collide on the unique index.</summary>
    public static Guid ClientOrderIdFor(Guid tenantId, string provider, string externalId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}|{provider}|{externalId}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static ExternalOrderResult Result(Order o, string provider, string externalId, decimal? expected, bool replayed) => new(
        o.Id, o.OrderNumber, provider, externalId,
        o.Status switch
        {
            OrderStatus.AwaitingAcceptance => "pending",
            OrderStatus.Settled => "completed",
            OrderStatus.Cancelled => "rejected",
            _ => "accepted",
        },
        o.GrandTotal, expected is null || expected == o.GrandTotal, o.Status == OrderStatus.Cancelled ? o.CancelReason : null, replayed);
}
