using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RoundTableMeeting.Application.AgentCollaboration;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AgentRecoveryConfigurationTests
{
    [Theory]
    [InlineData("wait")]
    [InlineData("renew")]
    [InlineData("backoff")]
    public async Task InvalidRecoveryConfigurationFailsHostStartup(string scenario)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddOptions<AgentCollaborationOptions>().Configure(options =>
        {
            if (scenario == "wait") options.ReliableWaitTimeoutSeconds = options.WaitTimeoutSeconds + 1;
            if (scenario == "renew") options.LeaseRenewIntervalSeconds = options.ConnectionLeaseSeconds;
            if (scenario == "backoff") options.RetryBackoffSeconds = [];
        }).ValidateDataAnnotations().ValidateOnStart();
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }
}
