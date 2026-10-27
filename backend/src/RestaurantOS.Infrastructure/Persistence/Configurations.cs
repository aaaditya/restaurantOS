using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RestaurantOS.Domain.Audit;
using RestaurantOS.Domain.Customers;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Integrations;
using RestaurantOS.Domain.Inventory;
using RestaurantOS.Domain.Kitchen;
using RestaurantOS.Domain.Menu;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Payments;
using RestaurantOS.Domain.Tables;
using RestaurantOS.Domain.Tenancy;

namespace RestaurantOS.Infrastructure.Persistence;

internal sealed class TenantConfig : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Slug).HasMaxLength(80);
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.PlanCode).HasMaxLength(40);
    }
}

internal sealed class OutletConfig : IEntityTypeConfiguration<Outlet>
{
    public void Configure(EntityTypeBuilder<Outlet> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Code).HasMaxLength(40);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.InvoicePrefix).HasMaxLength(20);
        b.Property(x => x.Gstin).HasMaxLength(20);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.TimeZone).HasMaxLength(60);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
    }
}

internal sealed class OutletCounterConfig : IEntityTypeConfiguration<OutletCounter>
{
    public void Configure(EntityTypeBuilder<OutletCounter> b)
    {
        b.HasKey(x => new { x.OutletId, x.Name });
        b.Property(x => x.Name).HasMaxLength(60);
        b.HasOne<Outlet>().WithMany().HasForeignKey(x => x.OutletId);
    }
}

internal sealed class UserConfig : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(x => x.Email).HasMaxLength(256);
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200);
        b.HasMany(x => x.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
    }
}

internal sealed class UserRoleConfig : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.HasIndex(x => new { x.UserId, x.OutletId, x.Role }).IsUnique().AreNullsDistinct(false);
        b.HasOne<Outlet>().WithMany().HasForeignKey(x => x.OutletId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RefreshTokenConfig : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.FamilyId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TaxGroupConfig : IEntityTypeConfiguration<TaxGroup>
{
    public void Configure(EntityTypeBuilder<TaxGroup> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasMany(x => x.Components).WithOne().HasForeignKey(c => c.TaxGroupId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(x => x.TotalRate);
    }
}

internal sealed class TaxComponentConfig : IEntityTypeConfiguration<TaxComponent>
{
    public void Configure(EntityTypeBuilder<TaxComponent> b)
    {
        b.ToTable("tax_components");
        b.Property(x => x.Code).HasMaxLength(20);
    }
}

internal sealed class MenuCategoryConfig : IEntityTypeConfiguration<MenuCategory>
{
    public void Configure(EntityTypeBuilder<MenuCategory> b) => b.Property(x => x.Name).HasMaxLength(120);
}

internal sealed class MenuItemConfig : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Sku).HasMaxLength(60);
        b.Property(x => x.Station).HasMaxLength(60);
        b.HasOne<MenuCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxGroup>().WithMany().HasForeignKey(x => x.TaxGroupId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Variants).WithOne().HasForeignKey(v => v.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.ModifierGroups).WithOne().HasForeignKey(m => m.MenuItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ItemVariantConfig : IEntityTypeConfiguration<ItemVariant>
{
    public void Configure(EntityTypeBuilder<ItemVariant> b)
    {
        b.ToTable("item_variants");
        b.Property(x => x.Name).HasMaxLength(120);
    }
}

internal sealed class ModifierGroupConfig : IEntityTypeConfiguration<ModifierGroup>
{
    public void Configure(EntityTypeBuilder<ModifierGroup> b)
    {
        b.Property(x => x.Name).HasMaxLength(120);
        b.HasMany(x => x.Options).WithOne().HasForeignKey(o => o.ModifierGroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ModifierOptionConfig : IEntityTypeConfiguration<ModifierOption>
{
    public void Configure(EntityTypeBuilder<ModifierOption> b)
    {
        b.ToTable("modifier_options");
        b.Property(x => x.Name).HasMaxLength(120);
    }
}

internal sealed class MenuItemModifierGroupConfig : IEntityTypeConfiguration<MenuItemModifierGroup>
{
    public void Configure(EntityTypeBuilder<MenuItemModifierGroup> b)
    {
        b.HasKey(x => new { x.MenuItemId, x.ModifierGroupId });
        b.HasOne<ModifierGroup>().WithMany().HasForeignKey(x => x.ModifierGroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutletItemOverrideConfig : IEntityTypeConfiguration<OutletItemOverride>
{
    public void Configure(EntityTypeBuilder<OutletItemOverride> b)
    {
        b.HasIndex(x => new { x.OutletId, x.MenuItemId }).IsUnique();
        b.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Outlet>().WithMany().HasForeignKey(x => x.OutletId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DiningTableConfig : IEntityTypeConfiguration<DiningTable>
{
    public void Configure(EntityTypeBuilder<DiningTable> b)
    {
        b.Property(x => x.Name).HasMaxLength(60);
        b.Property(x => x.Area).HasMaxLength(60);
        b.Property(x => x.QrToken).HasMaxLength(64);
        b.HasIndex(x => x.QrToken).IsUnique();
        b.HasIndex(x => new { x.OutletId, x.Name }).IsUnique();
        b.HasOne<Outlet>().WithMany().HasForeignKey(x => x.OutletId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrderConfig : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.OutletId, x.ClientOrderId }).IsUnique().HasFilter("client_order_id IS NOT NULL");
        b.HasIndex(x => new { x.OutletId, x.OrderNumber }).IsUnique();
        b.HasIndex(x => new { x.OutletId, x.InvoiceNumber }).IsUnique().HasFilter("invoice_number IS NOT NULL");
        b.HasIndex(x => new { x.OutletId, x.Status });
        b.HasIndex(x => new { x.OutletId, x.BusinessDate });
        b.HasIndex(x => new { x.OutletId, x.Source, x.ExternalRef }).IsUnique().HasFilter("external_ref IS NOT NULL");
        b.Property(x => x.InvoiceNumber).HasMaxLength(60);
        b.Property(x => x.TableName).HasMaxLength(60);
        b.Property(x => x.CustomerName).HasMaxLength(200);
        b.Property(x => x.CustomerPhone).HasMaxLength(20);
        b.Property(x => x.ExternalRef).HasMaxLength(120);
        b.OwnsMany(x => x.TaxBreakdown, nb => nb.ToJson());
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Outlet>().WithMany().HasForeignKey(x => x.OutletId);
        b.Ignore(x => x.ActiveLines);
        b.Ignore(x => x.IsEditable);
    }
}

internal sealed class OrderLineConfig : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.ToTable("order_lines");
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.VariantName).HasMaxLength(120);
        b.Property(x => x.Station).HasMaxLength(60);
        b.OwnsMany(x => x.Modifiers, nb => nb.ToJson());
        b.OwnsMany(x => x.TaxRates, nb => nb.ToJson());
        b.HasIndex(x => x.MenuItemId);
    }
}

internal sealed class KotConfig : IEntityTypeConfiguration<Kot>
{
    public void Configure(EntityTypeBuilder<Kot> b)
    {
        b.Property(x => x.KotNumber).HasMaxLength(40);
        b.Property(x => x.Station).HasMaxLength(60);
        b.HasIndex(x => new { x.OutletId, x.Status });
        b.HasIndex(x => x.OrderId);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.KotId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
    }
}

internal sealed class KotItemConfig : IEntityTypeConfiguration<KotItem>
{
    public void Configure(EntityTypeBuilder<KotItem> b)
    {
        b.ToTable("kot_items");
        b.Property(x => x.Name).HasMaxLength(200);
    }
}

internal sealed class SettlementConfig : IEntityTypeConfiguration<Settlement>
{
    public void Configure(EntityTypeBuilder<Settlement> b)
    {
        b.Property(x => x.IdempotencyKey).HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => x.OrderId).IsUnique();
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
    }
}

internal sealed class PaymentConfig : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(x => x.Reference).HasMaxLength(120);
        b.HasIndex(x => new { x.OutletId, x.BusinessDate });
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.CashSessionId);
        b.HasOne<Settlement>().WithMany().HasForeignKey(x => x.SettlementId);
    }
}

internal sealed class CashSessionConfig : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> b)
    {
        // At most one open session per outlet.
        b.HasIndex(x => x.OutletId).IsUnique().HasFilter("status = 0").HasDatabaseName("ix_cash_sessions_one_open_per_outlet");
        b.HasIndex(x => new { x.OutletId, x.OpenedAt });
    }
}

internal sealed class PaymentIntentConfig : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> b)
    {
        b.HasIndex(x => new { x.Provider, x.ProviderRef }).IsUnique();
        b.HasIndex(x => x.OrderId);
    }
}

