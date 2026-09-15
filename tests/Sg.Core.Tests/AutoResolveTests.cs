namespace Sg.Core.Tests;

/// <summary>
/// The files a stopped replay leaves in conflict, handed to a command that edits them. The command
/// here is a script that stands in for the agent, so what is measured is everything around it: the
/// prompt and the files it gets, what counts as settled, the line endings put back, and the run that
/// carries on from one stop to the next until the rebase is through.
/// </summary>
public sealed class AutoResolveTests : IDisposable
{
    readonly Fixture f = new();

    public void Dispose() => f.Dispose();

    static string Raw(string root, string rel) => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// A resolver that is a PowerShell script: it reads the file list the way any resolver does, from
    /// SG_FILES, and does whatever the body says with each. The command goes into the config the way a
    /// person would type it, quotes and all.
    /// </summary>
    void UseResolver(string body)
    {
        var script = Path.Combine(f.Base, "resolver-" + Guid.NewGuid().ToString("N")[..6] + ".ps1");
        File.WriteAllText(script,
            "$ErrorActionPreference = 'Stop'\n"
            + "$files = $env:SG_FILES -split \"`n\" | Where-Object { $_ -ne '' }\n"
            + "$prompt = [Console]::In.ReadToEnd()\n"
            + "foreach ($rel in $files) {\n"
            + "  $path = Join-Path $env:SG_WORKTREE $rel\n"
            + "  $text = [IO.File]::ReadAllText($path)\n"
            + body + "\n"
            + "}\n");
        f.Root.Config.ResolveCommand = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
    }

    /// <summary>Every block settled for the side the pattern names, written back byte for byte otherwise.</summary>
    const string KeepTheirs =
        "  $re = [regex]::new('<<<<<<< [^\\n]*\\n(.*?)(\\|\\|\\|\\|\\|\\|\\|[^\\n]*\\n(.*?))?=======\\r?\\n(.*?)>>>>>>> [^\\n]*\\r?\\n', 'Singleline')\n"
        + "  $out = $re.Replace($text, { param($m) $m.Groups[4].Value })\n"
        + "  [IO.File]::WriteAllText($path, $out)\n"
        + "  Write-Output \"resolved: $rel\"";

    const string KeepOurs =
        "  $re = [regex]::new('<<<<<<< [^\\n]*\\n(.*?)(\\|\\|\\|\\|\\|\\|\\|[^\\n]*\\n(.*?))?=======\\r?\\n(.*?)>>>>>>> [^\\n]*\\r?\\n', 'Singleline')\n"
        + "  $out = $re.Replace($text, { param($m) $m.Groups[1].Value })\n"
        + "  [IO.File]::WriteAllText($path, $out)\n"
        + "  Write-Output \"resolved: $rel\"";

    /// <summary>A branch edit and an SVN edit to the same line of CMakeLists.txt, and the rebase stopped on it.</summary>
    string StoppedRebase(string branchText = "project(fort)\n# branch side\n", string svnText = "project(fort)\n# svn side\n")
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-a", f.Co).Path;
        Fixture.Put(wt, "CMakeLists.txt", branchText);
        git.Ok(wt, "commit", "-q", "-am", "branch edit: a comment about the branch\n\nThe body says why.");

