using System.Net.Sockets;

namespace ilworkflow.Tests.Infrastructure;

/// <summary>
/// Environment switches for the live tests (replacing the Python command-line flags):
/// <list type="bullet">
/// <item><c>ILWORKFLOW_BASE_URL</c> - API base URL, default <c>http://localhost:5000</c>.</item>
/// <item><c>ILWORKFLOW_VERIFY_RESTART=1</c> - run the post-restart phases instead of the main phases.</item>
/// <item><c>ILWORKFLOW_VERIFY_PREVIEW=1</c> - run only the SwaggerPreview check.</item>
/// </list>
/// Tests are skipped when the API is not reachable.
/// </summary>
public static class LiveSettings
{
    public static string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("ILWORKFLOW_BASE_URL") ?? "http://localhost:5000").TrimEnd('/');

    public static bool VerifyRestart => Flag("ILWORKFLOW_VERIFY_RESTART");

    public static bool VerifyPreview => Flag("ILWORKFLOW_VERIFY_PREVIEW");

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

    /// <summary>Skips the current test unless the API is reachable and the requested mode is active.</summary>
    public static void Require(bool restart = false, bool preview = false)
    {
        if (!Reachable.Value) Assert.Skip($"ilworkflow API not reachable at {BaseUrl}; set ILWORKFLOW_BASE_URL and start the API.");
        if (VerifyPreview != preview) Assert.Skip(preview ? "Set ILWORKFLOW_VERIFY_PREVIEW=1 against a SwaggerPreview server." : "Preview mode active.");
        if (VerifyRestart != restart) Assert.Skip(restart ? "Set ILWORKFLOW_VERIFY_RESTART=1 after restarting the API." : "Restart verification mode active.");
    }

    public static string StateFile(string name) => Path.Combine(RepoRoot, "App_Data", name);

    private static bool Flag(string name)
        => Environment.GetEnvironmentVariable(name) is "1" or "true" or "True";

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ilworkflow.slnx"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
