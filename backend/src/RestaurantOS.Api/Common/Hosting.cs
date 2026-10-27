using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RestaurantOS.Api.Common;

public static class Hosting
{
    /// <summary>
    /// Trusts X-Forwarded-For/Proto only from configured proxies. Rate limits partition by client IP, so
    /// trusting every sender would let clients pick their own partition.
    /// </summary>
    public static void AddReverseProxySupport(this IServiceCollection services, IConfiguration config)
    {
        var proxies = config.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        var networks = config.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = config.GetValue("ReverseProxy:ForwardLimit", 1);
            foreach (var p in proxies)
                o.KnownProxies.Add(IPAddress.TryParse(p, out var ip) ? ip : throw new InvalidOperationException($"ReverseProxy:KnownProxies has an invalid IP '{p}'."));
            foreach (var n in networks)
                o.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(n, out var net) ? net : throw new InvalidOperationException($"ReverseProxy:KnownNetworks has an invalid CIDR '{n}'."));
        });
    }

    /// <summary>Traces and metrics over OTLP, enabled only when an endpoint is configured (standard OTEL_* variables).</summary>
    public static void AddObservability(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "restaurantos-api"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql")
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql")
                .AddOtlpExporter());
    }
}
