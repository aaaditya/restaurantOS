namespace RestaurantOS.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Row owned by a single tenant. Enforced by EF query filters and the SaveChanges guard.</summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

public interface IOutletScoped
{
    Guid OutletId { get; set; }
}

public abstract class TenantEntity : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
}
