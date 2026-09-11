using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

namespace RoundTableMeeting.AgentCollaboration;

/// <summary>Declares the concrete data alternatives hidden by the legacy object-valued envelope.</summary>
public static class AgentToolSchemas
{
    public static IEnumerable<McpServerTool> CreateTools(JsonSerializerOptions serializerOptions)
    {
        foreach (var method in typeof(McpAgentTools).GetMethods())
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } attribute) continue;
            var tool = McpServerTool.Create(method,
                request => ActivatorUtilities.CreateInstance<McpAgentTools>(request.Services!),
                new() { SerializerOptions = serializerOptions });
            var types = attribute.Name switch
            {
                AgentCollaborationContract.JoinToolName => new[] { typeof(AgentJoinResultDto), typeof(AgentJoinRecoveryRequiredDto) },
                AgentCollaborationContract.WaitToolName => [typeof(AgentMentionResultDto), typeof(AgentWaitIdleDto)],
                AgentCollaborationContract.ReplyToolName => [typeof(AgentReplyResultDto)],
                AgentCollaborationContract.StatusToolName => [typeof(AgentStatusResultDto)],
                AgentReliabilityProtocol.ResumeTool => [typeof(AgentResumeResultDto)],
                AgentReliabilityProtocol.ConfirmTool => [typeof(AgentConfirmContextResultDto)],
                AgentReliabilityProtocol.RenewTool => [typeof(AgentLeaseResultDto)],
                AgentReliabilityProtocol.FailureTool => [typeof(AgentFailureResultDto)],
                AgentReliabilityProtocol.RetryTool => [typeof(AgentRetryResultDto)],
                _ => throw new InvalidOperationException("Agent tool has no registered result schema.")
            };
            tool.ProtocolTool.OutputSchema = CreateOutputSchema(serializerOptions, types);
            yield return tool;
        }
    }

    public static JsonElement CreateOutputSchema(JsonSerializerOptions serializerOptions, params Type[] successTypes)
    {
        if (successTypes.Length == 0) throw new ArgumentException("At least one success DTO is required.", nameof(successTypes));
        var options = new JsonSerializerOptions(serializerOptions);
        options.MakeReadOnly(populateMissingResolver: true);
        var root = options.GetJsonSchemaAsNode(typeof(AgentOperationResult)).AsObject();
        root["type"] = "object";
        var definitions = new JsonObject();
        var success = new JsonArray();
        foreach (var type in successTypes.Distinct().Append(typeof(AgentRecoveryErrorData)))
        {
            var schema = options.GetJsonSchemaAsNode(type);
            schema["type"] = "object";
            var kind = type.Name switch
            {
                nameof(AgentJoinResultDto) => "joined",
                nameof(AgentJoinRecoveryRequiredDto) => "recovery_required",
                nameof(AgentResumeResultDto) => "resumed",
                nameof(AgentMentionResultDto) => "mention",
                nameof(AgentWaitIdleDto) => "idle",
                nameof(AgentConfirmContextResultDto) => "context_confirmed",
                nameof(AgentLeaseResultDto) => "lease_renewed",
                nameof(AgentFailureResultDto) => "task_failure_recorded",
                nameof(AgentRetryResultDto) => "task_retry_scheduled",
                nameof(AgentReplyResultDto) => "replied",
                nameof(AgentStatusResultDto) => "agent_status",
                _ => null
            };
            if (kind is not null)
            {
                schema["properties"]!["kind"] = new JsonObject { ["const"] = kind };
                // The old join/reply/status shapes omit kind. Every new result must declare it.
                if (type != typeof(AgentJoinResultDto) && type != typeof(AgentReplyResultDto) && type != typeof(AgentStatusResultDto))
                {
                    var required = schema["required"]?.AsArray() ?? new JsonArray();
                    if (!required.Any(value => value?.GetValue<string>() == "kind")) required.Add("kind");
                    if (schema["required"] is null) schema["required"] = required;
                }
            }
            RebaseReferences(schema, "#/$defs/" + type.Name);
            definitions[type.Name] = schema;
            if (type != typeof(AgentRecoveryErrorData)) success.Add(Reference(type));
        }
        root["$defs"] = definitions;
        var properties = root["properties"]!.AsObject();
        properties["protocolVersion"] = new JsonObject { ["const"] = AgentCollaborationContract.ProtocolVersion };
        properties["data"] = new JsonObject();
        root["oneOf"] = new JsonArray
        {
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["succeeded"] = new JsonObject { ["const"] = true },
                    ["errorCode"] = new JsonObject { ["type"] = "null" },
                    ["data"] = new JsonObject { ["anyOf"] = success }
                }
            },
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["succeeded"] = new JsonObject { ["const"] = false },
                    ["errorCode"] = new JsonObject { ["type"] = "string" },
                    ["data"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, Reference(typeof(AgentRecoveryErrorData))) }
                }
            }
        };
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject Reference(Type type) => new() { ["$ref"] = "#/$defs/" + type.Name };

    private static void RebaseReferences(JsonNode node, string prefix)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var value) && value.StartsWith('#'))
                obj["$ref"] = prefix + value[1..];
            foreach (var property in obj.ToArray())
                if (property.Value is { } child) RebaseReferences(child, prefix);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null) RebaseReferences(child, prefix);
    }
}
