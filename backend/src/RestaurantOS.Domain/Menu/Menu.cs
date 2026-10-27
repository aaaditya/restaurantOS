using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Menu;

public enum FoodType
{
    Veg = 0,
    NonVeg = 1,
    Egg = 2,
    NotApplicable = 3,
}

public class TaxGroup : TenantEntity
{
    public required string Name { get; set; }
    public List<TaxComponent> Components { get; set; } = [];
    public bool IsActive { get; set; } = true;

    public decimal TotalRate => Components.Sum(c => c.RatePercent);
}

public class TaxComponent : Entity
{
    public Guid TaxGroupId { get; set; }
    public required string Code { get; set; }
    public decimal RatePercent { get; set; }
    public int SortOrder { get; set; }
}

public class MenuCategory : TenantEntity
{
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class MenuItem : TenantEntity
{
    public Guid CategoryId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Sku { get; set; }
    /// <summary>Price when the item has no variants. Ignored when variants exist.</summary>
    public decimal Price { get; set; }
    public Guid? TaxGroupId { get; set; }
    public FoodType FoodType { get; set; } = FoodType.Veg;
    /// <summary>Kitchen station used to route KOTs, e.g. "Main", "Bar", "Tandoor".</summary>
    public string Station { get; set; } = "Main";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ItemVariant> Variants { get; set; } = [];
    public List<MenuItemModifierGroup> ModifierGroups { get; set; } = [];
}

public class ItemVariant : Entity
{
    public Guid MenuItemId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ModifierGroup : TenantEntity
{
    public required string Name { get; set; }
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; } = 1;
    public List<ModifierOption> Options { get; set; } = [];
    public bool IsActive { get; set; } = true;

    public void Validate()
    {
        if (MinSelect < 0 || MaxSelect < 1 || MinSelect > MaxSelect)
            throw new DomainException("invalid_modifier_group", "Modifier group selection limits are invalid.");
    }
}

public class ModifierOption : Entity
{
    public Guid ModifierGroupId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class MenuItemModifierGroup
{
    public Guid MenuItemId { get; set; }
    public Guid ModifierGroupId { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Per-outlet availability and optional price override for an item or variant.</summary>
public class OutletItemOverride : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid MenuItemId { get; set; }
    public bool IsAvailable { get; set; } = true;
    public decimal? PriceOverride { get; set; }
}
