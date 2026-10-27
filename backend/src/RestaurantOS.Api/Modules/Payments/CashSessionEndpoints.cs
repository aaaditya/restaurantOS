using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Payments;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Payments;

public sealed record OpenSessionRequest(decimal OpeningFloat);
public sealed record CloseSessionRequest(decimal CountedCash, string? Notes);

public static class CashSessionEndpoints
{
    public static void MapCashSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/outlets/{outletId:guid}/cash-sessions").WithTags("Payments").RequireAuthorization();
        g.MapGet("/current", Current).RequireOutlet(Permission.CashSessionManage);
        g.MapGet("", List).RequireOutlet(Permission.CashSessionManage);
        g.MapPost("", Open).RequireOutlet(Permission.CashSessionManage);
        g.MapPost("/{sessionId:guid}/close", Close).RequireOutlet(Permission.CashSessionManage);
    }

    private static async Task<IResult> Current(Guid outletId, AppDbContext db, CancellationToken ct)
    {
        var s = await db.CashSessions.AsNoTracking().FirstOrDefaultAsync(x => x.OutletId == outletId && x.Status == CashSessionStatus.Open, ct);
        if (s is null) return Results.Ok(new { session = (object?)null });
        var cash = await CashPaymentsAsync(db, s.Id, ct);
        return Results.Ok(new { session = s, cashPayments = cash, expectedCash = Money.Round(s.OpeningFloat + cash) });
    }

    private static async Task<IResult> List(Guid outletId, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await db.CashSessions.AsNoTracking().Where(x => x.OutletId == outletId).OrderByDescending(x => x.OpenedAt).Take(60)
            .ToListAsync(ct));

    private static async Task<IResult> Open(Guid outletId, OpenSessionRequest req, AppDbContext db, ITenantContext tenant, AuditWriter audit,
        TimeProvider clock, CancellationToken ct)
    {
        if (await db.CashSessions.AnyAsync(x => x.OutletId == outletId && x.Status == CashSessionStatus.Open, ct))
            throw new ConflictException("session_open", "A cash session is already open for this outlet.");
        var s = new CashSession
        {
            OutletId = outletId, OpenedByUserId = tenant.UserId!.Value, OpeningFloat = Money.Round(Guard.NonNegative(req.OpeningFloat, "Opening float")),
            OpenedAt = clock.GetUtcNow(),
        };
        s.ExpectedCash = s.OpeningFloat;
        db.CashSessions.Add(s);
        await audit.AddAsync("cash_session.opened", "CashSession", s.Id, new { s.OpeningFloat }, outletId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/outlets/{outletId}/cash-sessions/{s.Id}", s);
    }

    private static async Task<IResult> Close(Guid outletId, Guid sessionId, CloseSessionRequest req, AppDbContext db, ITenantContext tenant,
        AuditWriter audit, TimeProvider clock, CancellationToken ct)
    {
        var s = await db.CashSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.OutletId == outletId, ct) ?? throw new NotFoundException("Cash session");
        var cash = await CashPaymentsAsync(db, s.Id, ct);
        s.Close(cash, req.CountedCash, tenant.UserId!.Value, Guard.Optional(req.Notes, "Notes"), clock.GetUtcNow());
        await audit.AddAsync("cash_session.closed", "CashSession", s.Id, new { s.ExpectedCash, s.CountedCash, s.Variance }, outletId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(s);
    }

    private static async Task<decimal> CashPaymentsAsync(AppDbContext db, Guid sessionId, CancellationToken ct) =>
        await db.Payments.Where(p => p.CashSessionId == sessionId && p.Method == PaymentMethod.Cash).SumAsync(p => p.Amount, ct);
}
