using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace RoundTableMeeting.Errors;

/// <summary>
/// 扩展 RFC 7807 问题详情响应
/// </summary>
public sealed class ApiProblemDetails : ProblemDetails
{
    /// <summary>
    /// 机器可读的 API 错误码
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// 客户端报告问题时使用的追踪标识
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// 按请求字段索引的校验错误
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
