using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Inventory;

public enum StockUnit
{
    Gram = 0,
    Kilogram = 1,
    Millilitre = 2,
    Litre = 3,
    Piece = 4,
}

public enum StockMovementType
{
    Opening = 0,
    Purchase = 1,
    Consumption = 2,
    Wastage = 3,
    Adjustment = 4,
    ConsumptionReversal = 5,
}

/// <summary>Raw material tracked in stock (tenant-wide definition; stock is per outlet).</summary>
public class InventoryItem : TenantEntity
{
    public required string Name { get; set; }
    public string? Sku { get; set; }
    public StockUnit Unit { get; set; }
    public decimal ReorderLevel { get; set; }
    /// <summary>Last known purchase cost per unit, used to value consumption and wastage.</summary>
    public decimal CostPerUnit { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Append-only stock movement. Unique per (tenant, source type, source id, item).</summary>
public class StockLedgerEntry : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid InventoryItemId { get; set; }
    public StockMovementType Type { get; set; }
    /// <summary>Signed quantity: positive adds stock, negative removes it.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public string? Notes { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public decimal BalanceAfter { get; set; }
}

/// <summary>Projection of the ledger: current quantity per outlet and item.</summary>
public class StockBalance : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid InventoryItemId { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Ingredients consumed when one unit of a menu item (optionally a specific variant) is sold.</summary>
public class Recipe : TenantEntity
{
    public Guid MenuItemId { get; set; }
    public Guid? VariantId { get; set; }
    public List<RecipeLine> Lines { get; set; } = [];
}

public class RecipeLine : Entity
{
    public Guid RecipeId { get; set; }
    public Guid InventoryItemId { get; set; }
    public decimal Quantity { get; set; }
}

public class Supplier : TenantEntity
{
    public required string Name { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gstin { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum PurchaseOrderStatus
{
    Draft = 0,
    Ordered = 1,
    Received = 2,
    Cancelled = 3,
}

public class PurchaseOrder : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid SupplierId { get; set; }
    public long Number { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public string? Notes { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset? OrderedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];

    public void Recalculate() => Total = Money.Round(Lines.Sum(l => Money.Round(l.Quantity * l.UnitCost)));
}

public class PurchaseOrderLine : Entity
{
    public Guid PurchaseOrderId { get; set; }
    public Guid InventoryItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal? ReceivedQuantity { get; set; }
}
