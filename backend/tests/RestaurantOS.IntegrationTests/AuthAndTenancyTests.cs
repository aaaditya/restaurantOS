using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.IntegrationTests.Infrastructure;

namespace RestaurantOS.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthAndTenancyTests(ApiFactory factory)
{
    [Fact]
    public async Task Health_endpoint_reports_healthy_database()
    {
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signup_creates_owner_with_outlet_trial_and_default_taxes()
    {
        var s = await factory.SignupAsync(sampleMenu: false);
        var me = await s.Client.GetJsonAsync("/api/me");

        Assert.Equal("Owner One", me["user"]!["name"]!.GetValue<string>());
        Assert.Equal("Trialing", me["tenant"]!["subscriptionStatus"]!.GetValue<string>());
        Assert.Contains("Owner", me["tenantRoles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Single(me["outlets"]!.AsArray());
        var taxes = await s.Client.GetJsonAsync("/api/menu/tax-groups");
        Assert.Equal(2, taxes.AsArray().Count);
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var s = await factory.SignupAsync();
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/signup", new
        {
            restaurantName = "Other", ownerName = "X", email = s.Email.ToUpperInvariant(), password = "Another-pass1",
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Login_rejects_wrong_password_and_accepts_correct_one()
    {
        var s = await factory.SignupAsync();
        var client = factory.CreateClient();
        var bad = await client.PostAsJsonAsync("/api/auth/login", new { email = s.Email, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var ok = await client.PostAsJsonAsync("/api/auth/login", new { email = s.Email, password = s.Password });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_revokes_family()
    {
        var s = await factory.SignupAsync();
        var client = factory.CreateClient();

        var first = await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = s.RefreshToken });
        var rotated = first["tokens"]!["refreshToken"]!.GetValue<string>();
        Assert.NotEqual(s.RefreshToken, rotated);

        // Replaying the old token is treated as theft: it fails and revokes the rotated token too.
        await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = s.RefreshToken }, HttpStatusCode.Unauthorized);
        await client.PostJsonAsync("/api/auth/refresh", new { refreshToken = rotated }, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        var res = await factory.CreateClient().GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_modify_each_others_data()
    {
        var a = await factory.SignupAsync(name: "Tenant A");
        var b = await factory.SignupAsync(name: "Tenant B");
        var aItem = await a.MenuItemAsync("Paneer Tikka");
        var aTable = await a.CreateTableAsync();
        var aOrder = await a.Client.PostJsonAsync($"/api/outlets/{a.OutletId}/orders", new
        {
            type = "DineIn", tableId = aTable, lines = new[] { new { menuItemId = aItem.Id(), quantity = 1 } },
        }, HttpStatusCode.Created);

        // Outlet-scoped routes return 404 so ids cannot be probed.
        await b.Client.GetJsonAsync($"/api/outlets/{a.OutletId}", HttpStatusCode.NotFound);
        await b.Client.GetJsonAsync($"/api/outlets/{a.OutletId}/orders", HttpStatusCode.NotFound);
        await b.Client.GetJsonAsync($"/api/outlets/{a.OutletId}/orders/{aOrder.Id()}", HttpStatusCode.NotFound);
        await b.Client.GetJsonAsync($"/api/outlets/{b.OutletId}/orders/{aOrder.Id()}", HttpStatusCode.NotFound);
        await b.Client.PostJsonAsync($"/api/outlets/{a.OutletId}/orders/{aOrder.Id()}/cancel", new { reason = "x" }, HttpStatusCode.NotFound);

        // Tenant-level resources are filtered.
        var bItems = await b.Client.GetJsonAsync("/api/menu/items");
        Assert.DoesNotContain(bItems.AsArray(), i => i!.Id() == aItem.Id());
        var bCategories = await b.Client.GetJsonAsync("/api/menu/categories");
        await b.Client.PutJsonAsync($"/api/menu/items/{aItem.Id()}", new
        {
            categoryId = bCategories.AsArray()[0]!.Id(), name = "Hacked", price = 1, foodType = "Veg", sortOrder = 0, isActive = true,
        }, HttpStatusCode.NotFound);
        var bStaff = await b.Client.GetJsonAsync("/api/staff");
        Assert.Single(bStaff.AsArray());

        // Cannot attach another tenant's table to an order in your own outlet.
        var bItem = await b.MenuItemAsync("Paneer Tikka");
        await b.Client.PostJsonAsync($"/api/outlets/{b.OutletId}/orders", new
        {
            type = "DineIn", tableId = aTable, lines = new[] { new { menuItemId = bItem.Id(), quantity = 1 } },
        }, HttpStatusCode.NotFound);
        // Cannot order another tenant's menu item.
        await b.Client.PostJsonAsync($"/api/outlets/{b.OutletId}/orders", new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = aItem.Id(), quantity = 1 } },
        }, HttpStatusCode.NotFound);

        var aCheck = await a.Client.GetJsonAsync($"/api/outlets/{a.OutletId}/orders/{aOrder.Id()}");
        Assert.Equal("Open", aCheck["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Outlet_assignment_limits_staff_to_their_outlet()
    {
        var owner = await factory.SignupAsync();
        await factory.WithDbAsync(db => db.Tenants.Where(t => t.Id == owner.TenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.PlanCode, "growth")));
        var second = await owner.Client.PostJsonAsync("/api/outlets", new { name = "Second Outlet" }, HttpStatusCode.Created);

        var cashier = await factory.CreateStaffAsync(owner, "Cashier");
        await cashier.GetJsonAsync($"/api/outlets/{owner.OutletId}/orders");
        await cashier.GetJsonAsync($"/api/outlets/{second.Id()}/orders", HttpStatusCode.NotFound);
        var me = await cashier.GetJsonAsync("/api/me");
        Assert.Single(me["outlets"]!.AsArray());
    }

    [Fact]
    public async Task Role_permissions_are_enforced()
    {
        var owner = await factory.SignupAsync();
        var waiter = await factory.CreateStaffAsync(owner, "Waiter");
        var kitchen = await factory.CreateStaffAsync(owner, "Kitchen");
        var item = await owner.MenuItemAsync("Veg Spring Roll");

        var order = await waiter.PostJsonAsync($"/api/outlets/{owner.OutletId}/orders", new
        {
            type = "Takeaway", lines = new[] { new { menuItemId = item.Id(), quantity = 1 } }, sendToKitchen = true,
        }, HttpStatusCode.Created);

        await waiter.PutJsonAsync($"/api/outlets/{owner.OutletId}/orders/{order.Id()}/discount",
            new { type = "Percent", value = 10, reason = "x" }, HttpStatusCode.Forbidden);
        await waiter.PostJsonAsync($"/api/outlets/{owner.OutletId}/orders/{order.Id()}/settle",
            new { idempotencyKey = Guid.NewGuid().ToString(), payments = new[] { new { method = "Cash", amount = 189 } } }, HttpStatusCode.Forbidden);
        await kitchen.PostJsonAsync($"/api/outlets/{owner.OutletId}/orders", new { type = "Takeaway" }, HttpStatusCode.Forbidden);
        await kitchen.GetJsonAsync($"/api/outlets/{owner.OutletId}/kots");
        await waiter.GetJsonAsync("/api/staff", HttpStatusCode.Forbidden);
        await waiter.PostJsonAsync("/api/menu/categories", new { name = "X", sortOrder = 0 }, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Deactivated_staff_lose_access_immediately()
    {
        var owner = await factory.SignupAsync();
        var cashier = await factory.CreateStaffAsync(owner, "Cashier");
        var staff = await owner.Client.GetJsonAsync("/api/staff");
        var cashierId = staff.AsArray().First(u => u!["name"]!.GetValue<string>() == "Cashier User")!.Id();

        await owner.Client.PutJsonAsync($"/api/staff/{cashierId}", new
        {
            name = "Cashier User", isActive = false, roles = new[] { new { role = "Cashier", outletId = owner.OutletId } },
        });
        await cashier.GetJsonAsync("/api/me", HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Non_owner_cannot_grant_owner_role()
    {
        var owner = await factory.SignupAsync();
        var manager = await factory.CreateStaffAsync(owner, "Manager", tenantWide: true);
        await manager.PostJsonAsync("/api/staff", new
        {
            name = "Sneaky", email = $"sneaky-{Guid.NewGuid():N}@example.test", password = "Password-123",
            roles = new[] { new { role = "Owner", outletId = (Guid?)null } },
        }, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Plan_limits_block_extra_outlets()
    {
        var owner = await factory.SignupAsync();
        await owner.Client.PostJsonAsync("/api/outlets", new { name = "Second" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Expired_trial_blocks_writes_but_allows_reads()
    {
        var owner = await factory.SignupAsync();
        await factory.WithDbAsync(db => db.Tenants.Where(t => t.Id == owner.TenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.TrialEndsAt, DateTimeOffset.UtcNow.AddDays(-1))));
        await owner.Client.GetJsonAsync($"/api/outlets/{owner.OutletId}/orders");
        await owner.Client.PostJsonAsync($"/api/outlets/{owner.OutletId}/orders", new { type = "Takeaway" }, HttpStatusCode.PaymentRequired);
    }
}
