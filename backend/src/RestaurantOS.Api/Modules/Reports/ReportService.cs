using Microsoft.EntityFrameworkCore;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Payments;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Reports;

public sealed record SalesSummary(
    int Orders,
    int Guests,
    decimal GrossSales,
    decimal Discounts,
    decimal NetSales,
    decimal Tax,
    decimal RoundOff,
    decimal TotalCollected,
    decimal AverageOrderValue,
    int CancelledOrders,
    decimal CancelledValue,
    int VoidedItems,
    decimal VoidedValue);

public sealed record GroupRow(string Key, int Orders, decimal NetSales, decimal Total);
public sealed record PaymentRow(PaymentMethod Method, int Count, decimal Amount);
public sealed record TaxRow(string Code, decimal RatePercent, decimal Taxable, decimal Amount);
public sealed record ItemRow(Guid MenuItemId, string Name, string Category, decimal Quantity, decimal NetSales, decimal Total);
public sealed record CategoryRow(string Category, decimal Quantity, decimal NetSales, decimal Total);
public sealed record DailyRow(DateOnly Date, int Orders, decimal NetSales, decimal Total);
public sealed record HourlyRow(int Hour, int Orders, decimal Total);

public sealed record SalesReport(
    DateOnly From,
    DateOnly To,
    string Currency,
    SalesSummary Summary,
    IReadOnlyList<GroupRow> ByType,
    IReadOnlyList<GroupRow> BySource,
    IReadOnlyList<PaymentRow> Payments,
    IReadOnlyList<TaxRow> Taxes,
    IReadOnlyList<ItemRow> Items,
    IReadOnlyList<CategoryRow> Categories,
    IReadOnlyList<DailyRow> Daily,
    IReadOnlyList<HourlyRow> Hourly);

/// <summary>
/// Sales figures computed from settled orders' stored line amounts, so reports always agree with the printed invoices.
/// Net sales are pre-tax amounts after discounts; total collected includes tax and round-off.
/// </summary>
public sealed class ReportService(AppDbContext db)
{
    public const int MaxRangeDays = 366;

    public static (DateOnly From, DateOnly To) ResolveRange(Outlet outlet, DateOnly? from, DateOnly? to, DateTimeOffset now)
    {
        var today = outlet.LocalDate(now);
        var end = to ?? from ?? today;
        var start = from ?? end;
        if (start > end) throw new DomainException("validation", "The start date must be on or before the end date.");
        if (end.DayNumber - start.DayNumber >= MaxRangeDays)
            throw new DomainException("validation", $"Reports cover at most {MaxRangeDays} days.");
        return (start, end);
    }

    public async Task<SalesReport> SalesAsync(Outlet outlet, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var settled = await db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.OutletId == outlet.Id && o.Status == OrderStatus.Settled && o.BusinessDate >= from && o.BusinessDate <= to)
            .ToListAsync(ct);

