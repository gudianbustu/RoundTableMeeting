using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace RoundTableMeeting.Errors;

public sealed class ApiProblemDetails : ProblemDetails
{
    public required string Code { get; init; }

    public required string TraceId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
