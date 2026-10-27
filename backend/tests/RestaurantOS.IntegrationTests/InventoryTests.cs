using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.IntegrationTests.Infrastructure;

namespace RestaurantOS.IntegrationTests;

[Collection(ApiCollection.Name)]
public class InventoryTests(ApiFactory factory)
{
    private static async Task<Guid> CreateItemAsync(TenantSession s, string name, string unit = "Kilogram", decimal cost = 0, decimal reorder = 0) =>
        (await s.Client.PostJsonAsync("/api/inventory/items", new { name, unit, reorderLevel = reorder, costPerUnit = cost },
            HttpStatusCode.Created)).Id();

    private static async Task<decimal> BalanceAsync(TenantSession s, Guid itemId)
    {
        var stock = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/inventory/stock");
        return stock["items"]!.AsArray().First(i => i!["inventoryItemId"]!.GetValue<Guid>() == itemId)!.Dec("quantity");
    }

    private static async Task<JsonNode> SettleNewOrderAsync(TenantSession s, object[] lines, string? key = null)
    {
        var url = $"/api/outlets/{s.OutletId}/orders";
        var order = await s.Client.PostJsonAsync(url, new { type = "Takeaway", lines, sendToKitchen = true }, HttpStatusCode.Created);
        var body = new { idempotencyKey = key ?? Guid.NewGuid().ToString(), payments = new[] { new { method = "Card", amount = order.Dec("grandTotal") } } };
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/settle", body);
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/settle", body);
        return order;
    }

    [Fact]
    public async Task Stock_moves_through_opening_purchase_consumption_and_wastage_without_duplicates()
    {
        var s = await factory.SignupAsync();
        var stockUrl = $"/api/outlets/{s.OutletId}/inventory";
        var paneer = await CreateItemAsync(s, "Paneer", cost: 400, reorder: 2);
        await s.Client.PostJsonAsync("/api/inventory/items", new { name = "paneer", unit = "Kilogram", reorderLevel = 0, costPerUnit = 0 },
            HttpStatusCode.Conflict);

        var tikka = await s.MenuItemAsync("Paneer Tikka");
        await s.Client.PutJsonAsync("/api/inventory/recipes", new
        {
            menuItemId = tikka.Id(), lines = new[] { new { inventoryItemId = paneer, quantity = 0.2m } },
        });

        var opening = new
        {
            clientRef = Guid.NewGuid().ToString(), kind = "Opening",
            lines = new[] { new { inventoryItemId = paneer, quantity = 5m, unitCost = 400m } },
        };
        var first = await s.Client.PostJsonAsync($"{stockUrl}/adjustments", opening);
        var replay = await s.Client.PostJsonAsync($"{stockUrl}/adjustments", opening);
        Assert.Equal(first["entries"]![0]!.Id(), replay["entries"]![0]!.Id());
        Assert.Equal(5m, await BalanceAsync(s, paneer));

        var supplier = (await s.Client.PostJsonAsync("/api/suppliers", new { name = "Fresh Dairy Co", phone = "9800000000" },
            HttpStatusCode.Created)).Id();
        var po = await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/purchase-orders", new
        {
            supplierId = supplier, lines = new[] { new { inventoryItemId = paneer, quantity = 3m, unitCost = 380m } },
        }, HttpStatusCode.Created);
        Assert.Equal(1140m, po.Dec("total"));
        Assert.Equal(1, po["number"]!.GetValue<int>());
        var poUrl = $"/api/outlets/{s.OutletId}/purchase-orders/{po.Id()}";
        po = await s.Client.PostJsonAsync($"{poUrl}/order", null);
        Assert.Equal("Ordered", po["status"]!.GetValue<string>());
        await s.Client.PutJsonAsync(poUrl, new { supplierId = supplier, lines = Array.Empty<object>() }, HttpStatusCode.Conflict);

        var lineId = po["lines"]![0]!.Id();
        po = await s.Client.PostJsonAsync($"{poUrl}/receive", new { lines = new[] { new { lineId, receivedQuantity = 2.5m, unitCost = 390m } } });
        Assert.Equal("Received", po["status"]!.GetValue<string>());
        Assert.Equal(975m, po.Dec("total"));
        await s.Client.PostJsonAsync($"{poUrl}/receive", null, HttpStatusCode.Conflict);
        await s.Client.PostJsonAsync($"{poUrl}/cancel", null, HttpStatusCode.Conflict);
        Assert.Equal(7.5m, await BalanceAsync(s, paneer));
        var items = await s.Client.GetJsonAsync("/api/inventory/items");
        Assert.Equal(390m, items.AsArray().First(i => i!.Id() == paneer)!.Dec("costPerUnit"));

        // Settling twice with the same key (replay) must consume stock once: 2 x 0.2 kg.
        await SettleNewOrderAsync(s, [new { menuItemId = tikka.Id(), quantity = 2 }]);
        Assert.Equal(7.1m, await BalanceAsync(s, paneer));

        await s.Client.PostJsonAsync($"{stockUrl}/adjustments", new
        {
            clientRef = Guid.NewGuid().ToString(), kind = "Wastage", lines = new[] { new { inventoryItemId = paneer, quantity = 0.1m } },
        }, HttpStatusCode.BadRequest);
        await s.Client.PostJsonAsync($"{stockUrl}/adjustments", new
        {
            clientRef = Guid.NewGuid().ToString(), kind = "Wastage", reason = "Spoiled",
            lines = new[] { new { inventoryItemId = paneer, quantity = 0.1m } },
        });
        Assert.Equal(7m, await BalanceAsync(s, paneer));

