using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Kitchen;

public enum KotStatus
{
    New = 0,
    Preparing = 1,
    Ready = 2,
    Served = 3,
    Cancelled = 4,
}

public enum KotKind
{
    Normal = 0,
    /// <summary>Tells the kitchen to stop preparing voided items.</summary>
    Cancellation = 1,
}

public class Kot : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public Guid OrderId { get; set; }
    public long OrderNumber { get; set; }
    public required string KotNumber { get; set; }
    public KotKind Kind { get; set; }
    public required string Station { get; set; }
    public KotStatus Status { get; set; } = KotStatus.New;
    public string? TableName { get; set; }
    public string OrderTypeLabel { get; set; } = "";
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? ServedAt { get; set; }
    public List<KotItem> Items { get; set; } = [];

    public void MoveTo(KotStatus next, DateTimeOffset now)
    {
        var allowed = Kind == KotKind.Cancellation
            ? Status == KotStatus.New && next == KotStatus.Served
            : (Status, next) switch
        {
            (KotStatus.New, KotStatus.Preparing) => true,
            (KotStatus.New, KotStatus.Ready) => true,
            (KotStatus.Preparing, KotStatus.Ready) => true,
            (KotStatus.Ready, KotStatus.Served) => true,
            (KotStatus.Ready, KotStatus.Preparing) => true,
            (KotStatus.New or KotStatus.Preparing, KotStatus.Cancelled) => true,
            _ => false,
        };
        if (!allowed) throw new ConflictException("invalid_kot_transition", $"Cannot move KOT from {Status} to {next}.");

        Status = next;
        switch (next)
        {
            case KotStatus.Preparing: StartedAt ??= now; break;
            case KotStatus.Ready: ReadyAt = now; StartedAt ??= now; break;
            case KotStatus.Served: ServedAt = now; break;
        }
    }
}

public class KotItem : Entity
{
    public Guid KotId { get; set; }
    public Guid OrderLineId { get; set; }
    public required string Name { get; set; }
    public string? VariantName { get; set; }
    public string? ModifiersText { get; set; }
    public decimal Quantity { get; set; }
    public string? Notes { get; set; }
}
