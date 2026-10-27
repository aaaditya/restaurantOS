using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Auth;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Tables;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Tables;

public sealed record TableRequest(string Name, string? Area, int Capacity, int SortOrder, bool IsActive = true);

public static class TableEndpoints
{
    public static void MapTableEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/outlets/{outletId:guid}/tables").WithTags("Tables").RequireAuthorization();
        g.MapGet("", List).RequireOutlet(Permission.OrderCreate);
        g.MapPost("", Create).RequireOutlet(Permission.TablesManage);
        g.MapPut("/{tableId:guid}", Update).RequireOutlet(Permission.TablesManage);
        g.MapPost("/{tableId:guid}/regenerate-qr", RegenerateQr).RequireOutlet(Permission.TablesManage);
    }

    private static async Task<IResult> List(Guid outletId, AppDbContext db, CancellationToken ct)
    {
        var tables = await db.DiningTables.AsNoTracking().Where(t => t.OutletId == outletId)
            .OrderBy(t => t.Area).ThenBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var running = await db.Orders.AsNoTracking()
            .Where(o => o.OutletId == outletId && o.TableId != null
                        && (o.Status == OrderStatus.Open || o.Status == OrderStatus.Billed || o.Status == OrderStatus.AwaitingAcceptance))
            .Select(o => new { o.Id, o.TableId, o.Status, o.GrandTotal, o.CreatedAt, o.OrderNumber })
            .ToListAsync(ct);

        return Results.Ok(tables.Select(t =>
        {
            var order = running.Where(o => o.TableId == t.Id).OrderBy(o => o.CreatedAt).FirstOrDefault();
            return new
            {
                t.Id,
                t.Name,
                t.Area,
                t.Capacity,
                t.SortOrder,
                t.IsActive,
                t.QrToken,
                status = order is null ? "free" : order.Status == OrderStatus.Billed ? "billed" : "occupied",
                currentOrder = order,
            };
        }));
    }

    private static async Task<IResult> Create(Guid outletId, TableRequest req, AppDbContext db, CancellationToken ct)
    {
        var table = new DiningTable { OutletId = outletId, Name = "", QrToken = TokenService.GenerateSecret(18) };
        Apply(table, req);
        db.DiningTables.Add(table);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/outlets/{outletId}/tables/{table.Id}", table);
    }

    private static async Task<IResult> Update(Guid outletId, Guid tableId, TableRequest req, AppDbContext db, CancellationToken ct)
    {
        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == tableId && t.OutletId == outletId, ct)
                    ?? throw new NotFoundException("Table");
        Apply(table, req);
        await db.SaveChangesAsync(ct);
        return Results.Ok(table);
    }

    private static async Task<IResult> RegenerateQr(Guid outletId, Guid tableId, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == tableId && t.OutletId == outletId, ct)
                    ?? throw new NotFoundException("Table");
        table.QrToken = TokenService.GenerateSecret(18);
        await audit.AddAsync("table.qr_regenerated", "DiningTable", table.Id, null, outletId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(table);
    }

    private static void Apply(DiningTable t, TableRequest req)
    {
        if (req.Capacity is < 1 or > 100) throw new DomainException("validation", "Capacity must be between 1 and 100.");
        t.Name = Guard.Required(req.Name, "Name", 60);
        t.Area = Guard.Optional(req.Area, "Area", 60) ?? "Main";
        t.Capacity = req.Capacity;
        t.SortOrder = req.SortOrder;
        t.IsActive = req.IsActive;
    }
}
