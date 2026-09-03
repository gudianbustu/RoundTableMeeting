using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RoundTableMeeting.OpenApi;

public sealed class MeetingOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var path = context.Description.RelativePath?.TrimStart('/');
        var supportsRetryAfter = path is "api/meetings/join" or "api/meetings/join/preview"
            || path?.EndsWith("/messages", StringComparison.Ordinal) == true;
        if (!supportsRetryAfter || operation.Responses is null || !operation.Responses.TryGetValue("429", out var response))
            return Task.CompletedTask;
        var mutableResponse = response as OpenApiResponse ?? new OpenApiResponse { Description = response.Description, Content = new Dictionary<string, OpenApiMediaType>(), Headers = new Dictionary<string, IOpenApiHeader>() };
        if (mutableResponse != response)
        {
            if (response.Content is not null)
                foreach (var content in response.Content) mutableResponse.Content![content.Key] = content.Value;
            operation.Responses["429"] = mutableResponse;
        }
        var headers = mutableResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
        headers["Retry-After"] = new OpenApiHeader
        {
            Description = "Seconds until another request may be made.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        };
        return Task.CompletedTask;
    }
}
