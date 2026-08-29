using Serilog.Core;
using Serilog.Events;

namespace RoundTableMeeting.IntegrationTests;

internal sealed class StructuredLogSink : ILogEventSink
{
    private readonly List<LogEvent> events = [];

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

    public void Emit(LogEvent logEvent)
    {
        lock (events)
        {
            events.Add(logEvent);
        }
    }
}
