using Serilog.Core;
using Serilog.Events;

namespace RoundTableMeeting.Errors;

/// <summary>
/// 将请求日志属性转换为统一字段
/// </summary>
public sealed class RequestLogPropertyEnricher : ILogEventEnricher
{
    /// <summary>
    /// 补充统一的结构化日志字段
    /// </summary>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("StatusCode", out var statusCode))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("statusCode", GetScalarValue(statusCode)));
        }

        if (logEvent.Properties.TryGetValue("Elapsed", out var elapsed))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("elapsedMilliseconds", GetScalarValue(elapsed)));
        }
    }

    /// <summary>
    /// 提取日志属性中的标量值
    /// </summary>
    private static object GetScalarValue(LogEventPropertyValue value)
        => value is ScalarValue { Value: not null } scalar ? scalar.Value : value.ToString();
}
