using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Modules.Payments;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Kitchen;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Orders;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/outlets/{outletId:guid}/orders").WithTags("Orders").RequireAuthorization();

        g.MapGet("", List).RequireOutlet(Permission.OrderCreate);
        g.MapGet("/{orderId:guid}", async (Guid outletId, Guid orderId, OrderService s, CancellationToken ct) =>
            await s.GetAsync(outletId, orderId, ct) is { } o ? Results.Ok(OrderDto.From(o)) : throw new NotFoundException("Order"))
            .RequireOutlet(Permission.OrderCreate);

        g.MapPost("", async (Guid outletId, CreateOrderRequest req, OrderService s, CancellationToken ct) =>
        {
            var (order, created) = await s.CreateAsync(outletId, req, null, ct);
            return created
                ? Results.Created($"/api/outlets/{outletId}/orders/{order.Id}", OrderDto.From(order))
                : Results.Ok(OrderDto.From(order));
        }).RequireOutlet(Permission.OrderCreate);

        g.MapPost("/{orderId:guid}/lines", async (Guid outletId, Guid orderId, AddLinesRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.AddLinesAsync(outletId, orderId, req, ct)))).RequireOutlet(Permission.OrderCreate);

        g.MapPatch("/{orderId:guid}/lines/{lineId:guid}", async (Guid outletId, Guid orderId, Guid lineId, ChangeQuantityRequest req,
                OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.ChangeQuantityAsync(outletId, orderId, lineId, req, ct)))).RequireOutlet(Permission.OrderCreate);

        g.MapPost("/{orderId:guid}/lines/{lineId:guid}/void", async (Guid outletId, Guid orderId, Guid lineId, ReasonRequest req,
                OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.VoidLineAsync(outletId, orderId, lineId, req, ct)))).RequireOutlet(Permission.OrderVoidItem);

        g.MapPost("/{orderId:guid}/kot", async (Guid outletId, Guid orderId, VersionRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.SendToKitchenAsync(outletId, orderId, req.Version, ct)))).RequireOutlet(Permission.OrderCreate);

        g.MapPost("/{orderId:guid}/accept", async (Guid outletId, Guid orderId, VersionRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.AcceptAsync(outletId, orderId, req.Version, ct)))).RequireOutlet(Permission.OrderCreate);

        g.MapPut("/{orderId:guid}/discount", async (Guid outletId, Guid orderId, DiscountRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.ApplyDiscountAsync(outletId, orderId, req, ct)))).RequireOutlet(Permission.OrderDiscount);

        g.MapPut("/{orderId:guid}/customer", async (Guid outletId, Guid orderId, CustomerRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.SetCustomerAsync(outletId, orderId, req, ct)))).RequireOutlet(Permission.OrderCreate);

        g.MapPost("/{orderId:guid}/bill", async (Guid outletId, Guid orderId, VersionRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.BillAsync(outletId, orderId, req.Version, ct)))).RequireOutlet(Permission.OrderCreate);

        g.MapPost("/{orderId:guid}/reopen", async (Guid outletId, Guid orderId, VersionRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.ReopenAsync(outletId, orderId, req.Version, ct)))).RequireOutlet(Permission.OrderVoidItem);

        g.MapPost("/{orderId:guid}/cancel", async (Guid outletId, Guid orderId, ReasonRequest req, OrderService s, CancellationToken ct) =>
            Results.Ok(OrderDto.From(await s.CancelAsync(outletId, orderId, req, ct)))).RequireOutlet(Permission.OrderCancel);

        g.MapPost("/{orderId:guid}/settle", async (Guid outletId, Guid orderId, SettleRequest req, SettlementService s, CancellationToken ct) =>
            Results.Ok(await s.SettleAsync(outletId, orderId, req, ct))).RequireOutlet(Permission.OrderSettle);

        g.MapGet("/{orderId:guid}/invoice", Invoice).RequireOutlet(Permission.OrderCreate);
        g.MapGet("/{orderId:guid}/kots", async (Guid outletId, Guid orderId, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Kots.AsNoTracking().Include(k => k.Items)
                .Where(k => k.OutletId == outletId && k.OrderId == orderId).OrderBy(k => k.CreatedAt).ToListAsync(ct)))
            .RequireOutlet(Permission.OrderCreate);
    }

    private static async Task<IResult> List(Guid outletId, string? view, DateOnly? date, int? limit, AppDbContext db, CancellationToken ct)
    {
        var q = db.Orders.AsNoTracking().Where(o => o.OutletId == outletId);
        q = (view ?? "running") switch
        {
            "running" => q.Where(o => o.Status == OrderStatus.Open || o.Status == OrderStatus.Billed || o.Status == OrderStatus.AwaitingAcceptance),
            "pending" => q.Where(o => o.Status == OrderStatus.AwaitingAcceptance),
            "settled" => q.Where(o => o.Status == OrderStatus.Settled),
            "cancelled" => q.Where(o => o.Status == OrderStatus.Cancelled),
            "all" => q,
            _ => throw new DomainException("validation", "Unknown view."),
        };
        if (date is { } d)
        {
            var outlet = await db.Outlets.AsNoTracking().FirstAsync(o => o.Id == outletId, ct);
            var (start, end) = outlet.DayBounds(d);
            q = q.Where(o => o.BusinessDate == d || (o.BusinessDate == null && o.CreatedAt >= start && o.CreatedAt < end));
        }

        var orders = await q.OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(limit ?? 200, 1, 500))
            .Select(o => new
            {
                o.Id, o.OrderNumber, o.Type, o.Source, o.Status, o.TableId, o.TableName, o.CustomerName, o.CustomerPhone,
                o.GrandTotal, o.InvoiceNumber, o.CreatedAt, o.SettledAt, o.Version, o.ExternalRef,
                itemCount = o.Lines.Count(l => l.Status != OrderLineStatus.Voided),
                pendingItems = o.Lines.Count(l => l.Status == OrderLineStatus.Pending),
            })
            .ToListAsync(ct);
        return Results.Ok(orders);
    }

    private static async Task<IResult> Invoice(Guid outletId, Guid orderId, AppDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId && o.OutletId == outletId, ct)
                    ?? throw new NotFoundException("Order");
        var outlet = await db.Outlets.AsNoTracking().FirstAsync(o => o.Id == outletId, ct);
        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == outlet.TenantId).Select(t => t.Name).FirstAsync(ct);
        var payments = await db.Payments.AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.CreatedAt)
            .Select(p => new { p.Method, p.Amount, p.Tendered, p.Reference }).ToListAsync(ct);
        return Results.Ok(new
        {
            business = new { name = tenantName, outlet = outlet.Name, outlet.Address, outlet.Phone, outlet.Gstin },
            order = OrderDto.From(order),
            payments,
        });
    }
}

