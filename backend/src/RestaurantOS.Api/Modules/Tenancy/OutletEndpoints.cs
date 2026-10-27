using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Api.Modules.Identity;
using RestaurantOS.Domain.Billing;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Tenancy;

public sealed record CreateOutletRequest(string Name, string? Address, string? Phone, string? Gstin);

public sealed record UpdateOutletRequest(
    string Name,
    string? Address,
    string? Phone,
    string? Gstin,
    string InvoicePrefix,
    bool PricesIncludeTax,
    RoundingRule Rounding,
    decimal LoyaltyPointsPer100,
    decimal LoyaltyPointValue,
    bool QrOrderingEnabled);

public static class OutletEndpoints
{
    public static void MapOutletEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/outlets").WithTags("Outlets").RequireAuthorization();
        g.MapGet("", List);
        g.MapPost("", Create).RequireTenant(Permission.OutletManage);
        g.MapGet("/{outletId:guid}", Get).RequireOutlet(Permission.OutletView);
        g.MapPut("/{outletId:guid}", Update).RequireOutlet(Permission.OutletManage);
    }

    private static async Task<IResult> List(AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await access.GetAsync(ct);
        var outlets = await db.Outlets.AsNoTracking().OrderBy(o => o.CreatedAt).ToListAsync(ct);
        return Results.Ok(outlets.Where(o => me.CanAccessOutlet(o.Id)).Select(ToDto));
    }

    private static async Task<IResult> Get(Guid outletId, AppDbContext db, CancellationToken ct) =>
        Results.Ok(ToDto(await db.Outlets.AsNoTracking().FirstAsync(o => o.Id == outletId, ct)));

    private static async Task<IResult> Create(CreateOutletRequest req, AppDbContext db, AccessService access, AuditWriter audit,
        CancellationToken ct)
    {
        var tenant = await access.GetTenantAsync(ct);
        var plan = PlanCatalog.Get(tenant.PlanCode);
        if (await db.Outlets.CountAsync(o => o.IsActive, ct) >= plan.MaxOutlets)
            throw new DomainException("plan_limit", $"Your {plan.Name} plan allows up to {plan.MaxOutlets} outlet(s). Upgrade to add more.");

        var name = Guard.Required(req.Name, "Name");
        var outlet = new Outlet
        {
            Name = name,
            Code = AuthEndpoints.RandomCode(8),
            Address = Guard.Optional(req.Address, "Address"),
            Phone = Guard.Optional(req.Phone, "Phone", 20),
            Gstin = Guard.Optional(req.Gstin, "GSTIN", 20),
            InvoicePrefix = AuthEndpoints.InvoicePrefixFrom(name),
        };
        db.Outlets.Add(outlet);
        await audit.AddAsync("outlet.created", "Outlet", outlet.Id, new { outlet.Name }, outlet.Id, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/outlets/{outlet.Id}", ToDto(outlet));
    }

    private static async Task<IResult> Update(Guid outletId, UpdateOutletRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var outlet = await db.Outlets.FirstAsync(o => o.Id == outletId, ct);
        if (!Enum.IsDefined(req.Rounding)) throw new DomainException("validation", "Unknown rounding rule.");
        var prefix = Guard.Required(req.InvoicePrefix, "Invoice prefix", 20);
        if (!prefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new DomainException("validation", "Invoice prefix may contain only letters, digits and '-'.");

        outlet.Name = Guard.Required(req.Name, "Name");
        outlet.Address = Guard.Optional(req.Address, "Address");
        outlet.Phone = Guard.Optional(req.Phone, "Phone", 20);
        outlet.Gstin = Guard.Optional(req.Gstin, "GSTIN", 20);
        outlet.InvoicePrefix = prefix.ToUpperInvariant();
        outlet.PricesIncludeTax = req.PricesIncludeTax;
        outlet.Rounding = req.Rounding;
        outlet.LoyaltyPointsPer100 = Guard.NonNegative(req.LoyaltyPointsPer100, "Loyalty points per 100");
        outlet.LoyaltyPointValue = Guard.NonNegative(req.LoyaltyPointValue, "Loyalty point value");
        outlet.QrOrderingEnabled = req.QrOrderingEnabled;
        await audit.AddAsync("outlet.updated", "Outlet", outlet.Id, req, outlet.Id, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(outlet));
    }

    internal static object ToDto(Outlet o) => new
    {
        o.Id,
        o.Name,
        o.Code,
        o.Address,
        o.Phone,
        o.Gstin,
        o.Currency,
        o.TimeZone,
        o.PricesIncludeTax,
        o.Rounding,
        o.InvoicePrefix,
        o.LoyaltyPointsPer100,
        o.LoyaltyPointValue,
        o.QrOrderingEnabled,
        o.IsActive,
    };
}
