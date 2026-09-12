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
        private int cleanupSeen;
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") listener.Subscribe(this,
                name => name is "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitting"
                    or "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted"
                    or "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted");
        }
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (Volatile.Read(ref stopped) != 0) return;
            var arm = Path.Combine(directory, "crash-arm");
            if (!File.Exists(arm)) return;
            var selection = File.ReadAllText(arm).Split(':');
            if (selection.Length != 2) return;
            if (item.Value is CommandExecutedEventData executed)
            {
                if (executed.Command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                    && executed.Command.CommandText.Contains("ParentTaskId", StringComparison.Ordinal))
                {
                    Volatile.Write(ref cleanupSeen, 1);
                    if (selection[0] == "unlink") StopAt("unlink");
                }
                return;
            }
            if (item.Value is not TransactionEventData data || data.Context is null) return;
            var after = item.Key.EndsWith(".TransactionCommitted", StringComparison.Ordinal);
            if ((selection[1] == "after") != after) return;
            if (selection[0] == "unlink") return;
            if (selection[0] == "cleanup")
            {
                if (Volatile.Read(ref cleanupSeen) != 0) StopAt("cleanup");
                return;
            }
            var sql = selection[0] switch
            {
                "join" => "SELECT count(*) FROM AgentConnectionSessions WHERE State = 'Activating'",
                "activation" => "SELECT count(*) FROM AgentConnectionSessions WHERE State = 'Connected'",
                "reply" => "SELECT count(*) FROM AgentReplyTasks WHERE State = 'Replied'",
                "allocation" => "SELECT count(*) FROM AgentReplyTasks WHERE ParentTaskId IS NOT NULL",
                "deletion" => "SELECT count(*) FROM Meetings WHERE LifecycleState = 'Deleting'",
                _ => throw new InvalidOperationException("Unknown test transaction boundary."),
            };
            using DbCommand command = data.Context.Database.GetDbConnection().CreateCommand();
            if (!after) command.Transaction = data.Transaction;
            command.CommandText = sql;
            if (Convert.ToInt64(command.ExecuteScalar()) == 0) return;
            StopAt(selection[0]);
        }
        private void StopAt(string boundary)
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            // The marker contains no credential, session, message or context information.
            File.WriteAllText(Path.Combine(directory, "crash-reached"), boundary);
            using var pause = new ManualResetEventSlim();
            if (!pause.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("The child was not killed at the test crash point.");
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
