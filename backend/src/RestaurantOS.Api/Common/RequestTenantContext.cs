using System.Security.Claims;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Common;

public static class AppClaims
{
    public const string TenantId = "tid";
    public const string UserId = "sub";
}

/// <summary>
/// Tenant for the current scope. Derived from the authenticated principal unless explicitly set
/// (anonymous QR/webhook flows resolve the tenant from a token and call <see cref="Set"/>).
/// </summary>
public sealed class RequestTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private bool _explicit;
    private Guid? _tenantId;
    private Guid? _userId;

    public Guid? TenantId => _explicit ? _tenantId : FromClaims(AppClaims.TenantId);
    public Guid? UserId => _explicit ? _userId : FromClaims(AppClaims.UserId);

    public void Set(Guid? tenantId, Guid? userId = null)
    {
        _explicit = true;
        _tenantId = tenantId;
        _userId = userId;
    }

    public void SetFromPrincipal(ClaimsPrincipal principal) =>
        Set(Parse(principal.FindFirstValue(AppClaims.TenantId)), Parse(principal.FindFirstValue(AppClaims.UserId)));

    private Guid? FromClaims(string type) => Parse(accessor.HttpContext?.User.FindFirstValue(type));

    private static Guid? Parse(string? value) => Guid.TryParse(value, out var g) ? g : null;
}
