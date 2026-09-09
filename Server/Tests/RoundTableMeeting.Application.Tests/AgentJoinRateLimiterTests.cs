using System;
using System.Collections;
using System.Reflection;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using Xunit;

namespace RoundTableMeeting.Application.Tests;

public sealed class AgentJoinRateLimiterTests
{
	private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		private DateTimeOffset current = utcNow;

		public override DateTimeOffset GetUtcNow()
		{
			return current;
		}

		public void Advance(TimeSpan duration)
		{
			current += duration;
		}
	}

	[Fact]
	public void SourceMeetingNameAndConnectionLimitsAreIndependent()
	{
		AssertLimited(delegate(AgentCollaborationOptions options)
		{
			options.JoinRateLimitPerSource = 1;
		}, (int index, McpTransportSessionKey session) => (Source: "source", Meeting: $"meeting-{index}", Name: $"name-{index}", Session: session));
		AssertLimited(delegate(AgentCollaborationOptions options)
		{
			options.JoinRateLimitPerMeetingCode = 1;
		}, (int index, McpTransportSessionKey session) => (Source: $"source-{index}", Meeting: "meeting", Name: $"name-{index}", Session: session));
		AssertLimited(delegate(AgentCollaborationOptions options)
		{
			options.JoinRateLimitPerName = 1;
		}, (int index, McpTransportSessionKey session) => (Source: $"source-{index}", Meeting: $"meeting-{index}", Name: "name", Session: session));
		AssertLimited(delegate(AgentCollaborationOptions options)
		{
			options.JoinRateLimitPerConnection = 1;
		}, (int index, McpTransportSessionKey session) => (Source: $"source-{index}", Meeting: $"meeting-{index}", Name: $"name-{index}", Session: Session(1)));
	}

	[Fact]
	public void ExpiredWindowAllowsTheSameDimensionsAgain()
	{
		MutableTimeProvider mutableTimeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
		AgentCollaborationOptions agentCollaborationOptions = Options();
		agentCollaborationOptions.JoinRateLimitWindowSeconds = 10;
		agentCollaborationOptions.JoinRateLimitPerConnection = 1;
		AgentJoinRateLimiter agentJoinRateLimiter = new AgentJoinRateLimiter(agentCollaborationOptions, mutableTimeProvider);
		McpTransportSessionKey transportSession = Session(1);
		Assert.True(agentJoinRateLimiter.TryAcquire("source", "meeting", "name", transportSession, out var _));
		Assert.False(agentJoinRateLimiter.TryAcquire("source-2", "meeting-2", "name-2", transportSession, out var retryAfterSeconds2));
		Assert.True(retryAfterSeconds2 >= 10);
		mutableTimeProvider.Advance(TimeSpan.FromSeconds(10L));
		Assert.True(agentJoinRateLimiter.TryAcquire("source", "meeting", "name", transportSession, out retryAfterSeconds2));
		Assert.Equal(0, retryAfterSeconds2);
	}

	[Fact]
	public void RejectedDimensionDoesNotConsumeOtherDimensions()
	{
		AgentCollaborationOptions agentCollaborationOptions = Options();
		agentCollaborationOptions.JoinRateLimitPerSource = 1;
		agentCollaborationOptions.JoinRateLimitPerMeetingCode = 1;
		agentCollaborationOptions.JoinRateLimitPerName = 1;
		agentCollaborationOptions.JoinRateLimitPerConnection = 1;
		AgentJoinRateLimiter agentJoinRateLimiter = new AgentJoinRateLimiter(agentCollaborationOptions, TimeProvider.System);
		McpTransportSessionKey transportSession = Session(1);
		Assert.True(agentJoinRateLimiter.TryAcquire("seed-source", "seed-meeting", "seed-name", transportSession, out var retryAfterSeconds));
		Assert.False(agentJoinRateLimiter.TryAcquire("source", "meeting", "name", transportSession, out retryAfterSeconds));
		Assert.True(agentJoinRateLimiter.TryAcquire("source", "meeting", "name", Session(2), out retryAfterSeconds));
	}

	[Fact]
	public void RejectedRequestsDoNotAllocateNewDimensionCounters()
	{
		AgentCollaborationOptions agentCollaborationOptions = Options();
		agentCollaborationOptions.JoinRateLimitPerConnection = 1;
		AgentJoinRateLimiter agentJoinRateLimiter = new AgentJoinRateLimiter(agentCollaborationOptions, TimeProvider.System);
		McpTransportSessionKey transportSession = Session(1);
		Assert.True(agentJoinRateLimiter.TryAcquire("seed-source", "seed-meeting", "seed-name", transportSession, out var retryAfterSeconds));
		IDictionary dictionary = (IDictionary)typeof(AgentJoinRateLimiter).GetField("counters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agentJoinRateLimiter)!;
		Assert.Equal(4, dictionary.Count);
		Assert.False(agentJoinRateLimiter.TryAcquire("new-source", "new-meeting", "new-name", transportSession, out retryAfterSeconds));
		Assert.Equal(4, dictionary.Count);
	}

	private static void AssertLimited(Action<AgentCollaborationOptions> configure, Func<int, McpTransportSessionKey, (string Source, string Meeting, string Name, McpTransportSessionKey Session)> input)
	{
		AgentCollaborationOptions agentCollaborationOptions = Options();
		configure(agentCollaborationOptions);
		AgentJoinRateLimiter agentJoinRateLimiter = new AgentJoinRateLimiter(agentCollaborationOptions, TimeProvider.System);
		(string, string, string, McpTransportSessionKey) tuple = input(1, Session(1));
		(string, string, string, McpTransportSessionKey) tuple2 = input(2, Session(2));
		Assert.True(agentJoinRateLimiter.TryAcquire(tuple.Item1, tuple.Item2, tuple.Item3, tuple.Item4, out var _));
		Assert.False(agentJoinRateLimiter.TryAcquire(tuple2.Item1, tuple2.Item2, tuple2.Item3, tuple2.Item4, out var retryAfterSeconds2));
		Assert.True(retryAfterSeconds2 >= 1);
	}

	private static AgentCollaborationOptions Options()
	{
		return new AgentCollaborationOptions
		{
			JoinRateLimitWindowSeconds = 60,
			JoinRateLimitPerSource = 100,
			JoinRateLimitPerMeetingCode = 100,
			JoinRateLimitPerName = 100,
			JoinRateLimitPerConnection = 100
		};
	}

	private static McpTransportSessionKey Session(byte seed)
	{
		Span<byte> span = stackalloc byte[32];
		span.Fill(seed);
		return McpTransportSessionKey.FromDigest(span);
	}
}
