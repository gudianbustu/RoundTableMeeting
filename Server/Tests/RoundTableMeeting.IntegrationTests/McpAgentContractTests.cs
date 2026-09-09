using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using Xunit;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
	private sealed record McpSession(string TransportSessionId, string InitializeBody, JsonElement InitializeResult);

	private sealed record ToolCall(string Raw, JsonElement Payload);

	private sealed record McpBody(string Raw, JsonElement Json);

	private sealed class RejectingAgentConnectionRegistry : IAgentConnectionRegistry
	{
        public void TerminateMeeting(Guid meetingId) { }

		public bool ContainsTransport(McpTransportSessionKey transportSession)
		{
			return false;
		}

		public bool IsRegistered(Guid meetingId, Guid participantId, Guid connectionSessionId, string transportBinding)
		{
			return false;
		}

		public IReadOnlyList<AgentConnectionRegistration> ListRegistrations(Guid meetingId)
		{
			return [];
		}

		public IReadOnlyList<AgentConnectionRegistration> RemoveRegistrations(McpTransportSessionKey transportSession)
		{
			return [];
		}

		public bool TryGetRegistration(McpTransportSessionKey transportSession, out AgentConnectionRegistration registration)
		{
			registration = default!;
			return false;
		}

		public bool TryRegister(McpTransportSessionKey transportSession, AgentConnectionRegistration registration)
		{
			return false;
		}

		public bool Unregister(McpTransportSessionKey transportSession, Guid connectionSessionId)
		{
			return false;
		}
	}

	private static readonly string[] ExpectedToolNames = new string[4] { "get_agent_status", "join_meeting", "reply_to_mention", "wait_for_mention" };

	[Fact]
	public async Task AnonymousMcpSessionDiscoversOnlyTheVersionedAgentTools()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient client = CreateAnonymousClient(factory);
		McpSession session = await InitializeAsync(client);
		Assert.Equal("round-table-meeting-agent-messaging", session.InitializeResult.GetProperty("serverInfo").GetProperty("name").GetString());
		Assert.Equal("agent-messaging-v1", session.InitializeResult.GetProperty("serverInfo").GetProperty("version").GetString());
		Assert.DoesNotContain(session.TransportSessionId, session.InitializeBody, StringComparison.Ordinal);
		using HttpResponseMessage toolsResponse = await SendRpcAsync(client, session.TransportSessionId, new
		{
			jsonrpc = "2.0",
			id = 2,
			method = "tools/list",
			@params = new { }
		});
		McpBody toolsBody = await ReadMcpBodyAsync(toolsResponse);
		JsonElement[] tools = toolsBody.Json.GetProperty("result").GetProperty("tools").EnumerateArray()
			.ToArray();
		Assert.Equal(HttpStatusCode.OK, toolsResponse.StatusCode);
		Assert.NotEqual<string>("text/html", toolsResponse.Content.Headers.ContentType?.MediaType);
		Assert.Equal(ExpectedToolNames, from tool in tools
			select tool.GetProperty("name").GetString() into name
			orderby name
			select name);
		Assert.DoesNotContain(session.TransportSessionId, toolsBody.Raw, StringComparison.Ordinal);
		JsonElement joinSchema = tools.Single((JsonElement tool) => tool.GetProperty("name").GetString() == "join_meeting").GetProperty("inputSchema").GetProperty("properties");
		Assert.Equal(new[] { "contextCharacterLimit", "displayName", "meetingCode" }, from property in joinSchema.EnumerateObject()
			select property.Name into name
			orderby name
			select name);
		JsonElement replySchema = tools.Single((JsonElement tool) => tool.GetProperty("name").GetString() == "reply_to_mention").GetProperty("inputSchema").GetProperty("properties");
		Assert.Equal(new[] { "connectionSessionId", "content", "deliveryAttemptId", "replyIdempotencyKey", "taskId" }, from property in replySchema.EnumerateObject()
			select property.Name into name
			orderby name
			select name);
	}

	[Fact]
	public async Task StageOneToolsReturnStableErrorsWithoutLeakingTransportOrMeetingSecrets()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient client = CreateAnonymousClient(factory);
		McpSession session = await InitializeAsync(client);
		ToolCall status = await CallToolAsync(arguments: new
		{
			connectionSessionId = Guid.NewGuid()
		}, client: client, sessionId: session.TransportSessionId, requestId: 2, name: "get_agent_status");
		AssertToolFailure(status.Payload, "connection_invalid");
		AssertSafe(status.Raw, session.TransportSessionId, factory.DatabasePath);
		string meetingCode = Guid.NewGuid().ToString("D");
		ToolCall validJoin = await CallToolAsync(client, session.TransportSessionId, 3, "join_meeting", new
		{
			meetingCode = meetingCode,
			displayName = "Agenda Assistant",
			contextCharacterLimit = 12000
		});
		AssertToolFailure(validJoin.Payload, "meeting_not_found");
		AssertSafe(validJoin.Raw, session.TransportSessionId, factory.DatabasePath, meetingCode);
		ToolCall missingName = await CallToolAsync(client, session.TransportSessionId, 4, "join_meeting", new
		{
			meetingCode = meetingCode,
			displayName = "  ",
			contextCharacterLimit = 12000
		});
		AssertToolFailure(missingName.Payload, "agent_name_required");
		AssertSafe(missingName.Raw, session.TransportSessionId, factory.DatabasePath, meetingCode);
		ToolCall invalidContext = await CallToolAsync(client, session.TransportSessionId, 5, "join_meeting", new
		{
			meetingCode = meetingCode,
			displayName = "Agenda Assistant",
			contextCharacterLimit = 1
		});
		AssertToolFailure(invalidContext.Payload, "context_limit_invalid");
		AssertSafe(invalidContext.Raw, session.TransportSessionId, factory.DatabasePath, meetingCode);
	}

	[Fact]
	public async Task StatefulMcpSessionsKeepContinuityAndSeparateSessionKeys()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient client = CreateAnonymousClient(factory);
		McpSession first = await InitializeAsync(client);
		McpSession second = await InitializeAsync(client);
		Assert.NotEqual(first.TransportSessionId, second.TransportSessionId);
		using HttpResponseMessage firstTools = await SendRpcAsync(client, first.TransportSessionId, new
		{
			jsonrpc = "2.0",
			id = 2,
			method = "tools/list",
			@params = new { }
		});
		using HttpResponseMessage secondTools = await SendRpcAsync(client, second.TransportSessionId, new
		{
			jsonrpc = "2.0",
			id = 3,
			method = "tools/list",
			@params = new { }
		});
		Assert.Equal(HttpStatusCode.OK, firstTools.StatusCode);
		Assert.Equal(HttpStatusCode.OK, secondTools.StatusCode);
		Assert.Equal(4, (await ReadMcpBodyAsync(firstTools)).Json.GetProperty("result").GetProperty("tools").GetArrayLength());
		Assert.Equal(4, (await ReadMcpBodyAsync(secondTools)).Json.GetProperty("result").GetProperty("tools").GetArrayLength());
		using HttpResponseMessage invalidSession = await SendRpcAsync(client, "invalid-session", new
		{
			jsonrpc = "2.0",
			id = 4,
			method = "tools/list",
			@params = new { }
		});
		HttpStatusCode statusCode = invalidSession.StatusCode;
		bool condition = ((statusCode == HttpStatusCode.BadRequest || statusCode == HttpStatusCode.NotFound) ? true : false);
		Assert.True(condition, $"session_isolation: unexpected invalid-session status {(int)invalidSession.StatusCode}.");
	}

	[Fact]
	public async Task JoinCreatesOneAgentIdentityConnectionCursorAndInitialSnapshot()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient browser = CreateBrowserClient(factory);
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Join Snapshot");
		await PostHumanMessageAsync(browser, meeting.MeetingId, "welcome to the table");
		using HttpClient agent = CreateAnonymousClient(factory);
		McpSession session = await InitializeAsync(agent);
		ToolCall join = await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Analyst",
			contextCharacterLimit = 256
		});
		Assert.True(join.Payload.GetProperty("succeeded").GetBoolean());
		JsonElement data = join.Payload.GetProperty("data");
		Guid connectionSessionId = data.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
		JsonElement context = data.GetProperty("initialContext");
		Assert.Equal(meeting.MeetingId, data.GetProperty("meeting").GetProperty("meetingId").GetGuid());
		Assert.Equal("Analyst", data.GetProperty("agent").GetProperty("displayName").GetString());
		Assert.Equal("prepared", data.GetProperty("connection").GetProperty("initialContextState").GetString());
		Assert.Equal("resident-instruction-v1", data.GetProperty("residentInstructionVersion").GetString());
		Assert.Equal("wait_for_mention", data.GetProperty("nextAction").GetString());
		Assert.Equal(2, data.GetProperty("participants").GetArrayLength());
		Assert.Equal("agent-context-v1", context.GetProperty("contextFormatVersion").GetString());
		Assert.Equal(1L, context.GetProperty("snapshotSequence").GetInt64());
		Assert.Equal(1L, context.GetProperty("candidateFirstSequence").GetInt64());
		Assert.Equal(1L, context.GetProperty("candidateLastSequence").GetInt64());
		Assert.Equal(1L, context.GetProperty("deliveredFirstSequence").GetInt64());
		Assert.Equal(1L, context.GetProperty("deliveredLastSequence").GetInt64());
		Assert.Equal("welcome to the table", context.GetProperty("messages")[0].GetProperty("content").GetString());
		Assert.DoesNotContain(session.TransportSessionId, join.Raw, StringComparison.Ordinal);
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		Assert.Equal(1, await db.MeetingParticipants.CountAsync((MeetingParticipant item) => item.MeetingId == meeting.MeetingId && (int)item.ParticipantType == 1));
		Assert.Equal(1, await db.AgentConnectionSessions.CountAsync((AgentConnectionSession item) => item.MeetingId == meeting.MeetingId && item.ID == connectionSessionId && (int)item.State == 0));
		AgentContextCursor cursor = await db.AgentContextCursors.SingleAsync((AgentContextCursor item) => item.MeetingId == meeting.MeetingId && item.ParticipantId == ((JsonElement)data).GetProperty("agent").GetProperty("participantId").GetGuid());
		Assert.Equal(0L, cursor.DeliveredThroughSequence);
		Assert.Equal(1L, cursor.AllocatedThroughSequence);
		Assert.NotEqual("", (await db.AgentConnectionSessions.SingleAsync((AgentConnectionSession item) => item.ID == connectionSessionId)).McpTransportBinding);
	}

	[Fact]
	public async Task HumanMentionIsMaterializedBeforeTheDurableAgentDeliveryCompletes()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient browser = CreateBrowserClient(factory);
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Durable Delivery");
		using HttpClient agent = CreateAnonymousClient(factory);
		ToolCall join = await CallToolAsync(agent, (await InitializeAsync(agent)).TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "DispatcherAgent",
			contextCharacterLimit = 256
		});
		Assert.True(join.Payload.GetProperty("succeeded").GetBoolean());
		Guid agentParticipantId = join.Payload.GetProperty("data").GetProperty("agent").GetProperty("participantId")
			.GetGuid();
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meeting.MeetingId}/messages")
		{
			Content = JsonContent.Create(new
			{
				clientSubmissionId = Guid.NewGuid(),
				content = "@DispatcherAgent please respond"
			})
		};
		request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
		using HttpResponseMessage response = await browser.SendAsync(request);
		JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		Guid messageId = body.GetProperty("message").GetProperty("messageId").GetGuid();
		for (int attempt = 0; attempt < 100; attempt++)
		{
			await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
			RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
			MessageEventDeliveryState? deliveryState = await (from delivery in db.MessageEventDeliveries.AsNoTracking()
				join appendedEvent in db.MessageAppendedEvents.AsNoTracking() on delivery.EventId equals appendedEvent.ID
				where appendedEvent.MessageId == messageId && delivery.ConsumerName == "agent-mention-task-v1"
				select (MessageEventDeliveryState?)delivery.State).SingleOrDefaultAsync();
			AgentReplyTask? task = await db.AgentReplyTasks.AsNoTracking().SingleOrDefaultAsync((AgentReplyTask item) => item.MeetingId == meeting.MeetingId && item.AgentParticipantId == agentParticipantId && item.TriggerMessageId == messageId);
			if (deliveryState == MessageEventDeliveryState.Completed && task != null)
			{
				Assert.Equal(AgentReplyTaskState.Queued, task.State);
				Assert.Equal(AgentReplyIdempotencyKeyFactory.Create(task.ID), task.ReplyIdempotencyKey);
				return;
			}
			Assert.NotEqual(MessageEventDeliveryState.Suspended, deliveryState);
			await Task.Delay(100);
		}
		Assert.Fail("The durable Agent mention delivery did not commit its task before completion.");
	}

	[Fact]
	public async Task SameMcpSessionKeepsListeningReceivesMentionRepliesAndPublishesAgentMessage()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient browser = CreateBrowserClient(factory);
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Continuous Listening");
		using HttpClient agent = CreateAnonymousClient(factory);
		agent.Timeout = TimeSpan.FromSeconds(20);
		McpSession session = await InitializeAsync(agent);
		ToolCall join = await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "DispatcherAgent",
			contextCharacterLimit = 256
		});
		Assert.True(join.Payload.GetProperty("succeeded").GetBoolean());
		JsonElement joinData = join.Payload.GetProperty("data");
		Guid connectionSessionId = joinData.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();

		Task<ToolCall> waitTask = CallToolAsync(agent, session.TransportSessionId, 3, "wait_for_mention", new
		{
			connectionSessionId
		}, useSessionEventStream: true);
		await Task.Delay(250);

		(Guid TriggerMessageId, Guid TriggerMentionId, Guid SourceParticipantId, Guid MentionedAgentParticipantId) humanMessage = await PostHumanMentionAsync(
			factory,
			browser,
			meeting.MeetingId,
			"@DispatcherAgent please summarize the decision");
		ToolCall wait;
		try
		{
			wait = await waitTask;
		}
		catch (Exception exception)
		{
			string logs = string.Join("\n", factory.LogSink.Events.Select(item => item.RenderMessage() + " " + item.Exception));
			throw new Xunit.Sdk.XunitException($"wait_for_mention failed: {exception.Message}\n{logs}", exception);
		}
		Assert.True(wait.Payload.GetProperty("succeeded").GetBoolean(), wait.Raw);
		JsonElement mention = wait.Payload.GetProperty("data");
		Assert.Equal("mention", mention.GetProperty("kind").GetString());
		Assert.Equal(humanMessage.TriggerMessageId, mention.GetProperty("triggerMessageId").GetGuid());
		Assert.Equal(humanMessage.TriggerMentionId, mention.GetProperty("triggerMentionId").GetGuid());
		Assert.Equal(humanMessage.MentionedAgentParticipantId, joinData.GetProperty("agent").GetProperty("participantId").GetGuid());
		Assert.Equal(humanMessage.SourceParticipantId, mention.GetProperty("sourceParticipantId").GetGuid());
		Assert.Equal("processing", mention.GetProperty("taskState").GetString());

		ToolCall reply = await CallToolAsync(agent, session.TransportSessionId, 4, "reply_to_mention", new
		{
			connectionSessionId,
			taskId = mention.GetProperty("taskId").GetGuid(),
			deliveryAttemptId = mention.GetProperty("deliveryAttemptId").GetGuid(),
			replyIdempotencyKey = mention.GetProperty("replyIdempotencyKey").GetGuid(),
			content = "The decision is recorded and ready for the next step."
		});
		Assert.True(reply.Payload.GetProperty("succeeded").GetBoolean(), reply.Raw);
		JsonElement replyMessage = reply.Payload.GetProperty("data").GetProperty("message");
		Guid replyMessageId = replyMessage.GetProperty("messageId").GetGuid();
		Assert.Equal("agent", replyMessage.GetProperty("author").GetProperty("kind").GetString());
		Assert.Equal("DispatcherAgent", replyMessage.GetProperty("author").GetProperty("displayName").GetString());
		Assert.Equal("The decision is recorded and ready for the next step.", replyMessage.GetProperty("content").GetString());

		ToolCall replay = await CallToolAsync(agent, session.TransportSessionId, 5, "reply_to_mention", new
		{
			connectionSessionId,
			taskId = mention.GetProperty("taskId").GetGuid(),
			deliveryAttemptId = mention.GetProperty("deliveryAttemptId").GetGuid(),
			replyIdempotencyKey = mention.GetProperty("replyIdempotencyKey").GetGuid(),
			content = "The decision is recorded and ready for the next step."
		});
		Assert.True(replay.Payload.GetProperty("succeeded").GetBoolean(), replay.Raw);
		Assert.True(replay.Payload.GetProperty("data").GetProperty("replayed").GetBoolean());
		Assert.Equal(replyMessageId, replay.Payload.GetProperty("data").GetProperty("message").GetProperty("messageId").GetGuid());

		ToolCall status = await CallToolAsync(agent, session.TransportSessionId, 6, "get_agent_status", new
		{
			connectionSessionId
		});
		Assert.True(status.Payload.GetProperty("succeeded").GetBoolean(), status.Raw);
		JsonElement statusData = status.Payload.GetProperty("data");
		Assert.Equal("connected", statusData.GetProperty("connection").GetProperty("state").GetString());
		Assert.Equal(1, statusData.GetProperty("taskCounts").GetProperty("replied").GetInt32());
		Assert.Equal(JsonValueKind.Null, statusData.GetProperty("queueHead").ValueKind);

		await WaitForPersistedAgentReplyAsync(factory, meeting.MeetingId, humanMessage.TriggerMessageId, replyMessageId, humanMessage.MentionedAgentParticipantId);
		using HttpResponseMessage historyResponse = await browser.GetAsync($"/api/meetings/{meeting.MeetingId}/messages?afterSequence=0&limit=20");
		Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
		JsonElement history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
		JsonElement[] items = history.GetProperty("items").EnumerateArray().ToArray();
		JsonElement visibleReply = Assert.Single(items, item => item.GetProperty("messageId").GetGuid() == replyMessageId);
		Assert.Equal("agent", visibleReply.GetProperty("author").GetProperty("kind").GetString());
		Assert.Equal("The decision is recorded and ready for the next step.", visibleReply.GetProperty("content").GetString());
	}

	[Fact]
	public async Task AgentJoinRejectsHumanNameActiveConnectionAndDisconnectedRecovery()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient browser = CreateBrowserClient(factory);
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Join Boundaries");
		using HttpClient humanNameAgent = CreateAnonymousClient(factory);
		AssertToolFailure((await CallToolAsync(humanNameAgent, (await InitializeAsync(humanNameAgent)).TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Alice",
			contextCharacterLimit = 256
		})).Payload, "agent_name_in_use");
		using HttpClient firstAgent = CreateAnonymousClient(factory);
		ToolCall firstJoin = await CallToolAsync(firstAgent, (await InitializeAsync(firstAgent)).TransportSessionId, 3, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Reviewer",
			contextCharacterLimit = 256
		});
		Assert.True(firstJoin.Payload.GetProperty("succeeded").GetBoolean());
		using HttpClient activeAgent = CreateAnonymousClient(factory);
		AssertToolFailure((await CallToolAsync(activeAgent, (await InitializeAsync(activeAgent)).TransportSessionId, 4, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Reviewer",
			contextCharacterLimit = 256
		})).Payload, "agent_connection_already_active");
		Guid reviewerId = firstJoin.Payload.GetProperty("data").GetProperty("agent").GetProperty("participantId")
			.GetGuid();
		await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
		{
			RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
			await db.AgentConnectionSessions.Where((AgentConnectionSession item) => item.MeetingId == meeting.MeetingId && item.ParticipantId == reviewerId).ExecuteUpdateAsync(delegate(UpdateSettersBuilder<AgentConnectionSession> setters)
			{
				setters.SetProperty((AgentConnectionSession item) => item.State, AgentConnectionState.Disconnected).SetProperty((AgentConnectionSession item) => item.DisconnectedAtUtc, DateTimeOffset.UtcNow).SetProperty((AgentConnectionSession item) => item.LastActivityAtUtc, DateTimeOffset.UtcNow);
			});
		}
		using HttpClient recoveryAgent = CreateAnonymousClient(factory);
		AssertToolFailure((await CallToolAsync(recoveryAgent, (await InitializeAsync(recoveryAgent)).TransportSessionId, 5, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Reviewer",
			contextCharacterLimit = 256
		})).Payload, "agent_recovery_not_supported");
		await using AsyncServiceScope verifyScope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		Assert.Equal(1, await verifyDb.MeetingParticipants.CountAsync((MeetingParticipant item) => item.MeetingId == meeting.MeetingId && (int)item.ParticipantType == 1 && item.DisplayName == "Reviewer"));
		Assert.Equal(1, await verifyDb.AgentContextCursors.CountAsync((AgentContextCursor item) => item.MeetingId == meeting.MeetingId && item.ParticipantId == reviewerId));
	}

	[Fact]
	public async Task IdleWaitsCanRepeatOnTheSameMcpSession()
	{
		using TestApplicationFactory factory = new TestApplicationFactory(1);
		using HttpClient browser = CreateBrowserClient(factory);
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Repeated Idle");
		using HttpClient agent = CreateAnonymousClient(factory);
		McpSession session = await InitializeAsync(agent);
		JsonElement joinData = (await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Repeated Idle Agent",
			contextCharacterLimit = 256
		})).Payload.GetProperty("data");
		Guid connectionSessionId = joinData.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();

		ToolCall firstWait = await CallToolAsync(agent, session.TransportSessionId, 3, "wait_for_mention", new { connectionSessionId });
		ToolCall secondWait = await CallToolAsync(agent, session.TransportSessionId, 4, "wait_for_mention", new { connectionSessionId });
		Assert.True(firstWait.Payload.GetProperty("succeeded").GetBoolean(), firstWait.Raw);
		Assert.True(secondWait.Payload.GetProperty("succeeded").GetBoolean(), secondWait.Raw);
		Assert.Equal("idle", firstWait.Payload.GetProperty("data").GetProperty("kind").GetString());
		Assert.Equal("idle", secondWait.Payload.GetProperty("data").GetProperty("kind").GetString());

		ToolCall status = await CallToolAsync(agent, session.TransportSessionId, 5, "get_agent_status", new { connectionSessionId });
		Assert.True(status.Payload.GetProperty("succeeded").GetBoolean(), status.Raw);
		Assert.Equal("connected", status.Payload.GetProperty("data").GetProperty("connection").GetProperty("state").GetString());
	}

	[Fact]
	public async Task CancellingAnIdleWaitLeavesTheSameMcpSessionUsable()
	{
		using TestApplicationFactory factory = new TestApplicationFactory(30);
		using HttpClient browser = CreateBrowserClient(factory);
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Cancelled Wait");
		using HttpClient agent = CreateAnonymousClient(factory);
		McpSession session = await InitializeAsync(agent);
		JsonElement joinData = (await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Cancelled Wait Agent",
			contextCharacterLimit = 256
		})).Payload.GetProperty("data");
		Guid connectionSessionId = joinData.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();

		Task<HttpResponseMessage> waitRequest = SendRpcAsync(agent, session.TransportSessionId, new
		{
			jsonrpc = "2.0",
			id = 3,
			method = "tools/call",
			@params = new { name = "wait_for_mention", arguments = new { connectionSessionId } }
		});
		await Task.Delay(250);
		using HttpClient cancellationClient = CreateAnonymousClient(factory);
		using HttpResponseMessage cancellation = await SendCancelledAsync(cancellationClient, session.TransportSessionId, 3);
		Assert.True((int)cancellation.StatusCode is >= 200 and < 300, $"MCP cancellation returned HTTP {(int)cancellation.StatusCode}.");
		using HttpResponseMessage accepted = await waitRequest.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(accepted.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
			$"MCP wait request returned unexpected HTTP {(int)accepted.StatusCode}.");

		ToolCall wait = await CallToolAsync(agent, session.TransportSessionId, 4, "wait_for_mention", new { connectionSessionId });
		Assert.True(wait.Payload.GetProperty("succeeded").GetBoolean(), wait.Raw);
		Assert.Equal("idle", wait.Payload.GetProperty("data").GetProperty("kind").GetString());
		ToolCall status = await CallToolAsync(agent, session.TransportSessionId, 5, "get_agent_status", new { connectionSessionId });
		Assert.True(status.Payload.GetProperty("succeeded").GetBoolean(), status.Raw);
		Assert.Equal("connected", status.Payload.GetProperty("data").GetProperty("connection").GetProperty("state").GetString());
	}

	[Fact]
	public async Task McpDeleteDisconnectsAgentButKeepsItsIdentityAndMessages()
	{
		using TestApplicationFactory factory = new TestApplicationFactory(5);
		using HttpClient browser = CreateBrowserClient(factory);
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Explicit Close");
		await PostHumanMessageAsync(browser, meeting.MeetingId, "message retained after close");
		using HttpClient agent = CreateAnonymousClient(factory);
		McpSession session = await InitializeAsync(agent);
		ToolCall join = await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Explicit Close Agent",
			contextCharacterLimit = 256
		});
		JsonElement data = join.Payload.GetProperty("data");
		Guid agentParticipantId = data.GetProperty("agent").GetProperty("participantId").GetGuid();
		Guid connectionSessionId = data.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();

		using HttpResponseMessage delete = await DeleteSessionAsync(agent, session.TransportSessionId);
		Assert.True((int)delete.StatusCode is >= 200 and < 300, $"MCP DELETE returned HTTP {(int)delete.StatusCode}.");
		for (int attempt = 0; attempt < 50; attempt++)
		{
			await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
			RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
			AgentConnectionSession? connection = await db.AgentConnectionSessions.AsNoTracking().SingleOrDefaultAsync(item => item.ID == connectionSessionId);
			if (connection?.State == AgentConnectionState.Disconnected)
			{
				Assert.Equal(1, await db.MeetingParticipants.CountAsync(item => item.MeetingId == meeting.MeetingId && item.ID == agentParticipantId));
				using HttpResponseMessage historyResponse = await browser.GetAsync($"/api/meetings/{meeting.MeetingId}/messages?afterSequence=0&limit=20");
				Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
				JsonElement history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
				Assert.Contains(history.GetProperty("items").EnumerateArray(), item => item.GetProperty("content").GetString() == "message retained after close");
				return;
			}
		await Task.Delay(250);
		}
		Assert.Fail("MCP DELETE did not transition the Agent connection to disconnected.");
	}

	[Fact]
	public async Task RegistrationFailureCompensatesPersistedConnectionWithoutLeakingTransportBinding()
	{
		using TestApplicationFactory baseFactory = new TestApplicationFactory();
		using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(delegate(IWebHostBuilder builder)
		{
			builder.ConfigureTestServices(delegate(IServiceCollection services)
			{
				services.RemoveAll<IAgentConnectionRegistry>();
				services.AddSingleton<IAgentConnectionRegistry, RejectingAgentConnectionRegistry>();
			});
		});
		using HttpClient browser = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = true
		});
		await LoginAsync(browser, "alice", "test-password-alice");
		(Guid MeetingId, string MeetingCode) meeting = await CreateMeetingAsync(browser, "AI Registration Compensation");
		using HttpClient agent = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false
		});
		McpSession session = await InitializeAsync(agent);
		ToolCall join = await CallToolAsync(agent, session.TransportSessionId, 2, "join_meeting", new
		{
			meetingCode = meeting.MeetingCode,
			displayName = "Compensation Agent",
			contextCharacterLimit = 256
		});
		AssertToolFailure(join.Payload, "agent_operation_unavailable");
		Assert.DoesNotContain(session.TransportSessionId, join.Raw, StringComparison.Ordinal);
		Assert.DoesNotContain(baseFactory.DatabasePath, join.Raw, StringComparison.OrdinalIgnoreCase);
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		MeetingParticipant agentParticipant = await db.MeetingParticipants.SingleAsync((MeetingParticipant item) => item.MeetingId == meeting.MeetingId && item.DisplayName == "Compensation Agent");
		Assert.Equal(ParticipantType.Agent, agentParticipant.ParticipantType);
		Assert.NotNull(await db.AgentContextCursors.SingleOrDefaultAsync((AgentContextCursor item) => item.MeetingId == meeting.MeetingId && item.ParticipantId == agentParticipant.ID));
		AgentConnectionSession connection = await db.AgentConnectionSessions.SingleAsync((AgentConnectionSession item) => item.MeetingId == meeting.MeetingId && item.ParticipantId == agentParticipant.ID);
		Assert.Equal(AgentConnectionState.Disconnected, connection.State);
		Assert.NotEqual(session.TransportSessionId, connection.McpTransportBinding);
	}

	[Fact]
	public async Task McpPathsNeverFallThroughToTheSpaDocument()
	{
		using TestApplicationFactory factory = new TestApplicationFactory();
		using HttpClient client = CreateAnonymousClient(factory);
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/mcp/not-a-route");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
		using HttpResponseMessage response = await client.SendAsync(request);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.NotEqual<string>("text/html", response.Content.Headers.ContentType?.MediaType);
		Assert.DoesNotContain("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
	}

	private static HttpClient CreateAnonymousClient(TestApplicationFactory factory)
	{
		return factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false
		});
	}

	private static HttpClient CreateBrowserClient(TestApplicationFactory factory)
	{
		return factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = true
		});
	}

	private static async Task<(Guid MeetingId, string MeetingCode)> CreateMeetingAsync(HttpClient client, string name)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
		{
			Content = JsonContent.Create(new
			{
				creationRequestId = Guid.NewGuid(),
				name = name
			})
		};
		request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
		HttpResponseMessage response = await client.SendAsync(request);
		JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		return (
			MeetingId: body.GetProperty("meeting").GetProperty("meetingId").GetGuid(),
			MeetingCode: body.GetProperty("invitation").GetProperty("meetingCode").GetString()
				?? throw new InvalidDataException("The meeting response did not contain an invitation code."));
	}

	private static async Task LoginAsync(HttpClient client, string username, string password)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
		{
			Content = JsonContent.Create(new { username, password })
		};
		request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
		Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
	}

	private static async Task PostHumanMessageAsync(HttpClient client, Guid meetingId, string content)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meetingId}/messages")
		{
			Content = JsonContent.Create(new
			{
				clientSubmissionId = Guid.NewGuid(),
				content = content
			})
		};
		request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
		Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
	}

	private static async Task<(Guid TriggerMessageId, Guid TriggerMentionId, Guid SourceParticipantId, Guid MentionedAgentParticipantId)> PostHumanMentionAsync(
		TestApplicationFactory factory,
		HttpClient client,
		Guid meetingId,
		string content)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meetingId}/messages")
		{
			Content = JsonContent.Create(new
			{
				clientSubmissionId = Guid.NewGuid(),
				content
			})
		};
		request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
		using HttpResponseMessage response = await client.SendAsync(request);
		JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		JsonElement message = body.GetProperty("message");
		JsonElement mention = Assert.Single(message.GetProperty("mentions").EnumerateArray());
		Guid messageId = message.GetProperty("messageId").GetGuid();
		await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
		RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
		MessageMention persistedMention = await db.MessageMentions.AsNoTracking().SingleAsync(item => item.MeetingId == meetingId && item.MessageId == messageId);
		return (
			messageId,
			persistedMention.ID,
			message.GetProperty("author").GetProperty("participantId").GetGuid(),
			mention.GetProperty("participantId").GetGuid());
	}

	private static async Task WaitForPersistedAgentReplyAsync(TestApplicationFactory factory, Guid meetingId, Guid triggerMessageId, Guid replyMessageId, Guid agentParticipantId)
	{
		for (int attempt = 0; attempt < 100; attempt++)
		{
			await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
			RoundTableMeetingDbContext db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
			AgentReplyTask? task = await db.AgentReplyTasks.AsNoTracking().SingleOrDefaultAsync(item => item.MeetingId == meetingId && item.AgentParticipantId == agentParticipantId && item.TriggerMessageId == triggerMessageId);
			MeetingMessage[] replies = await db.MeetingMessages.AsNoTracking().Where(item => item.MeetingId == meetingId && item.ID == replyMessageId && item.AuthorParticipantId == agentParticipantId && item.AuthorKind == RoundTableMeeting.Domain.Enums.MessageAuthorKind.Agent).ToArrayAsync();
			int eventCount = await (from appendedEvent in db.MessageAppendedEvents.AsNoTracking()
				join message in db.MeetingMessages.AsNoTracking() on appendedEvent.MessageId equals message.ID
				where appendedEvent.MeetingId == meetingId && message.ID == replyMessageId
				select appendedEvent).CountAsync();
			int deliveryCount = await (from delivery in db.MessageEventDeliveries.AsNoTracking()
				join appendedEvent in db.MessageAppendedEvents.AsNoTracking() on delivery.EventId equals appendedEvent.ID
				where appendedEvent.MeetingId == meetingId && appendedEvent.MessageId == replyMessageId && delivery.ConsumerName == "agent-mention-task-v1" && delivery.State == MessageEventDeliveryState.Completed
				select delivery).CountAsync();
			if (task?.State == AgentReplyTaskState.Replied && task.ReplyMessageId == replyMessageId && replies.Length == 1 && eventCount == 1 && deliveryCount == 1)
			{
				Assert.Equal(1, await db.MeetingMessages.AsNoTracking().CountAsync(item => item.MeetingId == meetingId && item.ID == replyMessageId));
				Assert.Equal(1, await db.MessageAppendedEvents.AsNoTracking().CountAsync(item => item.MeetingId == meetingId && item.MessageId == replyMessageId));
				return;
			}
			await Task.Delay(100);
		}
		Assert.Fail("The Agent reply did not finish the unified message flow.");
	}

	private static async Task<McpSession> InitializeAsync(HttpClient client)
	{
		using HttpResponseMessage response = await SendRpcAsync(client, null, new
		{
			jsonrpc = "2.0",
			id = 1,
			method = "initialize",
			@params = new
			{
				protocolVersion = "2025-11-25",
				capabilities = new { },
				clientInfo = new
				{
					name = "round-table-meeting-integration-tests",
					version = "1"
				}
			}
		});
		McpBody body = await ReadMcpBodyAsync(response);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.NotEqual<string>("text/html", response.Content.Headers.ContentType?.MediaType);
		Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values));
		return new McpSession(values!.Single(), body.Raw, body.Json.GetProperty("result").Clone());
	}

	private static async Task<ToolCall> CallToolAsync(
		HttpClient client,
		string sessionId,
		int requestId,
		string name,
		object arguments,
		bool useSessionEventStream = false)
	{
		HttpResponseMessage? eventResponse = null;
		Task<McpBody>? eventBodyTask = null;
		if (useSessionEventStream)
		{
			eventResponse = await OpenSessionEventStreamAsync(client, sessionId);
			eventBodyTask = ReadFirstMcpEventAsync(eventResponse);
		}

		using HttpResponseMessage response = await SendRpcAsync(client, sessionId, new
		{
			jsonrpc = "2.0",
			id = requestId,
			method = "tools/call",
			@params = new { name, arguments }
		});
		McpBody body;
		if (response.StatusCode == HttpStatusCode.Accepted)
		{
			Assert.NotNull(eventBodyTask);
			body = await eventBodyTask;
		}
		else
		{
			body = await ReadMcpBodyAsync(response);
		}
		eventResponse?.Dispose();
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.NotEqual<string>("text/html", response.Content.Headers.ContentType?.MediaType);
		JsonElement result = body.Json.GetProperty("result");
		if (result.TryGetProperty("structuredContent", out var structured))
		{
			return new ToolCall(body.Raw, structured.Clone());
		}
		string textContent = result.GetProperty("content").EnumerateArray().Single((JsonElement content) => content.GetProperty("type").GetString() == "text")
			.GetProperty("text")
			.GetString() ?? throw new InvalidDataException("The MCP tool response did not contain text content.");
		try
		{
			return new ToolCall(Payload: JsonDocument.Parse(textContent).RootElement.Clone(), Raw: body.Raw);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException(
				$"MCP response id={body.Json.GetProperty("id").GetRawText()} returned non-JSON text: {textContent}",
				exception);
		}
	}

	private static async Task<HttpResponseMessage> OpenSessionEventStreamAsync(HttpClient client, string sessionId)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Headers.Add("Mcp-Session-Id", sessionId);
		HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.NotEqual<string>("text/html", response.Content.Headers.ContentType?.MediaType);
		return response;
	}

	private static async Task<McpBody> ReadFirstMcpEventAsync(HttpResponseMessage response)
	{
		await using Stream stream = await response.Content.ReadAsStreamAsync();
		using StreamReader reader = new StreamReader(stream);
		while (await reader.ReadLineAsync() is { } line)
		{
			if (!line.StartsWith("data:", StringComparison.Ordinal))
				continue;
			string json = line[5..].Trim();
			if (json.Length == 0)
				continue;
			return new McpBody(line, JsonDocument.Parse(json).RootElement.Clone());
		}
		throw new InvalidDataException("The MCP session event stream ended before a JSON-RPC response was received.");
	}

	private static async Task<HttpResponseMessage> SendRpcAsync(HttpClient client, string? sessionId, object payload, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = JsonContent.Create(payload)
		};
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		if (sessionId != null)
		{
			request.Headers.Add("Mcp-Session-Id", sessionId);
		}
		return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
	}

	private static async Task<HttpResponseMessage> DeleteSessionAsync(HttpClient client, string sessionId)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
		request.Headers.Add("Mcp-Session-Id", sessionId);
		return await client.SendAsync(request);
	}

	private static async Task<HttpResponseMessage> SendCancelledAsync(HttpClient client, string sessionId, int requestId)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = JsonContent.Create(new
			{
				jsonrpc = "2.0",
				method = "notifications/cancelled",
				@params = new { requestId }
			})
		};
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Headers.Add("Mcp-Session-Id", sessionId);
		return await client.SendAsync(request);
	}

	private static async Task<McpBody> ReadMcpBodyAsync(HttpResponseMessage response)
	{
		string raw = await response.Content.ReadAsStringAsync();
		string? data = (from line in raw.Split('\n')
			select line.Trim()).FirstOrDefault((string line) => line.StartsWith("data: ", StringComparison.Ordinal));
		JsonElement json = JsonDocument.Parse((data == null) ? raw.Trim() : data.Substring(6).Trim()).RootElement.Clone();
		return new McpBody(raw, json);
	}

	private static void AssertToolFailure(JsonElement payload, string errorCode)
	{
		Assert.Equal("agent-messaging-v1", payload.GetProperty("protocolVersion").GetString());
		Assert.False(payload.GetProperty("succeeded").GetBoolean());
		Assert.Equal(errorCode, payload.GetProperty("errorCode").GetString());
		Assert.Equal(JsonValueKind.Null, payload.GetProperty("data").ValueKind);
	}

	private static void AssertSafe(string body, string transportSessionId, string databasePath, params string[] secrets)
	{
		Assert.DoesNotContain(transportSessionId, body, StringComparison.Ordinal);
		Assert.DoesNotContain(databasePath, body, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
		Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
		foreach (string expectedSubstring in secrets)
		{
			Assert.DoesNotContain(expectedSubstring, body, StringComparison.Ordinal);
		}
	}
}
