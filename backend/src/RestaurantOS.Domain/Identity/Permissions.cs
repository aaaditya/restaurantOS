namespace RestaurantOS.Domain.Identity;

public enum Role
{
    Owner = 0,
    Manager = 1,
    Cashier = 2,
    Waiter = 3,
    Kitchen = 4,
}

public enum Permission
{
    OutletView,
    OutletManage,
    StaffManage,
    MenuView,
    MenuManage,
    TablesManage,
    OrderCreate,
    OrderDiscount,
    OrderVoidItem,
    OrderCancel,
    OrderSettle,
    KitchenView,
    KitchenUpdate,
    CashSessionManage,
    InventoryView,
    InventoryManage,
    PurchaseManage,
    CustomerView,
    CustomerManage,
    ReportsView,
    AuditView,
    BillingManage,
    IntegrationsManage,
}

public static class RolePermissions
{
    private static readonly Dictionary<Role, HashSet<Permission>> Matrix = new()
    {
        [Role.Owner] = [.. Enum.GetValues<Permission>()],
        [Role.Manager] =
        [
            Permission.OutletView, Permission.OutletManage, Permission.StaffManage, Permission.MenuView,
            Permission.MenuManage, Permission.TablesManage, Permission.OrderCreate, Permission.OrderDiscount,
            Permission.OrderVoidItem, Permission.OrderCancel, Permission.OrderSettle, Permission.KitchenView,
            Permission.KitchenUpdate, Permission.CashSessionManage, Permission.InventoryView,
            Permission.InventoryManage, Permission.PurchaseManage, Permission.CustomerView,
            Permission.CustomerManage, Permission.ReportsView, Permission.AuditView,
        ],
        [Role.Cashier] =
        [
            Permission.OutletView, Permission.MenuView, Permission.OrderCreate, Permission.OrderDiscount,
            Permission.OrderSettle, Permission.KitchenView, Permission.CashSessionManage,
            Permission.CustomerView, Permission.CustomerManage,
        ],
        [Role.Waiter] =
        [
            Permission.OutletView, Permission.MenuView, Permission.OrderCreate, Permission.KitchenView,
            Permission.CustomerView,
        ],
        [Role.Kitchen] =
        [
            Permission.OutletView, Permission.MenuView, Permission.KitchenView, Permission.KitchenUpdate,
            Permission.InventoryView,
        ],
    };

    public static bool Has(Role role, Permission permission) => Matrix[role].Contains(permission);

    public static IReadOnlySet<Permission> For(Role role) => Matrix[role];

    /// <summary>Roles that may only be granted tenant-wide.</summary>
    public static bool IsTenantWideOnly(Role role) => role == Role.Owner;
}
