using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Payments;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Reports;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var outlet = app.MapGroup("/api/outlets/{outletId:guid}/reports").WithTags("Reports").RequireAuthorization();
        outlet.MapGet("/sales", Sales).RequireOutlet(Permission.ReportsView);
        outlet.MapGet("/day-end", DayEnd).RequireOutlet(Permission.ReportsView);

        app.MapGet("/api/dashboard", Dashboard).WithTags("Reports").RequireAuthorization().RequireAny(Permission.ReportsView);
        app.MapGet("/api/audit", Audit).WithTags("Audit").RequireAuthorization().RequireAny(Permission.AuditView);
    }

    private static async Task<IResult> Sales(Guid outletId, DateOnly? from, DateOnly? to, AppDbContext db, ReportService reports, TimeProvider clock,
        CancellationToken ct)
    {
        var outlet = await db.Outlets.AsNoTracking().FirstAsync(o => o.Id == outletId, ct);
        var (start, end) = ReportService.ResolveRange(outlet, from, to, clock.GetUtcNow());
        return Results.Ok(await reports.SalesAsync(outlet, start, end, ct));
    }

    private static async Task<IResult> DayEnd(Guid outletId, DateOnly? date, AppDbContext db, ReportService reports, TimeProvider clock,
        CancellationToken ct)
    {
        var outlet = await db.Outlets.AsNoTracking().FirstAsync(o => o.Id == outletId, ct);
        return Results.Ok(await reports.DayEndAsync(outlet, date ?? outlet.LocalDate(clock.GetUtcNow()), ct));
    }

    /// <summary>Today's figures for every outlet the caller may report on, each using its own local calendar day.</summary>
    private static async Task<IResult> Dashboard(AppDbContext db, AccessService accessService, TimeProvider clock, CancellationToken ct)
    {
        var access = await accessService.GetAsync(ct);
        var outlets = (await db.Outlets.AsNoTracking().Where(o => o.IsActive).OrderBy(o => o.Name).ToListAsync(ct))
            .Where(o => access.HasOutlet(o.Id, Permission.ReportsView)).ToList();
        var now = clock.GetUtcNow();

        var rows = new List<object>();
        var trend = new Dictionary<DateOnly, decimal>();
        var topItems = new Dictionary<Guid, (string Name, decimal Quantity, decimal Total)>();
        decimal totalToday = 0, netToday = 0;
        var ordersToday = 0;
        foreach (var o in outlets)
        {
            var today = o.LocalDate(now);
            var weekStart = today.AddDays(-6);
            var days = await db.Orders.AsNoTracking()
                .Where(x => x.OutletId == o.Id && x.Status == OrderStatus.Settled && x.BusinessDate >= weekStart && x.BusinessDate <= today)
                .GroupBy(x => x.BusinessDate!.Value)
                .Select(g => new { Date = g.Key, Orders = g.Count(), Total = g.Sum(x => x.GrandTotal), Net = g.Sum(x => x.GrandTotal - x.TaxTotal - x.RoundOff) })
                .ToListAsync(ct);
            foreach (var d in days) trend[d.Date] = trend.GetValueOrDefault(d.Date) + d.Total;
            var t = days.FirstOrDefault(d => d.Date == today);

            var lines = await db.Orders.AsNoTracking()
                .Where(x => x.OutletId == o.Id && x.Status == OrderStatus.Settled && x.BusinessDate == today)
                .SelectMany(x => x.Lines).Where(l => l.Status != OrderLineStatus.Voided)
                .GroupBy(l => new { l.MenuItemId, l.ItemName })
                .Select(g => new { g.Key.MenuItemId, g.Key.ItemName, Quantity = g.Sum(l => l.Quantity), Total = g.Sum(l => l.Total) })
                .ToListAsync(ct);
            foreach (var l in lines)
            {
                var cur = topItems.GetValueOrDefault(l.MenuItemId, (l.ItemName, 0m, 0m));
                topItems[l.MenuItemId] = (cur.Item1, cur.Item2 + l.Quantity, cur.Item3 + l.Total);
            }

            var running = await db.Orders.AsNoTracking().Where(x => x.OutletId == o.Id
                    && (x.Status == OrderStatus.Open || x.Status == OrderStatus.Billed || x.Status == OrderStatus.AwaitingAcceptance))
                .GroupBy(x => x.Status == OrderStatus.AwaitingAcceptance).Select(g => new { Pending = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var lowStock = await db.InventoryItems.AsNoTracking().Where(i => i.IsActive)
                .Select(i => new
                {
                    i.ReorderLevel,
                    Quantity = db.StockBalances.Where(b => b.OutletId == o.Id && b.InventoryItemId == i.Id).Select(b => (decimal?)b.Quantity)
                        .FirstOrDefault() ?? 0m,
                })
                .CountAsync(i => i.Quantity <= i.ReorderLevel && (i.ReorderLevel > 0 || i.Quantity < 0), ct);
            var cashOpen = await db.CashSessions.AsNoTracking().AnyAsync(s => s.OutletId == o.Id && s.Status == CashSessionStatus.Open, ct);

            totalToday += t?.Total ?? 0;
            netToday += t?.Net ?? 0;
            ordersToday += t?.Orders ?? 0;
            rows.Add(new
            {
                outletId = o.Id,
                o.Name,
                date = today,
                orders = t?.Orders ?? 0,
                netSales = t?.Net ?? 0,
                totalCollected = t?.Total ?? 0,
                averageOrderValue = t is { Orders: > 0 } ? Money.Round(t.Total / t.Orders) : 0,
                runningOrders = running.Where(r => !r.Pending).Sum(r => r.Count),
                pendingQrOrders = running.Where(r => r.Pending).Sum(r => r.Count),
                lowStockItems = lowStock,
                cashSessionOpen = cashOpen,
            });
        }

        var anchor = outlets.Count > 0 ? outlets.Max(o => o.LocalDate(now)) : DateOnly.FromDateTime(now.UtcDateTime);
        return Results.Ok(new
        {
            currency = outlets.FirstOrDefault()?.Currency ?? "INR",
            totals = new
            {
                orders = ordersToday,
                netSales = netToday,
                totalCollected = totalToday,
                averageOrderValue = ordersToday > 0 ? Money.Round(totalToday / ordersToday) : 0,
            },
            outlets = rows,
            trend = Enumerable.Range(0, 7).Select(i => anchor.AddDays(i - 6))
                .Select(d => new { date = d, totalCollected = trend.GetValueOrDefault(d) }),
            topItems = topItems.OrderByDescending(kv => kv.Value.Total).Take(5)
                .Select(kv => new { menuItemId = kv.Key, name = kv.Value.Name, quantity = kv.Value.Quantity, total = kv.Value.Total }),
        });
    }

    private static async Task<IResult> Audit(AppDbContext db, AccessService accessService, Guid? outletId, string? action, string? entityType,
        string? entityId, DateTimeOffset? before, int? limit, CancellationToken ct)
    {
        var access = await accessService.GetAsync(ct);
        var q = db.AuditLogs.AsNoTracking();
        if (!access.HasTenant(Permission.AuditView))
        {
            // Outlet-level auditors only see entries for their outlets, never tenant-wide changes.
            var allowed = access.Roles.Where(r => r.OutletId is not null && RolePermissions.Has(r.Role, Permission.AuditView))
                .Select(r => r.OutletId!.Value).Distinct().ToList();
            q = q.Where(a => a.OutletId != null && allowed.Contains(a.OutletId.Value));
        }

        if (outletId is { } oid) q = q.Where(a => a.OutletId == oid);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action.Trim()));
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType.Trim());
        if (!string.IsNullOrWhiteSpace(entityId)) q = q.Where(a => a.EntityId == entityId.Trim());
        if (before is { } b) q = q.Where(a => a.CreatedAt < b);

        var take = Math.Clamp(limit ?? 50, 1, 200);
        var rows = await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take + 1)
            .Select(a => new { a.Id, a.CreatedAt, a.OutletId, a.UserId, a.UserName, a.Action, a.EntityType, a.EntityId, a.Data, a.IpAddress })
            .ToListAsync(ct);
        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();
        return Results.Ok(new { items = page, nextBefore = hasMore ? page[^1].CreatedAt : (DateTimeOffset?)null });
    }
}
