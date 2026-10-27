using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Audit;

public class AuditLog : TenantEntity
{
    public Guid? OutletId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }
    /// <summary>JSON document describing the change.</summary>
    public string? Data { get; set; }
    public string? IpAddress { get; set; }
}
