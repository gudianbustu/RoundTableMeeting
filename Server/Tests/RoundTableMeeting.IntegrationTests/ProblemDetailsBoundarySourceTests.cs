namespace RoundTableMeeting.IntegrationTests;

public sealed class ProblemDetailsBoundarySourceTests
{
    [Fact]
    public void HttpErrorProducersUseTheSharedErrorsWriter()
    {
        var root = FindRepositoryRoot();
        var writer = File.ReadAllText(Path.Combine(root, "Server", "RoundTableMeeting", "Errors", "ApiProblemDetailsWriter.cs"));
        var authentication = File.ReadAllText(Path.Combine(root, "Server", "RoundTableMeeting", "Authentication", "AuthenticationProblemDetailsExtensions.cs"));
        var origin = File.ReadAllText(Path.Combine(root, "Server", "RoundTableMeeting", "Authentication", "OriginValidationMiddleware.cs"));
        var services = File.ReadAllText(Path.Combine(root, "Server", "RoundTableMeeting", "Hosting", "ServiceCollectionExtensions.cs"));

        Assert.Contains("ProblemDetailsFactory.Create(context, status, code, errors)", writer, StringComparison.Ordinal);
        Assert.Contains("ApiProblemDetailsWriter.CreateResult(context, status, code, errors)", authentication, StringComparison.Ordinal);
        Assert.Contains("ApiProblemDetailsWriter.CreateValidationResult(context.HttpContext, context.ModelState)", services, StringComparison.Ordinal);
        Assert.Contains("ApiProblemDetailsWriter.WriteAsync(context, StatusCodes.Status403Forbidden, \"origin_not_allowed\")", origin, StringComparison.Ordinal);
        Assert.Contains("ApiProblemDetailsWriter.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, \"unsupported_media_type\")", origin, StringComparison.Ordinal);
        Assert.DoesNotContain("new ApiProblemDetails", authentication, StringComparison.Ordinal);
        Assert.DoesNotContain("new ApiProblemDetails", origin, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
