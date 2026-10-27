using RestaurantOS.Domain.Identity;

namespace RestaurantOS.Api.Common;

public static class EndpointFilterExtensions
{
    /// <summary>
    /// Requires the route's <c>{outletId}</c> to be accessible with <paramref name="permission"/>.
    /// Non-GET requests additionally require a usable subscription.
    /// </summary>
    public static TBuilder RequireOutlet<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!Guid.TryParse(http.Request.RouteValues["outletId"]?.ToString(), out var outletId))
                return Results.NotFound();
            var access = http.RequestServices.GetRequiredService<AccessService>();
            await access.RequireOutletAsync(outletId, permission, http.RequestAborted);
            if (!HttpMethods.IsGet(http.Request.Method))
                await access.RequireActiveSubscriptionAsync(http.RequestAborted);
            return await next(ctx);
        });

    /// <summary>Requires a tenant-wide role granting <paramref name="permission"/>.</summary>
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder, Permission permission, bool requireSubscription = true)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var access = http.RequestServices.GetRequiredService<AccessService>();
            await access.RequireTenantAsync(permission, http.RequestAborted);
            if (requireSubscription && !HttpMethods.IsGet(http.Request.Method))
                await access.RequireActiveSubscriptionAsync(http.RequestAborted);
            return await next(ctx);
        });

    /// <summary>Requires any role assignment (outlet or tenant-wide) granting <paramref name="permission"/>.</summary>
    public static TBuilder RequireAny<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var access = http.RequestServices.GetRequiredService<AccessService>();
            await access.RequireAnyAsync(permission, http.RequestAborted);
            if (!HttpMethods.IsGet(http.Request.Method))
                await access.RequireActiveSubscriptionAsync(http.RequestAborted);
            return await next(ctx);
        });
}
