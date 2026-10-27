using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RestaurantOS.Api.Auth;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Realtime;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Integrations;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Payments;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Payments;

public sealed record CreateIntentRequest(decimal Amount);
public sealed record MockPaymentEvent(string EventId, string ProviderRef, string Status);

/// <summary>
/// Test-mode payment gateway: issues payment links and confirms them through an HMAC-signed webhook, exactly like a real
/// gateway would. No money moves. A real UPI/card gateway adapter needs merchant credentials from the product owner.
/// </summary>
public sealed class MockPaymentGateway(AppDbContext db, RequestTenantContext tenant, IRealtimeNotifier notifier, IConfiguration config,
    AuditWriter audit)
{
    public const string Provider = "mock";
    public const string SignatureHeader = "X-Mock-Signature";

    private string Secret => config["Integrations:MockPaymentWebhookSecret"] ?? "";

    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    public async Task<PaymentIntent> CreateIntentAsync(Guid outletId, Guid orderId, decimal amount, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.OutletId == outletId, ct)
                    ?? throw new NotFoundException("Order");
        if (order.Status is not (OrderStatus.Open or OrderStatus.Billed))
            throw new ConflictException("order_not_payable", $"Order is {order.Status} and cannot take payments.");
        amount = Money.Round(Guard.Positive(amount, "Amount"));
        if (amount > order.GrandTotal) throw new DomainException("validation", "The payment link cannot exceed the bill amount.");

        var providerRef = $"mock_pi_{TokenService.GenerateSecret(12)}";
        var intent = new PaymentIntent
        {
            OutletId = outletId, OrderId = orderId, Amount = amount, Provider = Provider, ProviderRef = providerRef,
            Status = PaymentIntentStatus.Pending, PaymentLink = $"/pay/mock/{providerRef}",
        };
        db.PaymentIntents.Add(intent);
        await audit.AddAsync("payment.intent_created", "PaymentIntent", intent.Id, new { order.OrderNumber, amount, Provider }, outletId, ct);
        await db.SaveChangesAsync(ct);
        return intent;
    }

    /// <summary>Verifies the signature, then applies the event once. Returns false when the signature is invalid.</summary>
    public async Task<bool> HandleWebhookAsync(string body, string? signature, CancellationToken ct)
    {
        if (Secret.Length == 0) throw new InvalidOperationException("Mock payment webhooks are not configured.");
        var expected = Encoding.ASCII.GetBytes(Sign(Secret, body));
        if (signature is null || !CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature))) return false;

        MockPaymentEvent? evt;
        try { evt = JsonSerializer.Deserialize<MockPaymentEvent>(body, JsonSerializerOptions.Web); }
        catch (JsonException) { throw new DomainException("validation", "Malformed event."); }
        if (evt is null || string.IsNullOrWhiteSpace(evt.EventId) || string.IsNullOrWhiteSpace(evt.ProviderRef) || evt.EventId.Length > 100)
            throw new DomainException("validation", "Malformed event.");
        if (evt.Status is not ("succeeded" or "failed")) throw new DomainException("validation", "Unknown payment status.");

        var located = await db.PaymentIntents.IgnoreQueryFilters().AsNoTracking()
                          .FirstOrDefaultAsync(i => i.Provider == Provider && i.ProviderRef == evt.ProviderRef, ct)
                      ?? throw new NotFoundException("Payment intent");
        tenant.Set(located.TenantId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payment_intents WHERE id = {located.Id} FOR UPDATE", ct);
        if (await db.InboundEvents.AnyAsync(e => e.Provider == Provider && e.ExternalId == evt.EventId, ct)) return true;

        var intent = await db.PaymentIntents.FirstAsync(i => i.Id == located.Id, ct);
        db.InboundEvents.Add(new InboundEvent
        {
            Provider = Provider, ExternalId = evt.EventId, EventType = $"payment.{evt.Status}", Payload = body, ResultEntityId = intent.Id,
        });
        // A final state never changes: late or reordered events are recorded but ignored.
        if (intent.Status == PaymentIntentStatus.Pending)
            intent.Status = evt.Status == "succeeded" ? PaymentIntentStatus.Succeeded : PaymentIntentStatus.Failed;
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return true;
        }

        await notifier.PublishAsync(intent.OutletId, RealtimeEvents.PaymentIntentChanged,
            new { intentId = intent.Id, intent.OrderId, status = intent.Status });
        return true;
    }
}

