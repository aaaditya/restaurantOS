using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Auth;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Billing;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Integrations;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Integrations;

public sealed record CreateApiKeyRequest(string Name, Guid? OutletId, List<string>? Scopes);

/// <summary>The API key that authenticated the current request.</summary>
public sealed record ApiKeyPrincipal(Guid KeyId, Guid TenantId, Guid? OutletId, IReadOnlyList<string> Scopes);

public static class ApiKeyEndpoints
{
    public const string Header = "X-Api-Key";

    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/api-keys").WithTags("Integrations").RequireAuthorization();
        g.MapGet("", List).RequireTenant(Permission.IntegrationsManage);
        g.MapPost("", Create).RequireTenant(Permission.IntegrationsManage);
        g.MapPost("/{id:guid}/revoke", Revoke).RequireTenant(Permission.IntegrationsManage);
    }

    private static object View(ApiKey k) => new { k.Id, k.Name, k.Prefix, k.OutletId, k.Scopes, k.CreatedAt, k.LastUsedAt, k.RevokedAt };

    private static async Task<IResult> List(AppDbContext db, CancellationToken ct) =>
        Results.Ok((await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct)).Select(View));

    private static async Task<IResult> Create(CreateApiKeyRequest req, AppDbContext db, AccessService access, AuditWriter audit,
        CancellationToken ct)
    {
        var tenant = await access.GetTenantAsync(ct);
        if (!PlanCatalog.Get(tenant.PlanCode).Integrations)
            throw new DomainException("plan_feature", "API integrations are available on the Growth plan and above.");
        var name = Guard.Required(req.Name, "Name", 100);
        var scopes = (req.Scopes ?? []).Distinct().ToList();
        if (scopes.Count == 0 || scopes.Any(s => !ApiScopes.All.Contains(s)))
            throw new DomainException("validation", $"Choose at least one scope from: {string.Join(", ", ApiScopes.All)}.");
        if (req.OutletId is { } outletId && !await db.Outlets.AnyAsync(o => o.Id == outletId, ct)) throw new NotFoundException("Outlet");

        var me = await access.GetAsync(ct);
        var prefix = $"ros_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5))}";
        var secret = TokenService.GenerateSecret(32);
        var key = new ApiKey
        {
            Name = name, Prefix = prefix, SecretHash = TokenService.Hash(secret), OutletId = req.OutletId, Scopes = scopes,
            CreatedByUserId = me.UserId,
        };
        db.ApiKeys.Add(key);
        await audit.AddAsync("integration.api_key_created", "ApiKey", key.Id, new { name, prefix, scopes, req.OutletId }, ct: ct);
        await db.SaveChangesAsync(ct);
        // The full key is only ever returned here; the server keeps a hash of the secret part.
        return Results.Created($"/api/integrations/api-keys/{key.Id}", new { apiKey = View(key), key = $"{prefix}.{secret}" });
    }

    private static async Task<IResult> Revoke(Guid id, AppDbContext db, AuditWriter audit, TimeProvider clock, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct) ?? throw new NotFoundException("API key");
        if (key.RevokedAt is not null) throw new ConflictException("already_revoked", "This key is already revoked.");
        key.RevokedAt = clock.GetUtcNow();
        await audit.AddAsync("integration.api_key_revoked", "ApiKey", key.Id, new { key.Name, key.Prefix }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(key));
    }

    /// <summary>
    /// Authenticates <c>X-Api-Key: prefix.secret</c>, requires <paramref name="scope"/>, sets the tenant context and, for
    /// routes with <c>{outletId}</c>, requires the outlet to belong to the tenant and match the key's outlet restriction.
    /// </summary>
    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var services = http.RequestServices;
            var db = services.GetRequiredService<AppDbContext>();
            var clock = services.GetRequiredService<TimeProvider>();
            var raw = http.Request.Headers[Header].ToString();
            var dot = raw.IndexOf('.');
            if (dot <= 0 || dot == raw.Length - 1 || raw.Length > 200) return Unauthorized();

            var prefix = raw[..dot];
            var key = await db.ApiKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.Prefix == prefix, http.RequestAborted);
            if (key is null || key.RevokedAt is not null) return Unauthorized();
            var presented = Encoding.ASCII.GetBytes(TokenService.Hash(raw[(dot + 1)..]));
            if (!CryptographicOperations.FixedTimeEquals(presented, Encoding.ASCII.GetBytes(key.SecretHash))) return Unauthorized();
            if (!key.Scopes.Contains(scope)) return Problem(403, "insufficient_scope", $"This API key lacks the '{scope}' scope.");

            services.GetRequiredService<RequestTenantContext>().Set(key.TenantId);
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == key.TenantId, http.RequestAborted);
            var now = clock.GetUtcNow();
            if (!tenant.IsSubscriptionUsable(now) || !PlanCatalog.Get(tenant.PlanCode).Integrations)
                return Problem(402, "integrations_unavailable", "Integrations are not available on this subscription.");

            if (Guid.TryParse(http.Request.RouteValues["outletId"]?.ToString(), out var outletId))
            {
                if ((key.OutletId is { } restricted && restricted != outletId)
                    || !await db.Outlets.AnyAsync(o => o.Id == outletId && o.IsActive, http.RequestAborted))
                    return Results.NotFound();
            }

            if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1))
                await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == key.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), http.RequestAborted);
            http.Items[typeof(ApiKeyPrincipal)] = new ApiKeyPrincipal(key.Id, key.TenantId, key.OutletId, key.Scopes);
            return await next(ctx);
        });

    private static IResult Unauthorized() => Problem(401, "invalid_api_key", "A valid API key is required in the X-Api-Key header.");

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
