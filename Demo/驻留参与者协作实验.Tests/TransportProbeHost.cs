using System.Security.Cryptography;
using System.Text;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace AIC.ResidentLab.Transport.Tests;

/// <summary>
/// 为 MCP Transport Spike 启动隔离的真实 Kestrel HTTP 宿主。
/// </summary>
public sealed class TransportProbeHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private TransportProbeHost(WebApplication application, McpProbeState probe, Uri endpoint)
    {
        this.application = application;
        Probe = probe;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public McpProbeState Probe { get; }

    public static Task<TransportProbeHost> StartStatefulAsync() => StartAsync(useRunSessionHandler: false);

    public static Task<TransportProbeHost> StartRunSessionHandlerAsync() => StartAsync(useRunSessionHandler: true);

    private static async Task<TransportProbeHost> StartAsync(bool useRunSessionHandler)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TransportProbeHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.Protocols = HttpProtocols.Http1));

        var probe = new McpProbeState(useRunSessionHandler);
        builder.Services.AddSingleton(probe);

        var mcpBuilder = builder.Services.AddMcpServer();
#pragma warning disable MCPEXP002
        if (useRunSessionHandler)
        {
            mcpBuilder.WithHttpTransport(options =>
            {
                options.RunSessionHandler = async (_, server, cancellationToken) =>
                {
                    probe.SessionHandlerStarted(server.SessionId);
                    try
                    {
                        probe.RunAsyncEntered();
                        await server.RunAsync(cancellationToken);
                    }
                    finally
                    {
                        probe.SessionHandlerEnded(server.SessionId);
                    }
                };
            });
        }
        else
        {
            mcpBuilder.WithHttpTransport();
        }
#pragma warning restore MCPEXP002
        mcpBuilder.WithToolsFromAssembly();

        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(probe.HostStopping);
        app.MapMcp("/mcp");
        await app.StartAsync();

        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("lifecycle_callback: test host did not publish a listening address.");
        return new TransportProbeHost(app, probe, new Uri(new Uri(address), "mcp"));
    }

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
