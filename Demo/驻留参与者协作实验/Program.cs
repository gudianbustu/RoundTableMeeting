using System.ComponentModel;
using System.Threading.Channels;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ResidentLabState>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/lab", (ResidentLabState state) => Results.Ok(state.GetSnapshot()));
app.MapPost("/api/lab/messages", (PostLabMessageRequest request, ResidentLabState state) =>
{
    var result = state.PostUserMessage(request.Content);
    return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapPost("/api/lab/reset", (ResidentLabState state) => Results.Ok(state.Reset()));
app.MapMcp("/mcp");
app.MapFallbackToFile("index.html");

app.Run();

public sealed record PostLabMessageRequest(string? Content);

[McpServerToolType]
public static class ResidentLabTools
{
    [McpServerTool(Name = "get_lab_session")]
    [Description("Get the active AICollaboration resident-participant experiment session, invite code, participant, and messages.")]
    public static object GetLabSession(ResidentLabState state) => state.GetSnapshot();

    [McpServerTool(Name = "join_session")]
    [Description("Join the resident-participant experiment with its invite code. Call wait_for_turn immediately after joining.")]
    public static object JoinSession(
        ResidentLabState state,
        [Description("Invite code displayed in the experiment web page.")] string inviteCode,
        [Description("The name users will mention, for example CodexResident.")] string agentName)
        => state.Join(inviteCode, agentName);

    [McpServerTool(Name = "wait_for_turn")]
    [Description("Wait until the web user mentions this participant. This call intentionally blocks until a turn arrives.")]
    public static async Task<object> WaitForTurnAsync(
        ResidentLabState state,
        [Description("Participant ID returned by join_session.")] string participantId,
        CancellationToken cancellationToken)
        => await state.WaitForTurnAsync(participantId, cancellationToken);

    [McpServerTool(Name = "complete_turn")]
    [Description("Submit the participant response for a received turn. After this succeeds, call wait_for_turn again.")]
    public static object CompleteTurn(
        ResidentLabState state,
        [Description("Participant ID returned by join_session.")] string participantId,
        [Description("Turn ID returned by wait_for_turn.")] string turnId,
        [Description("The final response that should appear in the collaboration web page.")] string content)
        => state.CompleteTurn(participantId, turnId, content);
}

public sealed class ResidentLabState
{
    private readonly object gate = new();
    private Channel<LabTurn> turns = CreateChannel();
    private Guid sessionId = Guid.NewGuid();
    private string inviteCode = CreateInviteCode();
    private LabParticipant? participant;
    private readonly List<LabMessage> messages = [];
    private readonly Dictionary<Guid, LabTurn> claimedTurns = [];
    private long nextSequence;

    public object GetSnapshot()
    {
        lock (gate)
        {
            return new
            {
                sessionId,
                inviteCode,
                participant,
                messages = messages.ToArray()
            };
        }
    }

    public object Reset()
    {
        lock (gate)
        {
            turns.Writer.TryComplete();
            turns = CreateChannel();
            sessionId = Guid.NewGuid();
            inviteCode = CreateInviteCode();
            participant = null;
            messages.Clear();
            claimedTurns.Clear();
            nextSequence = 0;
            return GetSnapshot();
        }
    }

    public object Join(string suppliedInviteCode, string agentName)
    {
        lock (gate)
        {
            if (!string.Equals(inviteCode, suppliedInviteCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                return new { joined = false, error = "InviteCodeInvalid", message = "The invite code is invalid or expired." };
            if (string.IsNullOrWhiteSpace(agentName))
                return new { joined = false, error = "AgentNameRequired", message = "Agent name is required." };

            participant = new LabParticipant(Guid.NewGuid(), agentName.Trim(), DateTimeOffset.UtcNow);
            AddMessage("system", null, $"{participant.AgentName} joined the session.");
            return new
            {
                joined = true,
                sessionId,
                participantId = participant.Id,
                agentName = participant.AgentName,
                nextAction = "Call wait_for_turn with participantId and keep waiting. After a turn arrives, complete it and call wait_for_turn again."
            };
        }
    }

    public LabPostResult PostUserMessage(string? content)
    {
        lock (gate)
        {
            var normalized = content?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return new LabPostResult(false, "MessageRequired", "Please enter a message.");

            AddMessage("user", null, normalized);
            if (participant is null)
                return new LabPostResult(false, "ParticipantOffline", "No Codex participant has joined yet.");

            var mention = "@" + participant.AgentName;
            if (!normalized.Contains(mention, StringComparison.OrdinalIgnoreCase))
                return new LabPostResult(true, null, "Message added; no participant was mentioned.");

            var turn = new LabTurn(Guid.NewGuid(), sessionId, participant.Id, normalized, DateTimeOffset.UtcNow);
            if (!turns.Writer.TryWrite(turn))
                return new LabPostResult(false, "TurnQueueUnavailable", "The participant queue is unavailable.");
            return new LabPostResult(true, null, "Turn queued for the resident Codex participant.", turn.Id);
        }
    }

    public async Task<object> WaitForTurnAsync(string participantId, CancellationToken cancellationToken)
    {
        var parsedParticipantId = ParseParticipantId(participantId);
        ValidateParticipant(parsedParticipantId);

        var turn = await turns.Reader.ReadAsync(cancellationToken);
        lock (gate)
        {
            ValidateParticipant(parsedParticipantId);
            claimedTurns[turn.Id] = turn;
            return new
            {
                type = "turn",
                turnId = turn.Id,
                sessionId = turn.SessionId,
                content = turn.Content,
                receivedAt = turn.CreatedAt,
                instructions = "Respond to this user message. Then call complete_turn and immediately call wait_for_turn again."
            };
        }
    }

    public object CompleteTurn(string participantId, string turnId, string content)
    {
        lock (gate)
        {
            var parsedParticipantId = ParseParticipantId(participantId);
            ValidateParticipant(parsedParticipantId);
            if (!Guid.TryParse(turnId, out var parsedTurnId) || !claimedTurns.Remove(parsedTurnId))
                return new { completed = false, error = "TurnNotFound", message = "The turn is unknown or already completed." };
            if (string.IsNullOrWhiteSpace(content))
                return new { completed = false, error = "ResponseRequired", message = "Response content is required." };

            AddMessage("agent", participant!.AgentName, content.Trim());
            return new
            {
                completed = true,
                turnId = parsedTurnId,
                nextAction = "Call wait_for_turn again to remain in the collaboration session."
            };
        }
    }

    private void ValidateParticipant(Guid participantId)
    {
        lock (gate)
        {
            if (participant is null || participant.Id != participantId)
                throw new InvalidOperationException("Participant is not joined to the active lab session.");
        }
    }

    private static Guid ParseParticipantId(string value)
        => Guid.TryParse(value, out var participantId)
            ? participantId
            : throw new InvalidOperationException("Participant ID is invalid.");

    private void AddMessage(string role, string? author, string content)
        => messages.Add(new LabMessage(++nextSequence, role, author, content, DateTimeOffset.UtcNow));

    private static Channel<LabTurn> CreateChannel()
        => Channel.CreateUnbounded<LabTurn>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private static string CreateInviteCode()
        => $"LAB-{Random.Shared.Next(1000, 10000)}-{Random.Shared.Next(1000, 10000)}";
}

public sealed record LabParticipant(Guid Id, string AgentName, DateTimeOffset JoinedAt);
public sealed record LabMessage(long Sequence, string Role, string? Author, string Content, DateTimeOffset CreatedAt);
public sealed record LabTurn(Guid Id, Guid SessionId, Guid ParticipantId, string Content, DateTimeOffset CreatedAt);
public sealed record LabPostResult(bool Accepted, string? Error, string Message, Guid? TurnId = null);

public partial class Program;
