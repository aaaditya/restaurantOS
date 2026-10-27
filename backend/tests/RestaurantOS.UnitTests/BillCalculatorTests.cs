using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Pricing;

namespace RestaurantOS.UnitTests;

public class BillCalculatorTests
{
    private static readonly TaxRate[] Gst5 = [new("CGST", 2.5m), new("SGST", 2.5m)];
    private static readonly BillSettings Exclusive = new(PricesIncludeTax: false, RoundingRule.NearestRupee);
    private static readonly BillSettings ExclusiveNoRound = new(PricesIncludeTax: false, RoundingRule.None);
    private static readonly BillSettings Inclusive = new(PricesIncludeTax: true, RoundingRule.NearestRupee);

    private static BillLineInput Line(decimal price, decimal qty = 1, TaxRate[]? taxes = null) =>
        new(Guid.NewGuid(), price, qty, taxes ?? Gst5);

    [Fact]
    public void Exclusive_tax_is_added_per_component()
    {
        var bill = BillCalculator.Calculate([Line(100, 2)], null, Exclusive);

        Assert.Equal(200m, bill.Subtotal);
        Assert.Equal(10m, bill.TaxTotal);
        Assert.Equal(210m, bill.GrandTotal);
        Assert.Equal(0m, bill.RoundOff);
        Assert.Equal([5m, 5m], bill.TaxBreakdown.Select(t => t.Amount));
    }

    [Fact]
    public void Grand_total_rounds_to_nearest_rupee_with_explicit_round_off()
    {
        var bill = BillCalculator.Calculate([Line(99.99m)], null, Exclusive);

        Assert.Equal(5.00m, bill.TaxTotal); // 2.49975 → 2.50 per component
        Assert.Equal(105m, bill.GrandTotal);
        Assert.Equal(0.01m, bill.RoundOff);
    }

    [Fact]
    public void Rounding_none_keeps_paise()
    {
        var bill = BillCalculator.Calculate([Line(99.99m)], null, ExclusiveNoRound);
        Assert.Equal(104.99m, bill.GrandTotal);
        Assert.Equal(0m, bill.RoundOff);
    }

    [Fact]
    public void Percent_discount_is_applied_before_tax_per_line()
    {
        var bill = BillCalculator.Calculate([Line(150), Line(50)], new DiscountInput(DiscountType.Percent, 10), Exclusive);

        Assert.Equal([15m, 5m], bill.Lines.Select(l => l.Discount));
        Assert.Equal(180m, bill.TaxableTotal);
        // 135 × 2.5% = 3.375 → 3.38 (×2), 45 × 2.5% = 1.125 → 1.13 (×2)
        Assert.Equal(9.02m, bill.TaxTotal);
        Assert.Equal(189m, bill.GrandTotal);
        Assert.Equal(-0.02m, bill.RoundOff);
    }

    [Fact]
    public void Flat_discount_remainder_goes_to_largest_line()
    {
        var bill = BillCalculator.Calculate([Line(100), Line(100), Line(100)], new DiscountInput(DiscountType.Flat, 100), ExclusiveNoRound);

        Assert.Equal(100m, bill.DiscountTotal);
        Assert.Equal([33.34m, 33.33m, 33.33m], bill.Lines.Select(l => l.Discount));
    }

    [Fact]
    public void Flat_discount_is_capped_at_subtotal()
    {
        var bill = BillCalculator.Calculate([Line(80)], new DiscountInput(DiscountType.Flat, 500), Exclusive);

        Assert.Equal(80m, bill.DiscountTotal);
        Assert.Equal(0m, bill.GrandTotal);
    }

    [Fact]
    public void Inclusive_prices_extract_tax_and_keep_total()
    {
        var bill = BillCalculator.Calculate([Line(105)], null, Inclusive);

        Assert.Equal(100m, bill.TaxableTotal);
        Assert.Equal(5m, bill.TaxTotal);
        Assert.Equal(105m, bill.GrandTotal);
    }

    [Fact]
    public void Inclusive_tax_split_assigns_remainder_to_last_component()
    {
        var bill = BillCalculator.Calculate([Line(99)], null, Inclusive);
        var line = bill.Lines.Single();

        Assert.Equal(94.29m, line.Taxable);
        Assert.Equal(4.71m, line.TaxTotal);
        Assert.Equal([2.36m, 2.35m], line.Taxes.Select(t => t.Amount));
        Assert.Equal(99m, line.Total);
    }

    [Fact]
    public void Inclusive_with_discount_reduces_total_by_discount()
    {
        var bill = BillCalculator.Calculate([Line(210)], new DiscountInput(DiscountType.Percent, 50), Inclusive);

        Assert.Equal(105m, bill.GrandTotal);
        Assert.Equal(100m, bill.TaxableTotal);
    }

    [Fact]
    public void Untaxed_lines_have_no_tax()
    {
        var bill = BillCalculator.Calculate([Line(49.5m, 2, [])], null, Exclusive);
        Assert.Equal(0m, bill.TaxTotal);
        Assert.Equal(99m, bill.GrandTotal);
        Assert.Empty(bill.TaxBreakdown);
    }

    [Fact]
    public void Tax_breakdown_groups_components_across_rates()
    {
        TaxRate[] gst18 = [new("CGST", 9m), new("SGST", 9m)];
        var bill = BillCalculator.Calculate([Line(100), Line(100, 1, gst18)], null, ExclusiveNoRound);

        Assert.Equal(4, bill.TaxBreakdown.Count);
        Assert.Equal(23m, bill.TaxTotal);
        Assert.Equal(223m, bill.GrandTotal);
    }

    [Fact]
    public void Empty_bill_is_zero()
    {
        var bill = BillCalculator.Calculate([], new DiscountInput(DiscountType.Flat, 10), Exclusive);
        Assert.Equal(0m, bill.GrandTotal);
        Assert.Equal(0m, bill.DiscountTotal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_quantity_is_rejected(decimal qty) =>
        Assert.Throws<DomainException>(() => BillCalculator.Calculate([Line(10, qty)], null, Exclusive));

    [Fact]
    public void Percent_over_100_is_rejected() =>
        Assert.Throws<DomainException>(() => BillCalculator.Calculate([Line(10)], new DiscountInput(DiscountType.Percent, 101), Exclusive));

    [Fact]
    public void Fractional_quantities_are_supported()
    {
        var bill = BillCalculator.Calculate([Line(400, 0.5m)], null, ExclusiveNoRound);
        Assert.Equal(200m, bill.Subtotal);
        Assert.Equal(210m, bill.GrandTotal);
    }
}
