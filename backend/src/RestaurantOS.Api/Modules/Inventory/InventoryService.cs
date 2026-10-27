using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Realtime;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Inventory;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Inventory;

public sealed record StockMovement(Guid InventoryItemId, decimal Quantity, decimal UnitCost, string? Notes = null);

public sealed class InventoryService(AppDbContext db, ITenantContext tenant, IRealtimeNotifier notifier)
{
    public static class Sources
    {
        public const string Order = "order";
        public const string PurchaseOrder = "purchase_order";
        public const string Manual = "manual";
    }

    /// <summary>
    /// Appends ledger rows and updates balances atomically. Must run inside a transaction. The unique
    /// (source_type, source_id, item) index makes a second posting of the same source fail instead of double-counting.
    /// </summary>
    public async Task<List<StockLedgerEntry>> PostAsync(Guid outletId, StockMovementType type, string sourceType, string sourceId,
        IEnumerable<StockMovement> movements, Guid? userId, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant context required.");
        var entries = new List<StockLedgerEntry>();
        foreach (var m in movements.GroupBy(x => x.InventoryItemId)
                     .Select(g => new StockMovement(g.Key, g.Sum(x => x.Quantity), g.First().UnitCost, g.First().Notes)))
        {
            if (m.Quantity == 0) continue;
            var qty = Money.RoundQuantity(m.Quantity);
            var balances = await db.Database.SqlQuery<decimal>($"""
                INSERT INTO stock_balances (id, tenant_id, outlet_id, inventory_item_id, quantity, updated_at, created_at)
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {outletId}, {m.InventoryItemId}, {qty}, now(), now())
                ON CONFLICT (outlet_id, inventory_item_id)
                DO UPDATE SET quantity = stock_balances.quantity + EXCLUDED.quantity, updated_at = now()
                WHERE stock_balances.tenant_id = {tenantId}
                RETURNING quantity AS "Value"
                """).ToListAsync(ct);
            if (balances.Count == 0) throw new InvalidOperationException("Stock balance belongs to another tenant.");

            var entry = new StockLedgerEntry
            {
                OutletId = outletId,
                InventoryItemId = m.InventoryItemId,
                Type = type,
                Quantity = qty,
                UnitCost = m.UnitCost,
                SourceType = sourceType,
                SourceId = sourceId,
                Notes = m.Notes,
                CreatedByUserId = userId,
                BalanceAfter = balances[0],
            };
            db.StockLedger.Add(entry);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Deducts recipe ingredients for every active line of a settled order.</summary>
    public async Task<List<StockLedgerEntry>> PostConsumptionAsync(Order order, Guid? userId, CancellationToken ct)
    {
        var lines = order.ActiveLines.ToList();
        var itemIds = lines.Select(l => l.MenuItemId).Distinct().ToList();
        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Lines).Where(r => itemIds.Contains(r.MenuItemId)).ToListAsync(ct);
        if (recipes.Count == 0) return [];

        var ingredientIds = recipes.SelectMany(r => r.Lines).Select(l => l.InventoryItemId).Distinct().ToList();
        var costs = await db.InventoryItems.AsNoTracking().Where(i => ingredientIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.CostPerUnit, ct);

        var movements = new List<StockMovement>();
        foreach (var line in lines)
        {
            var recipe = recipes.FirstOrDefault(r => r.MenuItemId == line.MenuItemId && r.VariantId == line.VariantId && line.VariantId != null)
                         ?? recipes.FirstOrDefault(r => r.MenuItemId == line.MenuItemId && r.VariantId == null);
            if (recipe is null) continue;
            movements.AddRange(recipe.Lines.Select(rl =>
                new StockMovement(rl.InventoryItemId, -(rl.Quantity * line.Quantity), costs.GetValueOrDefault(rl.InventoryItemId))));
        }

        return await PostAsync(order.OutletId, StockMovementType.Consumption, Sources.Order, order.Id.ToString(), movements, userId, ct);
    }

    public Task PublishAsync(Guid outletId) => notifier.PublishAsync(outletId, RealtimeEvents.StockChanged, new { outletId });
}
