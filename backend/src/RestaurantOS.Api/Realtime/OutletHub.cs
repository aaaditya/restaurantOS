using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RestaurantOS.Api.Common;
using RestaurantOS.Domain.Identity;

namespace RestaurantOS.Api.Realtime;

[Authorize]
public sealed class OutletHub(AccessService access, RequestTenantContext tenant) : Hub
{
    public static string Group(Guid outletId) => $"outlet:{outletId}";

    public async Task JoinOutlet(Guid outletId)
    {
        tenant.SetFromPrincipal(Context.User!);
        try
        {
            await access.RequireOutletAsync(outletId, Permission.OutletView);
        }
        catch (Exception ex) when (ex is RestaurantOS.Domain.Common.NotFoundException or RestaurantOS.Domain.Common.ForbiddenException
                                       or UnauthorizedAccessException)
        {
            throw new HubException("Outlet not accessible.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(outletId));
    }

    public Task LeaveOutlet(Guid outletId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(outletId));
}

public static class RealtimeEvents
{
    public const string KotChanged = "kotChanged";
    public const string OrderChanged = "orderChanged";
    public const string QrOrderPlaced = "qrOrderPlaced";
    public const string StockChanged = "stockChanged";
}

public interface IRealtimeNotifier
{
    Task PublishAsync(Guid outletId, string eventName, object payload);
}

public sealed class SignalRNotifier(IHubContext<OutletHub> hub, ILogger<SignalRNotifier> logger) : IRealtimeNotifier
{
    public async Task PublishAsync(Guid outletId, string eventName, object payload)
    {
        try
        {
            await hub.Clients.Group(OutletHub.Group(outletId)).SendAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            // Realtime is best-effort; clients also poll/refetch, so never fail the business operation.
            logger.LogWarning(ex, "Failed to publish {Event} for outlet {OutletId}", eventName, outletId);
        }
    }
}