        var other = f.OtherWc(f.MonoUrl + "/trunk");
        Fixture.Put(other, "CMakeLists.txt", svnText);
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "svn edit");
        Ops.Sync(f.Root, f.Co);

        var rb = Ops.Rebase(f.Root, wt);
        Assert.True(rb.Conflict, rb.Output);
        return wt;
    }

    [Fact]
    public void The_resolver_gets_the_files_and_the_commit_and_what_it_settles_is_staged()
    {
        var wt = StoppedRebase();
        UseResolver("  Write-Output \"got: $rel\"\n  Write-Output ('prompt mentions the commit: ' + $prompt.Contains('branch edit: a comment about the branch'))\n"
                    + "  Write-Output ('prompt mentions the body: ' + $prompt.Contains('The body says why.'))\n"
                    + "  Write-Output ('markers: ' + $text.Contains('|||||||'))\n" + KeepTheirs);

        var r = Conflicts.AutoResolve(f.Root, wt);

        Assert.Equal(["CMakeLists.txt"], r.Resolved);
        Assert.Empty(r.Left);
        Assert.True(r.AllResolved);
        Assert.Contains("got: CMakeLists.txt", r.Output);
        Assert.Contains("prompt mentions the commit: True", r.Output);
        Assert.Contains("prompt mentions the body: True", r.Output);
        // zdiff3: the base is between the markers, so the resolver sees what each side changed.
        Assert.Contains("markers: True", r.Output);
        Assert.Contains("branch edit", r.Stopped);
        Assert.Empty(f.Root.Git.ConflictedFiles(wt));
        Assert.False(f.Root.Git.NothingStaged(wt));
        Assert.Equal("project(fort)\n# branch side\n", Raw(wt, "CMakeLists.txt").Replace("\r\n", "\n"));

        var cont = Conflicts.Continue(f.Root, wt);
        Assert.True(cont.Ok, cont.Output);
        Assert.Equal(1, cont.Ahead);
    }

    [Fact]
    public void A_file_the_resolver_did_not_touch_stays_in_conflict()
    {
        var wt = StoppedRebase();
        UseResolver("  Write-Output \"looked at $rel\"");

        var r = Conflicts.AutoResolve(f.Root, wt);

        Assert.Empty(r.Resolved);
        var left = Assert.Single(r.Left);
        Assert.Equal("CMakeLists.txt", left.Path);
        Assert.Equal("the resolver did not change it", left.Why);
        Assert.Equal(["CMakeLists.txt"], f.Root.Git.ConflictedFiles(wt));
    }

    [Fact]
    public void A_resolver_that_fails_before_it_starts_names_the_failure_as_the_reason()
    {
        var wt = StoppedRebase();
        UseResolver("  Write-Output 'Failed to authenticate: not logged in'\n  exit 1");

        var r = Conflicts.AutoResolve(f.Root, wt);

        Assert.Equal("the resolver failed: Failed to authenticate: not logged in", Assert.Single(r.Left).Why);
        Assert.Equal(["CMakeLists.txt"], f.Root.Git.ConflictedFiles(wt));
    }

    [Fact]
    public void Markers_left_in_a_file_keep_it_in_conflict_and_a_reason_the_resolver_gave_is_kept()
    {
        var wt = StoppedRebase();
        // It edits the file, but not to the end: one marker line is still there. And it says so.
        UseResolver("  [IO.File]::WriteAllText($path, $text + \"`n\")\n  Write-Output \"left: $rel - not sure which comment wins\"");

        var r = Conflicts.AutoResolve(f.Root, wt);

        var left = Assert.Single(r.Left);
        Assert.Equal("not sure which comment wins", left.Why);
        Assert.Equal(["CMakeLists.txt"], f.Root.Git.ConflictedFiles(wt));

        // The same without a word from it: the markers are the reason.
        UseResolver("  [IO.File]::WriteAllText($path, $text + \"`n\")");
        r = Conflicts.AutoResolve(f.Root, wt);
        Assert.Equal("conflict markers are still in it", Assert.Single(r.Left).Why);
    }

    [Fact]
    public void Line_endings_both_sides_had_come_back_when_the_resolver_wrote_the_other_kind()
    {
        // CRLF on both sides, as most files in the real repository are.
        var wt = StoppedRebase("project(fort)\r\n# branch side\r\n", "project(fort)\r\n# svn side\r\n");
        UseResolver(KeepTheirs + "\n  $lf = [IO.File]::ReadAllText($path).Replace(\"`r`n\", \"`n\")\n  [IO.File]::WriteAllText($path, $lf)");

        var r = Conflicts.AutoResolve(f.Root, wt);

        Assert.True(r.AllResolved, string.Join("; ", r.Left.Select(l => l.Why)));
        Assert.Equal("project(fort)\r\n# branch side\r\n", Raw(wt, "CMakeLists.txt"));
        // And what is staged is the same bytes, not the LF version the script wrote.
        Assert.Equal("project(fort)\r\n# branch side\r\n", f.Root.Git.ShowStageRaw(wt, 0, "CMakeLists.txt"));
    }

    [Fact]
    public void A_run_settles_one_stop_after_another_until_the_rebase_is_through()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-b", f.Co).Path;
        Fixture.Put(wt, "CMakeLists.txt", "project(fort)\n# branch one\n");
        git.Ok(wt, "commit", "-q", "-am", "one: the cmake file");
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 2;\n");
        git.Ok(wt, "commit", "-q", "-am", "two: the engine");
        Fixture.Put(wt, "fort/dev/new.txt", "no conflict here\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "three: a new file");

        var other = f.OtherWc(f.MonoUrl + "/trunk");
        Fixture.Put(other, "CMakeLists.txt", "project(fort)\n# svn one\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "svn edits the cmake file");
        // The engine is an external: its own repository, its own commit.
        var engine = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        Fixture.Put(engine, "engine.cpp", "int engine = 9;\n");
        f.Svn.Ok(engine, "commit", "--non-interactive", "-m", "svn edits the engine");
        Ops.Sync(f.Root, f.Co);

        Assert.True(Ops.Rebase(f.Root, wt).Conflict);
        UseResolver(KeepTheirs);

        var run = Conflicts.AutoResolveAll(f.Root, wt);

        Assert.True(run.Ok, run.Why);
        Assert.Equal(2, run.Steps.Count);
        Assert.Equal(["CMakeLists.txt"], run.Steps[0].Resolved);
        Assert.Equal(["schmetterling/engine.cpp"], run.Steps[1].Resolved);
        Assert.Equal(0, run.Skipped);
        Assert.NotNull(run.Finished);
        Assert.Equal(3, run.Finished!.Ahead);
        Assert.False(git.RebaseInProgress(wt));
        Assert.True(git.IsClean(wt));
        Assert.Equal("project(fort)\n# branch one\n", Raw(wt, "CMakeLists.txt").Replace("\r\n", "\n"));
        Assert.Equal("int engine = 2;\n", Raw(wt, "schmetterling/engine.cpp").Replace("\r\n", "\n"));
        Assert.Equal("no conflict here\n", Raw(wt, "fort/dev/new.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_commit_settled_to_what_svn_already_has_is_skipped_by_a_run()
    {
        var wt = StoppedRebase();
        UseResolver(KeepOurs);

        var run = Conflicts.AutoResolveAll(f.Root, wt);

        Assert.True(run.Ok, run.Why);
        Assert.Equal(1, run.Skipped);
        Assert.Equal(0, run.Finished!.Ahead);
        Assert.False(f.Root.Git.RebaseInProgress(wt));
        Assert.Equal("project(fort)\n# svn side\n", Raw(wt, "CMakeLists.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_run_stops_short_on_a_file_the_resolver_leaves_and_the_rebase_stays_stopped()
    {
        var wt = StoppedRebase();
        UseResolver("  Write-Output \"left: $rel - cannot tell\"");

        var run = Conflicts.AutoResolveAll(f.Root, wt);

        Assert.False(run.Ok);
        Assert.Contains("cannot tell", run.Why);
        Assert.Single(run.Steps);
        Assert.True(f.Root.Git.RebaseInProgress(wt));
        Assert.Equal(["CMakeLists.txt"], Conflicts.State(f.Root, wt).Conflicted);
    }

    [Fact]
    public void Nothing_in_conflict_is_refused_rather_than_run()
    {
        var wt = StoppedRebase();
        f.Root.Git.TakeSide(wt, ["CMakeLists.txt"], ours: false);
        var ex = Assert.Throws<SgException>(() => Conflicts.AutoResolve(f.Root, wt));
        Assert.Contains("nothing is in conflict", ex.Message);
    }

    [Fact]
    public void The_command_line_splits_the_way_a_shell_reads_it()
    {
        var (exe, args) = Resolver.Split("claude -p --allowedTools \"Read,Edit,Bash(git diff:*)\" --max-turns 80");
        Assert.Equal("claude", exe);
        Assert.Equal(["-p", "--allowedTools", "Read,Edit,Bash(git diff:*)", "--max-turns", "80"], args);
        var (exe2, args2) = Resolver.Split("\"C:\\Tools\\my resolver.exe\" --fix");
        Assert.Equal("C:\\Tools\\my resolver.exe", exe2);
        Assert.Equal(["--fix"], args2);
        Assert.Throws<SgException>(() => Resolver.Split("   "));
    }

    [Fact]
    public void A_store_made_before_the_merge_settings_gets_them_on_open()
    {
        f.Setup();
        var git = f.Root.Git;
        Assert.Equal("true", git.ConfigGet("rerere.enabled"));
        Assert.Equal("zdiff3", git.ConfigGet("merge.conflictStyle"));

        // An older store: no version stamp, none of the keys.
        git.ConfigUnset("sg.configVersion");
        git.ConfigUnset("rerere.enabled");
        git.ConfigUnset("merge.conflictStyle");
        Assert.Null(git.ConfigGet("rerere.enabled"));

        var again = SgRoot.Open(f.RootDir, f.Log);
        Assert.Equal("true", again.Git.ConfigGet("rerere.enabled"));
        Assert.Equal("true", again.Git.ConfigGet("rerere.autoUpdate"));
        Assert.Equal("zdiff3", again.Git.ConfigGet("merge.conflictStyle"));
        Assert.NotNull(again.Git.ConfigGet("sg.configVersion"));
    }

    [Fact]
    public void A_resolution_made_once_is_made_again_by_git_on_the_next_rebase()
    {
        // rerere: the rebase stops, a hand settles it, the rebase is undone, and the next one over
        // the same conflict still stops - git never continues on its own - but with the file settled
        // the same way and staged, so there is nothing to pick and Continue is the whole of it. That
        // is what a push that aborted on a conflict, and the rebase by hand after it, used to lack.
        var wt = StoppedRebase();
        var git = f.Root.Git;
        Fixture.Put(wt, "CMakeLists.txt", "project(fort)\n# svn side\n# branch side\n");
        git.MarkResolved(wt, ["CMakeLists.txt"]);
        Assert.True(Conflicts.Continue(f.Root, wt).Ok);

        // Back to before the rebase, and onto the same snapshot again.
        var before = git.Out(wt, "rev-parse", "ORIG_HEAD");
        git.Ok(wt, "reset", "-q", "--hard", before);
        Assert.True(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().NeedsRebase);

        var rb = Ops.Rebase(f.Root, wt);
        Assert.True(rb.Conflict, rb.Output);
        var state = Conflicts.State(f.Root, wt);
        Assert.Empty(state.Conflicted);
        Assert.False(state.Stuck);
        Assert.Equal("project(fort)\n# svn side\n# branch side\n", Raw(wt, "CMakeLists.txt").Replace("\r\n", "\n"));

        // And a run has nothing to ask a resolver about: it only carries on.
        UseResolver("  throw 'the resolver should not have been asked'");
        var run = Conflicts.AutoResolveAll(f.Root, wt);
        Assert.True(run.Ok, run.Why);
        Assert.Empty(run.Steps);
        Assert.Equal(1, run.Finished!.Ahead);
    }
}
