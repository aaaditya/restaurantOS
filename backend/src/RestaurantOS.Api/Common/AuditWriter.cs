using System.Text.Json;
using RestaurantOS.Domain.Audit;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Common;

/// <summary>Adds audit entries to the current unit of work; they commit with the business change.</summary>
public sealed class AuditWriter(AppDbContext db, ITenantContext tenant, IHttpContextAccessor http, AccessService access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task AddAsync(string action, string entityType, object? entityId, object? data = null, Guid? outletId = null,
        CancellationToken ct = default)
    {
        string? userName = null;
        if (tenant.UserId is not null)
        {
            try { userName = (await access.GetAsync(ct)).Name; }
            catch (UnauthorizedAccessException) { }
        }

        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenant.TenantId ?? Guid.Empty,
            OutletId = outletId,
            UserId = tenant.UserId,
            UserName = userName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Data = data is null ? null : JsonSerializer.Serialize(data, Json),
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
    }
}
