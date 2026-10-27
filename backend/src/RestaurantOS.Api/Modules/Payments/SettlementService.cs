using Microsoft.EntityFrameworkCore;
using Npgsql;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Modules.Customers;
using RestaurantOS.Api.Modules.Inventory;
using RestaurantOS.Api.Modules.Orders;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Payments;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Payments;

public sealed record PaymentInput(PaymentMethod Method, decimal Amount, decimal? Tendered, string? Reference);

public sealed record SettleRequest(string IdempotencyKey, List<PaymentInput> Payments, uint? Version);

public sealed record SettlementResult(Guid SettlementId, Guid OrderId, string InvoiceNumber, decimal Amount, decimal ChangeDue, bool Replayed);

public sealed class SettlementService(
    AppDbContext db,
    ITenantContext tenant,
    OrderService orders,
    InventoryService inventory,
    CustomerService customers,
    AuditWriter audit,
    TimeProvider clock)
{
    public async Task<SettlementResult> SettleAsync(Guid outletId, Guid orderId, SettleRequest req, CancellationToken ct)
    {
        var key = Guard.Required(req.IdempotencyKey, "Idempotency key", 100);
        var replay = await FindReplayAsync(orderId, key, ct);
        if (replay is not null) return replay;

        if (req.Payments is null) throw new DomainException("validation", "Payments are required.");
        foreach (var p in req.Payments)
        {
            if (!Enum.IsDefined(p.Method)) throw new DomainException("validation", "Unknown payment method.");
            Guard.Positive(p.Amount, "Payment amount");
            if (Money.Round(p.Amount) != p.Amount) throw new DomainException("validation", "Payment amounts must have at most 2 decimals.");
            if (p.Tendered is { } t && (p.Method != PaymentMethod.Cash || t < p.Amount))
                throw new DomainException("validation", "Tendered amount applies to cash and must cover the payment.");
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Row lock serialises concurrent settlement attempts for the same order.
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM orders WHERE id = {orderId} AND outlet_id = {outletId} AND tenant_id = {tenant.TenantId} FOR UPDATE", ct);
            var raced = await FindReplayAsync(orderId, key, ct);
            if (raced is not null)
            {
                await tx.RollbackAsync(ct);
                return raced;
            }

            var order = await orders.LoadForUpdateAsync(outletId, orderId, req.Version, ct);
            var outlet = await db.Outlets.FirstAsync(o => o.Id == outletId, ct);

            var paid = req.Payments.Sum(p => p.Amount);
            var loyaltyAmount = req.Payments.Where(p => p.Method == PaymentMethod.Loyalty).Sum(p => p.Amount);
            if (loyaltyAmount > 0 && order.CustomerId is null)
                throw new DomainException("customer_required", "Attach a customer to redeem loyalty points.");

            var businessDate = outlet.LocalDate(now);
            var fy = FinancialYear.Label(businessDate);
            var seq = await db.NextAsync(outletId, $"invoice:{fy}", ct);
            var invoiceNumber = $"{outlet.InvoicePrefix}/{fy}/{seq:D6}";
            order.Settle(invoiceNumber, paid, businessDate, now);
            order.UpdatedAt = now;

            var session = await db.CashSessions.FirstOrDefaultAsync(s => s.OutletId == outletId && s.Status == CashSessionStatus.Open, ct);
            var settlement = new Settlement
            {
                OutletId = outletId, OrderId = order.Id, IdempotencyKey = key, InvoiceNumber = invoiceNumber, Amount = paid,
                SettledByUserId = tenant.UserId,
            };
            db.Settlements.Add(settlement);
            foreach (var p in req.Payments)
            {
                db.Payments.Add(new Payment
                {
                    OutletId = outletId,
                    OrderId = order.Id,
                    SettlementId = settlement.Id,
                    CashSessionId = p.Method == PaymentMethod.Cash ? session?.Id : null,
                    Method = p.Method,
                    Amount = p.Amount,
                    Tendered = p.Tendered,
                    Reference = Guard.Optional(p.Reference, "Reference", 120),
                    BusinessDate = businessDate,
                    ReceivedByUserId = tenant.UserId,
                });
            }

            await ValidateOnlinePaymentsAsync(order, req.Payments, ct);
            await inventory.PostConsumptionAsync(order, tenant.UserId, ct);
            await customers.ApplySettlementAsync(order, outlet, loyaltyAmount, ct);
            await audit.AddAsync("order.settled", "Order", order.Id, new
            {
                order.OrderNumber, invoiceNumber, order.GrandTotal, payments = req.Payments.Select(p => new { p.Method, p.Amount }),
            }, outletId, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await orders.PublishAsync(order);
            await inventory.PublishAsync(outletId);
            var change = req.Payments.Where(p => p.Tendered is not null).Sum(p => p.Tendered!.Value - p.Amount);
            return new SettlementResult(settlement.Id, order.Id, invoiceNumber, paid, change, Replayed: false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await FindReplayAsync(orderId, key, ct)
                   ?? throw new ConflictException("already_settled", "This order has already been settled.");
        }
    }

    private async Task<SettlementResult?> FindReplayAsync(Guid orderId, string key, CancellationToken ct)
    {
        var existing = await db.Settlements.AsNoTracking().FirstOrDefaultAsync(s => s.IdempotencyKey == key, ct);
        if (existing is null) return null;
        if (existing.OrderId != orderId)
            throw new ConflictException("idempotency_key_reused", "This idempotency key was already used for a different order.");
        var change = await db.Payments.Where(p => p.SettlementId == existing.Id && p.Tendered != null)
            .SumAsync(p => p.Tendered!.Value - p.Amount, ct);
        return new SettlementResult(existing.Id, existing.OrderId, existing.InvoiceNumber, existing.Amount, change, Replayed: true);
    }

    /// <summary>Online payments must reference a succeeded, unused payment intent for this order with the same amount.</summary>
    private async Task ValidateOnlinePaymentsAsync(Order order, List<PaymentInput> payments, CancellationToken ct)
    {
        foreach (var p in payments.Where(p => p.Method == PaymentMethod.Online))
        {
            if (!Guid.TryParse(p.Reference, out var intentId))
                throw new DomainException("intent_required", "Online payments must reference a payment intent.");
            var intent = await db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == intentId && i.OrderId == order.Id, ct)
                         ?? throw new NotFoundException("Payment intent");
            if (intent.Status != PaymentIntentStatus.Succeeded)
                throw new DomainException("intent_not_paid", "The online payment has not been confirmed yet.");
            if (intent.Amount != p.Amount) throw new DomainException("intent_amount_mismatch", "Online payment amount does not match.");
        }
    }
}
