namespace Sg.App;

/// <summary>Opt-in isolation for Debug UI Automation runs; release builds ignore the environment.</summary>
internal static class DebugTestRun
{
    public static string? DirectoryPath { get; } = ReadDirectory();

    public static string UserFile(string name) => Path.Combine(DirectoryPath
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sg"), name);

    static string? ReadDirectory()
    {
#if DEBUG
        var path = Environment.GetEnvironmentVariable("SG_UI_TEST_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("SG_UI_TEST_DIRECTORY must be an absolute path.");
            return Path.GetFullPath(path);
        }
#endif
        return null;
    }

    public static string InstanceKey => DirectoryPath == null ? "sg-ui-debug-main"
        : "sg-ui-test-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant())));
}
