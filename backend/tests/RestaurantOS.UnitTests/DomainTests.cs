using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Customers;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Kitchen;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Payments;
using RestaurantOS.Domain.Pricing;
using RestaurantOS.Domain.Tenancy;

namespace RestaurantOS.UnitTests;

public class OrderTests
{
    private static Order NewOrder()
    {
        var order = new Order { Rounding = RoundingRule.NearestRupee };
        order.AddLine(new OrderLine
        {
            ItemName = "Paneer Tikka", UnitPrice = 260, Quantity = 1,
            TaxRates = [new LineTax { Code = "CGST", RatePercent = 2.5m }, new LineTax { Code = "SGST", RatePercent = 2.5m }],
        });
        order.AddLine(new OrderLine { ItemName = "Naan", UnitPrice = 60, Quantity = 2 });
        return order;
    }

    [Fact]
    public void Totals_follow_lines()
    {
        var order = NewOrder();
        Assert.Equal(380m, order.Subtotal);
        Assert.Equal(13m, order.TaxTotal);
        Assert.Equal(393m, order.GrandTotal);
    }

    [Fact]
    public void Voiding_a_line_requires_reason_and_recalculates()
    {
        var order = NewOrder();
        var naan = order.Lines[1];
        Assert.Throws<DomainException>(() => order.VoidLine(naan.Id, " "));
        order.VoidLine(naan.Id, "Guest changed mind");
        Assert.Equal(OrderLineStatus.Voided, naan.Status);
        Assert.Equal(273m, order.GrandTotal);
        Assert.Equal(0m, naan.Total);
    }

    [Fact]
    public void Discount_requires_reason()
    {
        var order = NewOrder();
        Assert.Throws<DomainException>(() => order.ApplyDiscount(DiscountType.Percent, 10, null));
        order.ApplyDiscount(DiscountType.Percent, 10, "Regular guest");
        Assert.Equal(38m, order.DiscountTotal);
        order.ApplyDiscount(null, 0, null);
        Assert.Equal(0m, order.DiscountTotal);
    }

    [Fact]
    public void Settle_requires_all_items_sent_and_exact_payment()
    {
        var order = NewOrder();
        var today = new DateOnly(2026, 10, 27);
        Assert.Throws<ConflictException>(() => order.Settle("INV/1", 393, today, DateTimeOffset.UtcNow));

        foreach (var l in order.Lines) l.Status = OrderLineStatus.Sent;
        Assert.Throws<DomainException>(() => order.Settle("INV/1", 390, today, DateTimeOffset.UtcNow));

        order.Settle("INV/1", 393, today, DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Settled, order.Status);
        Assert.Throws<ConflictException>(() => order.AddLine(new OrderLine { ItemName = "x", UnitPrice = 1, Quantity = 1 }));
        Assert.Throws<ConflictException>(() => order.Cancel("late", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Sent_lines_cannot_change_quantity()
    {
        var order = NewOrder();
        order.Lines[0].Status = OrderLineStatus.Sent;
        Assert.Throws<ConflictException>(() => order.ChangeLineQuantity(order.Lines[0].Id, 3));
    }

    [Fact]
    public void Cancel_voids_all_lines()
    {
        var order = NewOrder();
        order.Cancel("Guest left", DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.All(order.Lines, l => Assert.Equal(OrderLineStatus.Voided, l.Status));
        Assert.Equal(0m, order.GrandTotal);
    }

    [Fact]
    public void Billed_orders_are_locked_until_reopened()
    {
        var order = NewOrder();
        order.MarkBilled(DateTimeOffset.UtcNow);
        Assert.Throws<ConflictException>(() => order.ApplyDiscount(DiscountType.Flat, 10, "x"));
        order.Reopen();
        order.ApplyDiscount(DiscountType.Flat, 10, "x");
        Assert.Equal(10m, order.DiscountTotal);
    }
}

public class KotTests
{
    [Fact]
    public void Normal_kot_follows_kitchen_flow()
    {
        var kot = new Kot { KotNumber = "K1", Station = "Main" };
        var now = DateTimeOffset.UtcNow;
        kot.MoveTo(KotStatus.Preparing, now);
        kot.MoveTo(KotStatus.Ready, now);
        kot.MoveTo(KotStatus.Served, now);
        Assert.Equal(KotStatus.Served, kot.Status);
        Assert.NotNull(kot.ReadyAt);
        Assert.Throws<ConflictException>(() => kot.MoveTo(KotStatus.New, now));
    }

    [Fact]
    public void Cancellation_kot_can_only_be_acknowledged()
    {
        var kot = new Kot { KotNumber = "K2", Station = "Main", Kind = KotKind.Cancellation };
        Assert.Throws<ConflictException>(() => kot.MoveTo(KotStatus.Preparing, DateTimeOffset.UtcNow));
        kot.MoveTo(KotStatus.Served, DateTimeOffset.UtcNow);
    }
}

public class MiscDomainTests
{
    [Theory]
    [InlineData(2026, 3, 31, "2025-26")]
    [InlineData(2026, 4, 1, "2026-27")]
    [InlineData(2099, 12, 31, "2099-00")]
    public void Financial_year_label(int y, int m, int d, string expected) =>
        Assert.Equal(expected, FinancialYear.Label(new DateOnly(y, m, d)));

    [Theory]
    [InlineData("+91 98765-43210", "9876543210")]
    [InlineData("098765 43210", "09876543210")]
    public void Phone_is_normalised(string input, string expected) => Assert.Equal(expected, Customer.NormalizePhone(input));

    [Fact]
    public void Invalid_phone_is_rejected() => Assert.Throws<DomainException>(() => Customer.NormalizePhone("12ab"));

    [Fact]
    public void Cash_session_close_computes_variance()
    {
        var s = new CashSession { OpeningFloat = 500 };
        s.Close(cashPayments: 1250, counted: 1740, Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        Assert.Equal(1750m, s.ExpectedCash);
        Assert.Equal(-10m, s.Variance);
        Assert.Throws<ConflictException>(() => s.Close(0, 0, Guid.NewGuid(), null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Owner_has_every_permission_and_waiter_cannot_settle()
    {
        Assert.All(Enum.GetValues<Permission>(), p => Assert.True(RolePermissions.Has(Role.Owner, p)));
        Assert.False(RolePermissions.Has(Role.Waiter, Permission.OrderSettle));
        Assert.False(RolePermissions.Has(Role.Kitchen, Permission.OrderCreate));
        Assert.False(RolePermissions.Has(Role.Manager, Permission.BillingManage));
    }

    [Fact]
    public void Trial_subscription_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var t = new Tenant { Name = "x", Slug = "x", TrialEndsAt = now.AddDays(-1) };
        Assert.False(t.IsSubscriptionUsable(now));
        t.SubscriptionStatus = SubscriptionStatus.Active;
        Assert.True(t.IsSubscriptionUsable(now));
    }

    [Fact]
    public void Outlet_day_bounds_use_outlet_time_zone()
    {
        var outlet = new Outlet { Name = "x", Code = "x", InvoicePrefix = "X", TimeZone = "Asia/Kolkata" };
        var (start, end) = outlet.DayBounds(new DateOnly(2026, 10, 27));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 18, 30, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.FromDays(1), end - start);
        Assert.Equal(new DateOnly(2026, 10, 27), outlet.LocalDate(new DateTimeOffset(2026, 10, 26, 19, 0, 0, TimeSpan.Zero)));
    }
}
