using Microsoft.EntityFrameworkCore;
using Npgsql;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Modules.Customers;
using RestaurantOS.Api.Modules.Kitchen;
using RestaurantOS.Api.Modules.Menu;
using RestaurantOS.Api.Realtime;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Kitchen;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Pricing;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Orders;

public sealed record CreateOrderRequest(
    Guid? ClientOrderId,
    OrderType Type,
    Guid? TableId,
    int? Guests,
    string? CustomerPhone,
    string? CustomerName,
    string? DeliveryAddress,
    string? Notes,
    List<LineSelection>? Lines,
    bool SendToKitchen = false);

public sealed record AddLinesRequest(List<LineSelection> Lines, uint? Version, bool SendToKitchen = false);
public sealed record ChangeQuantityRequest(decimal Quantity, uint? Version);
public sealed record ReasonRequest(string Reason, uint? Version);
public sealed record DiscountRequest(DiscountType? Type, decimal Value, string? Reason, uint? Version);
public sealed record CustomerRequest(string? Phone, string? Name, uint? Version);
public sealed record VersionRequest(uint? Version);

public sealed record ExternalOrderContext(OrderSource Source, OrderStatus InitialStatus, string? ExternalRef);

public sealed class OrderService(
    AppDbContext db,
    ITenantContext tenant,
    OutletMenuService menu,
    KitchenService kitchen,
    CustomerService customers,
    AuditWriter audit,
    IRealtimeNotifier notifier,
    TimeProvider clock)
{
    public async Task<(Order Order, bool Created)> CreateAsync(Guid outletId, CreateOrderRequest req, ExternalOrderContext? external,
        CancellationToken ct)
    {
        if (req.ClientOrderId is { } existingKey)
        {
            var existing = await LoadAsync(outletId, o => o.ClientOrderId == existingKey, ct);
            if (existing is not null) return (existing, false);
        }

        if (!Enum.IsDefined(req.Type)) throw new DomainException("validation", "Unknown order type.");
        var outlet = await db.Outlets.FirstAsync(o => o.Id == outletId, ct);
        var source = external?.Source ?? OrderSource.Pos;

        var order = new Order
        {
            OutletId = outletId,
            ClientOrderId = req.ClientOrderId,
            Type = req.Type,
            Source = source,
            Status = external?.InitialStatus ?? OrderStatus.Open,
            ExternalRef = external?.ExternalRef,
            Guests = Math.Clamp(req.Guests ?? 1, 1, 100),
            Notes = Guard.Optional(req.Notes, "Notes", 500),
            DeliveryAddress = Guard.Optional(req.DeliveryAddress, "Delivery address", 500),
            CreatedByUserId = tenant.UserId,
            PricesIncludeTax = outlet.PricesIncludeTax,
            Rounding = outlet.Rounding,
        };

        if (req.Type == OrderType.DineIn)
        {
            if (req.TableId is not { } tableId) throw new DomainException("table_required", "Choose a table for dine-in orders.");
            var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == tableId && t.OutletId == outletId && t.IsActive, ct)
                        ?? throw new NotFoundException("Table");
            if (source == OrderSource.Pos)
            {
                var running = await db.Orders.Where(o => o.TableId == tableId
                        && (o.Status == OrderStatus.Open || o.Status == OrderStatus.Billed))
                    .Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
                if (running is not null)
                    throw new ConflictException("table_occupied", $"Table {table.Name} already has a running order ({running}).");
            }

            order.TableId = table.Id;
            order.TableName = table.Name;
        }
        else if (req.TableId is not null)
        {
            throw new DomainException("validation", "Only dine-in orders can have a table.");
        }

        if (!string.IsNullOrWhiteSpace(req.CustomerPhone))
        {
            var customer = await customers.FindOrCreateAsync(req.CustomerPhone, req.CustomerName, ct);
            order.CustomerId = customer.Id;
            order.CustomerName = customer.Name;
            order.CustomerPhone = customer.Phone;
        }
        else
        {
            order.CustomerName = Guard.Optional(req.CustomerName, "Customer name", 200);
        }

        var lines = await menu.ResolveLinesAsync(outletId, req.Lines ?? [], tenant.UserId, ct);
        foreach (var line in lines) order.Lines.Add(line);
        foreach (var line in order.Lines) line.OrderId = order.Id;
        order.Recalculate();

        List<Kot> kots = [];
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            order.OrderNumber = await db.NextAsync(outletId, "order", ct);
            db.Orders.Add(order);
            if (req.SendToKitchen && order.Status == OrderStatus.Open)
            {
                if (order.Lines.Count == 0) throw new DomainException("order_empty", "Add items before sending to the kitchen.");
                kots = await kitchen.SendPendingAsync(order, outlet, tenant.UserId, ct);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (req.ClientOrderId is { } key && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent retry with the same client id won the race; return its order.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await LoadAsync(outletId, o => o.ClientOrderId == key, ct);
            if (winner is not null) return (winner, false);
            throw;
        }

        await PublishAsync(order);
        await kitchen.PublishAsync(outletId, kots);
        return (order, true);
    }

    public async Task<Order> AddLinesAsync(Guid outletId, Guid orderId, AddLinesRequest req, CancellationToken ct)
    {
        if (req.Lines is null || req.Lines.Count == 0) throw new DomainException("validation", "Add at least one item.");
        var order = await LoadForUpdateAsync(outletId, orderId, req.Version, ct);
        order.EnsureEditable();
        var lines = await menu.ResolveLinesAsync(outletId, req.Lines, tenant.UserId, ct);
        foreach (var line in lines)
        {
            line.OrderId = order.Id;
            order.Lines.Add(line);
            db.Add(line);
        }

        order.Recalculate();
        List<Kot> kots = [];
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (req.SendToKitchen && order.Status == OrderStatus.Open)
            kots = await kitchen.SendPendingAsync(order, await OutletAsync(outletId, ct), tenant.UserId, ct);
        await SaveAsync(order, ct);
        await tx.CommitAsync(ct);
        await PublishAsync(order);
        await kitchen.PublishAsync(outletId, kots);
        return order;
    }

    public async Task<Order> ChangeQuantityAsync(Guid outletId, Guid orderId, Guid lineId, ChangeQuantityRequest req, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, req.Version, ct);
        if (req.Quantity > 999) throw new DomainException("invalid_quantity", "Quantity is too large.");
        var line = order.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException("Order line");
        order.ChangeLineQuantity(lineId, req.Quantity);
        if (req.Quantity <= 0) db.Remove(line);
        await SaveAsync(order, ct);
        await PublishAsync(order);
        return order;
    }

    public async Task<Order> VoidLineAsync(Guid outletId, Guid orderId, Guid lineId, ReasonRequest req, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, req.Version, ct);
        var wasSent = order.Lines.FirstOrDefault(l => l.Id == lineId)?.Status == OrderLineStatus.Sent;
        var line = order.VoidLine(lineId, req.Reason);
        Kot? kot = null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (wasSent) kot = await kitchen.SendCancellationAsync(order, await OutletAsync(outletId, ct), line, tenant.UserId, ct);
        await audit.AddAsync("order.line_voided", "Order", order.Id,
            new { order.OrderNumber, line.ItemName, line.VariantName, line.Quantity, line.UnitPrice, req.Reason, wasSent }, outletId, ct);
        await SaveAsync(order, ct);
        await tx.CommitAsync(ct);
        await PublishAsync(order);
        if (kot is not null) await kitchen.PublishAsync(outletId, [kot]);
        return order;
    }

    public async Task<Order> SendToKitchenAsync(Guid outletId, Guid orderId, uint? version, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, version, ct);
        if (order.Status != OrderStatus.Open) throw new ConflictException("order_not_open", "Only open orders can be sent to the kitchen.");
        if (!order.Lines.Any(l => l.Status == OrderLineStatus.Pending))
            throw new DomainException("nothing_to_send", "There are no new items to send.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var kots = await kitchen.SendPendingAsync(order, await OutletAsync(outletId, ct), tenant.UserId, ct);
        await SaveAsync(order, ct);
        await tx.CommitAsync(ct);
        await PublishAsync(order);
        await kitchen.PublishAsync(outletId, kots);
        return order;
    }

    public async Task<Order> AcceptAsync(Guid outletId, Guid orderId, uint? version, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, version, ct);
        if (order.TableId is { } tableId)
        {
            var runningId = await db.Orders.Where(o => o.Id != order.Id && o.TableId == tableId
                    && (o.Status == OrderStatus.Open || o.Status == OrderStatus.Billed))
                .OrderBy(o => o.CreatedAt).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
            if (runningId is { } targetId) return await MergeAsync(order, targetId, ct);
        }

        order.Accept();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var kots = await kitchen.SendPendingAsync(order, await OutletAsync(outletId, ct), tenant.UserId, ct);
        await audit.AddAsync("order.accepted", "Order", order.Id, new { order.OrderNumber, order.Source }, outletId, ct);
        await SaveAsync(order, ct);
        await tx.CommitAsync(ct);
        await PublishAsync(order);
        await kitchen.PublishAsync(outletId, kots);
        return order;
    }

    /// <summary>Accepting a QR order for a table that already has a running order adds its items to that order.</summary>
    private async Task<Order> MergeAsync(Order source, Guid targetId, CancellationToken ct)
    {
        var target = await LoadForUpdateAsync(source.OutletId, targetId, null, ct);
        var now = clock.GetUtcNow();
        var copies = source.MergeInto(target, tenant.UserId, now);
        foreach (var line in copies) db.Add(line);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var kots = await kitchen.SendPendingAsync(target, await OutletAsync(source.OutletId, ct), tenant.UserId, ct);
        await audit.AddAsync("order.merged", "Order", source.Id, new { source.OrderNumber, into = target.OrderNumber, source.Source },
            source.OutletId, ct);
        source.UpdatedAt = now;
        await SaveAsync(target, ct);
        await tx.CommitAsync(ct);
        await PublishAsync(source);
        await PublishAsync(target);
        await kitchen.PublishAsync(source.OutletId, kots);
        return target;
    }

    public async Task<Order> ApplyDiscountAsync(Guid outletId, Guid orderId, DiscountRequest req, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, req.Version, ct);
        if (req.Type is { } t && !Enum.IsDefined(t)) throw new DomainException("validation", "Unknown discount type.");
        var before = order.DiscountTotal;
        order.ApplyDiscount(req.Type, req.Value, req.Reason);
        await audit.AddAsync("order.discount_applied", "Order", order.Id,
            new { order.OrderNumber, req.Type, req.Value, req.Reason, before, after = order.DiscountTotal }, outletId, ct);
        await SaveAsync(order, ct);
        await PublishAsync(order);
        return order;
    }

    public async Task<Order> SetCustomerAsync(Guid outletId, Guid orderId, CustomerRequest req, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, req.Version, ct);
        if (order.Status is OrderStatus.Settled or OrderStatus.Cancelled)
            throw new ConflictException("order_closed", "Customer cannot be changed on a closed order.");
        if (string.IsNullOrWhiteSpace(req.Phone))
        {
            order.CustomerId = null;
            order.CustomerPhone = null;
            order.CustomerName = Guard.Optional(req.Name, "Customer name", 200);
        }
        else
        {
            var c = await customers.FindOrCreateAsync(req.Phone, req.Name, ct);
            order.CustomerId = c.Id;
            order.CustomerPhone = c.Phone;
            order.CustomerName = c.Name;
        }

        await SaveAsync(order, ct);
        await PublishAsync(order);
        return order;
    }

    public async Task<Order> BillAsync(Guid outletId, Guid orderId, uint? version, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, version, ct);
        if (order.Lines.Any(l => l.Status == OrderLineStatus.Pending))
            throw new ConflictException("unsent_items", "Send all items to the kitchen before printing the bill.");
        order.MarkBilled(clock.GetUtcNow());
        await SaveAsync(order, ct);
        await PublishAsync(order);
        return order;
    }

    public async Task<Order> ReopenAsync(Guid outletId, Guid orderId, uint? version, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, version, ct);
        order.Reopen();
        await audit.AddAsync("order.reopened", "Order", order.Id, new { order.OrderNumber }, outletId, ct);
        await SaveAsync(order, ct);
        await PublishAsync(order);
        return order;
    }

    public async Task<Order> CancelAsync(Guid outletId, Guid orderId, ReasonRequest req, CancellationToken ct)
    {
        var order = await LoadForUpdateAsync(outletId, orderId, req.Version, ct);
        var sentLines = order.Lines.Where(l => l.Status == OrderLineStatus.Sent).ToList();
        var total = order.GrandTotal;
        order.Cancel(req.Reason, clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var outlet = await OutletAsync(outletId, ct);
        var kots = new List<Kot>();
        foreach (var line in sentLines) kots.Add(await kitchen.SendCancellationAsync(order, outlet, line, tenant.UserId, ct));
        var openKots = await db.Kots.Where(k => k.OrderId == order.Id && k.Kind == KotKind.Normal
            && (k.Status == KotStatus.New || k.Status == KotStatus.Preparing)).ToListAsync(ct);
        foreach (var k in openKots) k.MoveTo(KotStatus.Cancelled, clock.GetUtcNow());
        await audit.AddAsync("order.cancelled", "Order", order.Id, new { order.OrderNumber, req.Reason, total }, outletId, ct);
        await SaveAsync(order, ct);
        await tx.CommitAsync(ct);
        await PublishAsync(order);
        await kitchen.PublishAsync(outletId, kots.Concat(openKots));
        return order;
    }

    public Task<Order?> GetAsync(Guid outletId, Guid orderId, CancellationToken ct) => LoadAsync(outletId, o => o.Id == orderId, ct);

    internal async Task<Order> LoadForUpdateAsync(Guid outletId, Guid orderId, uint? version, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId && o.OutletId == outletId, ct)
                    ?? throw new NotFoundException("Order");
        if (version is { } v && v != order.Version)
            throw new ConflictException("stale_order", "This order was changed on another device. Reload and try again.");
        return order;
    }

    private async Task SaveAsync(Order order, CancellationToken ct)
    {
        // Touch the order row so xmin changes (and concurrent edits conflict) even when only lines changed.
        order.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private Task<Order?> LoadAsync(Guid outletId, System.Linq.Expressions.Expression<Func<Order, bool>> predicate, CancellationToken ct) =>
        db.Orders.AsNoTracking().Include(o => o.Lines).Where(o => o.OutletId == outletId).FirstOrDefaultAsync(predicate, ct);

    private Task<Outlet> OutletAsync(Guid outletId, CancellationToken ct) => db.Outlets.FirstAsync(o => o.Id == outletId, ct);

    public Task PublishAsync(Order order) =>
        notifier.PublishAsync(order.OutletId, RealtimeEvents.OrderChanged,
            new { orderId = order.Id, order.Status, order.TableId, order.OrderNumber, order.Source });
}