        var (start, _) = outlet.DayBounds(from);
        var (_, end) = outlet.DayBounds(to);
        var cancelled = await db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.OutletId == outlet.Id && o.Status == OrderStatus.Cancelled && o.MergedIntoOrderId == null
                        && o.CancelledAt >= start && o.CancelledAt < end)
            .ToListAsync(ct);

        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.OutletId == outlet.Id && p.BusinessDate >= from && p.BusinessDate <= to)
            .GroupBy(p => p.Method)
            .Select(g => new PaymentRow(g.Key, g.Count(), g.Sum(p => p.Amount)))
            .ToListAsync(ct);

        var itemIds = settled.SelectMany(o => o.Lines).Select(l => l.MenuItemId).Distinct().ToList();
        var categories = await db.MenuItems.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.MenuCategories, i => i.CategoryId, c => c.Id, (i, c) => new { i.Id, c.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var sold = settled.SelectMany(o => o.Lines).Where(l => l.Status != OrderLineStatus.Voided).ToList();
        var voided = settled.SelectMany(o => o.Lines).Where(l => l.Status == OrderLineStatus.Voided).ToList();
        var orderCount = settled.Count;
        var collected = settled.Sum(o => o.GrandTotal);

        var summary = new SalesSummary(
            orderCount,
            settled.Sum(o => o.Guests),
            sold.Sum(l => l.Gross),
            sold.Sum(l => l.Discount),
            sold.Sum(l => l.Taxable),
            settled.Sum(o => o.TaxTotal),
            settled.Sum(o => o.RoundOff),
            collected,
            orderCount == 0 ? 0 : Money.Round(collected / orderCount),
            cancelled.Count,
            Money.Round(cancelled.SelectMany(o => o.Lines).Sum(l => l.UnitPrice * l.Quantity)),
            voided.Count,
            Money.Round(voided.Sum(l => l.UnitPrice * l.Quantity)));

        List<GroupRow> Group(Func<Order, string> key) => settled.GroupBy(key)
            .Select(g => new GroupRow(g.Key, g.Count(), g.SelectMany(o => o.ActiveLines).Sum(l => l.Taxable), g.Sum(o => o.GrandTotal)))
            .OrderByDescending(r => r.Total).ToList();

        var taxableByRate = sold.SelectMany(l => l.TaxRates.Select(t => (t.Code, t.RatePercent, l.Taxable)))
            .GroupBy(x => (x.Code, x.RatePercent)).ToDictionary(g => g.Key, g => g.Sum(x => x.Taxable));
        var taxes = settled.SelectMany(o => o.TaxBreakdown).GroupBy(t => (t.Code, t.RatePercent))
            .Select(g => new TaxRow(g.Key.Code, g.Key.RatePercent, taxableByRate.GetValueOrDefault(g.Key), g.Sum(t => t.Amount)))
            .OrderBy(t => t.RatePercent).ThenBy(t => t.Code).ToList();

        string CategoryOf(Guid itemId) => categories.GetValueOrDefault(itemId, "Uncategorised");
        var items = sold.GroupBy(l => l.MenuItemId)
            .Select(g => new ItemRow(g.Key, g.OrderByDescending(l => l.CreatedAt).First().ItemName, CategoryOf(g.Key), g.Sum(l => l.Quantity),
                g.Sum(l => l.Taxable), g.Sum(l => l.Total)))
            .OrderByDescending(i => i.NetSales).ThenBy(i => i.Name).ToList();
        var byCategory = items.GroupBy(i => i.Category)
            .Select(g => new CategoryRow(g.Key, g.Sum(i => i.Quantity), g.Sum(i => i.NetSales), g.Sum(i => i.Total)))
            .OrderByDescending(c => c.NetSales).ToList();

        var daily = settled.GroupBy(o => o.BusinessDate!.Value)
            .Select(g => new DailyRow(g.Key, g.Count(), g.SelectMany(o => o.ActiveLines).Sum(l => l.Taxable), g.Sum(o => o.GrandTotal)))
            .OrderBy(d => d.Date).ToList();

        var tz = TimeZoneInfo.FindSystemTimeZoneById(outlet.TimeZone);
        var hourly = settled.GroupBy(o => TimeZoneInfo.ConvertTime(o.SettledAt!.Value, tz).Hour)
            .Select(g => new HourlyRow(g.Key, g.Count(), g.Sum(o => o.GrandTotal)))
            .OrderBy(h => h.Hour).ToList();

        return new SalesReport(from, to, outlet.Currency, summary, Group(o => o.Type.ToString()), Group(o => o.Source.ToString()),
            payments.OrderByDescending(p => p.Amount).ToList(), taxes, items, byCategory, daily, hourly);
    }

    public async Task<object> DayEndAsync(Outlet outlet, DateOnly date, CancellationToken ct)
    {
        var (start, end) = outlet.DayBounds(date);
        var settled = await db.Orders.AsNoTracking()
            .Where(o => o.OutletId == outlet.Id && o.Status == OrderStatus.Settled && o.BusinessDate == date)
            .Select(o => o.GrandTotal).ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking().Where(p => p.OutletId == outlet.Id && p.BusinessDate == date).ToListAsync(ct);
        var sessions = await db.CashSessions.AsNoTracking()
            .Where(s => s.OutletId == outlet.Id && s.OpenedAt < end && (s.ClosedAt == null || s.ClosedAt >= start))
            .OrderBy(s => s.OpenedAt).ToListAsync(ct);
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var cashBySession = await db.Payments.AsNoTracking()
            .Where(p => p.CashSessionId != null && sessionIds.Contains(p.CashSessionId.Value) && p.Method == PaymentMethod.Cash)
            .GroupBy(p => p.CashSessionId!.Value).Select(g => new { g.Key, Amount = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, ct);
        var userIds = sessions.SelectMany(s => new[] { s.OpenedByUserId, s.ClosedByUserId ?? Guid.Empty }).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        var runningOrders = await db.Orders.AsNoTracking().CountAsync(o => o.OutletId == outlet.Id
            && (o.Status == OrderStatus.Open || o.Status == OrderStatus.Billed || o.Status == OrderStatus.AwaitingAcceptance), ct);

        var salesTotal = settled.Sum();
        var paymentsTotal = payments.Sum(p => p.Amount);
        var sessionRows = sessions.Select(s =>
        {
            var cash = cashBySession.GetValueOrDefault(s.Id);
            return new
            {
                s.Id,
                s.Status,
                openedBy = names.GetValueOrDefault(s.OpenedByUserId),
                closedBy = s.ClosedByUserId is { } c ? names.GetValueOrDefault(c) : null,
                s.OpenedAt,
                s.ClosedAt,
                s.OpeningFloat,
                cashPayments = cash,
                expectedCash = s.Status == CashSessionStatus.Closed ? s.ExpectedCash : Money.Round(s.OpeningFloat + cash),
                s.CountedCash,
                s.Variance,
            };
        }).ToList();

        return new
        {
            date,
            outlet.Currency,
            settledOrders = settled.Count,
            salesTotal,
            paymentsTotal,
            unreconciled = paymentsTotal - salesTotal,
            payments = payments.GroupBy(p => p.Method)
                .Select(g => new PaymentRow(g.Key, g.Count(), g.Sum(p => p.Amount))).OrderByDescending(p => p.Amount),
            cashOutsideSessions = payments.Where(p => p.Method == PaymentMethod.Cash && p.CashSessionId == null).Sum(p => p.Amount),
            cashSessions = sessionRows,
            cashVariance = sessionRows.Sum(s => s.Variance ?? 0),
            openCashSessions = sessionRows.Count(s => s.Status == CashSessionStatus.Open),
            runningOrders,
        };
    }
}
