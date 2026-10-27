using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.IntegrationTests.Infrastructure;

namespace RestaurantOS.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ReportTests(ApiFactory factory)
{
    private static JsonNode Row(JsonNode array, string prop, string value) =>
        array.AsArray().Single(r => r![prop]!.ToString() == value)!;

    /// <summary>
    /// Order A: 2 x Veg Spring Roll (180) + Masala Chaas (70) at GST 5% = 451.50 -> 452, paid 200 cash + 252 UPI.
    /// Order B: Cold Coffee (140) at GST 18% = 165.20 -> 165 by card, with a voided Masala Chaas.
    /// Order C: Veg Spring Roll, cancelled.
    /// </summary>
    private static async Task SeedDayAsync(TenantSession s)
    {
        var url = $"/api/outlets/{s.OutletId}/orders";
        var roll = (await s.MenuItemAsync("Veg Spring Roll")).Id();
        var chaas = (await s.MenuItemAsync("Masala Chaas")).Id();
        var coffee = (await s.MenuItemAsync("Cold Coffee")).Id();
        await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/cash-sessions", new { openingFloat = 1000m }, HttpStatusCode.Created);

        var a = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", sendToKitchen = true,
            lines = new object[] { new { menuItemId = roll, quantity = 2 }, new { menuItemId = chaas, quantity = 1 } },
        }, HttpStatusCode.Created);
        Assert.Equal(452m, a.Dec("grandTotal"));
        await s.Client.PostJsonAsync($"{url}/{a.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(),
            payments = new object[] { new { method = "Cash", amount = 200m }, new { method = "Upi", amount = 252m } },
        });

        var b = await s.Client.PostJsonAsync(url, new
        {
            type = "DineIn", tableId = await s.CreateTableAsync("R1"), guests = 3,
            lines = new object[] { new { menuItemId = coffee, quantity = 1 }, new { menuItemId = chaas, quantity = 1 } },
        }, HttpStatusCode.Created);
        var chaasLine = b["lines"]!.AsArray().Single(l => l!["itemName"]!.GetValue<string>() == "Masala Chaas")!.Id();
        await s.Client.PostJsonAsync($"{url}/{b.Id()}/lines/{chaasLine}/void", new { reason = "Guest changed mind" });
        b = await s.Client.PostJsonAsync($"{url}/{b.Id()}/kot", new { });
        Assert.Equal(165m, b.Dec("grandTotal"));
        await s.Client.PostJsonAsync($"{url}/{b.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(), payments = new[] { new { method = "Card", amount = 165m } },
        });

        var c = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", sendToKitchen = true, lines = new[] { new { menuItemId = roll, quantity = 1 } },
        }, HttpStatusCode.Created);
        await s.Client.PostJsonAsync($"{url}/{c.Id()}/cancel", new { reason = "Customer left" });
    }

    [Fact]
    public async Task Sales_report_matches_settled_invoices()
    {
        var s = await factory.SignupAsync();
        await SeedDayAsync(s);

        var report = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/sales");
        var summary = report["summary"]!;
        Assert.Equal(2, summary["orders"]!.GetValue<int>());
        Assert.Equal(570m, summary.Dec("grossSales"));
        Assert.Equal(0m, summary.Dec("discounts"));
        Assert.Equal(570m, summary.Dec("netSales"));
        Assert.Equal(46.7m, summary.Dec("tax"));
        Assert.Equal(0.3m, summary.Dec("roundOff"));
        Assert.Equal(617m, summary.Dec("totalCollected"));
        Assert.Equal(308.5m, summary.Dec("averageOrderValue"));
        Assert.Equal(1, summary["cancelledOrders"]!.GetValue<int>());
        Assert.Equal(180m, summary.Dec("cancelledValue"));
        Assert.Equal(1, summary["voidedItems"]!.GetValue<int>());
        Assert.Equal(70m, summary.Dec("voidedValue"));

        Assert.Equal(452m, Row(report["byType"]!, "key", "Takeaway").Dec("total"));
        Assert.Equal(140m, Row(report["byType"]!, "key", "DineIn").Dec("netSales"));
        Assert.Equal(200m, Row(report["payments"]!, "method", "Cash").Dec("amount"));
        Assert.Equal(252m, Row(report["payments"]!, "method", "Upi").Dec("amount"));
        Assert.Equal(165m, Row(report["payments"]!, "method", "Card").Dec("amount"));

        var taxes = report["taxes"]!.AsArray();
        Assert.Equal(4, taxes.Count);
        var cgst5 = taxes.Single(t => t!["code"]!.GetValue<string>() == "CGST" && t.Dec("ratePercent") == 2.5m)!;
        Assert.Equal(430m, cgst5.Dec("taxable"));
        Assert.Equal(10.75m, cgst5.Dec("amount"));
        var sgst18 = taxes.Single(t => t!["code"]!.GetValue<string>() == "SGST" && t.Dec("ratePercent") == 9m)!;
        Assert.Equal(140m, sgst18.Dec("taxable"));
        Assert.Equal(12.6m, sgst18.Dec("amount"));

        var items = report["items"]!.AsArray();
        Assert.Equal(["Veg Spring Roll", "Cold Coffee", "Masala Chaas"], items.Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal(2m, items[0]!.Dec("quantity"));
        Assert.Equal(360m, Row(report["categories"]!, "category", "Starters").Dec("netSales"));
        Assert.Equal(210m, Row(report["categories"]!, "category", "Beverages").Dec("netSales"));
        Assert.Equal(617m, Assert.Single(report["daily"]!.AsArray())!.Dec("total"));
        Assert.Equal(617m, report["hourly"]!.AsArray().Sum(h => h!.Dec("total")));

        var yesterday = DateOnly.Parse(report["from"]!.GetValue<string>()).AddDays(-1);
        var empty = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/sales?from={yesterday:yyyy-MM-dd}&to={yesterday:yyyy-MM-dd}");
        Assert.Equal(0, empty["summary"]!["orders"]!.GetValue<int>());
        await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/sales?from=2026-02-01&to=2026-01-01", HttpStatusCode.BadRequest);
        await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/sales?from=2024-01-01&to=2026-01-01", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Day_end_reconciles_payments_and_cash_sessions()
    {
        var s = await factory.SignupAsync();
        await SeedDayAsync(s);
        var dayEnd = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/day-end");
        Assert.Equal(617m, dayEnd.Dec("salesTotal"));
        Assert.Equal(617m, dayEnd.Dec("paymentsTotal"));
        Assert.Equal(0m, dayEnd.Dec("unreconciled"));
        Assert.Equal(1, dayEnd["openCashSessions"]!.GetValue<int>());
        Assert.Equal(1200m, dayEnd["cashSessions"]![0]!.Dec("expectedCash"));

        var session = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/cash-sessions/current");
        await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/cash-sessions/{session["session"]!.Id()}/close", new { countedCash = 1190m });
        dayEnd = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/day-end");
        Assert.Equal(0, dayEnd["openCashSessions"]!.GetValue<int>());
        Assert.Equal(-10m, dayEnd.Dec("cashVariance"));
        Assert.Equal(0, dayEnd["runningOrders"]!.GetValue<int>());
    }

    private Task UpgradeAsync(TenantSession s) => factory.WithDbAsync(async db =>
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == s.TenantId);
        tenant.PlanCode = "growth";
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Dashboard_and_reports_are_limited_to_permitted_outlets()
    {
        var s = await factory.SignupAsync();
        await SeedDayAsync(s);
        await UpgradeAsync(s);
        var second = await s.Client.PostJsonAsync("/api/outlets", new { name = "Second Outlet" }, HttpStatusCode.Created);

        var dashboard = await s.Client.GetJsonAsync("/api/dashboard");
        Assert.Equal(2, dashboard["outlets"]!.AsArray().Count);
        Assert.Equal(617m, dashboard["totals"]!.Dec("totalCollected"));
        Assert.Equal(570m, dashboard["totals"]!.Dec("netSales"));
        Assert.Equal(7, dashboard["trend"]!.AsArray().Count);
        Assert.Equal(617m, dashboard["trend"]![6]!.Dec("totalCollected"));
        Assert.Equal("Veg Spring Roll", dashboard["topItems"]![0]!["name"]!.GetValue<string>());

        var manager = await factory.CreateStaffAsync(s, "Manager", second.Id());
        var managerView = await manager.GetJsonAsync("/api/dashboard");
        Assert.Equal("Second Outlet", Assert.Single(managerView["outlets"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Equal(0m, managerView["totals"]!.Dec("totalCollected"));
        await manager.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/sales", HttpStatusCode.NotFound);

        var cashier = await factory.CreateStaffAsync(s, "Cashier");
        await cashier.GetJsonAsync("/api/dashboard", HttpStatusCode.Forbidden);
        await cashier.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/sales", HttpStatusCode.Forbidden);

        var other = await factory.SignupAsync();
        await other.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/reports/day-end", HttpStatusCode.NotFound);
        Assert.Equal(0m, (await other.Client.GetJsonAsync("/api/dashboard"))["totals"]!.Dec("totalCollected"));
    }

    [Fact]
    public async Task Audit_log_is_filtered_by_role_scope_and_paginates()
    {
        var s = await factory.SignupAsync();
        await SeedDayAsync(s);

        var settled = await s.Client.GetJsonAsync("/api/audit?action=order.settled");
        Assert.Equal(2, settled["items"]!.AsArray().Count);
        Assert.All(settled["items"]!.AsArray(), e => Assert.Equal("Owner One", e!["userName"]!.GetValue<string>()));

        var page = await s.Client.GetJsonAsync("/api/audit?limit=2");
        Assert.Equal(2, page["items"]!.AsArray().Count);
        var before = Uri.EscapeDataString(page["nextBefore"]!.GetValue<string>());
        var next = await s.Client.GetJsonAsync($"/api/audit?limit=2&before={before}");
        Assert.DoesNotContain(next["items"]!.AsArray().Select(e => e!.Id()), id => page["items"]!.AsArray().Any(p => p!.Id() == id));

        await UpgradeAsync(s);
        var second = await s.Client.PostJsonAsync("/api/outlets", new { name = "Second Outlet" }, HttpStatusCode.Created);
        var manager = await factory.CreateStaffAsync(s, "Manager", second.Id());
        var managerView = await manager.GetJsonAsync("/api/audit");
        Assert.All(managerView["items"]!.AsArray(), e => Assert.Equal(second.Id(), e!["outletId"]!.GetValue<Guid>()));
        Assert.Empty((await manager.GetJsonAsync("/api/audit?action=order.settled"))["items"]!.AsArray());

        var cashier = await factory.CreateStaffAsync(s, "Cashier");
        await cashier.GetJsonAsync("/api/audit", HttpStatusCode.Forbidden);
        var other = await factory.SignupAsync();
        Assert.Empty((await other.Client.GetJsonAsync("/api/audit?action=order"))["items"]!.AsArray());
    }
}
