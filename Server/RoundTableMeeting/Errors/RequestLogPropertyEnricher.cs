using Serilog.Core;
using Serilog.Events;

namespace RoundTableMeeting.Errors;

public sealed class RequestLogPropertyEnricher : ILogEventEnricher
{
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

    private static object GetScalarValue(LogEventPropertyValue value)
        => value is ScalarValue { Value: not null } scalar ? scalar.Value : value.ToString();
}
