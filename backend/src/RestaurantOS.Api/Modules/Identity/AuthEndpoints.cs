using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Auth;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Modules.Menu;
using RestaurantOS.Domain.Billing;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Identity;

public sealed record SignupRequest(
    string RestaurantName,
    string? OutletName,
    string OwnerName,
    string Email,
    string Password,
    string? Phone,
    bool SampleMenu = false);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth").RequireRateLimiting("auth");

        auth.MapPost("/signup", Signup).AllowAnonymous();
        auth.MapPost("/login", Login).AllowAnonymous();
        auth.MapPost("/refresh", Refresh).AllowAnonymous();
        auth.MapPost("/logout", Logout).AllowAnonymous();

        var me = app.MapGroup("/api/me").WithTags("Auth").RequireAuthorization();
        me.MapGet("", GetMe);
        me.MapPost("/password", ChangePassword);
    }

    private static async Task<IResult> Signup(SignupRequest req, AppDbContext db, TokenService tokens,
        IPasswordHasher<User> hasher, TimeProvider clock, CancellationToken ct)
    {
        var restaurantName = Guard.Required(req.RestaurantName, "Restaurant name");
        var ownerName = Guard.Required(req.OwnerName, "Owner name");
        var email = Guard.Email(req.Email);
        var password = Guard.Password(req.Password);
        var outletName = Guard.Optional(req.OutletName, "Outlet name", 200) ?? restaurantName;

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("email_taken", "An account with this email already exists.");

        var now = clock.GetUtcNow();
        var tenant = new Tenant
        {
            Name = restaurantName,
            Slug = $"{Slugify(restaurantName)}-{RandomCode(5)}",
            PlanCode = PlanCatalog.Starter.Code,
            SubscriptionStatus = SubscriptionStatus.Trialing,
            TrialEndsAt = now.AddDays(PlanCatalog.TrialDays),
        };
        var outlet = new Outlet
        {
            TenantId = tenant.Id,
            Name = outletName,
            Code = RandomCode(8),
            Phone = Guard.Optional(req.Phone, "Phone", 20),
            InvoicePrefix = InvoicePrefixFrom(outletName),
        };
        var user = new User { TenantId = tenant.Id, Email = email, Name = ownerName };
        user.PasswordHash = hasher.HashPassword(user, password);
        user.Roles.Add(new UserRole { TenantId = tenant.Id, UserId = user.Id, Role = Role.Owner });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Tenants.Add(tenant);
        db.Outlets.Add(outlet);
        db.Users.Add(user);
        MenuSeeder.AddDefaults(db, tenant.Id, req.SampleMenu);
        db.AuditLogs.Add(new Domain.Audit.AuditLog
        {
            TenantId = tenant.Id, UserId = user.Id, UserName = user.Name, Action = "tenant.signup", EntityType = "Tenant",
            EntityId = tenant.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        var pair = await tokens.IssueAsync(user, ct: ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new { tokens = pair, tenantId = tenant.Id, outletId = outlet.Id });
    }

    private static async Task<IResult> Login(LoginRequest req, AppDbContext db, TokenService tokens,
        IPasswordHasher<User> hasher, TimeProvider clock, CancellationToken ct)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email, ct);
        // Same response for unknown email, wrong password and inactive user to avoid account enumeration.
        if (user is null || !user.IsActive || string.IsNullOrEmpty(req.Password))
            return InvalidCredentials();

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (result == PasswordVerificationResult.Failed) return InvalidCredentials();
        if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, req.Password);

        user.LastLoginAt = clock.GetUtcNow();
        var pair = await tokens.IssueAsync(user, ct: ct);
        return Results.Ok(new { tokens = pair });
    }

    private static async Task<IResult> Refresh(RefreshRequest req, TokenService tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RefreshToken)) return InvalidCredentials();
        var pair = await tokens.RefreshAsync(req.RefreshToken, ct);
        return pair is null ? InvalidCredentials() : Results.Ok(new { tokens = pair });
    }

    private static async Task<IResult> Logout(RefreshRequest req, TokenService tokens, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.RefreshToken)) await tokens.RevokeAsync(req.RefreshToken, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMe(AccessService access, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await access.GetAsync(ct);
        var tenant = await access.GetTenantAsync(ct);
        var outlets = await db.Outlets.AsNoTracking().Where(o => o.IsActive).OrderBy(o => o.CreatedAt).ToListAsync(ct);
        var visible = outlets.Where(o => me.CanAccessOutlet(o.Id)).Select(o => new
        {
            o.Id,
            o.Name,
            o.Code,
            roles = me.RolesFor(o.Id).Distinct(),
            permissions = me.RolesFor(o.Id).SelectMany(RolePermissions.For).Distinct().Order(),
        });

        return Results.Ok(new
        {
            user = new { id = me.UserId, me.Name, me.Email },
            tenant = new
            {
                tenant.Id,
                tenant.Name,
                tenant.PlanCode,
                tenant.SubscriptionStatus,
                tenant.TrialEndsAt,
                subscriptionUsable = tenant.IsSubscriptionUsable(clock.GetUtcNow()),
            },
            tenantRoles = me.TenantWideRoles.Distinct(),
            tenantPermissions = me.TenantWideRoles.SelectMany(RolePermissions.For).Distinct().Order(),
            outlets = visible,
        });
    }

    private static async Task<IResult> ChangePassword(ChangePasswordRequest req, AccessService access, AppDbContext db,
        IPasswordHasher<User> hasher, TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        var me = await access.GetAsync(ct);
        var user = await db.Users.FirstAsync(u => u.Id == me.UserId, ct);
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
            throw new DomainException("invalid_password", "Current password is incorrect.");
        user.PasswordHash = hasher.HashPassword(user, Guard.Password(req.NewPassword));
        await audit.AddAsync("user.password_changed", "User", user.Id, ct: ct);
        await db.SaveChangesAsync(ct);
        await tokens.RevokeAllForUserAsync(user.Id, ct);
        return Results.NoContent();
    }

    private static IResult InvalidCredentials() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_credentials" });

    internal static string Slugify(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "restaurant" : slug;
    }

    internal static string RandomCode(int length)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(length, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[System.Security.Cryptography.RandomNumberGenerator.GetInt32(a.Length)];
        });
    }

    internal static string InvoicePrefixFrom(string name)
    {
        var letters = new string(name.Where(char.IsAsciiLetterOrDigit).Take(4).ToArray()).ToUpperInvariant();
        return letters.Length == 0 ? "INV" : letters;
    }
}
