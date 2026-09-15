using System.Text;

namespace Sg.Core;

/// <summary>What one run of the resolver did with the files a stopped replay left in conflict.</summary>
public sealed class AutoResolveResult
{
    public Replay Kind;

    /// <summary>The commit or patch the replay is stopped on, and where in the series that is.</summary>
    public string Stopped = "";
    public int At;
    public int Of;

    /// <summary>Files the resolver settled. They are staged, and the step can be continued once Left is empty.</summary>
    public List<string> Resolved = new();

    /// <summary>Files still in conflict, each with the reason: the resolver said so, or left markers in it, or never touched it.</summary>
    public List<AutoResolveLeft> Left = new();

    /// <summary>What the resolver printed, for the record: it is asked to end with one line per file.</summary>
    public string Output = "";

    /// <summary>The command it ran as.</summary>
    public string Command = "";

    public bool AllResolved => Left.Count == 0;
}

public sealed record AutoResolveLeft(string Path, string Why);

/// <summary>
/// A whole run of the resolver over a stopped replay: every stop it settled, and how it ended. Ok
/// means the replay is through. Otherwise Why says what it stopped short on, and the worktree is
/// still stopped there, with the last step's files as the resolver left them.
/// </summary>
public sealed class AutoResolveRun
{
    public Replay Kind;
    public string Branch = "";
    public string Checkout = "";
    public List<AutoResolveResult> Steps = new();

    /// <summary>Steps that had nothing to commit once settled, dropped the way Skip does. Rebase only.</summary>
    public int Skipped;

    public bool Ok;
    public string Why = "";

    /// <summary>What the last continue said, when the replay went through.</summary>
    public ResolveResult? Finished;

    public int FilesResolved => Steps.Sum(s => s.Resolved.Count);
    public string Verb => Conflicts.Verb(Kind);
}

/// <summary>
/// The conflicts a replay stops on, handed to an agent that reads code. git settles what it can settle
/// by lines; what it cannot is two changes to the same lines, and which of them survives is a question
/// about what the code means. That question goes to a command in the config - a coding agent run
/// without a terminal, Claude Code by default - with the worktree, the files, both sides and the
/// commit's own message, and it edits the files in place.
///
/// Nothing here trusts the answer. A file counts as settled when every marker is gone and its bytes
/// changed; one the agent said it could not settle, or left markers in, stays in conflict for a hand.
/// Line endings are put back the way both sides had them, because an editor that writes LF into a
/// CRLF file makes a diff of every line, and that would go into the branch as the resolution.
/// </summary>
public static class Resolver
{
    /// <summary>
    /// What runs when the config names nothing. Claude Code in print mode: the prompt comes in on
    /// stdin, edits inside the worktree are accepted without a prompt, and git may only be asked to
    /// show things. Every other tool stays off, so the agent can read and write files and nothing else.
    /// </summary>
    public const string DefaultCommand =
        "claude -p --output-format text --no-session-persistence --max-turns 80 --permission-mode acceptEdits"
        + " --allowedTools \"Read,Edit,Write,MultiEdit,Grep,Glob,Bash(git diff:*),Bash(git show:*),Bash(git log:*)\"";

    /// <summary>The command the config names, or the default when it names none.</summary>
    public static string Command(SgRoot root) =>
        string.IsNullOrWhiteSpace(root.Config.ResolveCommand) ? DefaultCommand : root.Config.ResolveCommand.Trim();

    /// <summary>
    /// Asks the resolver to settle every file the stop left in conflict, then checks its work and
    /// stages what passed. Binary files are never asked about: there is no middle version of one.
    /// </summary>
    public static AutoResolveResult Resolve(SgRoot root, ConflictState state)
    {
        var git = root.Git;
        var worktree = state.Worktree;
        if (!state.InProgress) throw new SgException("nothing is stopped in " + worktree + ", so there is nothing to resolve.");
        var res = new AutoResolveResult { Kind = state.Kind, Stopped = state.Stopped, At = state.At, Of = state.Of, Command = Command(root) };
        if (state.Conflicted.Count == 0) return res;

        var text = new List<string>();
        var before = new Dictionary<string, Sides>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in state.Conflicted)
        {
            var sides = new Sides(git.ShowStageRaw(worktree, 1, path), git.ShowStageRaw(worktree, 2, path), git.ShowStageRaw(worktree, 3, path), OnDisk(worktree, path));
            if (sides.Binary)
            {
                res.Left.Add(new AutoResolveLeft(path, "binary file, pick a version"));
                continue;
            }
            before[path] = sides;
            text.Add(path);
        }
        if (text.Count == 0) return res;

