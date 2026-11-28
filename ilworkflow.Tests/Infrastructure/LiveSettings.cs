using System.Net.Sockets;

namespace ilworkflow.Tests.Infrastructure;

/// <summary>
/// Live-test settings: <c>ILWORKFLOW_BASE_URL</c> is the API base URL (default <c>http://localhost:5000</c>).
/// Tests are skipped when the API is not reachable.
/// </summary>
public static class LiveSettings
{
    public static string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("ILWORKFLOW_BASE_URL") ?? "http://localhost:5000").TrimEnd('/');

    public static string RepoRoot { get; } = FindRepoRoot();

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        try
        {
            var uri = new Uri(BaseUrl);
            using var client = new TcpClient();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>Skips the current test unless the API is reachable.</summary>
    public static void Require()
    {
        if (!Reachable.Value) Assert.Skip($"ilworkflow API not reachable at {BaseUrl}; set ILWORKFLOW_BASE_URL and start the API.");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ilworkflow.slnx"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
