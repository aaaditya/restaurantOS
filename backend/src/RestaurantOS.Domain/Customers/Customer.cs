using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Customers;

public class Customer : TenantEntity
{
    /// <summary>Normalised phone number, unique per tenant.</summary>
    public required string Phone { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public DateOnly? Birthday { get; set; }
    public string? Notes { get; set; }
    public decimal LoyaltyPoints { get; set; }
    public decimal TotalSpend { get; set; }
    public int VisitCount { get; set; }
    public DateTimeOffset? LastVisitAt { get; set; }

    public static string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 12 && digits.StartsWith("91")) digits = digits[2..];
        if (digits.Length < 7 || digits.Length > 15) throw new DomainException("invalid_phone", "Phone number is invalid.");
        return digits;
    }
}

public enum LoyaltyTransactionType
{
    Earn = 0,
    Redeem = 1,
    Adjust = 2,
}

public class LoyaltyTransaction : TenantEntity
{
    public Guid CustomerId { get; set; }
    public Guid? OutletId { get; set; }
    public Guid? OrderId { get; set; }
    public LoyaltyTransactionType Type { get; set; }
    /// <summary>Signed points.</summary>
    public decimal Points { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Notes { get; set; }
}
