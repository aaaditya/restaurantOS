using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Auth;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Billing;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Billing;

public sealed record ChangePlanRequest(string PlanCode);

public sealed record ProviderSubscription(string CustomerRef, DateTimeOffset CurrentPeriodEndsAt);

/// <summary>Subscription billing provider. Only the mock exists until a real provider's credentials are supplied.</summary>
public interface IBillingProvider
{
    string Name { get; }
    Task<ProviderSubscription> SubscribeAsync(Tenant tenant, Plan plan, CancellationToken ct);
    Task CancelAsync(Tenant tenant, CancellationToken ct);
}

/// <summary>Activates plans immediately for one month without charging anything.</summary>
public sealed class MockBillingProvider(TimeProvider clock) : IBillingProvider
{
    public string Name => "mock";

    public Task<ProviderSubscription> SubscribeAsync(Tenant tenant, Plan plan, CancellationToken ct) =>
        Task.FromResult(new ProviderSubscription(tenant.BillingCustomerRef ?? $"mock_cus_{TokenService.GenerateSecret(9)}",
            clock.GetUtcNow().AddMonths(1)));

    public Task CancelAsync(Tenant tenant, CancellationToken ct) => Task.CompletedTask;
}

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/billing").WithTags("Billing").RequireAuthorization();
        g.MapGet("", Get).RequireTenant(Permission.BillingManage);
        // Lapsed tenants must still be able to resubscribe, so these skip the subscription write block.
        g.MapPost("/subscription", Subscribe).RequireTenant(Permission.BillingManage, requireSubscription: false);
        g.MapPost("/cancel", Cancel).RequireTenant(Permission.BillingManage, requireSubscription: false);
    }

    private static async Task<object> ViewAsync(AppDbContext db, Tenant tenant, IBillingProvider provider, TimeProvider clock, CancellationToken ct)
    {
        var outlets = await db.Outlets.CountAsync(o => o.IsActive, ct);
        var staff = await db.Users.CountAsync(u => u.IsActive, ct);
        return new
        {
            provider = provider.Name,
            current = new
            {
                tenant.PlanCode,
                status = tenant.SubscriptionStatus,
                tenant.TrialEndsAt,
                tenant.CurrentPeriodEndsAt,
                usable = tenant.IsSubscriptionUsable(clock.GetUtcNow()),
            },
            usage = new { outlets, staff },
            plans = PlanCatalog.All,
        };
    }

    private static async Task<IResult> Get(AppDbContext db, AccessService access, IBillingProvider provider, TimeProvider clock, CancellationToken ct)
    {
        var tenant = await access.GetTenantAsync(ct);
        return Results.Ok(await ViewAsync(db, tenant, provider, clock, ct));
    }

    private static async Task<IResult> Subscribe(ChangePlanRequest req, AppDbContext db, AccessService access, IBillingProvider provider,
        AuditWriter audit, TimeProvider clock, CancellationToken ct)
    {
        if (!PlanCatalog.Exists(req.PlanCode ?? "")) throw new DomainException("validation", "Unknown plan.");
        var plan = PlanCatalog.Get(req.PlanCode!);
        var outlets = await db.Outlets.CountAsync(o => o.IsActive, ct);
        var staff = await db.Users.CountAsync(u => u.IsActive, ct);
        if (outlets > plan.MaxOutlets)
            throw new DomainException("plan_limit", $"The {plan.Name} plan allows {plan.MaxOutlets} outlet(s); deactivate outlets first.");
        if (staff > plan.MaxStaff)
            throw new DomainException("plan_limit", $"The {plan.Name} plan allows {plan.MaxStaff} active staff; deactivate accounts first.");

        var me = await access.GetAsync(ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == me.TenantId, ct);
        var previous = new { tenant.PlanCode, tenant.SubscriptionStatus };
        var sub = await provider.SubscribeAsync(tenant, plan, ct);
        tenant.PlanCode = plan.Code;
        tenant.SubscriptionStatus = SubscriptionStatus.Active;
        tenant.BillingCustomerRef = sub.CustomerRef;
        tenant.CurrentPeriodEndsAt = sub.CurrentPeriodEndsAt;
        await audit.AddAsync("billing.subscribed", "Tenant", tenant.Id,
            new { from = previous.PlanCode, previousStatus = previous.SubscriptionStatus, to = plan.Code, provider = provider.Name }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, tenant, provider, clock, ct));
    }

    private static async Task<IResult> Cancel(AppDbContext db, AccessService access, IBillingProvider provider, AuditWriter audit,
        TimeProvider clock, CancellationToken ct)
    {
        var me = await access.GetAsync(ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == me.TenantId, ct);
        if (tenant.SubscriptionStatus == SubscriptionStatus.Cancelled)
            throw new ConflictException("already_cancelled", "The subscription is already cancelled.");
        await provider.CancelAsync(tenant, ct);
        tenant.SubscriptionStatus = SubscriptionStatus.Cancelled;
        await audit.AddAsync("billing.cancelled", "Tenant", tenant.Id, new { tenant.PlanCode, provider = provider.Name }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, tenant, provider, clock, ct));
    }
}
