namespace RestaurantOS.Infrastructure.Persistence;

/// <summary>Ambient tenant for the current unit of work (HTTP request, webhook, background job).</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? UserId { get; }
}

public sealed class FixedTenantContext(Guid? tenantId, Guid? userId = null) : ITenantContext
{
    public Guid? TenantId { get; } = tenantId;
    public Guid? UserId { get; } = userId;
}
