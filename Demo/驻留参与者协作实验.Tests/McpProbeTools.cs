using System;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace AIC.ResidentLab.Transport.Tests;

[McpServerToolType]
public static class McpProbeTools
{
	[McpServerTool(Name = "get_transport_probe")]
	public static string GetTransportProbe(McpServer server)
	{
		return McpProbeState.HashSessionId(server.SessionId);
	}

	[McpServerTool(Name = "wait_for_cancellation")]
	public static async Task<string> WaitForCancellationAsync(McpProbeState probe, McpServer server, CancellationToken cancellationToken)
	{
		probe.WaitToolStarted();
		try
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return "unexpected_completion";
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			probe.CancellationWasObserved(server.SessionId);
			return "cancelled";
		}
	}
}
