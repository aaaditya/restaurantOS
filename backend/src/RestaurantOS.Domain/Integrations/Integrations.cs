using RestaurantOS.Domain.Common;

namespace RestaurantOS.Domain.Integrations;

/// <summary>Credential for an authorized external system. Only a SHA-256 hash of the secret is stored.</summary>
public class ApiKey : TenantEntity
{
    public required string Name { get; set; }
    /// <summary>Public prefix shown to users to identify the key (e.g. "ros_ab12cd").</summary>
    public required string Prefix { get; set; }
    public required string SecretHash { get; set; }
    public Guid? OutletId { get; set; }
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
}

public static class ApiScopes
{
    public const string OrdersWrite = "orders:write";
    public const string MenuRead = "menu:read";
    public const string ReportsRead = "reports:read";
    public static readonly string[] All = [OrdersWrite, MenuRead, ReportsRead];
}

/// <summary>Deduplicates inbound webhook deliveries (aggregators, gateways).</summary>
public class InboundEvent : TenantEntity
{
    public required string Provider { get; set; }
    public required string ExternalId { get; set; }
    public required string EventType { get; set; }
    public string? Payload { get; set; }
    public Guid? ResultEntityId { get; set; }
}
