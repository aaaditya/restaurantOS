using Microsoft.EntityFrameworkCore;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Customers;
using RestaurantOS.Domain.Identity;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Customers;

public sealed record CustomerUpsertRequest(string Phone, string? Name, string? Email, DateOnly? Birthday, string? Notes);
public sealed record LoyaltyAdjustmentRequest(decimal Points, string Reason);

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/customers").WithTags("Customers").RequireAuthorization();
        g.MapGet("", List).RequireAny(Permission.CustomerView);
        g.MapGet("/lookup", Lookup).RequireAny(Permission.CustomerView);
        g.MapGet("/{id:guid}", Get).RequireAny(Permission.CustomerView);
        g.MapPost("", Create).RequireAny(Permission.CustomerManage);
        g.MapPut("/{id:guid}", Update).RequireAny(Permission.CustomerManage);
        // Manual point changes bypass sales, so they are limited to tenant-wide managers.
        g.MapPost("/{id:guid}/loyalty-adjustments", AdjustPoints).RequireTenant(Permission.CustomerManage);
    }

    private static async Task<IResult> List(AppDbContext db, string? search, string? sort, int? limit, CancellationToken ct)
    {
        var q = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            q = q.Where(c => (c.Name != null && EF.Functions.ILike(c.Name, $"%{term}%")) || (digits.Length >= 3 && c.Phone.Contains(digits)));
        }

        q = sort switch
        {
            "spend" => q.OrderByDescending(c => c.TotalSpend),
            "visits" => q.OrderByDescending(c => c.VisitCount),
            "points" => q.OrderByDescending(c => c.LoyaltyPoints),
            _ => q.OrderByDescending(c => c.LastVisitAt ?? c.CreatedAt),
        };
        return Results.Ok(await q.Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct));
    }

    private static async Task<IResult> Lookup(AppDbContext db, string phone, CancellationToken ct)
    {
        var normalized = Customer.NormalizePhone(phone);
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Phone == normalized, ct);
        return c is null ? Results.Ok(new { customer = (Customer?)null }) : Results.Ok(new { customer = c });
    }

    private static async Task<IResult> Get(Guid id, AppDbContext db, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer");
        var loyalty = await db.LoyaltyTransactions.AsNoTracking().Where(t => t.CustomerId == id).OrderByDescending(t => t.CreatedAt).Take(50)
            .ToListAsync(ct);
        var orders = await db.Orders.AsNoTracking().Where(o => o.CustomerId == id && o.Status == OrderStatus.Settled)
            .OrderByDescending(o => o.SettledAt).Take(20)
            .Select(o => new { o.Id, o.OutletId, o.OrderNumber, o.InvoiceNumber, o.Type, o.GrandTotal, o.SettledAt })
            .ToListAsync(ct);
        return Results.Ok(new { customer = c, loyalty, orders });
    }

    private static async Task<IResult> Create(CustomerUpsertRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var phone = Customer.NormalizePhone(req.Phone ?? "");
        if (await db.Customers.AnyAsync(c => c.Phone == phone, ct))
            throw new ConflictException("customer_exists", "A customer with this phone number already exists.");
        var c = new Customer { Phone = phone };
        Apply(c, req);
        db.Customers.Add(c);
        await audit.AddAsync("customer.created", "Customer", c.Id, new { c.Phone }, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/customers/{c.Id}", c);
    }

    private static async Task<IResult> Update(Guid id, CustomerUpsertRequest req, AppDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer");
        var phone = Customer.NormalizePhone(req.Phone ?? "");
        if (phone != c.Phone && await db.Customers.AnyAsync(x => x.Phone == phone, ct))
            throw new ConflictException("customer_exists", "A customer with this phone number already exists.");
        c.Phone = phone;
        Apply(c, req);
        await audit.AddAsync("customer.updated", "Customer", c.Id, ct: ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(c);
    }

    private static void Apply(Customer c, CustomerUpsertRequest req)
    {
        c.Name = Guard.Optional(req.Name, "Name", 200);
        c.Email = req.Email is { Length: > 0 } e ? Guard.Email(e) : null;
        c.Birthday = req.Birthday;
        c.Notes = Guard.Optional(req.Notes, "Notes", 1000);
    }

    private static async Task<IResult> AdjustPoints(Guid id, LoyaltyAdjustmentRequest req, AppDbContext db, AuditWriter audit,
        CancellationToken ct)
    {
        var reason = Guard.Required(req.Reason, "Reason");
        if (req.Points == 0 || decimal.Truncate(req.Points) != req.Points)
            throw new DomainException("validation", "Points must be a non-zero whole number.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM customers WHERE id = {id} FOR UPDATE", ct);
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer");
        if (c.LoyaltyPoints + req.Points < 0) throw new DomainException("insufficient_points", "Points balance cannot go below zero.");
        c.LoyaltyPoints += req.Points;
        db.LoyaltyTransactions.Add(new LoyaltyTransaction
        {
            CustomerId = c.Id, Type = LoyaltyTransactionType.Adjust, Points = req.Points, BalanceAfter = c.LoyaltyPoints, Notes = reason,
        });
        await audit.AddAsync("customer.loyalty_adjusted", "Customer", c.Id, new { req.Points, reason, balance = c.LoyaltyPoints }, ct: ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(c);
    }
}
