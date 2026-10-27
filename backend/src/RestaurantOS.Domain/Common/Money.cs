namespace RestaurantOS.Domain.Common;

public enum RoundingRule
{
    None = 0,
    NearestRupee = 1,
}

public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundQuantity(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    public static decimal ApplyRule(decimal value, RoundingRule rule) => rule switch
    {
        RoundingRule.None => Round(value),
        RoundingRule.NearestRupee => Math.Round(value, 0, MidpointRounding.AwayFromZero),
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };
}

public static class FinancialYear
{
    /// <summary>Indian financial year label (April–March), e.g. 2026-10-27 → "2026-27".</summary>
    public static string Label(DateOnly date)
    {
        var start = date.Month >= 4 ? date.Year : date.Year - 1;
        return $"{start}-{(start + 1) % 100:00}";
    }
}
