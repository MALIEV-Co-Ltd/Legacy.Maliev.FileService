using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Api.Http;

internal sealed class FileHostOptions
{
    public string TransportPolicy { get; set; } = FileHostTransportPolicy.PolicyName;
}

internal static class FileHostTransportPolicy
{
    internal const string PolicyName = "InternalHttpWithTrustedEdgeHttps";
    private const string OriginalSchemeHeader = "X-File-Original-Scheme";
    private static readonly object TrustedEdgeKey = new();
    private static readonly object ValidEdgeSchemeKey = new();

    internal static void AddFileHostTransportPolicy(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<FileHostOptions>()
            .Bind(builder.Configuration.GetSection("FileHost"))
            .Validate(options => options.TransportPolicy == PolicyName, "Unsupported FileHost transport policy.")
            .ValidateOnStart();
        builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(30));
        var proxyAddresses = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies")
            .GetChildren().Select(value => value.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Empty lists mean trust-all to the framework. File explicitly disables
            // forwarding unless the operator configured exact proxy addresses.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var value in proxyAddresses)
            {
                if (!IPAddress.TryParse(value, out var address))
                    throw new InvalidOperationException("A configured FileHost trusted proxy address is invalid.");
                options.KnownProxies.Add(address);
            }
            options.ForwardedHeaders = options.KnownProxies.Count == 0
                ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.OriginalProtoHeaderName = OriginalSchemeHeader;
        });
    }

    internal static void UseFileHostTransportBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
            var remote = context.Connection.RemoteIpAddress;
            var trusted = remote is not null && (options.KnownProxies.Contains(remote)
                || remote.IsIPv4MappedToIPv6 && options.KnownProxies.Contains(remote.MapToIPv4()));
            context.Items[TrustedEdgeKey] = trusted;
            context.Request.Headers.Remove(OriginalSchemeHeader);
            context.Request.Headers.Remove("X-Original-Proto");
            var schemes = context.Request.Headers.GetCommaSeparatedValues(options.ForwardedProtoHeaderName);
            context.Items[ValidEdgeSchemeKey] = trusted && schemes.Length == 1
                && (schemes[0] == "http" || schemes[0] == "https");
            if (!trusted || context.Items[ValidEdgeSchemeKey] is not true)
            {
                // Also protects servers whose remote address is unavailable: the
                // framework otherwise permits an initial null remote endpoint.
                context.Request.Headers.Remove(options.ForwardedForHeaderName);
                context.Request.Headers.Remove(options.ForwardedProtoHeaderName);
                context.Request.Headers.Remove(options.ForwardedHostHeaderName);
                context.Request.Headers.Remove(options.ForwardedPrefixHeaderName);
            }
            await next(context);
        });
    }

    internal static void UseFileHostTransportPolicy(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.Use(async (context, next) =>
        {
            var accepted = context.Request.Headers.ContainsKey(OriginalSchemeHeader);
            context.Request.Headers.Remove(OriginalSchemeHeader);
            var probe = context.Request.Path.Equals("/file/liveness", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals("/file/readiness", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals("/file/aspire-liveness", StringComparison.OrdinalIgnoreCase);
            if (context.Items[TrustedEdgeKey] is true && !probe
                && (context.Items[ValidEdgeSchemeKey] is not true || !accepted || !context.Request.IsHttps))
            {
                // Do not reflect an unvalidated Host/path/query into a redirect.
                await Results.Problem(statusCode: StatusCodes.Status426UpgradeRequired,
                    title: "HTTPS is required at the trusted edge.").ExecuteAsync(context);
                return;
            }
            await next(context);
        });
    }
}