        var ledger = await s.Client.GetJsonAsync($"{stockUrl}/ledger?itemId={paneer}");
        Assert.Equal(["Wastage", "Consumption", "Purchase", "Opening"], ledger.AsArray().Select(e => e!["type"]!.GetValue<string>()));
        Assert.Equal([7m, 7.1m, 7.5m, 5m], ledger.AsArray().Select(e => e!.Dec("balanceAfter")));

        await s.Client.PutJsonAsync($"/api/inventory/items/{paneer}", new { name = "Paneer", unit = "Kilogram", reorderLevel = 8m, costPerUnit = 390m });
        var low = await s.Client.GetJsonAsync($"{stockUrl}/stock?lowOnly=true");
        Assert.Equal(1, low["lowCount"]!.GetValue<int>());
        Assert.Equal(2730m, low["totalValue"]!.GetValue<decimal>());   // 7 kg x 390
        await s.Client.PutJsonAsync($"/api/inventory/items/{paneer}", new { name = "Paneer", unit = "Gram", reorderLevel = 8m, costPerUnit = 390m },
            HttpStatusCode.BadRequest);

        await factory.WithDbAsync(async db => Assert.Equal(4, await db.StockLedger.IgnoreQueryFilters().CountAsync(e => e.InventoryItemId == paneer)));
    }

    [Fact]
    public async Task Variant_recipe_takes_precedence_over_the_item_recipe()
    {
        var s = await factory.SignupAsync();
        var dalStock = await CreateItemAsync(s, "Dal base", "Gram", cost: 0.2m);
        var dal = await s.MenuItemAsync("Dal Makhani");
        var full = dal["variants"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "Full")!.Id();
        var half = dal["variants"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "Half")!.Id();

        await s.Client.PutJsonAsync("/api/inventory/recipes", new { menuItemId = dal.Id(), lines = new[] { new { inventoryItemId = dalStock, quantity = 150m } } });
        await s.Client.PutJsonAsync("/api/inventory/recipes", new
        {
            menuItemId = dal.Id(), variantId = full, lines = new[] { new { inventoryItemId = dalStock, quantity = 300m } },
        });

        await SettleNewOrderAsync(s, [new { menuItemId = dal.Id(), variantId = full, quantity = 1 }, new { menuItemId = dal.Id(), variantId = half, quantity = 2 }]);
        Assert.Equal(-600m, await BalanceAsync(s, dalStock));   // 300 + 2 x 150, negative because no opening stock

        var stock = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/inventory/stock");
        Assert.True(stock["items"]!.AsArray().First(i => i!["inventoryItemId"]!.GetValue<Guid>() == dalStock)!["isLow"]!.GetValue<bool>());

        var recipes = await s.Client.GetJsonAsync("/api/inventory/recipes");
        Assert.Equal(2, recipes.AsArray().Count);
        await (await s.Client.PutAsync("/api/inventory/recipes",
            System.Net.Http.Json.JsonContent.Create(new { menuItemId = dal.Id(), variantId = full, lines = Array.Empty<object>() }))).ReadJsonAsync(HttpStatusCode.NoContent);
        Assert.Single((await s.Client.GetJsonAsync("/api/inventory/recipes")).AsArray());
    }

    [Fact]
    public async Task Concurrent_adjustments_with_the_same_client_ref_post_once()
    {
        var s = await factory.SignupAsync();
        var rice = await CreateItemAsync(s, "Rice");
        var body = new { clientRef = Guid.NewGuid().ToString(), kind = "Opening", lines = new[] { new { inventoryItemId = rice, quantity = 10m } } };
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/inventory/adjustments", body)));
        Assert.Single(results.Select(r => r["entries"]![0]!.Id()).Distinct());
        Assert.Equal(10m, await BalanceAsync(s, rice));
    }

    [Fact]
    public async Task Inventory_access_follows_roles_and_tenants()
    {
        var s = await factory.SignupAsync();
        var oil = await CreateItemAsync(s, "Oil", "Litre");
        var adjust = new { clientRef = Guid.NewGuid().ToString(), kind = "Opening", lines = new[] { new { inventoryItemId = oil, quantity = 5m } } };

        var kitchen = await factory.CreateStaffAsync(s, "Kitchen");
        await kitchen.GetJsonAsync($"/api/outlets/{s.OutletId}/inventory/stock");
        await kitchen.PostJsonAsync($"/api/outlets/{s.OutletId}/inventory/adjustments", adjust, HttpStatusCode.Forbidden);

        var cashier = await factory.CreateStaffAsync(s, "Cashier");
        await cashier.GetJsonAsync($"/api/outlets/{s.OutletId}/inventory/stock", HttpStatusCode.Forbidden);
        await cashier.GetJsonAsync("/api/suppliers", HttpStatusCode.Forbidden);

        var outletManager = await factory.CreateStaffAsync(s, "Manager");
        await outletManager.PostJsonAsync($"/api/outlets/{s.OutletId}/inventory/adjustments", adjust);
        await outletManager.PostJsonAsync("/api/inventory/items", new { name = "Salt", unit = "Kilogram", reorderLevel = 0, costPerUnit = 0 },
            HttpStatusCode.Forbidden);

        var other = await factory.SignupAsync();
        Assert.Empty((await other.Client.GetJsonAsync("/api/inventory/items")).AsArray());
        await other.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/inventory/stock", HttpStatusCode.NotFound);
        await other.Client.PostJsonAsync($"/api/outlets/{other.OutletId}/inventory/adjustments",
            new { clientRef = Guid.NewGuid().ToString(), kind = "Opening", lines = new[] { new { inventoryItemId = oil, quantity = 1m } } },
            HttpStatusCode.NotFound);
        await other.Client.PutJsonAsync($"/api/inventory/items/{oil}", new { name = "Mine", unit = "Litre", reorderLevel = 0, costPerUnit = 0 },
            HttpStatusCode.NotFound);
    }
}
