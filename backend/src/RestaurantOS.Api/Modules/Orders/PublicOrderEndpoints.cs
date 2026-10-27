using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Modules.Menu;
using RestaurantOS.Api.Realtime;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Tables;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Orders;

public sealed record PublicOrderRequest(Guid ClientOrderId, string? CustomerName, string? CustomerPhone, string? Notes, List<LineSelection> Lines);

/// <summary>
/// Anonymous guest ordering from a table QR code. The QR token identifies the table (and so the tenant); the guest's
/// client-generated order id doubles as the credential for tracking that order.
/// </summary>
public static class PublicOrderEndpoints
{
    public static void MapPublicOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/public/tables/{qrToken}").WithTags("Public").AllowAnonymous().RequireRateLimiting("public");
        g.MapGet("", GetTable);
        g.MapPost("/orders", PlaceOrder);
        g.MapGet("/orders/{clientOrderId:guid}", Track);
    }

    private sealed record TableScope(DiningTable Table, Outlet Outlet, string Restaurant, bool OrderingOpen);

    private static async Task<TableScope> ResolveAsync(string qrToken, AppDbContext db, RequestTenantContext tenant, TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qrToken) || qrToken.Length > 64) throw new NotFoundException("Table");
        var table = await db.DiningTables.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.QrToken == qrToken && t.IsActive, ct)
                    ?? throw new NotFoundException("Table");
        tenant.Set(table.TenantId);
        var outlet = await db.Outlets.AsNoTracking().FirstOrDefaultAsync(o => o.Id == table.OutletId && o.IsActive, ct)
                     ?? throw new NotFoundException("Table");
        var tenantRow = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == table.TenantId, ct);
        return new TableScope(table, outlet, tenantRow.Name, outlet.QrOrderingEnabled && tenantRow.IsSubscriptionUsable(clock.GetUtcNow()));
    }

    private static async Task<IResult> GetTable(string qrToken, AppDbContext db, RequestTenantContext tenant, OutletMenuService menus,
        TimeProvider clock, CancellationToken ct)
    {
        var scope = await ResolveAsync(qrToken, db, tenant, clock, ct);
        return Results.Ok(new
        {
            restaurant = scope.Restaurant,
            outlet = scope.Outlet.Name,
            table = scope.Table.Name,
            orderingOpen = scope.OrderingOpen,
            pricesIncludeTax = scope.Outlet.PricesIncludeTax,
            menu = scope.OrderingOpen ? await menus.GetAsync(scope.Outlet.Id, includeUnavailable: false, ct) : null,
        });
    }

    private static async Task<IResult> PlaceOrder(string qrToken, PublicOrderRequest req, AppDbContext db, RequestTenantContext tenant,
        OrderService orders, IRealtimeNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var scope = await ResolveAsync(qrToken, db, tenant, clock, ct);
        if (!scope.OrderingOpen) throw new DomainException("qr_ordering_closed", "Ordering from this table is not available right now.");
        if (req.ClientOrderId == Guid.Empty) throw new DomainException("validation", "A client order id is required.");
        if (req.Lines is null || req.Lines.Count == 0) throw new DomainException("order_empty", "Add at least one item.");
        if (req.Lines.Count > 50) throw new DomainException("validation", "Too many items in one order.");

        var (order, created) = await orders.CreateAsync(scope.Outlet.Id, new CreateOrderRequest(
                req.ClientOrderId, OrderType.DineIn, scope.Table.Id, null, req.CustomerPhone, Guard.Optional(req.CustomerName, "Name", 120),
                null, Guard.Optional(req.Notes, "Notes", 300), req.Lines),
            new ExternalOrderContext(OrderSource.Qr, OrderStatus.AwaitingAcceptance, null), ct);
        if (order.TableId != scope.Table.Id) throw new ConflictException("client_order_reused", "This order id was already used.");
        if (created)
            await notifier.PublishAsync(scope.Outlet.Id, RealtimeEvents.QrOrderPlaced,
                new { orderId = order.Id, order.OrderNumber, table = scope.Table.Name, order.GrandTotal });
        return created ? Results.Created($"/api/public/tables/{qrToken}/orders/{req.ClientOrderId}", View(order)) : Results.Ok(View(order));
    }

    private static async Task<IResult> Track(string qrToken, Guid clientOrderId, AppDbContext db, RequestTenantContext tenant, TimeProvider clock,
        CancellationToken ct)
    {
        var scope = await ResolveAsync(qrToken, db, tenant, clock, ct);
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
                        .FirstOrDefaultAsync(o => o.ClientOrderId == clientOrderId && o.TableId == scope.Table.Id && o.Source == OrderSource.Qr, ct)
                    ?? throw new NotFoundException("Order");
        return Results.Ok(View(order));
    }

    private static object View(Order o) => new
    {
        o.ClientOrderId,
        o.OrderNumber,
        state = o.Status switch
        {
            OrderStatus.AwaitingAcceptance => "pending",
            OrderStatus.Cancelled when o.MergedIntoOrderId is not null => "accepted",
            OrderStatus.Cancelled => "rejected",
            OrderStatus.Settled => "completed",
            _ => "accepted",
        },
        o.TableName,
        grandTotal = o.Status == OrderStatus.Cancelled ? (decimal?)null : o.GrandTotal,
        o.CreatedAt,
        lines = o.Lines.Where(l => l.Status != OrderLineStatus.Voided || o.Status == OrderStatus.Cancelled)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).Select(l => new
        {
            l.ItemName, l.VariantName, l.Quantity, l.UnitPrice, modifiers = l.Modifiers.Select(m => m.Name), l.Notes,
        }),
    };
}
