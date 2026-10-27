using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RestaurantOS.IntegrationTests.Infrastructure;

public sealed record TenantSession(HttpClient Client, Guid TenantId, Guid OutletId, string Email, string Password, string RefreshToken);

public static class TestClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static async Task<TenantSession> SignupAsync(this ApiFactory factory, bool sampleMenu = true, string? name = null)
    {
        var client = factory.CreateClient();
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        const string password = "Sup3r-secret!";
        var res = await client.PostAsJsonAsync("/api/auth/signup", new
        {
            restaurantName = name ?? "Test Kitchen",
            outletName = "Main Outlet",
            ownerName = "Owner One",
            email,
            password,
            sampleMenu,
        }, Json);
        var body = await res.ReadJsonAsync(HttpStatusCode.OK);
        client.UseToken(body["tokens"]!["accessToken"]!.GetValue<string>());
        return new TenantSession(client, body["tenantId"]!.GetValue<Guid>(), body["outletId"]!.GetValue<Guid>(), email, password,
            body["tokens"]!["refreshToken"]!.GetValue<string>());
    }

    public static async Task<HttpClient> LoginAsync(this ApiFactory factory, string email, string password)
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password }, Json);
        var body = await res.ReadJsonAsync(HttpStatusCode.OK);
        client.UseToken(body["tokens"]!["accessToken"]!.GetValue<string>());
        return client;
    }

    /// <summary>Creates a staff user with the given role on the session's outlet and returns a logged-in client.</summary>
    public static async Task<HttpClient> CreateStaffAsync(this ApiFactory factory, TenantSession owner, string role, Guid? outletId = null,
        bool tenantWide = false)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.test";
        var res = await owner.Client.PostAsJsonAsync("/api/staff", new
        {
            name = $"{role} User",
            email,
            password = "Staff-pass-123",
            roles = new[] { new { role, outletId = tenantWide ? (Guid?)null : outletId ?? owner.OutletId } },
        }, Json);
        await res.ReadJsonAsync(HttpStatusCode.Created);
        return await factory.LoginAsync(email, "Staff-pass-123");
    }

    public static void UseToken(this HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage res, HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"Expected {(int)expected} but got {(int)res.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    public static async Task<JsonNode> GetJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK) =>
        await (await client.GetAsync(url)).ReadJsonAsync(expected);

    public static async Task<JsonNode> PostJsonAsync(this HttpClient client, string url, object? body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await (await client.PostAsJsonAsync(url, body ?? new { }, Json)).ReadJsonAsync(expected);

    public static async Task<JsonNode> PutJsonAsync(this HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await (await client.PutAsJsonAsync(url, body, Json)).ReadJsonAsync(expected);

    public static async Task<JsonNode> PatchJsonAsync(this HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await (await client.PatchAsJsonAsync(url, body, Json)).ReadJsonAsync(expected);

    public static Guid Id(this JsonNode node) => node["id"]!.GetValue<Guid>();

    public static decimal Dec(this JsonNode node, string prop) => node[prop]!.GetValue<decimal>();

    /// <summary>Finds a sample-menu item by name in the outlet menu.</summary>
    public static async Task<JsonNode> MenuItemAsync(this TenantSession s, string name)
    {
        var menu = await s.Client.GetJsonAsync($"/api/outlets/{s.OutletId}/menu");
        return menu["items"]!.AsArray().First(i => i!["name"]!.GetValue<string>() == name)!;
    }

    public static async Task<Guid> CreateTableAsync(this TenantSession s, string name = "T1")
    {
        var t = await s.Client.PostJsonAsync($"/api/outlets/{s.OutletId}/tables", new { name, area = "Main", capacity = 4, sortOrder = 0 },
            HttpStatusCode.Created);
        return t.Id();
    }
}
