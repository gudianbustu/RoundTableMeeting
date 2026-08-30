using System.Diagnostics;
using System.Net.Sockets;

namespace RoundTableMeeting.IntegrationTests;

public sealed class StartupFailureTests
{
    [Fact]
    public async Task MissingAuthenticationConfigurationStopsTheRealProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingStartupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = GetAvailablePort();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RoundTableMeeting.dll"));
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["Database__ConnectionString"] = $"Data Source={Path.Combine(directory, "database.db")};Mode=ReadWriteCreate;Default Timeout=5;Foreign Keys=True;Pooling=False";

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start RoundTableMeeting process.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token) + await process.StandardError.ReadToEndAsync(timeout.Token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("Application terminated unexpectedly", output, StringComparison.Ordinal);
            Assert.DoesNotContain("SeedAccounts", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password", output, StringComparison.OrdinalIgnoreCase);

            using var client = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () => await client.ConnectAsync("127.0.0.1", port));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DatabaseInitializationFailureStopsTheRealProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingStartupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = GetAvailablePort();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RoundTableMeeting.dll"));
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["Database__ConnectionString"] = $"Data Source={directory};Mode=ReadWriteCreate;Default Timeout=5;Foreign Keys=True;Pooling=False";
        startInfo.Environment["Authentication__AllowedOrigins__0"] = "http://localhost:5173";
        startInfo.Environment["Authentication__SeedAccounts__0__Username"] = "alice";
        startInfo.Environment["Authentication__SeedAccounts__0__Nickname"] = "Alice";
        startInfo.Environment["Authentication__SeedAccounts__0__Password"] = "test-password-alice";

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start RoundTableMeeting process.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token)
                + await process.StandardError.ReadToEndAsync(timeout.Token);
            var decodedOutput = DecodeJsonEscapedPath(output);

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("Application terminated unexpectedly", output, StringComparison.Ordinal);
            Assert.Contains("Microsoft.Data.Sqlite.SqliteException", output, StringComparison.Ordinal);
            Assert.DoesNotContain("OptionsValidationException", output, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, decodedOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory.Replace('\\', '/'), output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Data Source=", output, StringComparison.OrdinalIgnoreCase);

            using var client = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () => await client.ConnectAsync("127.0.0.1", port));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static string DecodeJsonEscapedPath(string value)
        => value.Replace("\\\\", "\\", StringComparison.Ordinal);

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