internal sealed class InventoryItemConfig : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Sku).HasMaxLength(60);
    }
}

internal sealed class StockLedgerConfig : IEntityTypeConfiguration<StockLedgerEntry>
{
    public void Configure(EntityTypeBuilder<StockLedgerEntry> b)
    {
        b.Property(x => x.SourceType).HasMaxLength(40);
        b.Property(x => x.SourceId).HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceId, x.InventoryItemId }).IsUnique();
        b.HasIndex(x => new { x.OutletId, x.InventoryItemId, x.CreatedAt });
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockBalanceConfig : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> b)
    {
        b.HasIndex(x => new { x.OutletId, x.InventoryItemId }).IsUnique();
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RecipeConfig : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> b)
    {
        b.HasIndex(x => new { x.MenuItemId, x.VariantId }).IsUnique().AreNullsDistinct(false);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.RecipeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RecipeLineConfig : IEntityTypeConfiguration<RecipeLine>
{
    public void Configure(EntityTypeBuilder<RecipeLine> b)
    {
        b.ToTable("recipe_lines");
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SupplierConfig : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b) => b.Property(x => x.Name).HasMaxLength(200);
}

internal sealed class PurchaseOrderConfig : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.HasIndex(x => new { x.OutletId, x.Number }).IsUnique();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseOrderLineConfig : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable("purchase_order_lines");
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerConfig : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(256);
        b.HasIndex(x => new { x.TenantId, x.Phone }).IsUnique();
    }
}

internal sealed class LoyaltyTransactionConfig : IEntityTypeConfiguration<LoyaltyTransaction>
{
    public void Configure(EntityTypeBuilder<LoyaltyTransaction> b)
    {
        b.HasIndex(x => x.CustomerId);
        b.HasIndex(x => new { x.OrderId, x.Type }).IsUnique().HasFilter("order_id IS NOT NULL");
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(x => x.Action).HasMaxLength(80);
        b.Property(x => x.EntityType).HasMaxLength(80);
        b.Property(x => x.EntityId).HasMaxLength(100);
        b.Property(x => x.Data).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}

internal sealed class ApiKeyConfig : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.Property(x => x.Prefix).HasMaxLength(20);
        b.HasIndex(x => x.Prefix).IsUnique();
        b.Property(x => x.SecretHash).HasMaxLength(128);
    }
}

internal sealed class InboundEventConfig : IEntityTypeConfiguration<InboundEvent>
{
    public void Configure(EntityTypeBuilder<InboundEvent> b)
    {
        b.HasIndex(x => new { x.TenantId, x.Provider, x.ExternalId }).IsUnique();
        b.Property(x => x.Payload).HasColumnType("jsonb");
    }
}
