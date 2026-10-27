using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Realtime;
using RestaurantOS.Domain.Kitchen;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Kitchen;

public sealed class KitchenService(AppDbContext db, TimeProvider clock, IRealtimeNotifier notifier)
{
    /// <summary>
    /// Creates one KOT per kitchen station for the pending lines and marks them sent.
    /// Caller must run inside a transaction (KOT numbers come from the outlet counter).
    /// </summary>
    public async Task<List<Kot>> SendPendingAsync(Order order, Outlet outlet, Guid? userId, CancellationToken ct)
    {
        var pending = order.Lines.Where(l => l.Status == OrderLineStatus.Pending).ToList();
        var kots = new List<Kot>();
        foreach (var station in pending.GroupBy(l => l.Station).OrderBy(g => g.Key))
        {
            var kot = await NewKotAsync(order, outlet, station.Key, KotKind.Normal, userId, ct);
            foreach (var line in station)
            {
                kot.Items.Add(ToItem(kot, line));
                line.Status = OrderLineStatus.Sent;
                line.KotId = kot.Id;
            }

            kots.Add(kot);
        }

        return kots;
    }

    public async Task<Kot> SendCancellationAsync(Order order, Outlet outlet, OrderLine line, Guid? userId, CancellationToken ct)
    {
        var kot = await NewKotAsync(order, outlet, line.Station, KotKind.Cancellation, userId, ct);
        var item = ToItem(kot, line);
        item.Notes = $"VOID: {line.VoidReason}";
        kot.Items.Add(item);
        return kot;
    }

    public Task PublishAsync(Guid outletId, IEnumerable<Kot> kots) =>
        Task.WhenAll(kots.Select(k => notifier.PublishAsync(outletId, RealtimeEvents.KotChanged,
            new { kotId = k.Id, k.OrderId, k.Status, k.Station, k.KotNumber, k.Kind })));

    private async Task<Kot> NewKotAsync(Order order, Outlet outlet, string station, KotKind kind, Guid? userId, CancellationToken ct)
    {
        var day = outlet.LocalDate(clock.GetUtcNow());
        var seq = await db.NextAsync(outlet.Id, $"kot:{day:yyyyMMdd}", ct);
        var kot = new Kot
        {
            OutletId = outlet.Id,
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            KotNumber = $"K{seq}",
            Kind = kind,
            Station = station,
            TableName = order.TableName,
            OrderTypeLabel = order.Type switch
            {
                OrderType.DineIn => "Dine-in",
                OrderType.Takeaway => "Takeaway",
                _ => order.Source == OrderSource.Aggregator ? "Delivery (online)" : "Delivery",
            },
            CreatedByUserId = userId,
        };
        db.Kots.Add(kot);
        return kot;
    }

    private static KotItem ToItem(Kot kot, OrderLine line) => new()
    {
        KotId = kot.Id,
        OrderLineId = line.Id,
        Name = line.ItemName,
        VariantName = line.VariantName,
        ModifiersText = line.Modifiers.Count == 0 ? null : string.Join(", ", line.Modifiers.Select(m => m.Name)),
        Quantity = line.Quantity,
        Notes = line.Notes,
    };

    public async Task<List<Kot>> ListActiveAsync(Guid outletId, string? station, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddHours(-24);
        var q = db.Kots.AsNoTracking().Include(k => k.Items)
            .Where(k => k.OutletId == outletId && k.CreatedAt >= since
                        && (k.Status == KotStatus.New || k.Status == KotStatus.Preparing || k.Status == KotStatus.Ready));
        if (!string.IsNullOrWhiteSpace(station)) q = q.Where(k => k.Station == station);
        return await q.OrderBy(k => k.CreatedAt).ToListAsync(ct);
    }
}