        var message = git.StoppedMessage(worktree);
        var prompt = Prompt(state, text, message);
        var promptFile = git.PrivateFile(worktree, "resolve-prompt.md");
        File.WriteAllText(promptFile, prompt, Utf8);
        var (exe, args) = Split(res.Command);
        var env = new Dictionary<string, string>
        {
            ["SG_WORKTREE"] = worktree,
            ["SG_REPLAY"] = state.Verb,
            ["SG_STOPPED"] = state.Stopped,
            ["SG_FILES"] = string.Join("\n", text),
            ["SG_PROMPT_FILE"] = promptFile,
        };
        root.Log.Info($"asking the resolver about {text.Count} file(s): {res.Command}");
        var said = new StringBuilder();
        var r = Proc.RunStreaming(exe, args, worktree, root.Log,
            line => { lock (said) said.AppendLine(line); root.Log.Info("resolver: " + line); },
            line => { lock (said) said.AppendLine(line); root.Log.Info("resolver: " + line); },
            env, keepStdout: false, stdin: Utf8.GetBytes(prompt));
        res.Output = said.ToString().Trim();
        if (!r.Ok && res.Output.Length == 0) res.Output = $"the resolver exited with code {r.ExitCode} and said nothing";

        // Its own verdicts first: a file it says it left alone is left alone, whatever it holds.
        var saidLeft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in res.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("left:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[5..].Trim();
            var dash = rest.IndexOf(" - ", StringComparison.Ordinal);
            var path = PathUtil.Rel(dash < 0 ? rest : rest[..dash]);
            saidLeft[path] = dash < 0 ? "the resolver left it" : rest[(dash + 3)..].Trim();
        }

        // A resolver that failed before it touched anything - not logged in, not installed - says why
        // on its last line, and that is the reason a file was left, not "it did not change it".
        var failed = r.Ok ? null
            : "the resolver failed: " + (res.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"exit code {r.ExitCode}");

