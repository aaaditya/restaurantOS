using Microsoft.EntityFrameworkCore;
using RestaurantOS.Domain.Audit;
using RestaurantOS.Domain.Common;
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

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    /// <summary>Referenced by query filters; EF parameterises it per query.</summary>
    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Outlet> Outlets => Set<Outlet>();
    public DbSet<OutletCounter> OutletCounters => Set<OutletCounter>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<TaxGroup> TaxGroups => Set<TaxGroup>();
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<MenuItemModifierGroup> MenuItemModifierGroups => Set<MenuItemModifierGroup>();
    public DbSet<OutletItemOverride> OutletItemOverrides => Set<OutletItemOverride>();
    public DbSet<DiningTable> DiningTables => Set<DiningTable>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Kot> Kots => Set<Kot>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<StockLedgerEntry> StockLedger => Set<StockLedgerEntry>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<InboundEvent> InboundEvents => Set<InboundEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Money values are rounded to 2 dp in code (Money.Round); 4 dp storage also fits rates and quantities.
        builder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in b.Model.GetEntityTypes())
        {
            // Ids are assigned in code (UUIDv7), so entities discovered through navigations are always tracked as Added.
            if (!entityType.IsOwned() && typeof(Entity).IsAssignableFrom(entityType.ClrType))
                b.Entity(entityType.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();

            if (entityType.IsOwned() || !typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType)) continue;
            typeof(AppDbContext)
                .GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [b]);
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantScoped
    {
        b.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<T>().HasIndex(e => e.TenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardTenant()
    {
        var current = tenant.TenantId;
        foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = current ?? throw new InvalidOperationException(
                    $"Cannot insert {entry.Entity.GetType().Name} without a tenant context.");
            }

            if (current is not null
                && (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                && entry.Entity.TenantId != current)
            {
                throw new InvalidOperationException(
                    $"Cross-tenant write blocked for {entry.Entity.GetType().Name}.");
            }
        }
    }
}
