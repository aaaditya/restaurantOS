using System.Net;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.IntegrationTests.Infrastructure;

namespace RestaurantOS.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PosFlowTests(ApiFactory factory)
{
    [Fact]
    public async Task Dine_in_order_flows_from_pos_to_kitchen_to_settlement()
    {
        var s = await factory.SignupAsync();
        var table = await s.CreateTableAsync("T7");
        var paneer = await s.MenuItemAsync("Paneer Tikka");      // Tandoor, 260, GST 5%
        var dal = await s.MenuItemAsync("Dal Makhani");          // Main, variants Half 160 / Full 240
        var coffee = await s.MenuItemAsync("Cold Coffee");       // Bar, 140, GST 18%
        var hot = paneer["modifierGroups"]![0]!["options"]!.AsArray().First(o => o!["name"]!.GetValue<string>() == "Hot")!;
        var full = dal["variants"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "Full")!;
        var url = $"/api/outlets/{s.OutletId}/orders";

        var order = await s.Client.PostJsonAsync(url, new
        {
            type = "DineIn", tableId = table, guests = 2,
            lines = new object[]
            {
                new { menuItemId = paneer.Id(), quantity = 1, modifierOptionIds = new[] { hot.Id() } },
                new { menuItemId = dal.Id(), variantId = full.Id(), quantity = 2 },
            },
        }, HttpStatusCode.Created);
        var orderId = order.Id();
        Assert.Equal(740m, order.Dec("subtotal"));
        Assert.Equal(37m, order.Dec("taxTotal"));
        Assert.Equal(777m, order.Dec("grandTotal"));
        Assert.Equal("T7", order["tableName"]!.GetValue<string>());

        // Same table cannot get a second running order.
        await s.Client.PostJsonAsync(url, new { type = "DineIn", tableId = table }, HttpStatusCode.Conflict);

        order = await s.Client.PostJsonAsync($"{url}/{orderId}/kot", new { version = order["version"]!.GetValue<uint>() });
        Assert.All(order["lines"]!.AsArray(), l => Assert.Equal("Sent", l!["status"]!.GetValue<string>()));

        order = await s.Client.PostJsonAsync($"{url}/{orderId}/lines", new
        {
            lines = new[] { new { menuItemId = coffee.Id(), quantity = 1 } }, version = order["version"]!.GetValue<uint>(), sendToKitchen = true,
        });
        Assert.Equal(880m, order.Dec("subtotal"));
        Assert.Equal(62.2m, order.Dec("taxTotal"));        // 37 + 25.20
        Assert.Equal(942m, order.Dec("grandTotal"));       // 942.20 → 942
        Assert.Equal(-0.2m, order.Dec("roundOff"));

        var kots = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/kots");
        var mine = kots.AsArray().Where(k => k!["orderId"]!.GetValue<Guid>() == orderId).ToList();
        Assert.Equal(["Bar", "Main", "Tandoor"], mine.Select(k => k!["station"]!.GetValue<string>()).Order());

        var kitchen = await factory.CreateStaffAsync(s, "Kitchen");
        var tandoor = mine.First(k => k!["station"]!.GetValue<string>() == "Tandoor")!;
        Assert.Equal("Hot", tandoor["items"]![0]!["modifiersText"]!.GetValue<string>());
        await kitchen.PostJsonAsync($"/api/outlets/{s.OutletId}/kots/{tandoor.Id()}/status", new { status = "Preparing" });
        await kitchen.PostJsonAsync($"/api/outlets/{s.OutletId}/kots/{tandoor.Id()}/status", new { status = "Ready" });
        await kitchen.PostJsonAsync($"/api/outlets/{s.OutletId}/kots/{tandoor.Id()}/status", new { status = "New" }, HttpStatusCode.Conflict);

        await s.Client.PutJsonAsync($"{url}/{orderId}/discount", new { type = "Flat", value = 42, reason = "Birthday" });
        order = await s.Client.PostJsonAsync($"{url}/{orderId}/bill", new { });
        Assert.Equal("Billed", order["status"]!.GetValue<string>());
        var total = order.Dec("grandTotal");
        Assert.Equal(42m, order.Dec("discountTotal"));

        var key = Guid.NewGuid().ToString();
        var settle = await s.Client.PostJsonAsync($"{url}/{orderId}/settle", new
        {
            idempotencyKey = key,
            payments = new object[]
            {
                new { method = "Upi", amount = 400, reference = "UPI-REF-1" },
                new { method = "Cash", amount = total - 400, tendered = 1000 },
            },
        });
        var invoice = settle["invoiceNumber"]!.GetValue<string>();
        Assert.Matches(@"^MAIN/\d{4}-\d{2}/000001$", invoice);
        Assert.Equal(1000m - (total - 400m), settle.Dec("changeDue"));
        Assert.False(settle["replayed"]!.GetValue<bool>());

        // Retrying with the same key returns the original result without new payments.
        var replay = await s.Client.PostJsonAsync($"{url}/{orderId}/settle", new
        {
            idempotencyKey = key, payments = new[] { new { method = "Cash", amount = total } },
        });
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal(invoice, replay["invoiceNumber"]!.GetValue<string>());

        // A different key cannot settle the order twice.
        await s.Client.PostJsonAsync($"{url}/{orderId}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(), payments = new[] { new { method = "Cash", amount = total } },
        }, HttpStatusCode.Conflict);

