using Microsoft.EntityFrameworkCore;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Common;

public sealed record UserAccess(Guid UserId, Guid TenantId, string Name, string Email, IReadOnlyList<UserRole> Roles)
{
    public IEnumerable<Role> TenantWideRoles => Roles.Where(r => r.OutletId is null).Select(r => r.Role);

    public IEnumerable<Role> RolesFor(Guid outletId) =>
        Roles.Where(r => r.OutletId is null || r.OutletId == outletId).Select(r => r.Role);

    public bool HasTenant(Permission p) => TenantWideRoles.Any(r => RolePermissions.Has(r, p));

    public bool HasOutlet(Guid outletId, Permission p) => RolesFor(outletId).Any(r => RolePermissions.Has(r, p));

    public bool CanAccessOutlet(Guid outletId) => RolesFor(outletId).Any();

    public bool IsOwner => TenantWideRoles.Contains(Role.Owner);
}

/// <summary>Loads the caller's roles once per scope and answers authorization questions.</summary>
public sealed class AccessService(AppDbContext db, ITenantContext tenant, TimeProvider clock)
{
    private UserAccess? _access;
    private Tenant? _tenant;

    public async Task<UserAccess> GetAsync(CancellationToken ct = default)
    {
        if (_access is not null) return _access;
        var userId = tenant.UserId ?? throw new UnauthorizedAccessException();
        var user = await db.Users.AsNoTracking().Include(u => u.Roles)
                       .FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new UnauthorizedAccessException();
        if (!user.IsActive) throw new UnauthorizedAccessException();
        _access = new UserAccess(user.Id, user.TenantId, user.Name, user.Email, user.Roles);
        return _access;
    }

    public async Task<Tenant> GetTenantAsync(CancellationToken ct = default)
    {
        if (_tenant is not null) return _tenant;
        var id = tenant.TenantId ?? throw new UnauthorizedAccessException();
        _tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
                  ?? throw new UnauthorizedAccessException();
        return _tenant;
    }

    /// <summary>
    /// Verifies the outlet belongs to the caller's tenant (404 otherwise, so other tenants' ids cannot be probed)
    /// and that one of the caller's roles for that outlet grants <paramref name="permission"/> (403 otherwise).
    /// </summary>
    public async Task<UserAccess> RequireOutletAsync(Guid outletId, Permission permission, CancellationToken ct = default)
    {
        var access = await GetAsync(ct);
        var exists = await db.Outlets.AnyAsync(o => o.Id == outletId, ct);
        if (!exists || !access.CanAccessOutlet(outletId)) throw new NotFoundException("Outlet");
        if (!access.HasOutlet(outletId, permission)) throw new ForbiddenException();
        return access;
    }

    public async Task<UserAccess> RequireTenantAsync(Permission permission, CancellationToken ct = default)
    {
        var access = await GetAsync(ct);
        if (!access.HasTenant(permission)) throw new ForbiddenException();
        return access;
    }

    /// <summary>Passes when any of the caller's assignments grants the permission.</summary>
    public async Task<UserAccess> RequireAnyAsync(Permission permission, CancellationToken ct = default)
    {
        var access = await GetAsync(ct);
        if (!access.Roles.Any(r => RolePermissions.Has(r.Role, permission))) throw new ForbiddenException();
        return access;
    }

    public async Task RequireActiveSubscriptionAsync(CancellationToken ct = default)
    {
        var t = await GetTenantAsync(ct);
        if (!t.IsSubscriptionUsable(clock.GetUtcNow())) throw new SubscriptionInactiveException();
    }
}

public sealed class SubscriptionInactiveException()
    : Exception("Your subscription is inactive. Renew it under Settings → Billing to continue.");
