using Serilog.Core;
using Serilog.Events;

namespace RoundTableMeeting.IntegrationTests;

/// <summary>
/// 收集集成测试期间产生的结构化日志
/// </summary>
internal sealed class StructuredLogSink : ILogEventSink
{
    /// <summary>
    /// 线程安全日志事件缓冲区
    /// </summary>
    private readonly List<LogEvent> events = [];

    /// <summary>
    /// 获取所有已捕获日志事件的稳定快照
    /// </summary>
    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (events)
            {
                return events.ToArray();
            }
        }
    }

    /// <summary>
    /// 记录一条结构化日志事件
    /// </summary>
    public void Emit(LogEvent logEvent)
    {
        lock (events)
        {
            events.Add(logEvent);
        }
    }
}
