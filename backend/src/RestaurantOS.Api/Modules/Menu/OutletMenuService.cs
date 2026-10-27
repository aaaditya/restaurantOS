using Microsoft.EntityFrameworkCore;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Menu;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Menu;

public sealed record LineSelection(Guid MenuItemId, Guid? VariantId, decimal Quantity, List<Guid>? ModifierOptionIds, string? Notes);

/// <summary>Builds the sellable menu for an outlet and resolves priced order lines (pricing snapshots).</summary>
public sealed class OutletMenuService(AppDbContext db)
{
    public async Task<object> GetAsync(Guid outletId, bool includeUnavailable, CancellationToken ct)
    {
        var data = await LoadAsync(outletId, ct);
        var categories = await db.MenuCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

        var items = data.Items
            .Where(i => includeUnavailable || data.IsAvailable(i.Id))
            .Select(i => new
            {
                i.Id,
                i.CategoryId,
                i.Name,
                i.Description,
                i.FoodType,
                i.Station,
                price = data.EffectivePrice(i),
                basePrice = i.Price,
                priceOverride = data.Overrides.TryGetValue(i.Id, out var ov) ? ov.PriceOverride : null,
                isAvailable = data.IsAvailable(i.Id),
                taxRate = i.TaxGroupId is { } tg && data.TaxGroups.TryGetValue(tg, out var g) ? g.TotalRate : 0m,
                variants = i.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => new { v.Id, v.Name, v.Price }),
                modifierGroups = i.ModifierGroups.OrderBy(m => m.SortOrder)
                    .Where(m => data.ModifierGroups.ContainsKey(m.ModifierGroupId))
                    .Select(m => data.ModifierGroups[m.ModifierGroupId])
                    .Select(m => new
                    {
                        m.Id,
                        m.Name,
                        m.MinSelect,
                        m.MaxSelect,
                        options = m.Options.Where(o => o.IsActive).OrderBy(o => o.SortOrder).Select(o => new { o.Id, o.Name, o.Price }),
                    }),
            })
            .ToList();

        var usedCategories = items.Select(i => i.CategoryId).ToHashSet();
        return new
        {
            categories = categories.Where(c => usedCategories.Contains(c.Id)).Select(c => new { c.Id, c.Name }),
            items,
        };
    }

    /// <summary>Validates a selection against the live menu and produces an order line with price/tax snapshots.</summary>
    public async Task<List<OrderLine>> ResolveLinesAsync(Guid outletId, IEnumerable<LineSelection> selections, Guid? userId, CancellationToken ct)
    {
        var data = await LoadAsync(outletId, ct);
        var lines = new List<OrderLine>();
        foreach (var s in selections)
        {
            if (s.Quantity <= 0 || s.Quantity > 999) throw new DomainException("invalid_quantity", "Quantity must be between 0 and 999.");
            if (decimal.Round(s.Quantity, 3) != s.Quantity) throw new DomainException("invalid_quantity", "Quantity has too many decimals.");
            var item = data.Items.FirstOrDefault(i => i.Id == s.MenuItemId) ?? throw new NotFoundException("Menu item");
            if (!data.IsAvailable(item.Id)) throw new DomainException("item_unavailable", $"{item.Name} is currently unavailable.");

            var activeVariants = item.Variants.Where(v => v.IsActive).ToList();
            ItemVariant? variant = null;
            if (activeVariants.Count > 0)
            {
                if (s.VariantId is null) throw new DomainException("variant_required", $"Choose a variant for {item.Name}.");
                variant = activeVariants.FirstOrDefault(v => v.Id == s.VariantId) ?? throw new NotFoundException("Variant");
            }
            else if (s.VariantId is not null)
            {
                throw new NotFoundException("Variant");
            }

            var optionIds = (s.ModifierOptionIds ?? []).Distinct().ToList();
            var modifiers = new List<LineModifier>();
            var groups = item.ModifierGroups.Where(m => data.ModifierGroups.ContainsKey(m.ModifierGroupId))
                .Select(m => data.ModifierGroups[m.ModifierGroupId]).ToList();
            var matched = 0;
            foreach (var group in groups)
            {
                var chosen = group.Options.Where(o => o.IsActive && optionIds.Contains(o.Id)).ToList();
                if (chosen.Count < group.MinSelect || chosen.Count > group.MaxSelect)
                    throw new DomainException("invalid_modifiers",
                        $"{group.Name}: choose between {group.MinSelect} and {group.MaxSelect} option(s).");
                matched += chosen.Count;
                modifiers.AddRange(chosen.Select(o => new LineModifier { OptionId = o.Id, GroupName = group.Name, Name = o.Name, Price = o.Price }));
            }

            if (matched != optionIds.Count) throw new DomainException("invalid_modifiers", "One or more modifiers are not valid for this item.");

            var basePrice = variant?.Price ?? data.EffectivePrice(item);
            var taxes = item.TaxGroupId is { } tg && data.TaxGroups.TryGetValue(tg, out var taxGroup)
                ? taxGroup.Components.OrderBy(c => c.SortOrder).Select(c => new LineTax { Code = c.Code, RatePercent = c.RatePercent }).ToList()
                : [];

            lines.Add(new OrderLine
            {
                MenuItemId = item.Id,
                VariantId = variant?.Id,
                ItemName = item.Name,
                VariantName = variant?.Name,
                Station = item.Station,
                UnitPrice = Money.Round(basePrice + modifiers.Sum(m => m.Price)),
                Quantity = s.Quantity,
                Modifiers = modifiers,
                TaxRates = taxes,
                Notes = string.IsNullOrWhiteSpace(s.Notes) ? null : s.Notes.Trim()[..Math.Min(s.Notes.Trim().Length, 300)],
                AddedByUserId = userId,
            });
        }

        return lines;
    }

    private async Task<MenuData> LoadAsync(Guid outletId, CancellationToken ct)
    {
        var items = await db.MenuItems.AsNoTracking()
            .Include(i => i.Variants).Include(i => i.ModifierGroups)
            .Where(i => i.IsActive && db.MenuCategories.Any(c => c.Id == i.CategoryId && c.IsActive))
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Name)
            .AsSplitQuery()
            .ToListAsync(ct);
        var groups = await db.ModifierGroups.AsNoTracking().Include(m => m.Options).Where(m => m.IsActive).ToDictionaryAsync(m => m.Id, ct);
        var taxGroups = await db.TaxGroups.AsNoTracking().Include(t => t.Components).ToDictionaryAsync(t => t.Id, ct);
        var overrides = await db.OutletItemOverrides.AsNoTracking().Where(o => o.OutletId == outletId).ToDictionaryAsync(o => o.MenuItemId, ct);
        return new MenuData(items, groups, taxGroups, overrides);
    }

    private sealed record MenuData(
        List<MenuItem> Items,
        Dictionary<Guid, ModifierGroup> ModifierGroups,
        Dictionary<Guid, TaxGroup> TaxGroups,
        Dictionary<Guid, OutletItemOverride> Overrides)
    {
        public bool IsAvailable(Guid itemId) => !Overrides.TryGetValue(itemId, out var o) || o.IsAvailable;

        public decimal EffectivePrice(MenuItem item) =>
            Overrides.TryGetValue(item.Id, out var o) && o.PriceOverride is { } p ? p : item.Price;
    }
}
