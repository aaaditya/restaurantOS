using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RestaurantOS.Api.Modules.Payments;
using RestaurantOS.IntegrationTests.Infrastructure;

namespace RestaurantOS.IntegrationTests;

[Collection(ApiCollection.Name)]
public class IntegrationsAndBillingTests(ApiFactory factory)
{
    private static Task<JsonNode> SubscribeAsync(TenantSession s, string plan, HttpStatusCode expected = HttpStatusCode.OK) =>
        s.Client.PostJsonAsync("/api/billing/subscription", new { planCode = plan }, expected);

    private HttpClient ApiClient(string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static object ExternalOrder(string externalId, Guid menuItemId, int quantity = 1) => new
    {
        provider = "swift-eats", externalId, type = "Delivery", prepaid = true, expectedTotal = 189m * quantity,
        customer = new { name = "Dev Patel", phone = "9123456780", address = "4 Lake Road" },
        lines = new[] { new { menuItemId, quantity } },
    };

    [Fact]
    public async Task Owner_changes_plan_cancels_and_resubscribes_through_the_mock_provider()
    {
        var s = await factory.SignupAsync();
        var billing = await s.Client.GetJsonAsync("/api/billing");
        Assert.Equal("starter", billing["current"]!["planCode"]!.GetValue<string>());
        Assert.Equal("Trialing", billing["current"]!["status"]!.GetValue<string>());
        Assert.Equal("mock", billing["provider"]!.GetValue<string>());
        Assert.Equal(3, billing["plans"]!.AsArray().Count);

        await s.Client.PostJsonAsync("/api/outlets", new { name = "Second" }, HttpStatusCode.BadRequest);
        var growth = await SubscribeAsync(s, "growth");
        Assert.Equal("Active", growth["current"]!["status"]!.GetValue<string>());
        Assert.NotNull(growth["current"]!["currentPeriodEndsAt"]);
        await s.Client.PostJsonAsync("/api/outlets", new { name = "Second" }, HttpStatusCode.Created);
        Assert.Equal(2, (await s.Client.GetJsonAsync("/api/billing"))["usage"]!["outlets"]!.GetValue<int>());
        await SubscribeAsync(s, "starter", HttpStatusCode.BadRequest);   // 2 outlets exceed the Starter limit
        await SubscribeAsync(s, "platinum", HttpStatusCode.BadRequest);

        var manager = await factory.CreateStaffAsync(s, "Manager");
        await manager.GetJsonAsync("/api/billing", HttpStatusCode.Forbidden);
        await manager.PostJsonAsync("/api/billing/cancel", null, HttpStatusCode.Forbidden);

        var cancelled = await s.Client.PostJsonAsync("/api/billing/cancel", null);
        Assert.False(cancelled["current"]!["usable"]!.GetValue<bool>());
        await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/tables", new { name = "T9", capacity = 2, sortOrder = 0 }, HttpStatusCode.PaymentRequired);
        await s.Client.PostJsonAsync("/api/billing/cancel", null, HttpStatusCode.Conflict);

        await SubscribeAsync(s, "growth");
        await s.CreateTableAsync("T9");
        var audit = await s.Client.GetJsonAsync("/api/audit?action=billing.");
        Assert.Equal(3, audit["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task External_orders_need_a_scoped_api_key_and_are_ingested_once()
    {
        var s = await factory.SignupAsync();
        var roll = (await s.MenuItemAsync("Veg Spring Roll")).Id();
        await s.Client.PostJsonAsync("/api/integrations/api-keys", new { name = "Aggregator", scopes = new[] { "orders:write" } },
            HttpStatusCode.BadRequest);   // Starter has no integrations
        await SubscribeAsync(s, "growth");
        await s.Client.PostJsonAsync("/api/integrations/api-keys", new { name = "Bad", scopes = new[] { "everything" } }, HttpStatusCode.BadRequest);

        var created = await s.Client.PostJsonAsync("/api/integrations/api-keys", new { name = "SwiftEats", scopes = new[] { "orders:write" } },
            HttpStatusCode.Created);
        var key = created["key"]!.GetValue<string>();
        Assert.StartsWith(created["apiKey"]!["prefix"]!.GetValue<string>() + ".", key);
        var listed = (await s.Client.GetJsonAsync("/api/integrations/api-keys")).AsArray().Single()!;
        Assert.Null(listed["key"]);
        Assert.Null(listed["secretHash"]);

        var api = ApiClient(key);
        var url = $"/api/v1/outlets/{s.OutletId}/orders";
        var first = await api.PostJsonAsync(url, ExternalOrder("SE-1001", roll, 2), HttpStatusCode.Created);
        Assert.Equal("pending", first["state"]!.GetValue<string>());
        Assert.Equal(378m, first.Dec("grandTotal"));
        Assert.True(first["totalMatches"]!.GetValue<bool>());
        var replay = await api.PostJsonAsync(url, ExternalOrder("SE-1001", roll, 2));
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal(first["orderId"]!.GetValue<Guid>(), replay["orderId"]!.GetValue<Guid>());

        var burst = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.PostAsJsonAsync(url, ExternalOrder("SE-1002", roll), TestClient.Json)));
        Assert.All(burst, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        var pending = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders?view=pending");
        Assert.Equal(2, pending.AsArray().Count);
        Assert.All(pending.AsArray(), o => Assert.Equal("Aggregator", o!["source"]!.GetValue<string>()));

        await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/orders/{first["orderId"]}/accept", new { });
        var status = await api.GetJsonAsync($"{url}/swift-eats/SE-1001");
        Assert.Equal("accepted", status["state"]!.GetValue<string>());

        await api.GetJsonAsync($"/api/v1/outlets/{s.OutletId}/menu", HttpStatusCode.Forbidden);   // key lacks menu:read
        await ApiClient(key + "x").PostJsonAsync(url, ExternalOrder("SE-1003", roll), HttpStatusCode.Unauthorized);
        await factory.CreateClient().PostJsonAsync(url, ExternalOrder("SE-1003", roll), HttpStatusCode.Unauthorized);

        var other = await factory.SignupAsync();
        await api.PostJsonAsync($"/api/v1/outlets/{other.OutletId}/orders", ExternalOrder("SE-1004", roll), HttpStatusCode.NotFound);
        var foreignItem = (await other.MenuItemAsync("Veg Spring Roll")).Id();
        await api.PostJsonAsync(url, ExternalOrder("SE-1005", foreignItem), HttpStatusCode.NotFound);
        Assert.Empty((await other.Client.GetJsonAsync($"/api/outlets/{other.OutletId}/orders?view=pending")).AsArray());

        var second = await s.Client.PostJsonAsync("/api/outlets", new { name = "Second" }, HttpStatusCode.Created);
        var scoped = await s.Client.PostJsonAsync("/api/integrations/api-keys", new
        {
            name = "Second only", outletId = second.Id(), scopes = new[] { "menu:read", "orders:write" },
        }, HttpStatusCode.Created);
        var scopedApi = ApiClient(scoped["key"]!.GetValue<string>());
        await scopedApi.GetJsonAsync($"/api/v1/outlets/{second.Id()}/menu");
        await scopedApi.PostJsonAsync(url, ExternalOrder("SE-1006", roll), HttpStatusCode.NotFound);

        await s.Client.PostJsonAsync($"/api/integrations/api-keys/{created["apiKey"]!.Id()}/revoke", null);
        await api.PostJsonAsync(url, ExternalOrder("SE-1007", roll), HttpStatusCode.Unauthorized);

        var manager = await factory.CreateStaffAsync(s, "Manager");
        await manager.GetJsonAsync("/api/integrations/api-keys", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Mock_aggregator_simulator_creates_a_pending_delivery_order()
    {
        var s = await factory.SignupAsync();
        var result = await s.Client.PostJsonAsync($"/api/dev/mock-aggregator/outlets/{s.OutletId}/orders", null);
        Assert.Equal("pending", result["state"]!.GetValue<string>());
        var order = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/orders/{result["orderId"]}");
        Assert.Equal("Delivery", order["type"]!.GetValue<string>());
        Assert.StartsWith("mock-aggregator:SIM-", order["externalRef"]!.GetValue<string>());
        var kitchen = await factory.CreateStaffAsync(s, "Kitchen");
        await kitchen.PostJsonAsync($"/api/dev/mock-aggregator/outlets/{s.OutletId}/orders", null, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Online_payment_settles_only_after_a_signed_webhook_confirms_it()
    {
        var s = await factory.SignupAsync();
        var url = $"/api/outlets/{s.OutletId}/orders";
        var roll = (await s.MenuItemAsync("Veg Spring Roll")).Id();
        var order = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", sendToKitchen = true, lines = new[] { new { menuItemId = roll, quantity = 1 } },
        }, HttpStatusCode.Created);
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/payment-intents", new { amount = 500m }, HttpStatusCode.BadRequest);
        var intent = await s.Client.PostJsonAsync($"{url}/{order.Id()}/payment-intents", new { amount = 189m }, HttpStatusCode.Created);
        Assert.Equal("Pending", intent["status"]!.GetValue<string>());
        var providerRef = intent["providerRef"]!.GetValue<string>();

        var settle = new
        {
            idempotencyKey = Guid.NewGuid().ToString(),
            payments = new[] { new { method = "Online", amount = 189m, reference = intent.Id().ToString() } },
        };
        await s.Client.PostJsonAsync($"{url}/{order.Id()}/settle", settle, HttpStatusCode.BadRequest);   // not paid yet

        var webhook = factory.CreateClient();
        async Task<HttpResponseMessage> Deliver(string eventId, string status, string? signature = null)
        {
            var body = JsonSerializer.Serialize(new { eventId, providerRef, status });
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/payments/mock/webhook")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add(MockPaymentGateway.SignatureHeader, signature ?? MockPaymentGateway.Sign(ApiFactory.MockPaymentSecret, body));
            return await webhook.SendAsync(req);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Deliver("evt_1", "succeeded", "sha256=00")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Deliver("evt_1", "succeeded")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Deliver("evt_1", "succeeded")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Deliver("evt_2", "failed")).StatusCode);   // late event cannot undo success
        var intents = await s.Client.GetJsonAsync($"{url}/{order.Id()}/payment-intents");
        Assert.Equal("Succeeded", intents[0]!["status"]!.GetValue<string>());

        var settled = await s.Client.PostJsonAsync($"{url}/{order.Id()}/settle", settle);
        Assert.False(settled["replayed"]!.GetValue<bool>());

        // The test-mode checkout drives the same signed path.
        var other = await s.Client.PostJsonAsync(url, new
        {
            type = "Takeaway", sendToKitchen = true, lines = new[] { new { menuItemId = roll, quantity = 1 } },
        }, HttpStatusCode.Created);
        var link = await s.Client.PostJsonAsync($"{url}/{other.Id()}/payment-intents", new { amount = 189m }, HttpStatusCode.Created);
        var checkout = factory.CreateClient();
        var page = await checkout.GetJsonAsync($"/api/dev/mock-payments/{link["providerRef"]}");
        Assert.Equal(189m, page.Dec("amount"));
        await checkout.PostJsonAsync($"/api/dev/mock-payments/{link["providerRef"]}/failed", null);
        Assert.Equal("Failed", (await s.Client.GetJsonAsync($"{url}/{other.Id()}/payment-intents"))[0]!["status"]!.GetValue<string>());
    }
}
