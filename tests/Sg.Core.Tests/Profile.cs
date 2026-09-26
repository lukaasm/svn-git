namespace Sg.Core.Tests;

/// <summary>
/// Every git and svn process a fixture's test started, written out when SG_TEST_PROFILE names a folder.
/// A test is slow because of the processes it starts, and an operation that starts dozens of them is one
/// that wants batching: this is how to see which.
/// </summary>
static class Profile
{
    public static void Write(string fixture, CollectingLog log)
    {
        var dir = Environment.GetEnvironmentVariable("SG_TEST_PROFILE");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, Path.GetFileName(fixture) + ".txt"),
            log.Lines.Where(l => l.StartsWith("cmd: ", StringComparison.Ordinal)).Select(l => l[5..].Replace(fixture, "<f>")));
    }
}
