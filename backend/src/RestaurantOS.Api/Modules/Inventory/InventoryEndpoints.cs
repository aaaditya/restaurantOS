using Microsoft.EntityFrameworkCore;
using Npgsql;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Inventory;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Inventory;

public sealed record InventoryItemRequest(string Name, string? Sku, StockUnit Unit, decimal ReorderLevel, decimal CostPerUnit, bool IsActive = true);

public enum AdjustmentKind
{
    Opening = 0,
    Wastage = 1,
    Correction = 2,
}

/// <summary>
/// Quantities: Opening and Wastage take positive amounts (wastage removes stock); Correction is signed.
/// <paramref name="ClientRef"/> makes the request idempotent: retrying with the same ref returns the original posting.
/// </summary>
public sealed record AdjustmentRequest(string ClientRef, AdjustmentKind Kind, string? Reason, List<AdjustmentLine> Lines);
public sealed record AdjustmentLine(Guid InventoryItemId, decimal Quantity, decimal? UnitCost, string? Notes);

public sealed record RecipeLineDto(Guid InventoryItemId, decimal Quantity);
public sealed record RecipeRequest(Guid MenuItemId, Guid? VariantId, List<RecipeLineDto> Lines);

public static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var tenant = app.MapGroup("/api/inventory").WithTags("Inventory").RequireAuthorization();
        tenant.MapGet("/items", ListItems).RequireAny(Permission.InventoryView);
        tenant.MapPost("/items", CreateItem).RequireTenant(Permission.InventoryManage);
        tenant.MapPut("/items/{id:guid}", UpdateItem).RequireTenant(Permission.InventoryManage);
        tenant.MapGet("/recipes", ListRecipes).RequireAny(Permission.InventoryView);
        tenant.MapPut("/recipes", SaveRecipe).RequireTenant(Permission.InventoryManage);

        var outlet = app.MapGroup("/api/outlets/{outletId:guid}/inventory").WithTags("Inventory").RequireAuthorization();
        outlet.MapGet("/stock", Stock).RequireOutlet(Permission.InventoryView);
        outlet.MapGet("/ledger", Ledger).RequireOutlet(Permission.InventoryView);
        outlet.MapPost("/adjustments", Adjust).RequireOutlet(Permission.InventoryManage);
    }

    private static async Task<IResult> ListItems(AppDbContext db, CancellationToken ct) =>
        Results.Ok(await db.InventoryItems.AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct));

    private static async Task<IResult> CreateItem(InventoryItemRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var item = new InventoryItem { Name = "" };
        await ApplyAsync(db, item, req, ct);
        db.InventoryItems.Add(item);
        await audit.AddAsync("inventory.item_created", "InventoryItem", item.Id, new { item.Name, item.Unit }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/inventory/items/{item.Id}", item);
    }

    private static async Task<IResult> UpdateItem(Guid id, InventoryItemRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var item = await db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Inventory item");
        if (req.Unit != item.Unit && await db.StockLedger.AnyAsync(e => e.InventoryItemId == id, ct))
            throw new DomainException("unit_locked", "The unit cannot change after stock has been recorded.");
        await ApplyAsync(db, item, req, ct);
        await audit.AddAsync("inventory.item_updated", "InventoryItem", item.Id, req, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(item);
    }

    private static async Task ApplyAsync(AppDbContext db, InventoryItem item, InventoryItemRequest req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Unit)) throw new DomainException("validation", "Unknown unit.");
        var name = Guard.Required(req.Name, "Name");
        if (await db.InventoryItems.AnyAsync(i => i.Id != item.Id && i.Name.ToLower() == name.ToLower(), ct))
            throw new ConflictException("duplicate_name", $"An inventory item named {name} already exists.");
        item.Name = name;
        item.Sku = Guard.Optional(req.Sku, "SKU", 60);
        item.Unit = req.Unit;
        item.ReorderLevel = Money.RoundQuantity(Guard.NonNegative(req.ReorderLevel, "Reorder level"));
        item.CostPerUnit = Money.RoundQuantity(Guard.NonNegative(req.CostPerUnit, "Cost per unit"));
        item.IsActive = req.IsActive;
    }

    private static async Task<IResult> Stock(Guid outletId, AppDbContext db, bool? lowOnly, CancellationToken ct)
    {
        var items = await db.InventoryItems.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.Name).ToListAsync(ct);
        var balances = await db.StockBalances.AsNoTracking().Where(b => b.OutletId == outletId)
            .ToDictionaryAsync(b => b.InventoryItemId, ct);
        var rows = items.Select(i =>
        {
            var qty = balances.TryGetValue(i.Id, out var b) ? b.Quantity : 0m;
            return new
            {
                inventoryItemId = i.Id,
                i.Name,
                i.Sku,
                i.Unit,
                i.ReorderLevel,
                i.CostPerUnit,
                quantity = qty,
                value = Money.Round(Math.Max(qty, 0) * i.CostPerUnit),
                isLow = qty <= i.ReorderLevel && (i.ReorderLevel > 0 || qty < 0),
                updatedAt = balances.TryGetValue(i.Id, out var bb) ? bb.UpdatedAt : (DateTimeOffset?)null,
            };
        }).Where(r => lowOnly != true || r.isLow).ToList();
        return Results.Ok(new { items = rows, totalValue = Money.Round(rows.Sum(r => r.value)), lowCount = rows.Count(r => r.isLow) });
    }

    private static async Task<IResult> Ledger(Guid outletId, AppDbContext db, Guid? itemId, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var q = db.StockLedger.AsNoTracking().Where(e => e.OutletId == outletId);
        if (itemId is { } id) q = q.Where(e => e.InventoryItemId == id);
        var entries = await q.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(take)
            .Join(db.InventoryItems, e => e.InventoryItemId, i => i.Id, (e, i) => new
            {
                e.Id,
                e.InventoryItemId,
                itemName = i.Name,
                i.Unit,
                e.Type,
                e.Quantity,
                e.UnitCost,
                e.BalanceAfter,
                e.SourceType,
                e.SourceId,
                e.Notes,
                e.CreatedAt,
            })
            .ToListAsync(ct);
        return Results.Ok(entries.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id));
    }

    private static async Task<IResult> Adjust(Guid outletId, AdjustmentRequest req, AppDbContext db, InventoryService inventory,
        ITenantContext tenant, AuditWriter audit, CancellationToken ct)
    {
        var clientRef = Guard.Required(req.ClientRef, "Client reference", 100);
        var existing = await ReplayAsync(db, outletId, clientRef, ct);
        if (existing is not null) return Results.Ok(existing);

        if (!Enum.IsDefined(req.Kind)) throw new DomainException("validation", "Unknown adjustment kind.");
        if (req.Lines is null || req.Lines.Count == 0) throw new DomainException("validation", "Add at least one line.");
        var reason = Guard.Optional(req.Reason, "Reason");
        if (req.Kind != AdjustmentKind.Opening && reason is null)
            throw new DomainException("reason_required", "A reason is required for wastage and corrections.");

        var ids = req.Lines.Select(l => l.InventoryItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        if (items.Count != ids.Count) throw new NotFoundException("Inventory item");

        var movements = new List<StockMovement>();
        foreach (var line in req.Lines)
        {
            var item = items[line.InventoryItemId];
            var qty = Money.RoundQuantity(line.Quantity);
            if (qty == 0) throw new DomainException("validation", "Quantities cannot be zero.");
            if (req.Kind != AdjustmentKind.Correction && qty < 0)
                throw new DomainException("validation", "Opening and wastage quantities must be positive.");
            if (line.UnitCost is < 0) throw new DomainException("validation", "Unit cost cannot be negative.");
            if (req.Kind == AdjustmentKind.Opening && line.UnitCost is > 0) item.CostPerUnit = Money.RoundQuantity(line.UnitCost.Value);
            var signed = req.Kind == AdjustmentKind.Wastage ? -qty : qty;
            movements.Add(new StockMovement(item.Id, signed, line.UnitCost ?? item.CostPerUnit,
                Guard.Optional(line.Notes, "Notes") ?? reason));
        }

        var type = req.Kind switch
        {
            AdjustmentKind.Opening => StockMovementType.Opening,
            AdjustmentKind.Wastage => StockMovementType.Wastage,
            _ => StockMovementType.Adjustment,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var entries = await inventory.PostAsync(outletId, type, InventoryService.Sources.Manual, clientRef, movements, tenant.UserId, ct);
            await audit.AddAsync($"inventory.{req.Kind.ToString().ToLowerInvariant()}", "StockAdjustment", clientRef, new
            {
                reason, lines = entries.Select(e => new { e.InventoryItemId, e.Quantity, e.BalanceAfter }),
            }, outletId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return Results.Ok(await ReplayAsync(db, outletId, clientRef, ct)
                              ?? throw new ConflictException("duplicate_adjustment", "This adjustment was already recorded."));
        }

        await inventory.PublishAsync(outletId);
        return Results.Ok(await ReplayAsync(db, outletId, clientRef, ct));
    }

    private static async Task<object?> ReplayAsync(AppDbContext db, Guid outletId, string clientRef, CancellationToken ct)
    {
        var entries = await db.StockLedger.AsNoTracking()
            .Where(e => e.SourceType == InventoryService.Sources.Manual && e.SourceId == clientRef)
            .Select(e => new { e.Id, e.OutletId, e.InventoryItemId, e.Type, e.Quantity, e.UnitCost, e.BalanceAfter, e.Notes, e.CreatedAt })
            .ToListAsync(ct);
        if (entries.Count == 0) return null;
        if (entries.Any(e => e.OutletId != outletId))
            throw new ConflictException("client_ref_reused", "This client reference was already used at another outlet.");
        return new { clientRef, entries };
    }

    private static async Task<IResult> ListRecipes(AppDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Recipes.AsNoTracking().Include(r => r.Lines).ToListAsync(ct));

    /// <summary>Creates, replaces or (with no lines) deletes the recipe for a menu item or one of its variants.</summary>
    private static async Task<IResult> SaveRecipe(RecipeRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var item = await db.MenuItems.AsNoTracking().Include(i => i.Variants).FirstOrDefaultAsync(i => i.Id == req.MenuItemId, ct)
                   ?? throw new NotFoundException("Menu item");
        if (req.VariantId is { } vid && item.Variants.All(v => v.Id != vid)) throw new NotFoundException("Variant");

        var lines = (req.Lines ?? []).GroupBy(l => l.InventoryItemId)
            .Select(g => new RecipeLineDto(g.Key, Money.RoundQuantity(g.Sum(x => x.Quantity)))).ToList();
        if (lines.Any(l => l.Quantity <= 0)) throw new DomainException("validation", "Ingredient quantities must be positive.");
        var ids = lines.Select(l => l.InventoryItemId).ToList();
        if (await db.InventoryItems.CountAsync(i => ids.Contains(i.Id), ct) != ids.Count) throw new NotFoundException("Inventory item");

        var recipe = await db.Recipes.Include(r => r.Lines).FirstOrDefaultAsync(r => r.MenuItemId == req.MenuItemId && r.VariantId == req.VariantId, ct);
        if (lines.Count == 0)
        {
            if (recipe is not null) db.Recipes.Remove(recipe);
            await audit.AddAsync("inventory.recipe_deleted", "MenuItem", item.Id, new { req.VariantId }, ct: ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        if (recipe is null)
        {
            recipe = new Recipe { MenuItemId = item.Id, VariantId = req.VariantId };
            db.Recipes.Add(recipe);
        }
        else
        {
            db.RemoveRange(recipe.Lines);
            recipe.Lines.Clear();
        }

        foreach (var l in lines)
        {
            var line = new RecipeLine { RecipeId = recipe.Id, InventoryItemId = l.InventoryItemId, Quantity = l.Quantity };
            recipe.Lines.Add(line);
            db.Add(line);
        }

        await audit.AddAsync("inventory.recipe_saved", "MenuItem", item.Id, new { req.VariantId, lines }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(recipe);
    }
}
