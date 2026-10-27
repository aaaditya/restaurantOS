using RestaurantOS.Api.Modules.Customers;
using RestaurantOS.Api.Modules.Identity;
using RestaurantOS.Api.Modules.Inventory;
using RestaurantOS.Api.Modules.Kitchen;
using RestaurantOS.Api.Modules.Menu;
using RestaurantOS.Api.Modules.Orders;
using RestaurantOS.Api.Modules.Payments;
using RestaurantOS.Api.Modules.Tables;
using RestaurantOS.Api.Modules.Tenancy;

namespace RestaurantOS.Api.Modules;

public static class ModuleRegistration
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<OutletMenuService>();
        services.AddScoped<KitchenService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<OrderService>();
        services.AddScoped<SettlementService>();
        return services;
    }

    public static void MapModules(this IEndpointRouteBuilder app, IConfiguration config)
    {
        app.MapAuthEndpoints();
        app.MapStaffEndpoints();
        app.MapOutletEndpoints();
        app.MapMenuEndpoints();
        app.MapTableEndpoints();
        app.MapOrderEndpoints();
        app.MapKitchenEndpoints();
        app.MapCashSessionEndpoints();
    }
}
