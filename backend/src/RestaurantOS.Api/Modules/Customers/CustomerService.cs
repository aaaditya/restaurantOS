using Microsoft.EntityFrameworkCore;
using RestaurantOS.Domain.Common;
using RestaurantOS.Domain.Customers;
using RestaurantOS.Domain.Orders;
using RestaurantOS.Domain.Tenancy;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Customers;

public sealed class CustomerService(AppDbContext db, TimeProvider clock)
{
    public async Task<Customer> FindOrCreateAsync(string phone, string? name, CancellationToken ct)
    {
        var normalized = Customer.NormalizePhone(phone);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Phone == normalized, ct);
        if (customer is null)
        {
            customer = new Customer { Phone = normalized, Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
            db.Customers.Add(customer);
        }
        else if (string.IsNullOrWhiteSpace(customer.Name) && !string.IsNullOrWhiteSpace(name))
        {
            customer.Name = name.Trim();
        }

        return customer;
    }

    /// <summary>Points needed to redeem <paramref name="amount"/> at the outlet's point value.</summary>
    public static decimal PointsFor(decimal amount, Outlet outlet)
    {
        if (outlet.LoyaltyPointValue <= 0) throw new DomainException("loyalty_disabled", "Loyalty redemption is disabled for this outlet.");
        return Math.Ceiling(amount / outlet.LoyaltyPointValue);
    }

    /// <summary>
    /// Applies the loyalty and visit effects of a settled order. Runs inside the settlement transaction; the unique
    /// (order_id, type) index guarantees an order earns or redeems at most once.
    /// </summary>
    public async Task ApplySettlementAsync(Order order, Outlet outlet, decimal loyaltyRedeemedAmount, CancellationToken ct)
    {
        if (order.CustomerId is not { } customerId) return;
        var customer = await db.Customers.FirstAsync(c => c.Id == customerId, ct);
        var now = clock.GetUtcNow();

        if (loyaltyRedeemedAmount > 0)
        {
            var points = PointsFor(loyaltyRedeemedAmount, outlet);
            if (customer.LoyaltyPoints < points)
                throw new DomainException("insufficient_points", $"Customer has {customer.LoyaltyPoints:0} points; {points:0} required.");
            customer.LoyaltyPoints -= points;
            db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                CustomerId = customer.Id, OutletId = outlet.Id, OrderId = order.Id, Type = LoyaltyTransactionType.Redeem,
                Points = -points, BalanceAfter = customer.LoyaltyPoints, Notes = $"Redeemed on {order.InvoiceNumber}",
            });
        }

        var eligible = order.GrandTotal - loyaltyRedeemedAmount;
        var earned = Math.Floor(eligible / 100m * outlet.LoyaltyPointsPer100);
        if (earned > 0)
        {
            customer.LoyaltyPoints += earned;
            db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                CustomerId = customer.Id, OutletId = outlet.Id, OrderId = order.Id, Type = LoyaltyTransactionType.Earn,
                Points = earned, BalanceAfter = customer.LoyaltyPoints, Notes = $"Earned on {order.InvoiceNumber}",
            });
        }

        customer.TotalSpend += order.GrandTotal;
        customer.VisitCount += 1;
        customer.LastVisitAt = now;
    }
}
