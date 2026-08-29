using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RoundTableMeeting.Errors;

public static class ProblemDetailsFactory
{
    public static ApiProblemDetails CreateValidation(HttpContext context, ModelStateDictionary modelState)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in modelState)
        {
            var key = NormalizeKey(entry.Key);
            var messages = entry.Value?.Errors
                .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The value is invalid." : error.ErrorMessage)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            if (messages.Length == 0)
            {
                continue;
            }

            if (errors.TryGetValue(key, out var existing))
            {
                errors[key] = existing.Concat(messages).Distinct(StringComparer.Ordinal).ToArray();
            }
            else
            {
                errors[key] = messages;
            }
        }

        return new ApiProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Type = "/problems/validation_failed",
            Title = "Validation failed",
            Detail = "One or more validation errors occurred.",
            Instance = context.Request.Path,
            Code = "validation_failed",
            TraceId = TraceIdResolver.Resolve(context),
            Errors = errors
        };
    }

    public static string NormalizeKey(string key)
    {
        var normalized = string.IsNullOrWhiteSpace(key) ? "$" : key.Trim();
        if (normalized.StartsWith("$.", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var firstSeparator = normalized.IndexOfAny(['.', '[']);
        var first = firstSeparator < 0 ? normalized : normalized[..firstSeparator];
        var rest = firstSeparator < 0 ? string.Empty : normalized[firstSeparator..];
        return ToCamelCase(first) + rest;
    }

    private static string ToCamelCase(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
