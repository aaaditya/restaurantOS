using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Identity;

public class User : TenantEntity
{
    public required string Email { get; set; }
    public required string Name { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<UserRole> Roles { get; set; } = [];
}

/// <summary>Role assignment. OutletId null means tenant-wide.</summary>
public class UserRole : TenantEntity
{
    public Guid UserId { get; set; }
    public Guid? OutletId { get; set; }
    public Role Role { get; set; }
}

public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public required string TokenHash { get; set; }
    public Guid FamilyId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}
