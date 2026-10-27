using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Modules.Menu;
using RestaurantOS.Api.Modules.Payments;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Integrations;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Integrations;

/// <summary>Versioned API for authorised external channels, authenticated with tenant API keys.</summary>
public static class ExternalOrderEndpoints
{
    public static void MapExternalOrderEndpoints(this IEndpointRouteBuilder app, IConfiguration config)
    {
        app.MapGet("/api/integrations/config", () => Results.Ok(new
        {
            mockSimulators = config.GetValue<bool>("Integrations:EnableMockSimulators"),
            paymentProvider = MockPaymentGateway.Provider,
            billingProvider = "mock",
            scopes = ApiScopes.All,
        })).WithTags("Integrations").RequireAuthorization();

        var g = app.MapGroup("/api/v1/outlets/{outletId:guid}").WithTags("External API").AllowAnonymous().RequireRateLimiting("public");
        g.MapGet("/menu", async (Guid outletId, OutletMenuService menus, CancellationToken ct) =>
            Results.Ok(await menus.GetAsync(outletId, includeUnavailable: false, ct))).RequireApiKey(ApiScopes.MenuRead);
        g.MapPost("/orders", async (Guid outletId, ExternalOrderRequest req, ExternalOrderService orders, CancellationToken ct) =>
        {
            var result = await orders.IngestAsync(outletId, req, ct);
            return result.Replayed ? Results.Ok(result) : Results.Created($"/api/v1/outlets/{outletId}/orders/{result.Provider}/{result.ExternalId}", result);
        }).RequireApiKey(ApiScopes.OrdersWrite);
        g.MapGet("/orders/{provider}/{externalId}", async (Guid outletId, string provider, string externalId, ExternalOrderService orders,
            CancellationToken ct) => Results.Ok(await orders.GetAsync(outletId, provider, externalId, ct))).RequireApiKey(ApiScopes.OrdersWrite);

        if (!config.GetValue<bool>("Integrations:EnableMockSimulators")) return;

        // Sends a realistic order through the same ingestion path an aggregator would use, for demos and testing.
        app.MapPost("/api/dev/mock-aggregator/outlets/{outletId:guid}/orders", async (Guid outletId, AppDbContext db,
                ExternalOrderService orders, CancellationToken ct) =>
            {
                var unavailable = db.OutletItemOverrides.Where(o => o.OutletId == outletId && !o.IsAvailable).Select(o => o.MenuItemId);
                var required = db.ModifierGroups.Where(m => m.MinSelect > 0).Select(m => m.Id);
                var simple = await db.MenuItems.AsNoTracking()
                    .Where(i => i.IsActive && !i.Variants.Any() && !i.ModifierGroups.Any(m => required.Contains(m.ModifierGroupId))
                                && !unavailable.Contains(i.Id))
                    .Select(i => new { i.Id }).ToListAsync(ct);
                if (simple.Count == 0) throw new DomainException("validation", "Add a menu item without required choices first.");
                var pick = simple.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(simple.Count, Random.Shared.Next(1, 4)))
                    .Select(i => new LineSelection(i.Id, null, Random.Shared.Next(1, 3), null, null)).ToList();
                var externalId = $"SIM-{Random.Shared.Next(100000, 999999)}";
                var result = await orders.IngestAsync(outletId, new ExternalOrderRequest("mock-aggregator", externalId, OrderType.Delivery,
                    new ExternalCustomer("Test Customer", null, "12 Sample Street, Test Nagar"), "Simulated order", pick, Prepaid: true), ct);
                return Results.Ok(result);
            })
            .WithTags("Simulators").RequireAuthorization().RequireOutlet(Permission.OrderCreate);
    }
}