public static class PaymentGatewayEndpoints
{
    public static void MapPaymentGatewayEndpoints(this IEndpointRouteBuilder app, IConfiguration config)
    {
        var g = app.MapGroup("/api/outlets/{outletId:guid}/orders/{orderId:guid}/payment-intents").WithTags("Payments").RequireAuthorization();
        g.MapPost("", async (Guid outletId, Guid orderId, CreateIntentRequest req, MockPaymentGateway gateway, CancellationToken ct) =>
            Results.Created("", View(await gateway.CreateIntentAsync(outletId, orderId, req.Amount, ct)))).RequireOutlet(Permission.OrderSettle);
        g.MapGet("", async (Guid outletId, Guid orderId, AppDbContext db, CancellationToken ct) =>
                Results.Ok((await db.PaymentIntents.AsNoTracking().Where(i => i.OrderId == orderId && i.OutletId == outletId)
                    .OrderByDescending(i => i.CreatedAt).ToListAsync(ct)).Select(View)))
            .RequireOutlet(Permission.OrderSettle);

        app.MapPost("/api/integrations/payments/mock/webhook", async (HttpRequest request, MockPaymentGateway gateway, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                if (body.Length > 16_384) return Results.BadRequest();
                return await gateway.HandleWebhookAsync(body, request.Headers[MockPaymentGateway.SignatureHeader], ct)
                    ? Results.Ok(new { received = true })
                    : Results.Problem(statusCode: 401, title: "Invalid signature.", extensions: new Dictionary<string, object?> { ["code"] = "invalid_signature" });
            })
            .WithTags("Payments").AllowAnonymous().RequireRateLimiting("public");

        if (!config.GetValue<bool>("Integrations:EnableMockSimulators")) return;

        // Test-mode checkout: lets a "customer" complete or fail a mock payment link. The event goes through the signed webhook path.
        var sim = app.MapGroup("/api/dev/mock-payments/{providerRef}").WithTags("Simulators").AllowAnonymous().RequireRateLimiting("public");
        sim.MapGet("", async (string providerRef, AppDbContext db, CancellationToken ct) =>
        {
            var intent = await db.PaymentIntents.IgnoreQueryFilters().AsNoTracking()
                             .FirstOrDefaultAsync(i => i.Provider == MockPaymentGateway.Provider && i.ProviderRef == providerRef, ct)
                         ?? throw new NotFoundException("Payment link");
            var outlet = await db.Outlets.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == intent.OutletId, ct);
            return Results.Ok(new { intent.ProviderRef, intent.Amount, intent.Status, merchant = outlet.Name });
        });
        sim.MapPost("/{outcome}", async (string providerRef, string outcome, MockPaymentGateway gateway, IConfiguration cfg, CancellationToken ct) =>
        {
            if (outcome is not ("succeeded" or "failed")) return Results.NotFound();
            var body = JsonSerializer.Serialize(new MockPaymentEvent($"evt_{Guid.NewGuid():N}", providerRef, outcome), JsonSerializerOptions.Web);
            await gateway.HandleWebhookAsync(body, MockPaymentGateway.Sign(cfg["Integrations:MockPaymentWebhookSecret"] ?? "", body), ct);
            return Results.Ok(new { status = outcome });
        });
    }

    private static object View(PaymentIntent i) => new { i.Id, i.OrderId, i.Amount, i.Provider, i.ProviderRef, i.Status, i.PaymentLink, i.CreatedAt };
}
