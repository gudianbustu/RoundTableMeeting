using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIC.ResidentLab.Transport.Tests;

public sealed class McpProbeState
{
	private readonly object gate = new object();

	private readonly TaskCompletionSource<bool> waitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly TaskCompletionSource<bool> cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly TaskCompletionSource<bool> sessionHandlerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly TaskCompletionSource<bool> runAsyncEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly TaskCompletionSource<bool> handlerEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly TaskCompletionSource<bool> hostStopping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly List<string> endedSessionHashes = new List<string>();

	private readonly List<string> cancellationSessionHashes = new List<string>();

	private int sessionHandlerStartedCount;

	private int runAsyncEnteredCount;

	private int sessionHandlerEndedCount;

	private int hostStoppingCount;

	public bool HasSessionHandler { get; }

	public Task WaitStarted => waitStarted.Task;

	public Task CancellationObserved => cancellationObserved.Task;

	public Task SessionHandlerStartedSignal => sessionHandlerStarted.Task;

	public Task RunAsyncEnteredSignal => runAsyncEntered.Task;

	public Task HandlerEnded => handlerEnded.Task;

	public Task HostStoppingSignal => hostStopping.Task;

	public int SessionHandlerStartedCount => Volatile.Read(in sessionHandlerStartedCount);

	public int RunAsyncEnteredCount => Volatile.Read(in runAsyncEnteredCount);

	public int SessionHandlerEndedCount => Volatile.Read(in sessionHandlerEndedCount);

	public int HostStoppingCount => Volatile.Read(in hostStoppingCount);

	public IReadOnlyList<string> EndedSessionHashes
	{
		get
		{
			lock (gate)
			{
				return endedSessionHashes.ToArray();
			}
		}
	}

	public IReadOnlyList<string> CancellationSessionHashes
	{
		get
		{
			lock (gate)
			{
				return cancellationSessionHashes.ToArray();
			}
		}
	}

	public McpProbeState(bool hasSessionHandler)
	{
		HasSessionHandler = hasSessionHandler;
	}

	public void SessionHandlerStarted(string? sessionId)
	{
		RequireSessionId(sessionId);
		Interlocked.Increment(ref sessionHandlerStartedCount);
		sessionHandlerStarted.TrySetResult(result: true);
	}

	public void RunAsyncEntered()
	{
		Interlocked.Increment(ref runAsyncEnteredCount);
		runAsyncEntered.TrySetResult(result: true);
	}

	public void SessionHandlerEnded(string? sessionId)
	{
		RequireSessionId(sessionId);
		lock (gate)
		{
			endedSessionHashes.Add(HashSessionId(sessionId));
		}
		Interlocked.Increment(ref sessionHandlerEndedCount);
		handlerEnded.TrySetResult(result: true);
	}

	public void HostStopping()
	{
		Interlocked.Increment(ref hostStoppingCount);
		hostStopping.TrySetResult(result: true);
	}

	public void WaitToolStarted()
	{
		waitStarted.TrySetResult(result: true);
	}

	public void CancellationWasObserved(string? sessionId)
	{
		RequireSessionId(sessionId);
		lock (gate)
		{
			cancellationSessionHashes.Add(HashSessionId(sessionId));
		}
		cancellationObserved.TrySetResult(result: true);
	}

	public static string HashSessionId(string? sessionId)
	{
		RequireSessionId(sessionId);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId!))).Substring(0, 12);
	}

	private static void RequireSessionId(string? sessionId)
	{
		if (string.IsNullOrWhiteSpace(sessionId))
		{
			throw new InvalidOperationException("stateful_session_continuity: SDK did not provide a protocol session identifier.");
		}
	}
}