        var inv = await s.Client.GetJsonAsync($"{url}/{orderId}/invoice");
        Assert.Equal(2, inv["payments"]!.AsArray().Count);
        Assert.Equal("Settled", inv["order"]!["status"]!.GetValue<string>());

        var tables = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/tables");
        Assert.Equal("free", tables[0]!["status"]!.GetValue<string>());

        await factory.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.Payments.IgnoreQueryFilters().CountAsync(p => p.OrderId == orderId));
            Assert.Equal(1, await db.Settlements.IgnoreQueryFilters().CountAsync(p => p.OrderId == orderId));
            Assert.True(await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.EntityId == orderId.ToString() && a.Action == "order.discount_applied"));
        });
    }

    [Fact]
    public async Task Concurrent_settlement_retries_produce_exactly_one_settlement()
    {
        var s = await factory.SignupAsync();
        var item = await s.MenuItemAsync("Butter Naan");
        var url = $"/api/outlets/{s.OutletId}/orders";
        var order = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 3 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);
        var total = order.Dec("grandTotal");
        var key = Guid.NewGuid().ToString();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            s.Client.PostAsync($"{url}/{order.Id()}/settle", System.Net.Http.Json.JsonContent.Create(new
            {
                idempotencyKey = key, payments = new[] { new { method = "Cash", amount = total } },
            }))));

        Assert.All(attempts, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var invoices = await Task.WhenAll(attempts.Select(async r => (await r.ReadJsonAsync(HttpStatusCode.OK))["invoiceNumber"]!.GetValue<string>()));
        Assert.Single(invoices.Distinct());
        await factory.WithDbAsync(async db =>
            Assert.Equal(1, await db.Payments.IgnoreQueryFilters().CountAsync(p => p.OrderId == order.Id())));
    }

    [Fact]
    public async Task Order_creation_is_idempotent_by_client_order_id()
    {
        var s = await factory.SignupAsync();
        var item = await s.MenuItemAsync("Masala Chaas");
        var clientOrderId = Guid.NewGuid();
        var body = new { clientOrderId, type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 1 } } };
        var url = $"/api/outlets/{s.OutletId}/orders";

        var first = await s.Client.PostJsonAsync(url, body, HttpStatusCode.Created);
        var second = await s.Client.PostJsonAsync(url, body, HttpStatusCode.OK);
        Assert.Equal(first.Id(), second.Id());
        await factory.WithDbAsync(async db =>
            Assert.Equal(1, await db.Orders.IgnoreQueryFilters().CountAsync(o => o.ClientOrderId == clientOrderId)));
    }

    [Fact]
    public async Task Stale_version_is_rejected()
    {
        var s = await factory.SignupAsync();
        var item = await s.MenuItemAsync("Masala Chaas");
        var url = $"/api/outlets/{s.OutletId}/orders";
        var order = await s.Client.PostJsonAsync(url, new { type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 1 } } },
            HttpStatusCode.Created);
        var v = order["version"]!.GetValue<uint>();
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/lines", new { lines = new[] { new { menuItemId = item.Id(), quantity = 1 } }, version = v });
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/lines", new { lines = new[] { new { menuItemId = item.Id(), quantity = 1 } }, version = v },
            HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Voiding_a_sent_item_creates_cancellation_kot_and_audit_entry()
    {
        var s = await factory.SignupAsync();
        var item = await s.MenuItemAsync("Veg Spring Roll");
        var url = $"/api/outlets/{s.OutletId}/orders";
        var order = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 2 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);
        var lineId = order["lines"]![0]!.Id();

        await s.Client.PostJsonAsync($"{url}/{order.Id()}/lines/{lineId}/void", new { reason = "" }, HttpStatusCode.BadRequest);
        order = await s.Client.PostJsonAsync($"{url}/{order.Id()}/lines/{lineId}/void", new { reason = "Guest cancelled" });
        Assert.Equal(0m, order.Dec("grandTotal"));

        var kots = await s.Client.GetJsonAsync($"{url}/{order.Id()}/kots");
        Assert.Contains(kots.AsArray(), k => k!["kind"]!.GetValue<string>() == "Cancellation");
        await factory.WithDbAsync(async db => Assert.True(await db.AuditLogs.IgnoreQueryFilters()
            .AnyAsync(a => a.Action == "order.line_voided" && a.EntityId == order.Id().ToString())));
    }

    [Fact]
    public async Task Settlement_rejects_mismatched_payment_and_unsent_items()
    {
        var s = await factory.SignupAsync();
        var item = await s.MenuItemAsync("Fresh Lime Soda");
        var url = $"/api/outlets/{s.OutletId}/orders";
        var order = await s.Client.PostJsonAsync(url, new { type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 1 } } },
            HttpStatusCode.Created);
        var total = order.Dec("grandTotal");

        await s.Client.PostJsonAsync($"{url}/{order.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(), payments = new[] { new { method = "Cash", amount = total } },
        }, HttpStatusCode.Conflict);

        await s.Client.PostJsonAsync($"{url}/{order.Id()}/kot", new { });
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(), payments = new[] { new { method = "Cash", amount = total - 1 } },
        }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invalid_selections_are_rejected()
    {
        var s = await factory.SignupAsync();
        var dal = await s.MenuItemAsync("Dal Makhani");
        var pizza = await s.MenuItemAsync("Margherita Pizza");
        var spiceOption = (await s.MenuItemAsync("Paneer Tikka"))["modifierGroups"]![0]!["options"]![0]!.Id();
        var url = $"/api/outlets/{s.OutletId}/orders";

        await s.Client.PostJsonAsync(url, new { type = "Takeaway", lines = new[] { new { menuItemId = dal.Id(), quantity = 1 } } },
            HttpStatusCode.BadRequest); // variant required
        await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = pizza.Id(), quantity = 1, modifierOptionIds = new[] { spiceOption } } },
        }, HttpStatusCode.BadRequest); // modifier from another item's group
        await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = pizza.Id(), quantity = -1 } },
        }, HttpStatusCode.BadRequest);

        await s.Client.PutJsonAsync($"/api/outlets/{s.OutletId}/menu/items/{pizza.Id()}/override", new { isAvailable = false });
        await s.Client.PostJsonAsync(url, new { type = "Takeaway", lines = new[] { new { menuItemId = pizza.Id(), quantity = 1 } } },
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cash_session_tracks_expected_cash_and_variance()
    {
        var s = await factory.SignupAsync();
        var item = await s.MenuItemAsync("Tandoori Roti");
        var sessionUrl = $"/api/outlets/{s.OutletId}/cash-sessions";
        var session = await s.Client.PostJsonAsync(sessionUrl, new { openingFloat = 500 }, HttpStatusCode.Created);
        await s.Client.PostJsonAsync(sessionUrl, new { openingFloat = 100 }, HttpStatusCode.Conflict);

        var url = $"/api/outlets/{s.OutletId}/orders";
        var order = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 4 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);
        var total = order.Dec("grandTotal"); // 120 + 6 = 126
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/settle", new
        {
            idempotencyKey = Guid.NewGuid().ToString(), payments = new[] { new { method = "Cash", amount = total } },
        });

        var current = await s.Client.GetJsonAsync($"{sessionUrl}/current");
        Assert.Equal(500m + total, current.Dec("expectedCash"));
        var closed = await s.Client.PostJsonAsync($"{sessionUrl}/{session.Id()}/close", new { countedCash = 600 });
        Assert.Equal(600m - (500m + total), closed.Dec("variance"));
    }

    [Fact]
    public async Task Cancelling_an_order_cancels_its_open_kots()
    {
        var s = await factory.SignupAsync();
        var item = await s.MenuItemAsync("Chicken Tikka");
        var url = $"/api/outlets/{s.OutletId}/orders";
        var order = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 1 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);

        var cashier = await factory.CreateStaffAsync(s, "Cashier");
        await cashier.PostJsonAsync($"{url}/{order.Id()}/cancel", new { reason = "x" }, HttpStatusCode.Forbidden);
        order = await s.Client.PostJsonAsync($"{url}/{order.Id()}/cancel", new { reason = "Customer left" });
        Assert.Equal("Cancelled", order["status"]!.GetValue<string>());
        var kots = await s.Client.GetJsonAsync($"{url}/{order.Id()}/kots");
        Assert.Contains(kots.AsArray(), k => k!["status"]!.GetValue<string>() == "Cancelled");
    }
}
