using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "restaurantos";
    public string Audience { get; set; } = "restaurantos";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    public SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

public sealed class TokenService(IOptions<JwtOptions> options, AppDbContext db, TimeProvider clock)
{
    private readonly JwtOptions _o = options.Value;

    public async Task<TokenPair> IssueAsync(User user, Guid? familyId = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_o.AccessTokenMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(AppClaims.TenantId, user.TenantId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Name, user.Name),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(_o.Key(), SecurityAlgorithms.HmacSha256),
        });

        var refresh = GenerateSecret();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TenantId = user.TenantId,
            TokenHash = Hash(refresh),
            FamilyId = familyId ?? Guid.NewGuid(),
            ExpiresAt = now.AddDays(_o.RefreshTokenDays),
        });
        await db.SaveChangesAsync(ct);
        return new TokenPair(access, expires, refresh);
    }

    /// <summary>Rotates a refresh token. Reusing an already-rotated token revokes the whole family.</summary>
    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return null;

        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            return null;
        }

        if (stored.ExpiresAt <= now) return null;

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || !user.IsActive) return null;

        stored.RevokedAt = now;
        var pair = await IssueAsync(user, stored.FamilyId, ct);
        stored.ReplacedById = (await db.RefreshTokens.FirstAsync(t => t.TokenHash == Hash(pair.RefreshToken), ct)).Id;
        await db.SaveChangesAsync(ct);
        return pair;
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is not null) await RevokeFamilyAsync(stored.FamilyId, ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public static string GenerateSecret(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
