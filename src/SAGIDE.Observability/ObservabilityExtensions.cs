using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SAGIDE.Observability;

/// <summary>
/// DI registration for the SAGIDE observability spine.
///
/// Wires OpenTelemetry tracing with all SAGIDE module activity sources, ASP.NET Core
/// request instrumentation, and outbound HttpClient instrumentation. The exporter is
/// chosen by configuration:
///
/// <list type="bullet">
///   <item><c>SAGIDE:Observability:Exporter = otlp</c> — sends to an OTLP endpoint
///     (Aspire Dashboard, Jaeger, Tempo). Default endpoint: http://localhost:4317.</item>
///   <item><c>SAGIDE:Observability:Exporter = console</c> — writes spans to stdout
///     (useful in CI or when no backend is running).</item>
///   <item><c>SAGIDE:Observability:Exporter = none</c> (or absent) — ActivitySources are
///     registered but no exporter is wired; zero overhead in production when disabled.</item>
/// </list>
///
/// Aspire Dashboard quick-start (no install needed):
/// <code>
///   docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest
/// </code>
/// Then set <c>SAGIDE:Observability:Exporter = otlp</c> and open http://localhost:18888.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddSagideObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var exporter = configuration["SAGIDE:Observability:Exporter"] ?? "none";
        var otlpEndpoint = configuration["SAGIDE:Observability:OtlpEndpoint"] ?? "http://localhost:4317";

        // Always register the TracerProvider so ActivitySources are listened to
        // (without a listener, StartActivity returns null and has zero cost).
        var otelBuilder = services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(
                    serviceName:    SagideActivitySource.ServiceName,
                    serviceVersion: SagideActivitySource.ServiceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production",
                }))
            .WithTracing(tracing =>
            {
                // Listen to all SAGIDE module sources
                foreach (var name in SagideActivitySource.AllSourceNames)
                    tracing.AddSource(name);

                // Built-in instrumentation: ASP.NET Core inbound requests + HttpClient outbound calls
                tracing.AddAspNetCoreInstrumentation(opts =>
                {
                    // Stamp TraceContext at the ASP.NET Core entry point so all log lines
                    // within the request carry the W3C trace ID.
                    opts.EnrichWithHttpRequest = (activity, request) =>
                    {
                        var sourceTag = request.Headers["X-Source-Tag"].FirstOrDefault();
                        activity.SetTag("sagide.source_tag", sourceTag ?? "rest");
                    };
                });
                tracing.AddHttpClientInstrumentation();

                // Exporter selection
                switch (exporter.ToLowerInvariant())
                {
                    case "otlp":
                        tracing.AddOtlpExporter(opts =>
                        {
                            opts.Endpoint = new Uri(otlpEndpoint);
                        });
                        break;

                    case "console":
                        tracing.AddConsoleExporter();
                        break;

                    // "none" or unrecognised: no exporter — ActivitySources active but no export overhead
                }
            });

        return services;
    }
}
