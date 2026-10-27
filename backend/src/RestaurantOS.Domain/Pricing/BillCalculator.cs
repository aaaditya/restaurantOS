using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Pricing;

public enum DiscountType
{
    Percent = 0,
    Flat = 1,
}

public sealed record TaxRate(string Code, decimal RatePercent);

public sealed record BillLineInput(Guid LineId, decimal UnitPrice, decimal Quantity, IReadOnlyList<TaxRate> Taxes);

public sealed record DiscountInput(DiscountType Type, decimal Value);

public sealed record BillSettings(bool PricesIncludeTax, RoundingRule Rounding);

public sealed record TaxAmount(string Code, decimal RatePercent, decimal Amount);

public sealed record BillLineResult(
    Guid LineId,
    decimal Gross,
    decimal Discount,
    decimal Taxable,
    IReadOnlyList<TaxAmount> Taxes,
    decimal TaxTotal,
    decimal Total);

public sealed record BillResult(
    IReadOnlyList<BillLineResult> Lines,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxableTotal,
    decimal TaxTotal,
    IReadOnlyList<TaxAmount> TaxBreakdown,
    decimal RoundOff,
    decimal GrandTotal);

/// <summary>
/// Deterministic bill computation. See docs/adr/0003-money-tax-rounding.md for the rules.
/// </summary>
public static class BillCalculator
{
    public static BillResult Calculate(IReadOnlyList<BillLineInput> lines, DiscountInput? discount, BillSettings settings)
    {
        foreach (var line in lines)
        {
            if (line.Quantity <= 0) throw new DomainException("invalid_quantity", "Quantity must be positive.");
            if (line.UnitPrice < 0) throw new DomainException("invalid_price", "Unit price cannot be negative.");
            if (line.Taxes.Any(t => t.RatePercent < 0)) throw new DomainException("invalid_tax", "Tax rate cannot be negative.");
        }

        var grosses = lines.Select(l => Money.Round(l.UnitPrice * l.Quantity)).ToArray();
        var subtotal = grosses.Sum();
        var discounts = AllocateDiscount(grosses, subtotal, discount);

        var results = new List<BillLineResult>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var net = grosses[i] - discounts[i];
            decimal taxable;
            List<TaxAmount> taxes;
            decimal total;

            if (settings.PricesIncludeTax)
            {
                var totalRate = line.Taxes.Sum(t => t.RatePercent);
                taxable = Money.Round(net / (1 + totalRate / 100m));
                var taxTotal = net - taxable;
                taxes = SplitInclusiveTax(line.Taxes, taxable, taxTotal);
                total = net;
            }
            else
            {
                taxable = net;
                taxes = line.Taxes.Select(t => new TaxAmount(t.Code, t.RatePercent, Money.Round(taxable * t.RatePercent / 100m))).ToList();
                total = taxable + taxes.Sum(t => t.Amount);
            }

            results.Add(new BillLineResult(line.LineId, grosses[i], discounts[i], taxable, taxes, taxes.Sum(t => t.Amount), total));
        }

        var beforeRounding = results.Sum(r => r.Total);
        var grand = Money.ApplyRule(beforeRounding, settings.Rounding);

        var breakdown = results
            .SelectMany(r => r.Taxes)
            .GroupBy(t => (t.Code, t.RatePercent))
            .Select(g => new TaxAmount(g.Key.Code, g.Key.RatePercent, g.Sum(x => x.Amount)))
            .OrderBy(t => t.Code).ThenBy(t => t.RatePercent)
            .ToList();

        return new BillResult(
            results,
            subtotal,
            discounts.Sum(),
            results.Sum(r => r.Taxable),
            results.Sum(r => r.TaxTotal),
            breakdown,
            grand - beforeRounding,
            grand);
    }

    private static decimal[] AllocateDiscount(decimal[] grosses, decimal subtotal, DiscountInput? discount)
    {
        var result = new decimal[grosses.Length];
        if (discount is null || discount.Value == 0 || subtotal == 0) return result;
        if (discount.Value < 0) throw new DomainException("invalid_discount", "Discount cannot be negative.");

        if (discount.Type == DiscountType.Percent)
        {
            if (discount.Value > 100) throw new DomainException("invalid_discount", "Discount percent cannot exceed 100.");
            for (var i = 0; i < grosses.Length; i++) result[i] = Money.Round(grosses[i] * discount.Value / 100m);
            return result;
        }

        var flat = Math.Min(Money.Round(discount.Value), subtotal);
        var allocated = 0m;
        for (var i = 0; i < grosses.Length; i++)
        {
            result[i] = Money.Round(flat * grosses[i] / subtotal);
            allocated += result[i];
        }

        var remainder = flat - allocated;
        if (remainder != 0)
        {
            var largest = Array.IndexOf(grosses, grosses.Max());
            result[largest] += remainder;
        }

        return result;
    }

    private static List<TaxAmount> SplitInclusiveTax(IReadOnlyList<TaxRate> rates, decimal taxable, decimal taxTotal)
    {
        var list = new List<TaxAmount>(rates.Count);
        var allocated = 0m;
        for (var i = 0; i < rates.Count; i++)
        {
            var amount = i == rates.Count - 1
                ? taxTotal - allocated
                : Money.Round(taxable * rates[i].RatePercent / 100m);
            allocated += amount;
            list.Add(new TaxAmount(rates[i].Code, rates[i].RatePercent, amount));
        }

        return list;
    }
}