        var stage = new List<string>();
        foreach (var path in text)
        {
            var sides = before[path];
            var now = OnDisk(worktree, path);
            if (saidLeft.TryGetValue(path, out var why)) res.Left.Add(new AutoResolveLeft(path, why));
            else if (now == null) res.Left.Add(new AutoResolveLeft(path, "the file is gone from the worktree"));
            else if (now == sides.Conflicted) res.Left.Add(new AutoResolveLeft(path, failed ?? "the resolver did not change it"));
            else if (HasMarkers(now)) res.Left.Add(new AutoResolveLeft(path, "conflict markers are still in it"));
            else
            {
                var fixedUp = sides.Restore(now);
                if (!ReferenceEquals(fixedUp, now)) File.WriteAllText(PathUtil.Join(worktree, path), fixedUp, Bytes);
                stage.Add(path);
            }
        }
        if (!r.Ok && stage.Count > 0)
            root.Log.Warn($"the resolver exited with code {r.ExitCode}, but {stage.Count} file(s) came back whole and are taken");
        git.MarkResolved(worktree, stage);
        res.Resolved.AddRange(stage);
        return res;
    }

    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// Files are read and written as Latin-1: every byte is one char and comes back as the same byte,
    /// so a comparison and a line ending fix work on the exact bytes of a file in any encoding.
    /// </summary>
    static readonly Encoding Bytes = Encoding.Latin1;

    static string? OnDisk(string worktree, string rel)
    {
        try
        {
            var abs = PathUtil.Join(worktree, rel);
            return File.Exists(abs) ? File.ReadAllText(abs, Bytes) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A line git wrote as a marker. "=======" alone is a line some code has, so it does not count on its own.</summary>
    public static bool HasMarkers(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            if (raw.StartsWith("<<<<<<< ", StringComparison.Ordinal) || raw.StartsWith(">>>>>>> ", StringComparison.Ordinal)
                || raw.StartsWith("|||||||", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The three versions of one file and what the worktree holds with the markers in, as bytes. What
    /// the sides agree on about line endings and a BOM is what the settled file has to have again.
    /// </summary>
    sealed class Sides
    {
        readonly string _ours, _theirs;
        public readonly string Conflicted;

        public Sides(string @base, string ours, string theirs, string? conflicted)
        {
            _ours = ours;
            _theirs = theirs;
            Conflicted = conflicted ?? "";
            Binary = @base.Contains('\0') || ours.Contains('\0') || theirs.Contains('\0');
        }

        public bool Binary { get; }

        static bool AllCrlf(string s) => s.Contains('\n') && !s.Replace("\r\n", "").Contains('\n');
        static bool NoCr(string s) => !s.Contains('\r');
        static bool Bom(string s) => s.StartsWith("ï»¿", StringComparison.Ordinal);

        /// <summary>The same text, with the line endings and BOM both sides had. The same object when nothing had to change.</summary>
        public string Restore(string now)
        {
            var result = now;
            if (AllCrlf(_ours) && AllCrlf(_theirs) && !AllCrlf(result))
                result = result.Replace("\r\n", "\n").Replace("\n", "\r\n");
            else if (NoCr(_ours) && NoCr(_theirs) && result.Contains("\r\n", StringComparison.Ordinal))
                result = result.Replace("\r\n", "\n");
            if (Bom(_ours) && Bom(_theirs) && !Bom(result)) result = "ï»¿" + result;
            else if (!Bom(_ours) && !Bom(_theirs) && Bom(result)) result = result[3..];
            return result;
        }
    }

    /// <summary>
    /// Everything the agent needs to know and nothing it should guess: which side is which, what the
    /// commit meant to do, and what it may and may not touch. The sides are named from the replay, the
    /// way the page names them, because "the SVN version" means nothing during an import.
    /// </summary>
    public static string Prompt(ConflictState state, IReadOnlyList<string> files, string message)
    {
        var rebase = state.Kind != Replay.Import;
        var ours = rebase
            ? "the SVN version: what the checkout holds now, the newer snapshot this branch is being moved onto"
            : "the branch version: what this branch holds now";
        var theirs = rebase
            ? "the branch version: what the commit being replayed wants"
            : "the imported version: what the patch being replayed wants";
        var sb = new StringBuilder();
        sb.AppendLine($"A git {state.Verb} stopped on conflicts in this worktree. Settle them.");
        sb.AppendLine();
        sb.AppendLine($"Worktree: {state.Worktree}");
        sb.AppendLine("This is a git worktree managed by sg. The master repository is SVN; git holds one snapshot of it per sync and this branch on top.");
        sb.AppendLine(rebase
            ? $"The branch {state.Branch} is being rebased onto the newest snapshot of the checkout {state.Checkout}, one commit at a time."
            : $"A series of patches is being replayed onto the branch {state.Branch}, one at a time.");
        var where = state.Of > 0 ? $"{state.At} of {state.Of}" : "";
        sb.AppendLine($"It stopped on {(where.Length > 0 ? "commit " + where : "a commit")}{(state.Stopped.Length > 0 ? ": \"" + state.Stopped + "\"" : "")}.");
        if (message.Length > 0)
        {
            sb.AppendLine("That commit's own message, which says what it meant to do:");
            foreach (var line in message.Split('\n')) sb.AppendLine("    " + line.TrimEnd('\r'));
        }
        sb.AppendLine();
        sb.AppendLine("Files in conflict:");
        foreach (var f in files) sb.AppendLine("    " + f);
        sb.AppendLine();
        sb.AppendLine("Each file holds both versions between markers, with the version both started from in the middle:");
        sb.AppendLine($"    <<<<<<< ... up to |||||||   is {ours}");
        sb.AppendLine("    ||||||| ... up to =======   is the version both sides started from");
        sb.AppendLine($"    ======= ... up to >>>>>>>   is {theirs}");
        sb.AppendLine("Outside the markers the file already holds both sides' changes merged; only the blocks are open.");
        sb.AppendLine();
        sb.AppendLine("What to do:");
        sb.AppendLine("1. For each file, work out what each side changed against the base. `git diff :1:<path> :2:<path>` shows the first side's change,");
        sb.AppendLine("   `git diff :1:<path> :3:<path>` the second side's, and `git show REBASE_HEAD -- <path>` the whole commit being replayed.");
        sb.AppendLine("2. Settle every block so that both changes survive: the replayed commit's change applied on top of the other side's.");
        sb.AppendLine("   When both changed the same thing in different ways, keep the other side's structure and carry the replayed commit's intent into it.");
        sb.AppendLine("   When one side renamed or moved something, the other side's new lines may still use the old name, also outside the markers");
        sb.AppendLine("   in the same file: bring those in line too, and nothing else.");
        sb.AppendLine("3. Remove every marker line. Keep the file's line endings, encoding, BOM and indentation exactly as they are; many files here use CRLF.");
        sb.AppendLine("4. Change nothing else: no reformatting, no fixes to code that was not in conflict, no new features, no other files.");
        sb.AppendLine("5. Do not stage, commit, or run any git command that writes. The tool checks the files and stages them itself.");
        sb.AppendLine("6. A file you cannot settle with confidence stays as it is, markers included.");
        sb.AppendLine();
        sb.AppendLine("When you are done, print one line per file and nothing after them:");
        sb.AppendLine("    resolved: <path>");
        sb.AppendLine("    left: <path> - <why>");
        return sb.ToString();
    }

    /// <summary>
    /// A command line into the program and its arguments, the way a shell would read it: split on
    /// spaces, with double quotes holding an argument together. Enough for a program name and flags.
    /// </summary>
    public static (string Exe, List<string> Args) Split(string command)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        var has = false;
        foreach (var c in command)
        {
            if (c == '"') { quoted = !quoted; has = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (has) { parts.Add(cur.ToString()); cur.Clear(); has = false; }
                continue;
            }
            cur.Append(c);
            has = true;
        }
        if (has) parts.Add(cur.ToString());
        if (parts.Count == 0) throw new SgException("the resolver command is empty. Set resolveCommand in sg.json, or clear it to use Claude Code.");
        return (parts[0], parts.Skip(1).ToList());
    }
}
