using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Menu;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Menu;

public sealed record TaxComponentDto(string Code, decimal RatePercent);
public sealed record TaxGroupRequest(string Name, List<TaxComponentDto> Components, bool IsActive = true);
public sealed record CategoryRequest(string Name, int SortOrder, bool IsActive = true);
public sealed record VariantDto(Guid? Id, string Name, decimal Price, bool IsActive = true);
public sealed record ModifierOptionDto(Guid? Id, string Name, decimal Price, bool IsActive = true);
public sealed record ModifierGroupRequest(string Name, int MinSelect, int MaxSelect, List<ModifierOptionDto> Options, bool IsActive = true);

public sealed record MenuItemRequest(
    Guid CategoryId,
    string Name,
    string? Description,
    string? Sku,
    decimal Price,
    Guid? TaxGroupId,
    FoodType FoodType,
    string? Station,
    int SortOrder,
    bool IsActive,
    List<VariantDto>? Variants,
    List<Guid>? ModifierGroupIds);

public sealed record OverrideRequest(bool IsAvailable, decimal? PriceOverride);

public static class MenuEndpoints
{
    public static void MapMenuEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/menu").WithTags("Menu").RequireAuthorization();

        g.MapGet("/tax-groups", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.TaxGroups.AsNoTracking().Include(t => t.Components).OrderBy(t => t.Name).ToListAsync(ct)))
            .RequireAny(Permission.MenuView);
        g.MapPost("/tax-groups", CreateTaxGroup).RequireTenant(Permission.MenuManage);
        g.MapPut("/tax-groups/{id:guid}", UpdateTaxGroup).RequireTenant(Permission.MenuManage);

        g.MapGet("/categories", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.MenuCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct)))
            .RequireAny(Permission.MenuView);
        g.MapPost("/categories", CreateCategory).RequireTenant(Permission.MenuManage);
        g.MapPut("/categories/{id:guid}", UpdateCategory).RequireTenant(Permission.MenuManage);

        g.MapGet("/modifier-groups", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ModifierGroups.AsNoTracking().Include(m => m.Options.OrderBy(o => o.SortOrder)).OrderBy(m => m.Name).ToListAsync(ct)))
            .RequireAny(Permission.MenuView);
        g.MapPost("/modifier-groups", CreateModifierGroup).RequireTenant(Permission.MenuManage);
        g.MapPut("/modifier-groups/{id:guid}", UpdateModifierGroup).RequireTenant(Permission.MenuManage);

        g.MapGet("/items", async (AppDbContext db, CancellationToken ct) =>
            {
                var items = await db.MenuItems.AsNoTracking()
                    .Include(i => i.Variants)
                    .Include(i => i.ModifierGroups)
                    .OrderBy(i => i.SortOrder).ThenBy(i => i.Name)
                    .AsSplitQuery()
                    .ToListAsync(ct);
                return Results.Ok(items.Select(ItemDto));
            })
            .RequireAny(Permission.MenuView);
        g.MapPost("/items", CreateItem).RequireTenant(Permission.MenuManage);
        g.MapPut("/items/{id:guid}", UpdateItem).RequireTenant(Permission.MenuManage);

        var outlet = app.MapGroup("/api/outlets/{outletId:guid}/menu").WithTags("Menu").RequireAuthorization();
        outlet.MapGet("", (Guid outletId, OutletMenuService menus, CancellationToken ct) => menus.GetAsync(outletId, includeUnavailable: true, ct))
            .RequireOutlet(Permission.MenuView);
        outlet.MapPut("/items/{itemId:guid}/override", SetOverride).RequireOutlet(Permission.MenuManage);
    }

    private static async Task<IResult> CreateTaxGroup(TaxGroupRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var group = new TaxGroup { Name = Guard.Required(req.Name, "Name", 100), IsActive = req.IsActive };
        SetComponents(group, req.Components);
        db.TaxGroups.Add(group);
        await audit.AddAsync("menu.tax_group_created", "TaxGroup", group.Id, req, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/menu/tax-groups/{group.Id}", group);
    }

    private static async Task<IResult> UpdateTaxGroup(Guid id, TaxGroupRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var group = await db.TaxGroups.Include(t => t.Components).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tax group");
        group.Name = Guard.Required(req.Name, "Name", 100);
        group.IsActive = req.IsActive;
        db.RemoveRange(group.Components);
        group.Components.Clear();
        SetComponents(group, req.Components);
        await audit.AddAsync("menu.tax_group_updated", "TaxGroup", group.Id, req, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(group);
    }

    private static void SetComponents(TaxGroup group, List<TaxComponentDto>? components)
    {
        if (components is null || components.Count == 0) throw new DomainException("validation", "At least one tax component is required.");
        var i = 0;
        foreach (var c in components)
        {
            if (c.RatePercent < 0 || c.RatePercent > 100) throw new DomainException("validation", "Tax rate must be between 0 and 100.");
            group.Components.Add(new TaxComponent
            {
                TaxGroupId = group.Id, Code = Guard.Required(c.Code, "Tax code", 20).ToUpperInvariant(), RatePercent = c.RatePercent, SortOrder = i++,
            });
        }
    }

    private static async Task<IResult> CreateCategory(CategoryRequest req, AppDbContext db, CancellationToken ct)
    {
        var c = new MenuCategory { Name = Guard.Required(req.Name, "Name", 120), SortOrder = req.SortOrder, IsActive = req.IsActive };
        db.MenuCategories.Add(c);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/menu/categories/{c.Id}", c);
    }

    private static async Task<IResult> UpdateCategory(Guid id, CategoryRequest req, AppDbContext db, CancellationToken ct)
    {
        var c = await db.MenuCategories.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Category");
        c.Name = Guard.Required(req.Name, "Name", 120);
        c.SortOrder = req.SortOrder;
        c.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
        return Results.Ok(c);
    }

    private static async Task<IResult> CreateModifierGroup(ModifierGroupRequest req, AppDbContext db, CancellationToken ct)
    {
        var g = new ModifierGroup { Name = "" };
        ApplyModifierGroup(g, req);
        db.ModifierGroups.Add(g);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/menu/modifier-groups/{g.Id}", g);
    }

    private static async Task<IResult> UpdateModifierGroup(Guid id, ModifierGroupRequest req, AppDbContext db, CancellationToken ct)
    {
        var g = await db.ModifierGroups.Include(m => m.Options).FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Modifier group");
        ApplyModifierGroup(g, req);
        await db.SaveChangesAsync(ct);
        return Results.Ok(g);
    }

    private static void ApplyModifierGroup(ModifierGroup g, ModifierGroupRequest req)
    {
        g.Name = Guard.Required(req.Name, "Name", 120);
        g.MinSelect = req.MinSelect;
        g.MaxSelect = req.MaxSelect;
        g.IsActive = req.IsActive;
        g.Validate();
        if (req.Options is null || req.Options.Count == 0) throw new DomainException("validation", "At least one option is required.");

        var i = 0;
        var keep = new HashSet<Guid>();
        foreach (var o in req.Options)
        {
            Guard.NonNegative(o.Price, "Option price");
            var existing = o.Id is { } oid ? g.Options.FirstOrDefault(x => x.Id == oid) : null;
            if (existing is null)
            {
                existing = new ModifierOption { ModifierGroupId = g.Id, Name = "" };
                g.Options.Add(existing);
            }

            existing.Name = Guard.Required(o.Name, "Option name", 120);
            existing.Price = Money.Round(o.Price);
            existing.IsActive = o.IsActive;
            existing.SortOrder = i++;
            keep.Add(existing.Id);
        }

        // Options are never hard-deleted because historical order lines reference them by id.
        foreach (var o in g.Options.Where(o => !keep.Contains(o.Id))) o.IsActive = false;
    }

    private static async Task<IResult> CreateItem(MenuItemRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var item = new MenuItem { Name = "" };
        await ApplyItemAsync(db, item, req, ct);
        db.MenuItems.Add(item);
        await audit.AddAsync("menu.item_created", "MenuItem", item.Id, new { item.Name, item.Price }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/menu/items/{item.Id}", ItemDto(item));
    }

    private static async Task<IResult> UpdateItem(Guid id, MenuItemRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var item = await db.MenuItems.Include(i => i.Variants).Include(i => i.ModifierGroups).FirstOrDefaultAsync(i => i.Id == id, ct)
                   ?? throw new NotFoundException("Menu item");
        var before = new { item.Name, item.Price, variants = item.Variants.Select(v => new { v.Name, v.Price }) };
        await ApplyItemAsync(db, item, req, ct);
        await audit.AddAsync("menu.item_updated", "MenuItem", item.Id,
            new { before, after = new { item.Name, item.Price, variants = item.Variants.Select(v => new { v.Name, v.Price }) } }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ItemDto(item));
    }

    private static async Task ApplyItemAsync(AppDbContext db, MenuItem item, MenuItemRequest req, CancellationToken ct)
    {
        if (!await db.MenuCategories.AnyAsync(c => c.Id == req.CategoryId, ct)) throw new NotFoundException("Category");
        if (req.TaxGroupId is { } tg && !await db.TaxGroups.AnyAsync(t => t.Id == tg, ct)) throw new NotFoundException("Tax group");
        if (!Enum.IsDefined(req.FoodType)) throw new DomainException("validation", "Unknown food type.");

        item.CategoryId = req.CategoryId;
        item.Name = Guard.Required(req.Name, "Name");
        item.Description = Guard.Optional(req.Description, "Description", 1000);
        item.Sku = Guard.Optional(req.Sku, "SKU", 60);
        item.Price = Money.Round(Guard.NonNegative(req.Price, "Price"));
        item.TaxGroupId = req.TaxGroupId;
        item.FoodType = req.FoodType;
        item.Station = Guard.Optional(req.Station, "Station", 60) ?? "Main";
        item.SortOrder = req.SortOrder;
        item.IsActive = req.IsActive;

        var keep = new HashSet<Guid>();
        var i = 0;
        foreach (var v in req.Variants ?? [])
        {
            var existing = v.Id is { } vid ? item.Variants.FirstOrDefault(x => x.Id == vid) : null;
            if (existing is null)
            {
                existing = new ItemVariant { MenuItemId = item.Id, Name = "" };
                item.Variants.Add(existing);
            }

            existing.Name = Guard.Required(v.Name, "Variant name", 120);
            existing.Price = Money.Round(Guard.NonNegative(v.Price, "Variant price"));
            existing.IsActive = v.IsActive;
            existing.SortOrder = i++;
            keep.Add(existing.Id);
        }

        foreach (var v in item.Variants.Where(v => !keep.Contains(v.Id))) v.IsActive = false;

        var groupIds = (req.ModifierGroupIds ?? []).Distinct().ToList();
        var validCount = await db.ModifierGroups.CountAsync(m => groupIds.Contains(m.Id), ct);
        if (validCount != groupIds.Count) throw new NotFoundException("Modifier group");
        db.RemoveRange(item.ModifierGroups);
        item.ModifierGroups.Clear();
        for (var s = 0; s < groupIds.Count; s++)
            item.ModifierGroups.Add(new MenuItemModifierGroup { MenuItemId = item.Id, ModifierGroupId = groupIds[s], SortOrder = s });
    }

    private static async Task<IResult> SetOverride(Guid outletId, Guid itemId, OverrideRequest req, AppDbContext db, AuditWriter audit,
        CancellationToken ct)
    {
        if (!await db.MenuItems.AnyAsync(i => i.Id == itemId, ct)) throw new NotFoundException("Menu item");
        if (req.PriceOverride is < 0) throw new DomainException("validation", "Price override cannot be negative.");
        var o = await db.OutletItemOverrides.FirstOrDefaultAsync(x => x.OutletId == outletId && x.MenuItemId == itemId, ct);
        if (o is null)
        {
            o = new OutletItemOverride { OutletId = outletId, MenuItemId = itemId };
            db.OutletItemOverrides.Add(o);
        }

        o.IsAvailable = req.IsAvailable;
        o.PriceOverride = req.PriceOverride is { } p ? Money.Round(p) : null;
        await audit.AddAsync("menu.outlet_override", "MenuItem", itemId, req, outletId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { o.MenuItemId, o.IsAvailable, o.PriceOverride });
    }

    internal static object ItemDto(MenuItem i) => new
    {
        i.Id,
        i.CategoryId,
        i.Name,
        i.Description,
        i.Sku,
        i.Price,
        i.TaxGroupId,
        i.FoodType,
        i.Station,
        i.SortOrder,
        i.IsActive,
        variants = i.Variants.OrderBy(v => v.SortOrder).Select(v => new { v.Id, v.Name, v.Price, v.IsActive }),
        modifierGroupIds = i.ModifierGroups.OrderBy(m => m.SortOrder).Select(m => m.ModifierGroupId),
    };
}
