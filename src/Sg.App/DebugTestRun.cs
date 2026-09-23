namespace Sg.App;

/// <summary>Opt-in isolation for Debug UI Automation runs; release builds ignore the environment.</summary>
internal static class DebugTestRun
{
    /// <summary>Replay captured activity in an isolated Debug test. No repository operation is executed.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void ReplayTasks()
    {
#if DEBUG
        var root = Session.Root;
        if (DirectoryPath == null || root == null) return;
        var path = Path.Combine(DirectoryPath, "task-replay.json");
        if (!File.Exists(path)) return;
        _ = Task.Run(async () =>
        {
            var records = System.Text.Json.JsonSerializer.Deserialize<Sg.Core.OperationRecord[]>(
                File.ReadAllText(path), Sg.Core.SgConfig.JsonOptions) ?? [];
            if (records.Length == 0) return;
            // Exercise the full retained-history capacity with real titles, paths, and completed steps.
            for (var n = 0; n < 96; n++)
            {
                var record = records[n % records.Length];
                var task = Session.Tasks.TryStart(record.Kind + " · " + record.Branch, root.RootPath);
                if (task == null) return;
                foreach (var line in record.Steps) task.Append(line);
                task.Finish(Sg.Core.TaskState.Succeeded, record.Detail ?? string.Join("\n", record.Steps));
            }
            var sample = records[^1];
            var active = Session.Tasks.TryStart("Replay: " + sample.Kind + " · " + sample.Branch, root.RootPath);
            if (active == null) return;
            active.Running();
            Sg.Core.AtomicFile.WriteAllText(Path.Combine(DirectoryPath, "task-replay-started.txt"), active.Snapshot().Id.ToString());
            var lines = sample.Steps.Count > 0 ? sample.Steps.ToArray() : [sample.Kind];
            var batches = 0;
            while (!active.Token.IsCancellationRequested)
            {
                for (var i = 0; i < 24; i++) active.Append(lines[i % lines.Length]);
                active.Progress($"Replaying captured progress · {++batches * 24} lines");
                await Task.Delay(25);
            }
            active.Finish(Sg.Core.TaskState.Cancelled, "Replay stopped. No repository operation was executed.");
        });
#endif
    }

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
