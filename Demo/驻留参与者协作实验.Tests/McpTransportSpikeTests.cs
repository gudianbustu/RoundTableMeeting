using System.Net;

namespace AIC.ResidentLab.Transport.Tests;

[Collection("MCP Transport Spike")]
public sealed class McpTransportSpikeTests
{
    [Fact]
    public async Task StatefulSession_supports_binding_isolation_and_tool_session_identity()
    {
        await using var host = await TransportProbeHost.StartStatefulAsync();
        await using var first = await McpHttpSession.ConnectAsync(host);
        await using var second = await McpHttpSession.ConnectAsync(host);

        var firstTools = await first.ListToolNamesAsync();
        Require(firstTools.Contains("get_transport_probe"), "stateful_session_continuity", "same-session tools/list did not expose the probe tool.");
        Require(firstTools.Contains("wait_for_cancellation"), "stateful_session_continuity", "same-session tools/list did not expose the cancellation tool.");

        var firstToolSessionHash = await first.CallToolTextAsync("get_transport_probe");
        var secondToolSessionHash = await second.CallToolTextAsync("get_transport_probe");
        Require(firstToolSessionHash == first.SessionIdHash, "stateful_session_continuity", "tool McpSession.SessionId did not match the first protocol session.");
        Require(secondToolSessionHash == second.SessionIdHash, "stateful_session_continuity", "tool McpSession.SessionId did not match the second protocol session.");
        Require(first.SessionIdHash != second.SessionIdHash, "session_isolation", "two initialize requests received the same protocol session binding.");

        var secondTools = await second.ListToolNamesAsync();
        Require(secondTools.SetEquals(firstTools), "session_isolation", "the second protocol session did not receive an isolated, equivalent tool catalog.");
    }

    [Fact]
    public async Task StatefulSession_protocol_cancel_cancels_only_the_tool_call()
    {
        await using var host = await TransportProbeHost.StartStatefulAsync();
        await using var session = await McpHttpSession.ConnectAsync(host, sendInitializedNotification: false);

        const int requestId = 73;
        using var requestLifetime = new CancellationTokenSource();
        var pendingCall = session.CallToolTextAsync("wait_for_cancellation", requestId, requestLifetime.Token);
        await host.Probe.WaitStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelStatus = await session.SendCancelledAsync(requestId);
        Require((int)cancelStatus is >= 200 and < 300, "cancellation", "notifications/cancelled was not accepted by the HTTP transport.");
        await host.Probe.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
        Require(host.Probe.CancellationSessionHashes.Count == 1, "cancellation", "the cancelled tool did not record exactly one session-bound cancellation observation.");
        Require(host.Probe.CancellationSessionHashes.Single() == session.SessionIdHash, "cancellation", "the cancelled tool observation was not correlated to the protocol session.");
        requestLifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pendingCall);

