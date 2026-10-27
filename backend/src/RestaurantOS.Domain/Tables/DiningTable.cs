using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Tables;

public class DiningTable : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public required string Name { get; set; }
    public string Area { get; set; } = "Main";
    public int Capacity { get; set; } = 4;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Unguessable token printed in the table QR code.</summary>
    public required string QrToken { get; set; }
}
