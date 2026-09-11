using System.Data.Common;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

[assembly: HostingStartup(typeof(RoundTableMeeting.IntegrationTests.RecoveryProcessDiagnosticStartup))]

namespace RoundTableMeeting.IntegrationTests;

// Loaded exclusively by the isolated child process fixture. DiagnosticSource invokes
// observers synchronously at the actual EF transaction commit boundary.
public sealed class RecoveryProcessDiagnosticStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        var directory = Environment.GetEnvironmentVariable("RTM_TEST_CRASH_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        DiagnosticListener.AllListeners.Subscribe(new TransactionCrashObserver(directory));
    }

    private sealed class TransactionCrashObserver(string directory)
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private int stopped;
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") listener.Subscribe(this,
                name => name is "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitting"
                    or "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted");
        }
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (Volatile.Read(ref stopped) != 0 || item.Value is not TransactionEventData data || data.Context is null) return;
            var arm = Path.Combine(directory, "crash-arm");
            if (!File.Exists(arm)) return;
            var selection = File.ReadAllText(arm).Split(':');
            var after = item.Key.EndsWith(".TransactionCommitted", StringComparison.Ordinal);
            if (selection.Length != 2 || (selection[1] == "after") != after) return;
            var sql = selection[0] switch
            {
                "join" => "SELECT count(*) FROM AgentConnectionSessions WHERE State = 'Activating'",
                "activation" => "SELECT count(*) FROM AgentConnectionSessions WHERE State = 'Connected'",
                "reply" => "SELECT count(*) FROM AgentReplyTasks WHERE State = 'Replied'",
                "deletion" => "SELECT count(*) FROM Meetings WHERE LifecycleState = 'Deleting'",
                _ => throw new InvalidOperationException("Unknown test transaction boundary."),
            };
            using DbCommand command = data.Context.Database.GetDbConnection().CreateCommand();
            if (!after) command.Transaction = data.Transaction;
            command.CommandText = sql;
            if (Convert.ToInt64(command.ExecuteScalar()) == 0 || Interlocked.Exchange(ref stopped, 1) != 0) return;
            // The marker contains no credential, session, message or context information.
            File.WriteAllText(Path.Combine(directory, "crash-reached"), selection[0]);
            using var pause = new ManualResetEventSlim();
            if (!pause.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The child was not killed at the test crash point.");
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
