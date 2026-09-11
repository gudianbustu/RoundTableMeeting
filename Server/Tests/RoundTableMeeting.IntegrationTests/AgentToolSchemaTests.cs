using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.AgentCollaboration;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AgentToolSchemaTests
{
    [Fact]
    public void AllAdvertisedToolsHaveExplicitSuccessAndFailureSchemas()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        var tools = AgentToolSchemas.CreateTools(options).ToArray();
        Assert.Equal(9, tools.Length);
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.OutputSchema!.Value;
            Assert.Equal(2, schema.GetProperty("oneOf").GetArrayLength());
            Assert.True(schema.GetProperty("$defs").TryGetProperty("AgentRecoveryErrorData", out var error));
            Assert.Contains(error.GetProperty("required").EnumerateArray(), p => p.GetString() == "nextAction");
        }
    }

    [Fact]
    public void EnvelopeSchemaRejectsMissingErrorActionAndMismatchedSuccessData()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var schema = JsonSchema.FromText(AgentToolSchemas.CreateOutputSchema(options, typeof(AgentLeaseResultDto)).GetRawText());
        var lease = new AgentLeaseResultDto(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2), 30);
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToElement(AgentOperationResult.Success(lease, "renewed"), options)).IsValid);
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToElement(AgentOperationResult.Failure("invalid", "invalid"), options)).IsValid);
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToElement(AgentOperationResult.Failure("invalid", "invalid", new("stop")), options)).IsValid);
        foreach (var json in new[]
        {
            """{"protocolVersion":"agent-messaging-v1","succeeded":true,"errorCode":null,"message":"invalid","data":null}""",
            """{"protocolVersion":"agent-messaging-v1","succeeded":false,"errorCode":"invalid","message":"invalid","data":{}}""",
            """{"protocolVersion":"agent-messaging-v1","succeeded":true,"errorCode":"invalid","message":"invalid","data":{"nextAction":"stop"}}""",
            """{"protocolVersion":"agent-messaging-v1","succeeded":true,"errorCode":null,"message":"invalid","data":{"kind":"wrong","serverTime":"2026-09-09T08:00:00Z","leaseExpiresAt":"2026-09-09T08:02:00Z","leaseRenewIntervalSeconds":30}}""",
            """{"protocolVersion":"unknown","succeeded":false,"errorCode":"invalid","message":"invalid","data":null}"""
        })
            Assert.False(schema.Evaluate(JsonSerializer.Deserialize<JsonElement>(json)).IsValid);
    }
}
