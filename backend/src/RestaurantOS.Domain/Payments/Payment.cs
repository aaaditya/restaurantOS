using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Payments;

public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    Upi = 2,
    Wallet = 3,
    Online = 4,
    Loyalty = 5,
    /// <summary>Paid to an aggregator platform; settled later by the platform.</summary>
    Aggregator = 6,
}

/// <summary>One atomic settlement of an order. Unique per (tenant, idempotency key).</summary>
public class Settlement : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid OrderId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string InvoiceNumber { get; set; }
    public decimal Amount { get; set; }
    public Guid? SettledByUserId { get; set; }
}

public class Payment : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid OrderId { get; set; }
    public Guid SettlementId { get; set; }
    public Guid? CashSessionId { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Cash handed over by the guest (Amount + change).</summary>
    public decimal? Tendered { get; set; }
    public string? Reference { get; set; }
    public DateOnly BusinessDate { get; set; }
    public Guid? ReceivedByUserId { get; set; }
}

public enum CashSessionStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>A cashier shift: opening float, cash payments received, and counted cash at close.</summary>
public class CashSession : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid OpenedByUserId { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public CashSessionStatus Status { get; set; } = CashSessionStatus.Open;
    public decimal OpeningFloat { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? Variance { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    public void Close(decimal cashPayments, decimal counted, Guid userId, string? notes, DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Open) throw new ConflictException("session_closed", "Cash session is already closed.");
        if (counted < 0) throw new DomainException("invalid_amount", "Counted cash cannot be negative.");
        ExpectedCash = Money.Round(OpeningFloat + cashPayments);
        CountedCash = Money.Round(counted);
        Variance = CountedCash - ExpectedCash;
        Status = CashSessionStatus.Closed;
        ClosedByUserId = userId;
        ClosedAt = now;
        Notes = notes;
    }
}

public enum PaymentIntentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
}

/// <summary>Online/UPI collection via a payment gateway adapter (mock until credentials exist).</summary>
public class PaymentIntent : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public required string Provider { get; set; }
    public required string ProviderRef { get; set; }
    public PaymentIntentStatus Status { get; set; }
    public string? PaymentLink { get; set; }
}