public static class OrderDto
{
    public static object From(Order o) => new
    {
        o.Id,
        o.OutletId,
        o.OrderNumber,
        o.ClientOrderId,
        o.Type,
        o.Source,
        o.Status,
        o.TableId,
        o.TableName,
        o.CustomerId,
        o.CustomerName,
        o.CustomerPhone,
        o.DeliveryAddress,
        o.Guests,
        o.Notes,
        o.ExternalRef,
        o.PricesIncludeTax,
        o.DiscountType,
        o.DiscountValue,
        o.DiscountReason,
        o.Subtotal,
        o.DiscountTotal,
        o.TaxTotal,
        o.RoundOff,
        o.GrandTotal,
        o.PaidTotal,
        taxBreakdown = o.TaxBreakdown.Select(t => new { t.Code, t.RatePercent, t.Amount }),
        o.InvoiceNumber,
        o.BusinessDate,
        o.CreatedAt,
        o.BilledAt,
        o.SettledAt,
        o.CancelledAt,
        o.CancelReason,
        o.MergedIntoOrderId,
        o.Version,
        lines = o.Lines.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).Select(l => new
        {
            l.Id,
            l.MenuItemId,
            l.VariantId,
            l.ItemName,
            l.VariantName,
            l.Station,
            l.UnitPrice,
            l.Quantity,
            modifiers = l.Modifiers.Select(m => new { m.OptionId, m.GroupName, m.Name, m.Price }),
            taxRates = l.TaxRates.Select(t => new { t.Code, t.RatePercent }),
            l.Notes,
            l.Status,
            l.KotId,
            l.VoidReason,
            l.Gross,
            l.Discount,
            l.Taxable,
            l.TaxTotal,
            l.Total,
        }),
    };
}
