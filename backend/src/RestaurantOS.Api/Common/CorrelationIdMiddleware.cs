namespace RestaurantOS.Api.Common;

/// <summary>Propagates or creates an X-Correlation-Id and adds it to the logging scope.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[Header].ToString();
        var id = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64 ? incoming : ctx.TraceIdentifier;
        ctx.Response.Headers[Header] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(ctx);
        }
    }
}
