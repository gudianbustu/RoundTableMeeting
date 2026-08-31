using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RoundTableMeeting.Errors;

/// <summary>
/// 构建统一的问题详情内容
/// </summary>
public static class ProblemDetailsFactory
{
    /// <summary>
    /// 创建统一的问题详情或领域对象
    /// </summary>
    public static ApiProblemDetails Create(HttpContext context, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
        => new()
        {
            Status = status,
            Type = $"/problems/{code}",
            Title = code switch
            {
                "origin_not_allowed" => "Origin not allowed",
                "unsupported_media_type" => "Unsupported media type",
                "validation_failed" => "Validation failed",
                "route_not_found" => "Route not found",
                "invalid_credentials" => "Invalid credentials",
                "unauthenticated" => "Authentication required",
                "forbidden" => "Forbidden",
                "concurrency_conflict" => "Concurrency conflict",
                "username_conflict" => "Username conflict",
                "user_not_found" => "User not found",
                "internal_error" => "Internal server error",
                _ => code
            },
            Detail = code switch
            {
                "origin_not_allowed" => "The browser origin is not allowed.",
                "unsupported_media_type" => "The request media type is not supported.",
                "validation_failed" => "One or more validation errors occurred.",
                "route_not_found" => "The requested API route was not found.",
                "invalid_credentials" => "The username or password is invalid.",
                "unauthenticated" => "Authentication is required.",
                "forbidden" => "You do not have permission to access this resource.",
                "concurrency_conflict" => "The requested change could not be saved.",
                "username_conflict" => "The username is already in use.",
                "user_not_found" => "The requested user was not found.",
                "internal_error" => "An unexpected error occurred.",
                _ => "The request could not be completed."
            },
            Instance = context.Request.Path,
            Code = code,
            TraceId = TraceIdResolver.Resolve(context),
            Errors = errors
        };

    /// <summary>
    /// 将模型状态转换为统一校验问题响应
    /// </summary>
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

        return Create(context, StatusCodes.Status400BadRequest, "validation_failed", errors);
    }

    /// <summary>
    /// 将 JSON 和 MVC 字段路径规范化为客户端字段名
    /// </summary>
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

    /// <summary>
    /// 将字段名首字母转换为小写
    /// </summary>
    private static string ToCamelCase(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
