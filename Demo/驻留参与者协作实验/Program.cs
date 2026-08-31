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

/// <summary>
/// 表示从实验网页提交的消息
/// </summary>
public sealed record PostLabMessageRequest(string? Content);

/// <summary>
/// 通过 MCP 工具提供驻留参与者实验能力
/// </summary>
[McpServerToolType]
public static class ResidentLabTools
{
    /// <summary>
    /// 获取当前实验会话快照
    /// </summary>
    [McpServerTool(Name = "get_lab_session")]
    [Description("Get the active AICollaboration resident-participant experiment session, invite code, participant, and messages.")]
    public static object GetLabSession(ResidentLabState state) => state.GetSnapshot();

    /// <summary>
    /// 使用入会码加入当前实验会话
    /// </summary>
    [McpServerTool(Name = "join_session")]
    [Description("Join the resident-participant experiment with its invite code. Call wait_for_turn immediately after joining.")]
    public static object JoinSession(
        ResidentLabState state,
        [Description("Invite code displayed in the experiment web page.")] string inviteCode,
        [Description("The name users will mention, for example CodexResident.")] string agentName)
        => state.Join(inviteCode, agentName);

    /// <summary>
    /// 等待被提及的用户消息分配给参与者
    /// </summary>
    [McpServerTool(Name = "wait_for_turn")]
    [Description("Wait until the web user mentions this participant. This call intentionally blocks until a turn arrives.")]
    public static async Task<object> WaitForTurnAsync(
        ResidentLabState state,
        [Description("Participant ID returned by join_session.")] string participantId,
        CancellationToken cancellationToken)
        => await state.WaitForTurnAsync(participantId, cancellationToken);

    /// <summary>
    /// 完成已领取轮次并发布参与者响应
    /// </summary>
    [McpServerTool(Name = "complete_turn")]
    [Description("Submit the participant response for a received turn. After this succeeds, call wait_for_turn again.")]
    public static object CompleteTurn(
        ResidentLabState state,
        [Description("Participant ID returned by join_session.")] string participantId,
        [Description("Turn ID returned by wait_for_turn.")] string turnId,
        [Description("The final response that should appear in the collaboration web page.")] string content)
        => state.CompleteTurn(participantId, turnId, content);
}

/// <summary>
/// 管理内存中的会话、参与者、消息日志和轮次队列
/// </summary>
public sealed class ResidentLabState
{
    /// <summary>
    /// 串行化访问所有可变会话状态
    /// </summary>
    private readonly object gate = new();
    /// <summary>
    /// 为唯一驻留参与者读取器排队保存被提及的消息
    /// </summary>
    private Channel<LabTurn> turns = CreateChannel();
    /// <summary>
    /// 标识当前可重置的实验会话
    /// </summary>
    private Guid sessionId = Guid.NewGuid();
    /// <summary>
    /// MCP 参与者加入会话所需的入会码
    /// </summary>
    private string inviteCode = CreateInviteCode();
    /// <summary>
    /// 当前已加入的驻留参与者
    /// </summary>
    private LabParticipant? participant;
    /// <summary>
    /// 网页显示的有序消息历史
    /// </summary>
    private readonly List<LabMessage> messages = [];
    /// <summary>
    /// 已从通道读取但尚未完成的轮次
    /// </summary>
    private readonly Dictionary<Guid, LabTurn> claimedTurns = [];
    /// <summary>
    /// 供浏览器增量渲染使用的单调递增序号
    /// </summary>
    private long nextSequence;

    /// <summary>
    /// 返回当前会话的线程安全快照
    /// </summary>
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

    /// <summary>
    /// 启动新会话并使旧队列和入会码失效
    /// </summary>
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

    /// <summary>
    /// 为当前会话登记唯一的驻留参与者
    /// </summary>
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

    /// <summary>
    /// 添加用户消息并在提及参与者时排队创建轮次
    /// </summary>
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

    /// <summary>
    /// 等待并领取分配给参与者的下一轮次
    /// </summary>
    public async Task<object> WaitForTurnAsync(string participantId, CancellationToken cancellationToken)
    {
        var parsedParticipantId = ParseParticipantId(participantId);
        ValidateParticipant(parsedParticipantId);

        // 在锁外等待，使浏览器请求可以继续读取和发布消息
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

    /// <summary>
    /// 发布先前已领取轮次的响应
    /// </summary>
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

    /// <summary>
    /// 确保调用者是当前会话中已加入的参与者
    /// </summary>
    private void ValidateParticipant(Guid participantId)
    {
        lock (gate)
        {
            if (participant is null || participant.Id != participantId)
                throw new InvalidOperationException("Participant is not joined to the active lab session.");
        }
    }

    /// <summary>
    /// 解析 MCP 参与者标识，失败时抛出协议层错误
    /// </summary>
    private static Guid ParseParticipantId(string value)
        => Guid.TryParse(value, out var participantId)
            ? participantId
            : throw new InvalidOperationException("Participant ID is invalid.");

    /// <summary>
    /// 使用下一个浏览器可见序号追加消息
    /// </summary>
    private void AddMessage(string role, string? author, string content)
        => messages.Add(new LabMessage(++nextSequence, role, author, content, DateTimeOffset.UtcNow));

    /// <summary>
    /// 创建供驻留参与者轮次使用的单读取器通道
    /// </summary>
    private static Channel<LabTurn> CreateChannel()
        => Channel.CreateUnbounded<LabTurn>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>
    /// 为实验页面创建简短易读的入会码
    /// </summary>
    private static string CreateInviteCode()
        => $"LAB-{Random.Shared.Next(1000, 10000)}-{Random.Shared.Next(1000, 10000)}";
}

/// <summary>
/// 表示已加入实验的驻留参与者
/// </summary>
public sealed record LabParticipant(Guid Id, string AgentName, DateTimeOffset JoinedAt);
/// <summary>
/// 表示实验中显示的一条有序消息
/// </summary>
public sealed record LabMessage(long Sequence, string Role, string? Author, string Content, DateTimeOffset CreatedAt);
/// <summary>
/// 表示等待参与者响应的被提及用户消息
/// </summary>
public sealed record LabTurn(Guid Id, Guid SessionId, Guid ParticipantId, string Content, DateTimeOffset CreatedAt);
/// <summary>
/// 表示从网页发布消息的结果
/// </summary>
public sealed record LabPostResult(bool Accepted, string? Error, string Message, Guid? TurnId = null);

/// <summary>
/// 向测试宿主提供顶层实验入口
/// </summary>
public partial class Program;
