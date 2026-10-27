using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.IntegrationTests.Infrastructure;

namespace RestaurantOS.IntegrationTests;

[Collection(ApiCollection.Name)]
public class QrAndCustomerTests(ApiFactory factory)
{
    private static async Task<string> QrTokenAsync(TenantSession s, Guid tableId)
    {
        var tables = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/tables");
        return tables.AsArray().First(t => t!.Id() == tableId)!["qrToken"]!.GetValue<string>();
    }

    private static object GuestOrder(Guid clientOrderId, Guid menuItemId, int quantity = 1, string? phone = null) => new
    {
        clientOrderId, customerName = "Guest", customerPhone = phone, lines = new[] { new { menuItemId, quantity } },
    };

    [Fact]
    public async Task Guest_places_a_qr_order_that_staff_accepts_and_the_guest_can_track()
    {
        var s = await factory.SignupAsync();
        var table = await s.CreateTableAsync("T7");
        var token = await QrTokenAsync(s, table);
        var guest = factory.CreateClient();

        var page = await guest.GetJsonAsync($"/api/public/tables/{token}");
        Assert.Equal("T7", page["table"]!.GetValue<string>());
        Assert.True(page["orderingOpen"]!.GetValue<bool>());
        var roll = page["menu"]!["items"]!.AsArray().First(i => i!["name"]!.GetValue<string>() == "Veg Spring Roll")!.Id();

        var clientOrderId = Guid.NewGuid();
        var placed = await guest.PostJsonAsync($"/api/public/tables/{token}/orders", GuestOrder(clientOrderId, roll, 2), HttpStatusCode.Created);
        Assert.Equal("pending", placed["state"]!.GetValue<string>());
        Assert.Equal(378m, placed.Dec("grandTotal"));
        var replay = await guest.PostJsonAsync($"/api/public/tables/{token}/orders", GuestOrder(clientOrderId, roll, 2));
        Assert.Equal(placed["orderNumber"]!.GetValue<int>(), replay["orderNumber"]!.GetValue<int>());

        var pending = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders?view=pending");
        var order = Assert.Single(pending.AsArray())!;
        Assert.Equal("Qr", order["source"]!.GetValue<string>());
        Assert.Empty((await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders/{order.Id()}/kots")).AsArray());

        var accepted = await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/orders/{order.Id()}/accept", new { });
        Assert.Equal("Open", accepted["status"]!.GetValue<string>());
        Assert.Single((await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders/{order.Id()}/kots")).AsArray());
        await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/orders/{order.Id()}/accept", new { }, HttpStatusCode.Conflict);

        var tracked = await guest.GetJsonAsync($"/api/public/tables/{token}/orders/{clientOrderId}");
        Assert.Equal("accepted", tracked["state"]!.GetValue<string>());
        Assert.Equal(2m, tracked["lines"]![0]!.Dec("quantity"));
        await guest.GetJsonAsync($"/api/public/tables/{token}/orders/{Guid.NewGuid()}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accepting_a_qr_order_on_an_occupied_table_merges_into_the_running_order()
    {
        var s = await factory.SignupAsync();
        var table = await s.CreateTableAsync("T2");
        var token = await QrTokenAsync(s, table);
        var roll = (await s.MenuItemAsync("Veg Spring Roll")).Id();
        var chaas = (await s.MenuItemAsync("Masala Chaas")).Id();

        var running = await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/orders", new
        {
            type = "DineIn", tableId = table, lines = new[] { new { menuItemId = roll, quantity = 2 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);

        var guest = factory.CreateClient();
        var clientOrderId = Guid.NewGuid();
        var placed = await guest.PostJsonAsync($"/api/public/tables/{token}/orders", GuestOrder(clientOrderId, chaas), HttpStatusCode.Created);
        var qrOrder = (await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders?view=pending")).AsArray().Single()!;
        Assert.Equal(placed["orderNumber"]!.GetValue<int>(), qrOrder["orderNumber"]!.GetValue<int>());

        var merged = await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/orders/{qrOrder.Id()}/accept", new { });
        Assert.Equal(running.Id(), merged.Id());
        Assert.Equal(452m, merged.Dec("grandTotal"));   // 2 x 189 + 73.50, rounded
        Assert.Equal(2, (await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders/{running.Id()}/kots")).AsArray().Count);

        var source = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders/{qrOrder.Id()}");
        Assert.Equal("Cancelled", source["status"]!.GetValue<string>());
        Assert.Equal(running.Id(), source["mergedIntoOrderId"]!.GetValue<Guid>());
        Assert.Equal("accepted", (await guest.GetJsonAsync($"/api/public/tables/{token}/orders/{clientOrderId}"))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Qr_ordering_respects_outlet_settings_tokens_and_tenants()
    {
        var s = await factory.SignupAsync();
        var table = await s.CreateTableAsync();
        var token = await QrTokenAsync(s, table);
        var roll = (await s.MenuItemAsync("Veg Spring Roll")).Id();
        var guest = factory.CreateClient();

        await guest.GetJsonAsync("/api/public/tables/not-a-real-token", HttpStatusCode.NotFound);
        await guest.PostJsonAsync($"/api/public/tables/{token}/orders", new { clientOrderId = Guid.NewGuid(), lines = Array.Empty<object>() },
            HttpStatusCode.BadRequest);

        var outlet = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}");
        outlet["qrOrderingEnabled"] = false;
        await s.Client.PutJsonAsync($"/api/outlets/{s.OutletId}", outlet);
        var closed = await guest.GetJsonAsync($"/api/public/tables/{token}");
        Assert.False(closed["orderingOpen"]!.GetValue<bool>());
        Assert.Null(closed["menu"]);
        await guest.PostJsonAsync($"/api/public/tables/{token}/orders", GuestOrder(Guid.NewGuid(), roll), HttpStatusCode.BadRequest);
        outlet["qrOrderingEnabled"] = true;
        await s.Client.PutJsonAsync($"/api/outlets/{s.OutletId}", outlet);

        // A menu item from another tenant cannot be ordered through this tenant's table.
        var other = await factory.SignupAsync();
        var foreignItem = (await other.MenuItemAsync("Veg Spring Roll")).Id();
        await guest.PostJsonAsync($"/api/public/tables/{token}/orders", GuestOrder(Guid.NewGuid(), foreignItem), HttpStatusCode.NotFound);
        Assert.Empty((await other.Client.GetJsonAsync($"/api/outlets/{other.OutletId}/orders?view=pending")).AsArray());

        // Regenerating the QR code invalidates the printed token.
        await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/tables/{table}/regenerate-qr", new { });
        await guest.GetJsonAsync($"/api/public/tables/{token}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Settlement_earns_and_redeems_loyalty_points_once()
    {
        var s = await factory.SignupAsync();
        var url = $"/api/outlets/{s.OutletId}/orders";
        var roll = (await s.MenuItemAsync("Veg Spring Roll")).Id();

        var customer = await s.Client.PostJsonAsync("/api/customers", new { phone = "+91 98765 43210", name = "Asha" }, HttpStatusCode.Created);
        await s.Client.PostJsonAsync("/api/customers", new { phone = "9876543210" }, HttpStatusCode.Conflict);
        var found = await s.Client.GetJsonAsync("/api/customers/lookup?phone=98765-43210");
        Assert.Equal(customer.Id(), found["customer"]!.Id());

        // 4 x 189 = 756 -> floor(756 / 100 x 5) = 37 points.
        var first = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", customerPhone = "9876543210", lines = new[] { new { menuItemId = roll, quantity = 4 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);
        var settle = new { idempotencyKey = Guid.NewGuid().ToString(), payments = new[] { new { method = "Cash", amount = 756m } } };
        await s.Client.PostJsonAsync($"{url}/{first.Id()}/settle", settle);
        await s.Client.PostJsonAsync($"{url}/{first.Id()}/settle", settle);
        var detail = await s.Client.GetJsonAsync($"/api/customers/{customer.Id()}");
        Assert.Equal(37m, detail["customer"]!.Dec("loyaltyPoints"));
        Assert.Equal(756m, detail["customer"]!.Dec("totalSpend"));
        Assert.Equal(1, detail["customer"]!["visitCount"]!.GetValue<int>());

        // Redeem ₹5 (20 points at 0.25) on a ₹189 bill: earns floor(184 / 100 x 5) = 9.
        var second = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", customerPhone = "9876543210", lines = new[] { new { menuItemId = roll, quantity = 1 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);
        await s.Client.PostJsonAsync($"{url}/{second.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(),
            payments = new object[] { new { method = "Loyalty", amount = 5m }, new { method = "Upi", amount = 184m } },
        });
        detail = await s.Client.GetJsonAsync($"/api/customers/{customer.Id()}");
        Assert.Equal(26m, detail["customer"]!.Dec("loyaltyPoints"));
        Assert.Equal([-20m, 9m, 37m], detail["loyalty"]!.AsArray().Select(t => t!.Dec("points")).Order());
        Assert.Equal(2, detail["orders"]!.AsArray().Count);

        var third = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", customerPhone = "9876543210", lines = new[] { new { menuItemId = roll, quantity = 1 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);
        await s.Client.PostJsonAsync($"{url}/{third.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(),
            payments = new object[] { new { method = "Loyalty", amount = 10m }, new { method = "Cash", amount = 179m } },
        }, HttpStatusCode.BadRequest);   // needs 40 points, has 26

        await factory.WithDbAsync(async db =>
            Assert.Equal(3, await db.LoyaltyTransactions.IgnoreQueryFilters().CountAsync(t => t.CustomerId == customer.Id())));
    }

    [Fact]
    public async Task Concurrent_redemptions_cannot_overspend_points()
    {
        var s = await factory.SignupAsync();
        var url = $"/api/outlets/{s.OutletId}/orders";
        var roll = (await s.MenuItemAsync("Veg Spring Roll")).Id();
        var customer = await s.Client.PostJsonAsync("/api/customers", new { phone = "9000000001", name = "Ravi" }, HttpStatusCode.Created);
        await s.Client.PostJsonAsync($"/api/customers/{customer.Id()}/loyalty-adjustments", new { points = 40m, reason = "Welcome bonus" });

        var orders = new List<JsonNode>();
        for (var i = 0; i < 4; i++)
            orders.Add(await s.Client.PostJsonAsync(url, new
            {
                type = "Takeaway", customerPhone = "9000000001", lines = new[] { new { menuItemId = roll, quantity = 1 } }, sendToKitchen = true,
            }, HttpStatusCode.Created));

        // Each redemption of ₹10 needs 40 points, so exactly one can succeed.
        var responses = await Task.WhenAll(orders.Select(o => s.Client.PostAsJsonAsync($"{url}/{o.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(),
            payments = new object[] { new { method = "Loyalty", amount = 10m }, new { method = "Cash", amount = 179m } },
        }, TestClient.Json)));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.All(responses.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        var detail = await s.Client.GetJsonAsync($"/api/customers/{customer.Id()}");
        Assert.Equal(8m, detail["customer"]!.Dec("loyaltyPoints"));   // 40 - 40 + floor(179 / 100 x 5)
    }

    [Fact]
    public async Task Customer_access_follows_roles_and_tenants()
    {
        var s = await factory.SignupAsync();
        var customer = await s.Client.PostJsonAsync("/api/customers", new { phone = "9111111111", name = "Meera" }, HttpStatusCode.Created);

        var cashier = await factory.CreateStaffAsync(s, "Cashier");
        Assert.Single((await cashier.GetJsonAsync("/api/customers?search=mee")).AsArray());
        await cashier.PostJsonAsync($"/api/customers/{customer.Id()}/loyalty-adjustments", new { points = 100m, reason = "Gift" },
            HttpStatusCode.Forbidden);
        await s.Client.PostJsonAsync($"/api/customers/{customer.Id()}/loyalty-adjustments", new { points = -1m, reason = "Fix" },
            HttpStatusCode.BadRequest);
        await s.Client.PostJsonAsync($"/api/customers/{customer.Id()}/loyalty-adjustments", new { points = 2.5m, reason = "Fix" },
            HttpStatusCode.BadRequest);

        var kitchen = await factory.CreateStaffAsync(s, "Kitchen");
        await kitchen.GetJsonAsync("/api/customers", HttpStatusCode.Forbidden);

        var other = await factory.SignupAsync();
        Assert.Empty((await other.Client.GetJsonAsync("/api/customers")).AsArray());
        await other.Client.GetJsonAsync($"/api/customers/{customer.Id()}", HttpStatusCode.NotFound);
        await other.Client.PutJsonAsync($"/api/customers/{customer.Id()}", new { phone = "9111111111", name = "Mine" }, HttpStatusCode.NotFound);
        // Phone numbers are unique per tenant, not globally.
        await other.Client.PostJsonAsync("/api/customers", new { phone = "9111111111" }, HttpStatusCode.Created);
    }
}
