using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Auth;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Billing;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Identity;

public sealed record RoleAssignmentDto(Role Role, Guid? OutletId);

public sealed record CreateStaffRequest(string Name, string Email, string Password, List<RoleAssignmentDto> Roles);

public sealed record UpdateStaffRequest(string Name, bool IsActive, List<RoleAssignmentDto> Roles);

public sealed record ResetPasswordRequest(string Password);

public static class StaffEndpoints
{
    public static void MapStaffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/staff").WithTags("Staff").RequireAuthorization().RequireTenant(Permission.StaffManage);
        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapPut("/{userId:guid}", Update);
        g.MapPost("/{userId:guid}/reset-password", ResetPassword);
    }

    private static async Task<IResult> List(AppDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Include(u => u.Roles).OrderBy(u => u.Name).ToListAsync(ct);
        return Results.Ok(users.Select(ToDto));
    }

    private static async Task<IResult> Create(CreateStaffRequest req, AppDbContext db, AccessService access, IPasswordHasher<User> hasher,
        AuditWriter audit, CancellationToken ct)
    {
        var me = await access.GetAsync(ct);
        var tenant = await access.GetTenantAsync(ct);
        var plan = PlanCatalog.Get(tenant.PlanCode);
        if (await db.Users.CountAsync(u => u.IsActive, ct) >= plan.MaxStaff)
            throw new DomainException("plan_limit", $"Your {plan.Name} plan allows up to {plan.MaxStaff} active staff accounts.");

        var email = Guard.Email(req.Email);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("email_taken", "An account with this email already exists.");

        var user = new User { Email = email, Name = Guard.Required(req.Name, "Name") };
        user.PasswordHash = hasher.HashPassword(user, Guard.Password(req.Password));
        await ApplyRolesAsync(db, me, user, req.Roles, ct);
        db.Users.Add(user);
        await audit.AddAsync("staff.created", "User", user.Id, new { user.Email, roles = req.Roles }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/staff/{user.Id}", ToDto(user));
    }

    private static async Task<IResult> Update(Guid userId, UpdateStaffRequest req, AppDbContext db, AccessService access,
        TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        var me = await access.GetAsync(ct);
        var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new NotFoundException("User");
        var targetIsOwner = user.Roles.Any(r => r.Role == Role.Owner);
        if (targetIsOwner && !me.IsOwner) throw new ForbiddenException("Only an owner can modify another owner.");
        if (user.Id == me.UserId && !req.IsActive) throw new DomainException("self_deactivate", "You cannot deactivate yourself.");

        var willBeOwner = req.Roles.Any(r => r.Role == Role.Owner);
        if (targetIsOwner && (!willBeOwner || !req.IsActive))
        {
            var otherOwners = await db.UserRoles.CountAsync(r => r.Role == Role.Owner && r.UserId != user.Id
                && db.Users.Any(u => u.Id == r.UserId && u.IsActive), ct);
            if (otherOwners == 0) throw new DomainException("last_owner", "The tenant must keep at least one active owner.");
        }

        user.Name = Guard.Required(req.Name, "Name");
        var wasActive = user.IsActive;
        user.IsActive = req.IsActive;
        db.UserRoles.RemoveRange(user.Roles);
        user.Roles.Clear();
        await ApplyRolesAsync(db, me, user, req.Roles, ct);
        await audit.AddAsync("staff.updated", "User", user.Id, new { user.IsActive, roles = req.Roles }, ct: ct);
        await db.SaveChangesAsync(ct);
        if (wasActive && !user.IsActive) await tokens.RevokeAllForUserAsync(user.Id, ct);
        return Results.Ok(ToDto(user));
    }

    private static async Task<IResult> ResetPassword(Guid userId, ResetPasswordRequest req, AppDbContext db, AccessService access,
        IPasswordHasher<User> hasher, TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        var me = await access.GetAsync(ct);
        var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User");
        if (user.Roles.Any(r => r.Role == Role.Owner) && !me.IsOwner) throw new ForbiddenException("Only an owner can reset an owner's password.");
        user.PasswordHash = hasher.HashPassword(user, Guard.Password(req.Password));
        await audit.AddAsync("staff.password_reset", "User", user.Id, ct: ct);
        await db.SaveChangesAsync(ct);
        await tokens.RevokeAllForUserAsync(user.Id, ct);
        return Results.NoContent();
    }

    private static async Task ApplyRolesAsync(AppDbContext db, UserAccess me, User user, List<RoleAssignmentDto>? roles, CancellationToken ct)
    {
        if (roles is null || roles.Count == 0) throw new DomainException("validation", "At least one role is required.");
        var outletIds = await db.Outlets.Select(o => o.Id).ToListAsync(ct);
        foreach (var r in roles.Distinct())
        {
            if (!Enum.IsDefined(r.Role)) throw new DomainException("validation", "Unknown role.");
            if (r.Role == Role.Owner && !me.IsOwner) throw new ForbiddenException("Only an owner can grant the owner role.");
            if (RolePermissions.IsTenantWideOnly(r.Role) && r.OutletId is not null)
                throw new DomainException("validation", $"{r.Role} must be assigned tenant-wide.");
            if (r.OutletId is { } oid && !outletIds.Contains(oid)) throw new NotFoundException("Outlet");
            user.Roles.Add(new UserRole { UserId = user.Id, OutletId = r.OutletId, Role = r.Role });
        }
    }

    private static object ToDto(User u) => new
    {
        u.Id,
        u.Name,
        u.Email,
        u.IsActive,
        u.LastLoginAt,
        roles = u.Roles.Select(r => new { r.Role, r.OutletId }),
    };
}
