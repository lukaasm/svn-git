using System.Diagnostics;
using System.Text.Json;

namespace Sg.Core;

public sealed class ReviewCheckConfig
{
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
}
public sealed class ReviewCheckResult
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public int ExitCode { get; set; }
    public double Seconds { get; set; }
    public string Output { get; set; } = "";
}
public sealed class ReviewRecord
{
    public string Branch { get; set; } = "";
    public string Head { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string Version { get; set; } = "";
    public string Configuration { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public List<ReviewCheckResult> Checks { get; set; } = new();
    public DateTimeOffset Checked { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Ready { get; set; }
}

/// <summary>Local checks run only on explicit request. A readiness stamp binds their inputs and the reviewed commit range.</summary>
public static class Review
{
    static string FileFor(SgRoot root, string branch) => Path.Combine(root.StorePath, "reviews", WorkspaceVersion.Hash(branch) + ".json");
    static ReviewRecord Current(SgRoot root, string path)
    {
        var git = root.Git;
        var branch = git.CurrentBranch(path);
        var snapshot = git.RefSha(root.SnapshotRef(Ops.BaseCheckout(root, branch))) ?? throw new SgException("No snapshot.");
        return new ReviewRecord { Branch = branch, Head = git.HeadSha(path), Snapshot = snapshot,
            Version = WorkspaceVersion.Of(root, path), Configuration = WorkspaceVersion.Hash(JsonSerializer.Serialize(root.Config.ReviewChecks, SgConfig.JsonOptions)),
            Files = git.Out(path, "diff", "--name-only", snapshot, "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() };
    }
    static bool Same(ReviewRecord a, ReviewRecord b) => a.Head == b.Head && a.Snapshot == b.Snapshot && a.Version == b.Version && a.Configuration == b.Configuration;
    public static ReviewRecord? Read(SgRoot root, string path)
    {
        var file = FileFor(root, root.Git.CurrentBranch(path));
        return File.Exists(file) ? JsonSerializer.Deserialize<ReviewRecord>(File.ReadAllText(file), SgConfig.JsonOptions) : null;
    }
    public static string Status(SgRoot root, string path)
    {
        using var operation = root.Lock();
        var record = Read(root, path);
        if (record == null) return "Not reviewed";
        if (!Same(record, Current(root, path))) return "Changed since review";
        return record.Ready != null ? "Ready for this version" : record.Checks.Any(x => x.ExitCode != 0) ? "Checks failed" : "Checks complete; review required";
    }
    static void Save(SgRoot root, ReviewRecord record) => AtomicFile.WriteAllText(FileFor(root, record.Branch), JsonSerializer.Serialize(record, SgConfig.JsonOptions));
    public static ReviewRecord RunChecks(SgRoot root, string path)
    {
        using var operation = root.Lock();
        if (Conflicts.HasPending(root.Git, path) || Operations.Pending(root, path) != null) throw new SgException("Finish the pending operation first.");
        var record = Current(root, path);
        foreach (var check in root.Config.ReviewChecks)
        {
            if (string.IsNullOrWhiteSpace(check.Executable)) throw new SgException("A review check has no executable: " + check.Name);
            var watch = Stopwatch.StartNew();
            var result = Proc.Run(check.Executable, check.Arguments, path, root.Log);
            record.Checks.Add(new() { Name = check.Name, Command = result.CommandLine, ExitCode = result.ExitCode,
                Seconds = watch.Elapsed.TotalSeconds, Output = result.StdOut + "\n" + result.StdErr });
        }
        Save(root, record);
        // A formatter or test may have changed its inputs. Retain results but never mark those inputs ready.
        if (!Same(record, Current(root, path))) throw new SgException("Files changed while checks ran. Results were saved; review and rerun checks.");
        return record;
    }
    public static ReviewRecord MarkReady(SgRoot root, string path)
    {
        using var operation = root.Lock();
        if (Conflicts.HasPending(root.Git, path) || Operations.Pending(root, path) != null) throw new SgException("Finish the pending operation before marking ready.");
        var record = Read(root, path) ?? throw new SgException("Run checks for this version first (an empty check list is allowed).");
        if (!Same(record, Current(root, path))) throw new SgException("Changed since review. Run checks and review again.");
        if (record.Checks.Any(x => x.ExitCode != 0)) throw new SgException("Fix the failed checks first.");
        record.Ready = DateTimeOffset.UtcNow;
        Save(root, record);
        return record;
    }
}
