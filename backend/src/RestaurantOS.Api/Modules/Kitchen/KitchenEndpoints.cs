using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Kitchen;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Kitchen;

public sealed record KotStatusRequest(KotStatus Status);

public static class KitchenEndpoints
{
    public static void MapKitchenEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/outlets/{outletId:guid}/kots").WithTags("Kitchen").RequireAuthorization();

        g.MapGet("", async (Guid outletId, string? station, KitchenService kitchen, CancellationToken ct) =>
            Results.Ok(await kitchen.ListActiveAsync(outletId, station, ct))).RequireOutlet(Permission.KitchenView);

        g.MapGet("/stations", async (Guid outletId, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.MenuItems.AsNoTracking().Select(i => i.Station).Distinct().OrderBy(s => s).ToListAsync(ct)))
            .RequireOutlet(Permission.KitchenView);

        g.MapPost("/{kotId:guid}/status", async (Guid outletId, Guid kotId, KotStatusRequest req, AppDbContext db, KitchenService kitchen,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(req.Status)) throw new DomainException("validation", "Unknown status.");
            var kot = await db.Kots.Include(k => k.Items).FirstOrDefaultAsync(k => k.Id == kotId && k.OutletId == outletId, ct)
                      ?? throw new NotFoundException("KOT");
            kot.MoveTo(req.Status, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await kitchen.PublishAsync(outletId, [kot]);
            return Results.Ok(kot);
        }).RequireOutlet(Permission.KitchenUpdate);
    }
}
