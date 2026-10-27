using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Pricing;

namespace RestaurantOS.Domain.Orders;

public enum OrderType
{
    DineIn = 0,
    Takeaway = 1,
    Delivery = 2,
}

public enum OrderSource
{
    Pos = 0,
    Qr = 1,
    Aggregator = 2,
}

public enum OrderStatus
{
    /// <summary>Placed by a guest via QR or an aggregator; staff must accept before it reaches the kitchen.</summary>
    AwaitingAcceptance = 0,
    Open = 1,
    Billed = 2,
    Settled = 3,
    Cancelled = 4,
}

public enum OrderLineStatus
{
    Pending = 0,
    Sent = 1,
    Voided = 2,
}

public class LineModifier
{
    public Guid OptionId { get; set; }
    public string GroupName { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public class LineTax
{
    public string Code { get; set; } = "";
    public decimal RatePercent { get; set; }
    public decimal Amount { get; set; }
}

public class Order : TenantEntity, IOutletScoped
{
    public Guid OutletId { get; set; }
    public long OrderNumber { get; set; }
    /// <summary>Client-generated id; makes order creation idempotent per outlet.</summary>
    public Guid? ClientOrderId { get; set; }
    public OrderType Type { get; set; }
    public OrderSource Source { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Open;
    public Guid? TableId { get; set; }
    public string? TableName { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? DeliveryAddress { get; set; }
    public int Guests { get; set; } = 1;
    public string? Notes { get; set; }
    public string? ExternalRef { get; set; }
    public Guid? CreatedByUserId { get; set; }

    public bool PricesIncludeTax { get; set; }
    public RoundingRule Rounding { get; set; }

    public DiscountType? DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public string? DiscountReason { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal RoundOff { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidTotal { get; set; }
    public List<LineTax> TaxBreakdown { get; set; } = [];

    public string? InvoiceNumber { get; set; }
    public DateOnly? BusinessDate { get; set; }
    public DateTimeOffset? BilledAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public Guid? MergedIntoOrderId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL xmin).</summary>
    public uint Version { get; set; }

    public List<OrderLine> Lines { get; set; } = [];

    public IEnumerable<OrderLine> ActiveLines => Lines.Where(l => l.Status != OrderLineStatus.Voided);

    public bool IsEditable => Status is OrderStatus.Open or OrderStatus.AwaitingAcceptance;

    public void EnsureEditable()
    {
        if (!IsEditable)
            throw new ConflictException("order_not_editable", $"Order is {Status} and cannot be modified.");
    }

    public void AddLine(OrderLine line)
    {
        EnsureEditable();
        line.OrderId = Id;
        Lines.Add(line);
        Recalculate();
    }

    public void ChangeLineQuantity(Guid lineId, decimal quantity)
    {
        EnsureEditable();
        var line = FindLine(lineId);
        if (line.Status != OrderLineStatus.Pending)
            throw new ConflictException("line_already_sent", "Item already sent to kitchen; void it instead.");
        if (quantity <= 0)
        {
            Lines.Remove(line);
        }
        else
        {
            line.Quantity = quantity;
        }

        Recalculate();
    }

    public OrderLine VoidLine(Guid lineId, string reason)
    {
        EnsureEditable();
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "A void reason is required.");
        var line = FindLine(lineId);
        if (line.Status == OrderLineStatus.Voided) throw new ConflictException("line_already_voided", "Item already voided.");
        line.Status = OrderLineStatus.Voided;
        line.VoidReason = reason.Trim();
        Recalculate();
        return line;
    }

    public void ApplyDiscount(DiscountType? type, decimal value, string? reason)
    {
        EnsureEditable();
        if (type is null || value == 0)
        {
            DiscountType = null;
            DiscountValue = 0;
            DiscountReason = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "A discount reason is required.");
            DiscountType = type;
            DiscountValue = value;
            DiscountReason = reason.Trim();
        }

        Recalculate();
    }

    public void Accept()
    {
        if (Status != OrderStatus.AwaitingAcceptance)
            throw new ConflictException("order_not_awaiting", "Order is not awaiting acceptance.");
        Status = OrderStatus.Open;
    }

    /// <summary>
    /// Moves the items of this order (awaiting acceptance, e.g. a QR order) into a running order for the same table and
    /// cancels this one. Returns the copied lines, which are pending in <paramref name="target"/>.
    /// </summary>
    public List<OrderLine> MergeInto(Order target, Guid? userId, DateTimeOffset now)
    {
        if (Status != OrderStatus.AwaitingAcceptance)
            throw new ConflictException("order_not_awaiting", "Order is not awaiting acceptance.");
        if (target.OutletId != OutletId || target.Id == Id) throw new DomainException("validation", "Orders cannot be merged.");
        if (target.Status != OrderStatus.Open)
            throw new ConflictException("order_not_open", "The running order has been billed. Reopen it before accepting more items.");

        var copies = ActiveLines.Select(l => new OrderLine
        {
            OrderId = target.Id,
            MenuItemId = l.MenuItemId,
            VariantId = l.VariantId,
            ItemName = l.ItemName,
            VariantName = l.VariantName,
            Station = l.Station,
            UnitPrice = l.UnitPrice,
            Quantity = l.Quantity,
            Modifiers = l.Modifiers.Select(m => new LineModifier { OptionId = m.OptionId, GroupName = m.GroupName, Name = m.Name, Price = m.Price }).ToList(),
            TaxRates = l.TaxRates.Select(t => new LineTax { Code = t.Code, RatePercent = t.RatePercent }).ToList(),
            Notes = l.Notes,
            AddedByUserId = userId,
        }).ToList();
        if (copies.Count == 0) throw new DomainException("order_empty", "Order has no items.");

        target.Lines.AddRange(copies);
        target.Recalculate();
        Cancel($"Merged into order #{target.OrderNumber}", now);
        MergedIntoOrderId = target.Id;
        return copies;
    }

    public void MarkBilled(DateTimeOffset now)
    {
        if (Status != OrderStatus.Open) throw new ConflictException("order_not_open", "Only open orders can be billed.");
        EnsureHasItems();
        Status = OrderStatus.Billed;
        BilledAt = now;
    }

    public void Reopen()
    {
        if (Status != OrderStatus.Billed) throw new ConflictException("order_not_billed", "Only billed orders can be reopened.");
        Status = OrderStatus.Open;
        BilledAt = null;
    }

    public void Settle(string invoiceNumber, decimal paidTotal, DateOnly businessDate, DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Open or OrderStatus.Billed))
            throw new ConflictException("order_not_settleable", $"Order is {Status} and cannot be settled.");
        EnsureHasItems();
        if (Lines.Any(l => l.Status == OrderLineStatus.Pending))
            throw new ConflictException("unsent_items", "Send all items to the kitchen before settling.");
        if (paidTotal != GrandTotal)
            throw new DomainException("payment_mismatch", $"Payments ({paidTotal:0.00}) must equal the bill total ({GrandTotal:0.00}).");
        Status = OrderStatus.Settled;
        PaidTotal = paidTotal;
        InvoiceNumber = invoiceNumber;
        BusinessDate = businessDate;
        SettledAt = now;
        BilledAt ??= now;
    }

    public void Cancel(string reason, DateTimeOffset now)
    {
        if (Status is OrderStatus.Settled or OrderStatus.Cancelled)
            throw new ConflictException("order_not_cancellable", $"Order is {Status} and cannot be cancelled.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "A cancellation reason is required.");
        Status = OrderStatus.Cancelled;
        CancelReason = reason.Trim();
        CancelledAt = now;
        foreach (var line in Lines.Where(l => l.Status != OrderLineStatus.Voided))
        {
            line.Status = OrderLineStatus.Voided;
            line.VoidReason ??= "Order cancelled";
        }

        Recalculate();
    }

    public void Recalculate()
    {
        var active = ActiveLines.ToList();
        var discount = DiscountType is { } t ? new DiscountInput(t, DiscountValue) : null;
        var bill = BillCalculator.Calculate(
            active.Select(l => new BillLineInput(l.Id, l.UnitPrice, l.Quantity, l.TaxRates.Select(x => new TaxRate(x.Code, x.RatePercent)).ToList())).ToList(),
            discount,
            new BillSettings(PricesIncludeTax, Rounding));

        var byId = bill.Lines.ToDictionary(l => l.LineId);
        foreach (var line in Lines)
        {
            if (byId.TryGetValue(line.Id, out var r))
            {
                line.Gross = r.Gross;
                line.Discount = r.Discount;
                line.Taxable = r.Taxable;
                line.TaxTotal = r.TaxTotal;
                line.Total = r.Total;
            }
            else
            {
                line.Gross = line.Discount = line.Taxable = line.TaxTotal = line.Total = 0;
            }
        }

        Subtotal = bill.Subtotal;
        DiscountTotal = bill.DiscountTotal;
        TaxTotal = bill.TaxTotal;
        RoundOff = bill.RoundOff;
        GrandTotal = bill.GrandTotal;
        TaxBreakdown = bill.TaxBreakdown.Select(x => new LineTax { Code = x.Code, RatePercent = x.RatePercent, Amount = x.Amount }).ToList();
    }

    private void EnsureHasItems()
    {
        if (!ActiveLines.Any()) throw new DomainException("order_empty", "Order has no items.");
    }

    private OrderLine FindLine(Guid lineId) =>
        Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException("Order line");
}

public class OrderLine : Entity
{
    public Guid OrderId { get; set; }
    public Guid MenuItemId { get; set; }
    public Guid? VariantId { get; set; }
    public required string ItemName { get; set; }
    public string? VariantName { get; set; }
    public string Station { get; set; } = "Main";
    /// <summary>Unit price snapshot including modifier prices.</summary>
    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public List<LineModifier> Modifiers { get; set; } = [];
    /// <summary>Tax components snapshot (code + rate). Amount is unused here.</summary>
    public List<LineTax> TaxRates { get; set; } = [];
    public string? Notes { get; set; }
    public OrderLineStatus Status { get; set; } = OrderLineStatus.Pending;
    public Guid? KotId { get; set; }
    public string? VoidReason { get; set; }
    public Guid? AddedByUserId { get; set; }

    public decimal Gross { get; set; }
    public decimal Discount { get; set; }
    public decimal Taxable { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
}