        var probeHash = await session.CallToolTextAsync("get_transport_probe");
        Require(probeHash == session.SessionIdHash, "cancellation", "cancelling one tool call lost the protocol session binding.");
    }

    [Fact]
    public async Task RunSessionHandler_invokes_RunAsync_and_records_a_correlated_end()
    {
        await using var host = await TransportProbeHost.StartRunSessionHandlerAsync();
        await using var session = await McpHttpSession.ConnectAsync(host);
        await host.Probe.SessionHandlerStartedSignal.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Probe.RunAsyncEnteredSignal.WaitAsync(TimeSpan.FromSeconds(5));

        var continuityStatus = await session.SendRawRpcStatusAsync("tools/list", new { }, 2);
        Require(continuityStatus == HttpStatusCode.OK, "stateful_session_continuity", "RunSessionHandler did not preserve the initialized session for a same-session request.");

        var deleteStatus = await session.DeleteAsync();
        Require((int)deleteStatus is >= 200 and < 300, "lifecycle_callback", "DELETE /mcp did not complete as an HTTP operation.");
        var ended = await CompletesWithinAsync(host.Probe.HandlerEnded, TimeSpan.FromSeconds(5));
        Require(ended, "lifecycle_callback", "RunSessionHandler did not provide a correlated end callback after protocol session close.");

        Require(host.Probe.SessionHandlerStartedCount == 1, "lifecycle_callback", "RunSessionHandler was not entered exactly once for initialize.");
        Require(host.Probe.RunAsyncEnteredCount == 1, "lifecycle_callback", "RunSessionHandler did not call McpServer.RunAsync.");
        Require(host.Probe.SessionHandlerEndedCount == 1, "lifecycle_callback", "RunSessionHandler did not return exactly one end callback.");
        Require(host.Probe.EndedSessionHashes.Single() == session.SessionIdHash, "lifecycle_callback", "the end callback was not correlated to the initialize protocol session.");
    }

    [Fact]
    public async Task RunSessionHandler_preserves_stateful_continuity_until_session_close()
    {
        await using var host = await TransportProbeHost.StartRunSessionHandlerAsync();
        await using var session = await McpHttpSession.ConnectAsync(host);
        await host.Probe.RunAsyncEnteredSignal.WaitAsync(TimeSpan.FromSeconds(5));

        var status = await session.SendRawRpcStatusAsync("tools/list", new { }, 2);
        Require(status == HttpStatusCode.OK, "stateful_session_continuity", "RunSessionHandler broke the initialized session before a same-session tools/list request.");
    }

    [Fact]
    public async Task RunSessionHandler_client_abort_cancels_only_the_request_and_preserves_the_session()
    {
        await using var host = await TransportProbeHost.StartRunSessionHandlerAsync();
        await using var session = await McpHttpSession.ConnectAsync(host);
        await host.Probe.RunAsyncEnteredSignal.WaitAsync(TimeSpan.FromSeconds(5));

        using var abort = new CancellationTokenSource();
        var request = session.CallToolTextAsync("wait_for_cancellation", 91, abort.Token);
        await host.Probe.WaitStarted.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);

        var endObserved = await CompletesWithinAsync(host.Probe.HandlerEnded, TimeSpan.FromSeconds(1));
        Require(!endObserved && host.Probe.SessionHandlerEndedCount == 0, "lifecycle_callback", "a client request abort was incorrectly treated as an MCP protocol session close.");

        var probeHash = await session.CallToolTextAsync("get_transport_probe");
        Require(probeHash == session.SessionIdHash, "stateful_session_continuity", "the MCP session could not continue after a single client request was aborted.");
    }

    [Fact]
    public async Task Http_delete_and_host_stop_have_single_correlated_lifecycle_observations()
    {
        await using var handlerHost = await TransportProbeHost.StartRunSessionHandlerAsync();
        await using var handlerSession = await McpHttpSession.ConnectAsync(handlerHost);
        await handlerHost.Probe.RunAsyncEnteredSignal.WaitAsync(TimeSpan.FromSeconds(5));

        var deleteStatus = await handlerSession.DeleteAsync();
        Require((int)deleteStatus is >= 200 and < 300, "lifecycle_callback", "DELETE /mcp did not complete as an HTTP operation.");
        var deleteEnded = await CompletesWithinAsync(handlerHost.Probe.HandlerEnded, TimeSpan.FromSeconds(5));
        var deleteCallbackCount = handlerHost.Probe.SessionHandlerEndedCount;
        var deleteCallbackCorrelation = handlerHost.Probe.EndedSessionHashes.Count == 1
            && handlerHost.Probe.EndedSessionHashes[0] == handlerSession.SessionIdHash;
        Require(deleteEnded && deleteCallbackCount == 1 && deleteCallbackCorrelation, "lifecycle_callback", $"delete endObserved={deleteEnded}, callbackCount={deleteCallbackCount}, correlated={deleteCallbackCorrelation}.");

        var shutdownHost = await TransportProbeHost.StartRunSessionHandlerAsync();
        await using var shutdownSession = await McpHttpSession.ConnectAsync(shutdownHost);
        await shutdownHost.Probe.RunAsyncEnteredSignal.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdownHost.DisposeAsync();
        await shutdownHost.Probe.HostStoppingSignal.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdownEnded = await CompletesWithinAsync(shutdownHost.Probe.HandlerEnded, TimeSpan.FromSeconds(5));
        var shutdownCallbackCount = shutdownHost.Probe.SessionHandlerEndedCount;
        var shutdownCallbackCorrelation = shutdownHost.Probe.EndedSessionHashes.Count == 1
            && shutdownHost.Probe.EndedSessionHashes[0] == shutdownSession.SessionIdHash;
        Require(shutdownEnded && shutdownHost.Probe.HostStoppingCount == 1 && shutdownCallbackCount == 1 && shutdownCallbackCorrelation, "lifecycle_callback", $"host_stop endObserved={shutdownEnded}, hostStoppingCount={shutdownHost.Probe.HostStoppingCount}, callbackCount={shutdownCallbackCount}, correlated={shutdownCallbackCorrelation}.");
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        return completed == task;
    }

    private static void Require(bool condition, string category, string message)
        => Assert.True(condition, $"{category}: {message}");
}
