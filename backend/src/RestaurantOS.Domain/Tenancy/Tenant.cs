using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Tenancy;

public enum SubscriptionStatus
{
    Trialing = 0,
    Active = 1,
    PastDue = 2,
    Cancelled = 3,
}

public class Tenant : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string PlanCode { get; set; } = "starter";
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Trialing;
    public DateTimeOffset? TrialEndsAt { get; set; }
    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }
    public string? BillingCustomerRef { get; set; }

    public bool IsSubscriptionUsable(DateTimeOffset now) => SubscriptionStatus switch
    {
        SubscriptionStatus.Active => true,
        SubscriptionStatus.PastDue => true,
        SubscriptionStatus.Trialing => TrialEndsAt is null || TrialEndsAt > now,
        _ => false,
    };
}

public class Outlet : TenantEntity
{
    public required string Name { get; set; }
    /// <summary>Short public code used in QR URLs and invoice prefixes.</summary>
    public required string Code { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Gstin { get; set; }
    public string Currency { get; set; } = "INR";
    public string TimeZone { get; set; } = "Asia/Kolkata";
    public bool PricesIncludeTax { get; set; }
    public RoundingRule Rounding { get; set; } = RoundingRule.NearestRupee;
    public required string InvoicePrefix { get; set; }
    /// <summary>Loyalty points earned per 100 currency units spent (0 disables earning).</summary>
    public decimal LoyaltyPointsPer100 { get; set; } = 5;
    /// <summary>Currency value of one loyalty point when redeemed.</summary>
    public decimal LoyaltyPointValue { get; set; } = 0.25m;
    public bool QrOrderingEnabled { get; set; } = true;
    public bool IsActive { get; set; } = true;

    public DateOnly LocalDate(DateTimeOffset instant)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tz).DateTime);
    }

    /// <summary>UTC instants bounding the outlet-local calendar day [start, end).</summary>
    public (DateTimeOffset Start, DateTimeOffset End) DayBounds(DateOnly date)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        DateTimeOffset ToUtc(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
        }

        return (ToUtc(date), ToUtc(date.AddDays(1)));
    }
}

/// <summary>Atomic per-outlet sequence (orders, KOTs, invoices).</summary>
public class OutletCounter
{
    public Guid OutletId { get; set; }
    public required string Name { get; set; }
    public long Value { get; set; }
}
