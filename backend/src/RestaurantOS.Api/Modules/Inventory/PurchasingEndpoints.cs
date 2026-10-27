using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Inventory;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Inventory;

public sealed record SupplierRequest(string Name, string? ContactName, string? Phone, string? Email, string? Gstin, string? Address, bool IsActive = true);

public sealed record PurchaseOrderLineDto(Guid InventoryItemId, decimal Quantity, decimal UnitCost);
public sealed record PurchaseOrderRequest(Guid SupplierId, string? Notes, List<PurchaseOrderLineDto> Lines);
public sealed record ReceiveLineDto(Guid LineId, decimal ReceivedQuantity, decimal? UnitCost);
public sealed record ReceiveRequest(List<ReceiveLineDto>? Lines);

public static class PurchasingEndpoints
{
    public static void MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        var suppliers = app.MapGroup("/api/suppliers").WithTags("Purchasing").RequireAuthorization();
        suppliers.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Suppliers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct))).RequireAny(Permission.PurchaseManage);
        suppliers.MapPost("", CreateSupplier).RequireTenant(Permission.PurchaseManage);
        suppliers.MapPut("/{id:guid}", UpdateSupplier).RequireTenant(Permission.PurchaseManage);

        var po = app.MapGroup("/api/outlets/{outletId:guid}/purchase-orders").WithTags("Purchasing").RequireAuthorization();
        po.MapGet("", List).RequireOutlet(Permission.PurchaseManage);
        po.MapGet("/{poId:guid}", Get).RequireOutlet(Permission.PurchaseManage);
        po.MapPost("", Create).RequireOutlet(Permission.PurchaseManage);
        po.MapPut("/{poId:guid}", Update).RequireOutlet(Permission.PurchaseManage);
        po.MapPost("/{poId:guid}/order", MarkOrdered).RequireOutlet(Permission.PurchaseManage);
        po.MapPost("/{poId:guid}/receive", Receive).RequireOutlet(Permission.PurchaseManage);
        po.MapPost("/{poId:guid}/cancel", Cancel).RequireOutlet(Permission.PurchaseManage);
    }

    private static async Task<IResult> CreateSupplier(SupplierRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var s = new Supplier { Name = "" };
        Apply(s, req);
        db.Suppliers.Add(s);
        await audit.AddAsync("supplier.created", "Supplier", s.Id, new { s.Name }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/suppliers/{s.Id}", s);
    }

    private static async Task<IResult> UpdateSupplier(Guid id, SupplierRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier");
        Apply(s, req);
        await audit.AddAsync("supplier.updated", "Supplier", s.Id, req, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(s);
    }

    private static void Apply(Supplier s, SupplierRequest req)
    {
        s.Name = Guard.Required(req.Name, "Name");
        s.ContactName = Guard.Optional(req.ContactName, "Contact name", 120);
        s.Phone = Guard.Optional(req.Phone, "Phone", 20);
        s.Email = req.Email is { Length: > 0 } e ? Guard.Email(e) : null;
        s.Gstin = Guard.Optional(req.Gstin, "GSTIN", 20);
        s.Address = Guard.Optional(req.Address, "Address");
        s.IsActive = req.IsActive;
    }

    private static async Task<IResult> List(Guid outletId, AppDbContext db, PurchaseOrderStatus? status, CancellationToken ct)
    {
        var q = db.PurchaseOrders.AsNoTracking().Where(p => p.OutletId == outletId);
        if (status is { } s) q = q.Where(p => p.Status == s);
        var list = await q.OrderByDescending(p => p.Number).Take(200)
            .Join(db.Suppliers, p => p.SupplierId, s => s.Id, (p, s) => new
            {
                p.Id, p.Number, p.Status, p.SupplierId, supplierName = s.Name, p.Total, p.CreatedAt, p.OrderedAt, p.ReceivedAt,
                lineCount = p.Lines.Count,
            })
            .ToListAsync(ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> Get(Guid outletId, Guid poId, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await DtoAsync(db, await LoadAsync(db, outletId, poId, tracking: false, ct), ct));

    private static async Task<IResult> Create(Guid outletId, PurchaseOrderRequest req, AppDbContext db, ITenantContext tenant,
        AuditWriter audit, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var po = new PurchaseOrder { OutletId = outletId, CreatedByUserId = tenant.UserId };
        await ApplyAsync(db, po, req, ct);
        po.Number = await db.NextAsync(outletId, "po", ct);
        db.PurchaseOrders.Add(po);
        await audit.AddAsync("purchase_order.created", "PurchaseOrder", po.Id, new { po.Number, po.Total }, outletId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/outlets/{outletId}/purchase-orders/{po.Id}", await DtoAsync(db, po, ct));
    }

    private static async Task<IResult> Update(Guid outletId, Guid poId, PurchaseOrderRequest req, AppDbContext db, AuditWriter audit,
        CancellationToken ct)
    {
        var po = await LoadAsync(db, outletId, poId, tracking: true, ct);
        po.EnsureEditable();
        db.RemoveRange(po.Lines);
        po.Lines.Clear();
        await ApplyAsync(db, po, req, ct);
        await audit.AddAsync("purchase_order.updated", "PurchaseOrder", po.Id, new { po.Number, po.Total }, outletId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DtoAsync(db, po, ct));
    }

    private static async Task ApplyAsync(AppDbContext db, PurchaseOrder po, PurchaseOrderRequest req, CancellationToken ct)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == req.SupplierId && s.IsActive, ct)) throw new NotFoundException("Supplier");
        if (req.Lines is null || req.Lines.Count == 0) throw new DomainException("po_empty", "Add at least one line.");
        var ids = req.Lines.Select(l => l.InventoryItemId).Distinct().ToList();
        if (ids.Count != req.Lines.Count) throw new DomainException("validation", "Each inventory item can appear only once.");
        if (await db.InventoryItems.CountAsync(i => ids.Contains(i.Id) && i.IsActive, ct) != ids.Count) throw new NotFoundException("Inventory item");

        po.SupplierId = req.SupplierId;
        po.Notes = Guard.Optional(req.Notes, "Notes");
        foreach (var l in req.Lines)
        {
            var line = new PurchaseOrderLine
            {
                PurchaseOrderId = po.Id,
                InventoryItemId = l.InventoryItemId,
                Quantity = Money.RoundQuantity(Guard.Positive(l.Quantity, "Quantity")),
                UnitCost = Money.Round(Guard.NonNegative(l.UnitCost, "Unit cost")),
            };
            po.Lines.Add(line);
            if (db.Entry(po).State != EntityState.Detached) db.Add(line);
        }

        po.Recalculate();
    }

    private static async Task<IResult> MarkOrdered(Guid outletId, Guid poId, AppDbContext db, AuditWriter audit, TimeProvider clock,
        CancellationToken ct)
    {
        var po = await LoadAsync(db, outletId, poId, tracking: true, ct);
        po.MarkOrdered(clock.GetUtcNow());
        await audit.AddAsync("purchase_order.ordered", "PurchaseOrder", po.Id, new { po.Number }, outletId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DtoAsync(db, po, ct));
    }

    private static async Task<IResult> Receive(Guid outletId, Guid poId, ReceiveRequest? req, AppDbContext db, InventoryService inventory,
        ITenantContext tenant, AuditWriter audit, TimeProvider clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Row lock: concurrent receipts of the same PO serialise here and the second one sees status Received.
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM purchase_orders WHERE id = {poId} AND outlet_id = {outletId} AND tenant_id = {tenant.TenantId} FOR UPDATE", ct);
        var po = await LoadAsync(db, outletId, poId, tracking: true, ct);

        var received = (req?.Lines ?? []).ToDictionary(l => l.LineId, l => (l.ReceivedQuantity, l.UnitCost));
        po.Receive(received, clock.GetUtcNow());

        var itemIds = po.Lines.Select(l => l.InventoryItemId).ToList();
        var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        foreach (var line in po.Lines.Where(l => l.ReceivedQuantity > 0)) items[line.InventoryItemId].CostPerUnit = line.UnitCost;

        var movements = po.Lines.Where(l => l.ReceivedQuantity > 0)
            .Select(l => new StockMovement(l.InventoryItemId, l.ReceivedQuantity!.Value, l.UnitCost, $"PO #{po.Number}"));
        await inventory.PostAsync(outletId, StockMovementType.Purchase, InventoryService.Sources.PurchaseOrder, po.Id.ToString(), movements,
            tenant.UserId, ct);
        await audit.AddAsync("purchase_order.received", "PurchaseOrder", po.Id, new
        {
            po.Number, po.Total, lines = po.Lines.Select(l => new { l.InventoryItemId, l.Quantity, l.ReceivedQuantity, l.UnitCost }),
        }, outletId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await inventory.PublishAsync(outletId);
        return Results.Ok(await DtoAsync(db, po, ct));
    }

    private static async Task<IResult> Cancel(Guid outletId, Guid poId, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var po = await LoadAsync(db, outletId, poId, tracking: true, ct);
        po.Cancel();
        await audit.AddAsync("purchase_order.cancelled", "PurchaseOrder", po.Id, new { po.Number }, outletId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DtoAsync(db, po, ct));
    }

    private static async Task<PurchaseOrder> LoadAsync(AppDbContext db, Guid outletId, Guid poId, bool tracking, CancellationToken ct)
    {
        var q = db.PurchaseOrders.Include(p => p.Lines).Where(p => p.Id == poId && p.OutletId == outletId);
        if (!tracking) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Purchase order");
    }

    private static async Task<object> DtoAsync(AppDbContext db, PurchaseOrder po, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == po.SupplierId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var ids = po.Lines.Select(l => l.InventoryItemId).ToList();
        var items = await db.InventoryItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return new
        {
            po.Id, po.OutletId, po.Number, po.Status, po.SupplierId, supplierName = supplier, po.Notes, po.Total, po.CreatedAt, po.OrderedAt,
            po.ReceivedAt,
            lines = po.Lines.Select(l => new
            {
                l.Id, l.InventoryItemId, itemName = items.GetValueOrDefault(l.InventoryItemId)?.Name, unit = items.GetValueOrDefault(l.InventoryItemId)?.Unit,
                l.Quantity, l.UnitCost, l.ReceivedQuantity, amount = Money.Round((l.ReceivedQuantity ?? l.Quantity) * l.UnitCost),
            }),
        };
    }
}
