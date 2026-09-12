using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.Hosting;

/// <summary>Explicit local maintenance invocation; never starts web or MCP listeners.</summary>
public static class MessageEventMaintenance
{
    public static async Task<bool> TryRunAsync(string[] args, IServiceProvider services)
    {
        const string prefix = "--resume-message-event=";
        var requested = args.Where(argument => argument.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (requested.Length == 0) return false;
        if (requested.Length != 1 || !Guid.TryParse(requested[0][prefix.Length..], out var deliveryId) || deliveryId == Guid.Empty)
            throw new ArgumentException("Supply one --resume-message-event=<delivery-id> argument.", nameof(args));
        await using var scope = services.CreateAsyncScope();
        var resumed = await scope.ServiceProvider.GetRequiredService<IMessageEventRecoveryService>().ResumeAsync(deliveryId);
        if (!resumed) throw new InvalidOperationException("The delivery is not suspended in an active meeting.");
        return true;
    }
}
