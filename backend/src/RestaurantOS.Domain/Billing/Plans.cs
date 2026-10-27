namespace RestaurantOS.Domain.Billing;

public sealed record Plan(string Code, string Name, decimal MonthlyPrice, int MaxOutlets, int MaxStaff, bool Inventory, bool Integrations);

public static class PlanCatalog
{
    public static readonly Plan Starter = new("starter", "Starter", 999m, 1, 10, Inventory: true, Integrations: false);
    public static readonly Plan Growth = new("growth", "Growth", 2499m, 5, 50, Inventory: true, Integrations: true);
    public static readonly Plan Scale = new("scale", "Scale", 5999m, 25, 500, Inventory: true, Integrations: true);

    public static readonly IReadOnlyList<Plan> All = [Starter, Growth, Scale];

    public static Plan Get(string code) =>
        All.FirstOrDefault(p => p.Code == code) ?? throw new ArgumentException($"Unknown plan '{code}'.", nameof(code));

    public static bool Exists(string code) => All.Any(p => p.Code == code);

    public const int TrialDays = 14;
}
