using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sg.Core;

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] argv)
    {
        try
        {
            var a = new Args(argv);
            var log = new ConsoleLog(a.Has("--verbose") || a.Has("-v"));
            if (a.Command == null || a.Has("--help") || a.Has("-h"))
            {
                Help();
                return a.Command == null ? 1 : 0;
            }
            return a.Command switch
            {
                "init" => Init(a, log),
                "checkout" => Checkout(a, log),
                "sync" => Sync(a, log),
                "branch" => Branch(a, log),
                "rebase" => Rebase(a, log),
                "resolve" => ResolveCmd(a, log),
                "push" => PushCmd(a, log),
                "rm" => Rm(a, log),
                "shelve" => Shelve(a, log),
                "shelf" => ShelfCmd(a, log),
                "export" => ExportCmd(a, log),
                "import" => ImportCmd(a, log),
                "backup" => BackupCmd(a, log),
                "status" => Status(a, log),
                "server-branch" => ServerBranch(a, log),
                "server-checkout" => ServerCheckout(a, log),
                "update" => UpdateCmd(a, log),
                "version" => Version(a, log),
                _ => Unknown(a.Command),
            };
        }
        catch (SgException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    static void Help()
    {
        Console.WriteLine("""
            sg - git branches and worktrees over SVN checkouts. SVN stays the master.

            sg init [<root>]                          make the shared store in <root>/.sg
            sg checkout add <folder> [--name n]       register an SVN checkout and build its first snapshot
            sg checkout add --url <url> [<folder>]    svn checkout the URL first, into <root>\<name> by default
                 [--skip p]... [--junction p]... [--optional p]... [--shared junction|clone|copy]
                                                      --shared says how worktrees get the --junction folders:
                                                      a junction, a ReFS clone (copy-on-write), or a full copy
            sg sync [<checkout>] [--ignores]          svn update, new snapshot, move svn/<checkout>
            sg branch <name> [--from <checkout>]      new branch and worktree from svn/<checkout>
                 [--without p]... [--minimal] [--shared junction|clone|copy]
            sg rebase                                 rebase this worktree's branch on the latest snapshot
            sg resolve [status]                       what stopped here - a rebase or an import - and what is in conflict
            sg resolve ours|theirs [<path>...]        keep one whole version, of the named files or of every conflict
            sg resolve resolved <path>...             say those files are done, after editing them by hand
            sg resolve force                          when a patch will not go in at all: write what fits, leave the rest as .rej
            sg resolve continue|skip|abort            carry on, drop the one it stopped on, or put it all back
            sg push [-m <message>] [--check]          commit this branch to SVN, one commit per repository
                                                      --check only runs the pre-checks, and exits 10 when one fails
            sg rm <branch> [--force]                  remove a worktree and its branch
            sg shelve [-m <title>] [<path>...]        put local changes aside, here or in the named paths
            sg shelf [list] [--json]                  what is on the shelf
            sg shelf show <id> [--json]               one shelf: where it came from, and every file in it
            sg shelf restore <id> [--keep]            put it back where it came from. --keep leaves it on the shelf
            sg shelf drop <id>                        throw one away
            sg export [<worktree>] [-o <file>]        pack this branch's commits into one file for another machine
            sg import <file> [--name <b>] [--into <c>] [--show]
                                                      put one back here, against a checkout of the same repository
                                                      --show only reads the file and says what is in it
            sg backup [--check] [--force]             every branch, the uncommitted changes and the shelves, to the backup
                                                      repository as thin histories. --check only says what would go.
                                                      Exit code 10 when the remote holds a version that did not come from here
            sg backup set <url> [--prefix <p>] [--no-uncommitted]
                                                      where backups go. A prefix keeps two machines apart in one repository
            sg backup list                            what the remote holds, and how far each checkout here has drifted
            sg backup restore <branch> [--name <b>] [--into <c>] [--wip]
                                                      make the branch here again. --wip brings its uncommitted changes back too
            sg backup prune [--yes]                   what is on the remote and not here; --yes deletes it
            sg status [--full] [--json]               checkouts, worktrees, what needs a rebase or a push
            sg server-branch <name> [--from <checkout>] [--dry-run] [--no-checkout] [-m <message>]
                 [--keep <external>]... [--as <external>=<name>]...
                                                      copy the branch on the server, every repository, then check it out
                                                      --keep leaves an external on its branch, --as gives one a name of its own
            sg server-checkout <name|url> [--near <checkout>] [--name <n>]
                                                      new checkout of a server branch: copy the nearest one, svn switch
            sg update [--check] [--force]             install the newest GitHub build over this sg.exe
                 [--repo owner/name] [--dir <folder>]  --check only reports, and exits 10 when a newer build exists
            sg version

            Options: --json (machine output), --verbose (show every git and svn command), --root <folder>
            """);
    }

    static int Unknown(string cmd)
    {
        Console.Error.WriteLine("unknown command: " + cmd);
        Help();
        return 1;
    }

    static int UpdateCmd(Args a, ILog log)
    {
        var repo = Updater.Repo(a.Get("--repo"));
        var dir = Path.GetFullPath(a.Get("--dir") ?? Updater.InstallDir());
        var json = a.Has("--json");
        var check = Updater.Check(repo, dir, log);

        if (a.Has("--check"))
        {
            if (json) Json(new { repo, installDir = dir, local = check.Local, remote = check.Remote, newer = check.Newer });
            else
            {
                Console.WriteLine("installed: " + (check.Local?.ToString() ?? "unknown, no build.json in " + dir));
                Console.WriteLine("newest:    " + check.Remote);
                Console.WriteLine(check.Newer ? "a newer build is on GitHub. Run: sg update" : "up to date");
            }
            return check.Newer ? 10 : 0;
        }

        // sg-ui starts us with its own pid and asks to be started again once its files are replaced.
        var waitPid = a.Get("--wait-pid");
        var relaunch = a.Get("--relaunch");

        // Wait first, and refuse to linger. A waiter that outlives the attempt would install
        // later, when the user closes the app for their own reasons, which looks like the app
        // restarting itself out of nowhere.
        var pid = 0;
        if (waitPid != null && !int.TryParse(waitPid, out pid))
            throw new SgException("--wait-pid needs a process id, got: " + waitPid);
        if (waitPid != null && !WaitForExit(pid, log))
            throw new SgException($"sg-ui (process {waitPid}) did not close, so nothing was installed. "
                                  + "Close every sg window and run 'sg update' again.");

        if (!check.Newer && !a.Has("--force"))
        {
            if (json) Json(new { repo, installDir = dir, local = check.Local, remote = check.Remote, updated = false });
            else Console.WriteLine("up to date: " + check.Remote);
            Relaunch(relaunch, log);   // it closed itself for us, so put it back
            return 0;
        }

        UpdateResult r;
        try
        {
            r = Updater.Apply(check, dir, log);
        }
        catch (Exception)
        {
            Relaunch(relaunch, log);   // never leave the user without the app
            throw;
        }

        if (json) Json(new { repo, installDir = r.InstallDir, before = r.Before, after = r.After, files = r.Files, updated = true });
        else
        {
            Console.WriteLine($"updated {(r.Before?.ToString() ?? "unknown build")} -> {r.After}");
            Console.WriteLine("folder: " + r.InstallDir);
            if (relaunch == null) Console.WriteLine("close and reopen sg-ui to pick it up.");
        }
        Relaunch(relaunch, log);
        return 0;
    }

    /// <summary>True when the process is gone. False when it outlived the wait, and nothing should be installed.</summary>
    static bool WaitForExit(int pid, ILog log)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return true; }   // already gone
        using (p)
        {
            log.Info($"waiting for process {pid} to close");
            return p.WaitForExit(TimeSpan.FromSeconds(30));
        }
    }

    static void Relaunch(string? exe, ILog log)
    {
        if (exe == null) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (Exception ex) { log.Warn("could not start " + exe + ": " + ex.Message); }
    }

    static int Version(Args a, ILog log)
    {
        Console.WriteLine("sg 0.1 milestone 1");
        var stamp = Updater.ReadStamp(Updater.InstallDir());
        if (stamp != null) Console.WriteLine(stamp.ToString());
        Console.WriteLine(new Git("git", Path.GetTempPath(), log).Version());
        Console.WriteLine("svn " + new Svn("svn", log).Version());
        return 0;
    }

    static SgRoot FindRoot(Args a, ILog log, string? extra = null)
    {
        var explicitRoot = a.Get("--root") ?? Environment.GetEnvironmentVariable("SG_ROOT");
        if (explicitRoot != null) return SgRoot.Open(explicitRoot, log);
        return SgRoot.Find(Environment.CurrentDirectory, log)
               ?? (extra != null ? SgRoot.Find(extra, log) : null)
               ?? throw new SgException("no sg root found. Use --root <folder>, or run inside a root, a checkout, or a worktree.");
    }

    static int Init(Args a, ILog log)
    {
        var root = a.Pos.Count > 0 ? a.Pos[0] : Environment.CurrentDirectory;
        var r = Ops.Init(root, log, fsmonitor: !a.Has("--no-fsmonitor"));
        Console.WriteLine("sg root: " + r.RootPath);
        Console.WriteLine("next: sg checkout add <folder> [--skip <path>] [--junction <path>] [--optional <path>] [--shared junction|clone|copy]");
        return 0;
    }

    static int Checkout(Args a, ILog log)
    {
        if (a.Arg(0, "subcommand") != "add")
            throw new SgException("usage: sg checkout add <folder> [--name n] [--skip p]... [--junction p]... [--optional p]... [--shared junction|clone|copy]\n"
                                  + "       sg checkout add --url <url> [<folder>] [--name n] [--skip p]... [--junction p]... [--optional p]... [--shared junction|clone|copy]");
        var url = a.Get("--url");
        var folder = a.Pos.Count > 1 ? Path.GetFullPath(a.Pos[1]) : url != null ? null : Path.GetFullPath(a.Arg(1, "folder"));
        // A folder that is still to be checked out is no place to look for the root from; its parent is.
        var hint = folder == null ? null : Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder);
        var root = FindRoot(a, log, hint);
        var shared = SharedArg(a);
        var res = url != null
            ? Ops.CheckoutFromUrl(root, url, folder, a.GetAll("--skip"), a.GetAll("--junction"), a.GetAll("--optional"), a.Get("--name"), shared)
            : Ops.CheckoutAdd(root, folder!, a.GetAll("--skip"), a.GetAll("--junction"), a.GetAll("--optional"), a.Get("--name"), shared);
        if (a.Has("--json"))
            Json(new { checkout = res.Checkout, revision = res.Snapshot.Revision, snapshot = res.Snapshot.Sha, externals = res.Snapshot.Externals, warnings = res.Snapshot.Warnings });
        else
        {
            Console.WriteLine($"{res.Checkout.Name}: r{res.Snapshot.Revision}, snapshot {res.Snapshot.Sha[..10]}, {res.Snapshot.Externals.Count} externals");
            Warn(res.Snapshot.Warnings);
        }
        return 0;
    }

    static int Sync(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var co = Ops.ResolveCheckout(root, a.Pos.Count > 0 ? a.Pos[0] : null, Environment.CurrentDirectory);
        var r = Ops.Sync(root, co);
        if (a.Has("--ignores")) root.RefreshExcludes();
        if (a.Has("--json")) Json(r);
        else
        {
            Console.WriteLine($"{r.Checkout}: r{r.Revision}, {(r.Changed ? "new snapshot" : "no change")} {r.Sha[..10]}"
                              + (r.Overlaid > 0 ? $", {r.Overlaid} local edit(s) left out" : "")
                              + (r.Conflicts > 0 ? $", {r.Conflicts} svn conflict(s) in the checkout" : "")
                              + (r.KeptSwitched.Count > 0 ? $", kept {string.Join(", ", r.KeptSwitched)} switched" : ""));
            Warn(r.Warnings);
        }
        return 0;
    }

    static int Branch(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var name = a.Arg(0, "branch name");
        var co = Ops.ResolveCheckout(root, a.Get("--from"), Environment.CurrentDirectory);
        var r = Ops.Branch(root, name, co, a.GetAll("--without"), a.Has("--minimal"), SharedArg(a));
        if (a.Has("--json")) Json(r);
        else
        {
            Console.WriteLine($"branch {r.Branch} from svn/{r.Checkout}");
            Console.WriteLine("worktree: " + r.Path);
            if (r.Excluded.Count > 0) Console.WriteLine("left out: " + string.Join(", ", r.Excluded));
            if (r.Shared.Count > 0) Console.WriteLine(SharedFolders.Describe(r.SharedMode) + ": " + string.Join(", ", r.Shared));
        }
        return 0;
    }

    /// <summary>--shared as a mode, or null for "whatever the checkout says".</summary>
    static SharedMode? SharedArg(Args a)
    {
        var s = a.Get("--shared");
        return s == null ? null : SharedFolders.Parse(s);
    }

    static int Rebase(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var r = Ops.Rebase(root, Environment.CurrentDirectory);
        if (a.Has("--json")) Json(r);
        else if (r.Ok)
        {
            Console.WriteLine($"{r.Branch} is on the latest svn/{r.Checkout}, {r.Ahead} commit(s) ahead");
            if (r.Refreshed.Count > 0) Console.WriteLine("shared folders refreshed from the checkout: " + string.Join(", ", r.Refreshed));
        }
        else Console.WriteLine("conflict. Run 'sg resolve' to see it, pick a version per file, then 'sg resolve continue'.\n" + r.Output);
        return r.Ok ? 0 : 2;
    }


    /// <summary>
    /// The one place work that stopped is read and moved on, whatever stopped it. A rebase and an
    /// import leave the same thing behind - files at three stages, and a commit waiting to be made -
    /// so they are one command here with the same verbs, and none of those verbs names git.
    /// </summary>
    static int ResolveCmd(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var here = Environment.CurrentDirectory;
        var sub = a.Pos.Count > 0 ? a.Pos[0] : "status";
        var paths = a.Pos.Skip(1).Select(PathUtil.Rel).ToList();

        switch (sub)
        {
            case "status":
            {
                var s = Conflicts.State(root, here);
                if (a.Has("--json")) { Json(s); return s.InProgress ? 2 : 0; }
                if (!s.InProgress)
                {
                    Console.WriteLine(s.Branch + ": nothing is stopped here.");
                    return 0;
                }
                Console.WriteLine($"the {s.Verb} of {s.Branch} stopped"
                                  + (s.Of > 0 ? $" at {s.At} of {s.Of}" : "")
                                  + (s.Stopped.Length > 0 ? $", on '{s.Stopped}'" : ""));
                foreach (var p in s.Conflicted) Console.WriteLine("  conflict: " + p);
                foreach (var p in s.ByHand) Console.WriteLine("  by hand:  " + p);
                Console.WriteLine(s.Stuck
                    ? "  " + Conflicts.StuckNote(s.Kind) + (s.Kind == Replay.Import
                        ? "\n  Skip it with 'sg resolve skip', or write what fits of it into the worktree with"
                          + " 'sg resolve force' and put the refused hunks in by hand from the .rej files beside them."
                        : "\n  Skip it with 'sg resolve skip'.")
                    : s.Conflicted.Count == 0
                    ? "  nothing is in conflict any more. Carry on with: sg resolve continue"
                    : $"  ours is the {s.OursLabel}, theirs is the {s.TheirsLabel}."
                      + " Pick one per file with 'sg resolve ours|theirs <path>...', or edit them and say"
                      + " 'sg resolve resolved <path>...'. Then: sg resolve continue");
                return 2;
            }

            case "continue":
            case "skip":
            {
                var r = sub == "skip" ? Conflicts.Skip(root, here) : Conflicts.Continue(root, here);
                if (a.Has("--json")) { Json(r); return r.Ok ? 0 : 2; }
                if (r.Ok)
                {
                    Console.WriteLine($"the {r.Verb} is through. {r.Branch} is {r.Ahead} commit(s) ahead of svn/{r.Checkout}.");
                    if (r.Refreshed.Count > 0) Console.WriteLine("shared folders refreshed from the checkout: " + string.Join(", ", r.Refreshed));
                    return 0;
                }
                Console.WriteLine("it moved on and stopped again"
                                  + (r.Stopped.Length > 0 ? $", on '{r.Stopped}'" : "")
                                  + ". Run 'sg resolve' to see what is in conflict now.");
                if (r.Output.Length > 0) Console.WriteLine(r.Output);
                return 2;
            }

            case "abort":
            {
                var s = Conflicts.State(root, here);
                Conflicts.Abort(root, here);
                Console.WriteLine(s.Kind == Replay.Import
                    ? $"the import is off {s.Branch}, all of it: git undoes a series as one thing. The export file still holds every commit."
                    : $"the rebase is undone. {s.Branch} is back exactly as it was.");
                return 0;
            }

            case "ours":
            case "theirs":
            {
                var s = Conflicts.State(root, here);
                if (!s.InProgress) throw new SgException("nothing is stopped here, so there is no version to pick.");
                var pick = paths.Count > 0 ? paths : s.Conflicted;
                if (pick.Count == 0) throw new SgException("nothing is in conflict. Carry on with: sg resolve continue");
                root.Git.TakeSide(s.Worktree, pick, ours: sub == "ours");
                Console.WriteLine($"{pick.Count} file(s) keep the {(sub == "ours" ? s.OursLabel : s.TheirsLabel)}.");
                return Left(root, s.Worktree);
            }

            case "force":
            {
                var r = Conflicts.ApplyWhatFits(root, here);
                if (a.Has("--json")) { Json(r); return 0; }
                foreach (var p in r.Applied) Console.WriteLine("  took all of it: " + p);
                foreach (var p in r.Rejected) Console.WriteLine("  some refused:  " + p + "  (see " + p + ".rej)");
                Console.WriteLine(r.Rejected.Count == 0
                    ? "all of it fitted. Mark those files resolved, then: sg resolve continue"
                    : "put the refused hunks in by hand, delete the .rej files, then 'sg resolve resolved <path>...' and 'sg resolve continue'.");
                return 0;
            }

            case "resolved":
            {
                var s = Conflicts.State(root, here);
                if (!s.InProgress) throw new SgException("nothing is stopped here, so there is nothing to mark resolved.");
                if (paths.Count == 0) throw new SgException("name the files you edited, or use 'sg resolve ours|theirs' to keep a whole version.");
                root.Git.MarkResolved(s.Worktree, paths);
                Console.WriteLine($"{paths.Count} file(s) marked resolved.");
                return Left(root, s.Worktree);
            }

            default:
                throw new SgException("unknown: sg resolve " + sub
                                      + ". It takes status, ours, theirs, resolved, force, continue, skip or abort.");
        }
    }

    /// <summary>What is still in conflict after a version was picked, and what to do next.</summary>
    static int Left(SgRoot root, string worktree)
    {
        var left = root.Git.ConflictedFiles(worktree);
        Console.WriteLine(left.Count == 0
            ? "nothing is in conflict any more. Carry on with: sg resolve continue"
            : $"{left.Count} still in conflict: " + string.Join(", ", left.Take(10)));
        return 0;
    }

    static int PushCmd(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var msg = a.Get("-m", "--message");

        // --check answers the same questions the Push window shows, without writing anything.
        if (a.Has("--check"))
        {
            var p = Push.Preview(root, Environment.CurrentDirectory);
            if (a.Has("--json")) Json(new { p.Branch, p.Checkout, p.Ready, p.Checks });
            else
            {
                foreach (var c in p.Checks)
                    Console.WriteLine($"  {(c.Ok ? "ok  " : "FAIL")}  {c.Name}: {c.Detail}");
                Console.WriteLine(p.Ready ? "ready to push" : "push would refuse");
            }
            return p.Ready ? 0 : 10;
        }

        var interactive = !Console.IsInputRedirected
                          && Environment.GetEnvironmentVariable("CLAUDECODE") == null
                          && Environment.GetEnvironmentVariable("CURSOR_AGENT") == null;
        var r = Push.Run(root, Environment.CurrentDirectory, msg, interactive, text => EditMessage(root, text));
        if (a.Has("--json")) Json(r);
        else
        {
            foreach (var g in r.Groups)
                Console.WriteLine($"  {(g.Wc.Length == 0 ? "root" : g.Wc),-30} {g.State,-10}"
                                  + (g.Revision.HasValue ? $" r{g.Revision}" : "")
                                  + (g.Error != null ? "  " + g.Error.Split('\n')[0] : ""));
            Console.WriteLine(r.AllCommitted
                ? $"pushed. {r.Branch} now equals svn/{r.Checkout} at r{r.Revision}"
                : $"partly pushed. {r.Branch} keeps the rest: {r.BranchState}");
            Warn(r.Warnings);
        }
        return r.AllCommitted ? 0 : 2;
    }

    static string? EditMessage(SgRoot root, string initial)
    {
        var f = Path.Combine(Path.GetTempPath(), "sg-push-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(f, initial.TrimEnd() + "\n\n# Lines that start with # are dropped. Save and close to push. An empty message aborts.\n", new UTF8Encoding(false));
        var editor = root.Git.ConfigGet("core.editor")
                     ?? Environment.GetEnvironmentVariable("VISUAL")
                     ?? Environment.GetEnvironmentVariable("EDITOR")
                     ?? "notepad";
        var script = Path.ChangeExtension(f, ".cmd");
        File.WriteAllText(script, "@" + editor + " \"" + f + "\"\r\n");
        try
        {
            var r = Proc.Run("cmd", ["/c", script], null, new NullLog());
            if (!r.Ok) throw new SgException("editor failed: " + editor + "\n" + r.StdErr);
            return Push.CleanMessage(File.ReadAllText(f)) is { Length: > 0 } clean ? clean : null;
        }
        finally
        {
            File.Delete(f);
            File.Delete(script);
        }
    }

    static int ServerBranch(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var name = a.Arg(0, "branch name");
        var co = Ops.ResolveCheckout(root, a.Get("--from"), Environment.CurrentDirectory);
        var parts = a.GetAll("--keep").Select(k => new BranchPart { Wc = k, Keep = true }).ToList();
        foreach (var s in a.GetAll("--as"))
        {
            var eq = s.IndexOf('=');
            if (eq <= 0 || eq == s.Length - 1) throw new SgException("--as wants <external>=<branch name>, for example: --as libs/tools=rel-1-tools");
            parts.Add(new BranchPart { Wc = s[..eq], Branch = s[(eq + 1)..] });
        }
        var plan = Server.PlanBranch(root, co, name, a.Get("-m", "--message"), parts);
        Console.Error.WriteLine(plan.Describe());
        if (a.Has("--dry-run"))
        {
            Console.WriteLine("dry run. Nothing changed on the server.");
            return 0;
        }
        Server.ExecuteBranch(root, plan);
        foreach (var r in plan.Repos) Console.WriteLine($"  {r.ReposRoot,-60} r{r.Revision}");
        if (a.Has("--no-checkout"))
        {
            Console.WriteLine($"branch {name} is on the server. Check it out with: sg server-checkout {name}");
            return 0;
        }
        var res = Server.Checkout(root, co, plan.NewRootUrl, name);
        PrintCheckout(res);
        return 0;
    }

    static int ServerCheckout(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var target = a.Arg(0, "branch name or URL");
        var near = Ops.ResolveCheckout(root, a.Get("--near"), Environment.CurrentDirectory);
        PrintCheckout(Server.Checkout(root, near, target, a.Get("--name")));
        return 0;
    }

    static void PrintCheckout(CheckoutResult res)
    {
        Console.WriteLine($"checkout {res.Checkout.Name}: {res.Checkout.Path}");
        Console.WriteLine($"  {res.Checkout.Url} r{res.Snapshot.Revision}, snapshot {res.Snapshot.Sha[..10]}, {res.Snapshot.Externals.Count} externals");
        Console.WriteLine($"  next: sg branch <name> --from {res.Checkout.Name}");
        Warn(res.Snapshot.Warnings);
    }

    static int Shelve(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var title = a.Get("-m", "--message") ?? "shelf";
        var paths = a.Pos.Count > 0 ? a.Pos.Select(p => Rel(root, p)).ToList() : null;
        var res = Shelf.Save(root, Environment.CurrentDirectory, paths, title);
        if (a.Has("--json")) { Json(res); return 0; }
        Console.WriteLine($"{res.Shelf.Id}: {res.Shelf.Count} file(s) from {res.Shelf.Where}");
        foreach (var f in res.Shelf.Files.Take(20)) Console.WriteLine($"  {f.Code,-12} {f.Path}");
        if (res.Shelf.Count > 20) Console.WriteLine($"  ... and {res.Shelf.Count - 20} more");
        if (res.LeftBehind.Count > 0)
            Console.WriteLine("left in the checkout, these carry an svn property change a shelf cannot hold: "
                              + string.Join(", ", res.LeftBehind));
        Console.WriteLine("put it back with: sg shelf restore " + res.Shelf.Id);
        return 0;
    }

    /// <summary>
    /// Packs a branch into one file. The commits go as patches and the base goes as SVN revisions,
    /// because the far side builds its own snapshot and a sha from here means nothing there.
    /// </summary>
    static int ExportCmd(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var worktree = a.Pos.Count > 0 ? Path.GetFullPath(a.Pos[0]) : Environment.CurrentDirectory;
        var branch = root.Git.CurrentBranch(worktree);
        var file = a.Get("-o", "--out") ?? Path.Combine(Environment.CurrentDirectory, Export.SuggestName(branch));
        var res = Export.Write(root, worktree, file);
        if (a.Has("--json")) { Json(res); return 0; }
        Console.WriteLine($"{res.File}");
        Console.WriteLine($"  {res.Branch} on {res.Checkout}: {res.Commits} commit(s), {res.Bytes / 1024} KB");
        if (res.Uncommitted > 0)
            Console.WriteLine($"  {res.Uncommitted} uncommitted change(s) are NOT in it. Commit them and export again, or shelve them.");
        Console.WriteLine("  put it back with: sg import " + Path.GetFileName(res.File));
        return 0;
    }

    /// <summary>Puts one back, onto whatever revision this checkout is at, and says how far that is from the export.</summary>
    static int ImportCmd(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var file = Path.GetFullPath(a.Arg(0, "an export file"));

        if (a.Has("--show"))
        {
            var m = Export.Read(file);
            if (a.Has("--json")) { Json(m); return 0; }
            Console.WriteLine($"{m.Branch} from {m.Checkout}, {m.Commits} commit(s), made {m.Created:yyyy-MM-dd HH:mm} on {m.From}");
            foreach (var b in m.Bases) Console.WriteLine($"  {b.Where,-26} r{b.Revision,-10} {b.Url}");
            foreach (var s in m.Subjects) Console.WriteLine("  . " + s);
            return 0;
        }

        var res = Export.Import(root, file, a.Get("--name"), a.Get("--into"));
        if (a.Has("--json")) { Json(res); return 0; }
        Console.WriteLine($"{res.Branch} on {res.Checkout}: {res.Applied} of {res.Commits} commit(s) in {res.Path}");
        foreach (var d in res.Drift) Console.WriteLine("  moved on since the export: " + d);
        if (res.Ok)
        {
            Console.WriteLine(res.Drift.Count == 0
                ? "  the checkout here is at the revision it was exported from."
                : "  every commit merged across that, so read the diff before you push.");
            return 0;
        }
        Console.Error.WriteLine($"stopped at: {res.Stopped}");
        foreach (var p in res.Conflicted.Take(20)) Console.Error.WriteLine("  conflict: " + p);
        if (res.Why != null) Console.Error.WriteLine(res.Why.Split('\n')[0]);
        Console.Error.WriteLine($"the import is waiting in {res.Path}, with the rest of the series behind it."
                                + "\nGo there and run 'sg resolve' to see it, pick a version per file, then 'sg resolve continue'.");
        return 1;
    }

    static int ShelfCmd(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var sub = a.Pos.Count > 0 ? a.Pos[0] : "list";
        switch (sub)
        {
            case "list":
            {
                var all = Shelf.List(root);
                if (a.Has("--json")) { Json(all); return 0; }
                if (all.Count == 0) { Console.WriteLine("the shelf is empty. Put something on it with 'sg shelve'."); return 0; }
                foreach (var sh in all)
                    Console.WriteLine($"  {sh.Id,-34} {sh.Count,3} file(s)  {(sh.IsCheckout ? "checkout" : "branch"),-8} {sh.Where,-18} {sh.Title}"
                                      + (sh.Gone ? "   (its folder is gone)" : ""));
                return 0;
            }
            case "show":
            {
                var sh = Shelf.Read(root, a.Arg(1, "shelf id"));
                if (a.Has("--json")) { Json(sh); return 0; }
                Console.WriteLine($"{sh.Id}  {sh.Title}");
                Console.WriteLine($"  from {(sh.IsCheckout ? "checkout " + sh.Checkout : "branch " + sh.Branch)} in {sh.Path}");
                Console.WriteLine($"  made {sh.Created:yyyy-MM-dd HH:mm}, against {(sh.Base.Length >= 10 ? sh.Base[..10] : sh.Base)}");
                foreach (var f in sh.Files) Console.WriteLine($"  {f.Code,-12} {f.Path}");
                return 0;
            }
            case "restore":
            {
                var res = Shelf.Restore(root, a.Arg(1, "shelf id"), a.Has("--keep"));
                if (a.Has("--json")) { Json(res); return res.Conflicted.Count > 0 ? 2 : 0; }
                Console.WriteLine($"{res.Shelf.Id}: {res.Written.Count} written, {res.Deleted.Count} deleted"
                                  + (res.Merged.Count > 0 ? $", {res.Merged.Count} merged into a file that had moved on" : ""));
                if (res.Conflicted.Count > 0)
                {
                    Console.WriteLine("these hold conflict markers now, and the shelf was kept:");
                    foreach (var c in res.Conflicted) Console.WriteLine("  " + c);
                    return 2;
                }
                if (res.Kept) Console.WriteLine("kept on the shelf: " + res.Shelf.Id);
                return 0;
            }
            case "drop":
                Shelf.Drop(root, a.Arg(1, "shelf id"));
                return 0;
            default:
                throw new SgException("usage: sg shelf [list] | show <id> | restore <id> [--keep] | drop <id>");
        }
    }

    /// <summary>A path a caller typed, as the working copy names it: relative, with forward slashes.</summary>
    static string Rel(SgRoot root, string path)
    {
        var full = Path.GetFullPath(path);
        var co = root.CheckoutContaining(full);
        var top = co?.Path ?? root.Git.Toplevel(Environment.CurrentDirectory);
        return PathUtil.RelativeTo(top, full);
    }

    static int Rm(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var name = a.Arg(0, "branch name");
        Ops.Remove(root, name, a.Has("--force"));
        Console.WriteLine("removed " + name);
        return 0;
    }

    static int Status(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var s = Ops.Status(root, a.Has("--full"));
        if (a.Has("--json")) { Json(s); return 0; }
        Console.WriteLine("root: " + s.Root);
        foreach (var c in s.Checkouts)
            Console.WriteLine($"  checkout {c.Name,-20} r{c.Revision,-7} {c.Path}" + (c.LocalEdits.HasValue ? $"  {c.LocalEdits} local edit(s)" : "")
                              + (c.Shelves > 0 ? $"  {c.Shelves} shelf/shelves" : ""));
        foreach (var w in s.Worktrees)
            Console.WriteLine($"  branch   {w.Branch,-20} base svn/{w.Base,-12} +{w.Ahead}"
                              + (w.NeedsRebase ? " needs-rebase" : "")
                              + (w.Dirty ? $" dirty({w.DirtyFiles})" : "")
                              + (w.Pending ? " NOT-PUSHED-YET" : "")
                              + (w.Missing ? " folder-missing" : "")
                              + (w.Shelves > 0 ? $" shelved({w.Shelves})" : "")
                              + BackupNote(w)
                              + "  " + w.Path);
        if (s.BackupUrl != null) Console.WriteLine("backup: " + s.BackupUrl);
        return 0;
    }

    static string BackupNote(WorktreeStatus w) =>
        w.NotBackedUp < 0 ? "" : w.NotBackedUp == 0 ? " backed-up" : $" not-backed-up({w.NotBackedUp})";

    /// <summary>The backup: where it goes, what goes, what is there, and one branch back again.</summary>
    static int BackupCmd(Args a, ILog log)
    {
        var root = FindRoot(a, log);
        var sub = a.Pos.Count > 0 ? a.Pos[0] : "";
        var json = a.Has("--json");
        switch (sub)
        {
            case "set":
            {
                var url = a.Arg(1, "the URL of the backup repository");
                bool? uncommitted = a.Has("--no-uncommitted") ? false : a.Has("--uncommitted") ? true : null;
                var b = Backup.Set(root, url, a.Get("--prefix"), uncommitted);
                if (json) { Json(b); return 0; }
                Console.WriteLine("backups go to " + b.Url + (b.Prefix.Length > 0 ? " under " + b.Prefix + "/" : "")
                                  + (b.Uncommitted ? "" : ", committed work only"));
                Console.WriteLine("  send them with: sg backup");
                return 0;
            }
            case "clear":
                Backup.Clear(root);
                Console.WriteLine("no backup URL any more. What is on the remote stays there.");
                return 0;
            case "list":
            {
                var list = Backup.List(root);
                if (json) { Json(list); return 0; }
                Console.WriteLine("backup: " + Backup.Require(root).Url);
                if (list.Count == 0) Console.WriteLine("  nothing of sg's is there yet");
                foreach (var e in list)
                {
                    if (e.Unreadable != null) { Console.WriteLine($"  {e.Kind,-7} {e.Name,-28} cannot be read: {e.Unreadable}"); continue; }
                    var what = e.Kind switch
                    {
                        "branch" => $"{e.Commits} commit(s)",
                        "wip" => "uncommitted changes of " + e.Branch,
                        "edits" => "local edits of checkout " + e.Branch,
                        _ => "shelf \"" + e.Title + "\"" + (e.Branch.Length > 0 ? " of " + e.Branch : ""),
                    };
                    Console.WriteLine($"  {e.Kind,-7} {e.Name,-28} {what}  from {(e.Checkout.Length > 0 ? e.Checkout : e.Url)} r{e.Revision}"
                                      + (e.ExistsHere ? "  (here)" : "") + (e.HasWip ? "  +wip" : "")
                                      + (e.Last is { } t ? $"  {t.LocalDateTime:yyyy-MM-dd HH:mm}" : ""));
                    foreach (var d in e.Drift) Console.WriteLine("           moved on since: " + d);
                }
                return 0;
            }
            case "restore":
            {
                var name = a.Arg(1, "the branch to restore");
                var r = Backup.Restore(root, name, a.Get("--name"), a.Get("--into"), a.Has("--wip"));
                if (json) { Json(r); return r.Ok ? 0 : 1; }
                if (r.Branch.Length == 0)
                {
                    Console.WriteLine($"the local edits of {r.Checkout} came back" + (r.WipWritten ? " into " + r.Path : " as shelf " + r.WipShelf));
                }
                else
                {
                    Console.WriteLine($"{r.Branch} on {r.Checkout}: {r.Applied} of {r.Commits} commit(s) in {r.Path}"
                                      + (r.Relinked ? "  (the store still had them)" : ""));
                    foreach (var d in r.Drift) Console.WriteLine("  moved on since the backup: " + d);
                }
                if (r.WipShelf != null && r.Branch.Length > 0)
                    Console.WriteLine(r.WipWritten ? "  the uncommitted changes are written into the worktree" : "  the uncommitted changes wait as shelf " + r.WipShelf);
                foreach (var c in r.WipConflicted.Take(20)) Console.WriteLine("  uncommitted change in conflict: " + c);
                foreach (var sh in r.Shelves) Console.WriteLine("  shelf made again: " + sh);
                if (r.Ok) return 0;
                Console.Error.WriteLine($"stopped at: {r.Stopped}");
                foreach (var c in r.Conflicted.Take(20)) Console.Error.WriteLine("  conflict: " + c);
                if (r.Why != null) Console.Error.WriteLine(r.Why.Split('\n')[0]);
                Console.Error.WriteLine($"the branch keeps the {r.Applied} commit(s) that did go in.");
                return 1;
            }
            case "prune":
            {
                var yes = a.Has("--yes");
                var gone = Backup.Prune(root, yes);
                if (json) { Json(new { deleted = yes, refs = gone }); return 0; }
                if (gone.Count == 0) { Console.WriteLine("nothing on the remote that is not here"); return 0; }
                foreach (var r in gone) Console.WriteLine((yes ? "  deleted  " : "  remote only  ") + r);
                if (!yes) Console.WriteLine("delete them with: sg backup prune --yes");
                return 0;
            }
            case "":
            {
                var check = a.Has("--check");
                var r = Backup.Run(root, check, a.Has("--force"));
                if (json) { Json(r); return r.Rejected > 0 ? 10 : r.Ok ? 0 : 1; }
                Console.WriteLine((check ? "backup check: " : "backup: ") + r.Url);
                foreach (var i in r.Items)
                    Console.WriteLine($"  {i.State,-11} {i.Kind,-7} {i.Name,-28}" + (i.Kind == "branch" ? $" {i.Commits} commit(s)" : "")
                                      + (i.Why != null ? "\n              " + i.Why : ""));
                foreach (var o in r.RemoteOnly) Console.WriteLine("  remote only " + o + "   (sg backup prune)");
                if (r.Items.Count == 0) Console.WriteLine("  nothing here to back up");
                return r.Rejected > 0 ? 10 : r.Ok ? 0 : 1;
            }
            default:
                throw new SgException("sg backup takes: set <url>, list, restore <branch>, prune, clear, or nothing at all (which sends everything)");
        }
    }

    static void Warn(IEnumerable<string> warnings)
    {
        foreach (var w in warnings) Console.Error.WriteLine("warning: " + w);
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        // As words, the way the config file writes them. A reader of "stopped": 2 has to go and find
        // the enum to learn it means an import; a reader of "stopped": "import" already knows.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    static void Json(object o) => Console.WriteLine(JsonSerializer.Serialize(o, JsonOpts));
}

/// <summary>Everything goes to stderr, so --json output on stdout stays clean. Progress redraws one line when stderr is a terminal.</summary>
sealed class ConsoleLog(bool verbose) : ILog
{
    static readonly bool Tty = !Console.IsErrorRedirected;
    readonly object _lock = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    long _lastMs = -100000;
    long _lastLinePercent = -1;
    int _lastLen;

    public void Info(string message) { lock (_lock) { ClearLine(); Console.Error.WriteLine(message); } }
    public void Warn(string message) { lock (_lock) { ClearLine(); Console.Error.WriteLine("warning: " + message); } }

    public void Cmd(string message)
    {
        if (!verbose) return;
        lock (_lock) { ClearLine(); Console.Error.WriteLine("$ " + message); }
    }

    public void Progress(string label, long done, long total, string unit, string? detail)
    {
        lock (_lock)
        {
            var now = _clock.ElapsedMilliseconds;
            var final = total > 0 && done >= total;
            if (Tty)
            {
                if (now - _lastMs < 100 && !final) return;
                _lastMs = now;
                var text = Format(label, done, total, unit, detail);
                var width = Width() - 1;
                if (text.Length > width) text = text[..Math.Max(0, width)];
                Console.Error.Write("\r" + text.PadRight(_lastLen));
                _lastLen = text.Length;
            }
            else
            {
                // No terminal: one line per 10 percent, or every 5 seconds when the total is unknown.
                var percent = total > 0 ? done * 100 / total / 10 * 10 : -1;
                if (total > 0 ? percent == _lastLinePercent && !final : now - _lastMs < 5000) return;
                _lastMs = now;
                _lastLinePercent = percent;
                Console.Error.WriteLine(Format(label, done, total, unit, null));
            }
        }
    }

    public void ProgressEnd(string label, string? summary)
    {
        lock (_lock)
        {
            ClearLine();
            Console.Error.WriteLine(label + ": " + (summary ?? "done"));
            _lastMs = -100000;
            _lastLinePercent = -1;
        }
    }

    void ClearLine()
    {
        if (!Tty || _lastLen == 0) return;
        Console.Error.Write("\r" + new string(' ', _lastLen) + "\r");
        _lastLen = 0;
    }

    static string Format(string label, long done, long total, string unit, string? detail)
    {
        var bar = "";
        if (total > 0)
        {
            var frac = Math.Clamp((double)done / total, 0, 1);
            const int width = 24;
            var filled = (int)(frac * width);
            bar = "[" + new string('#', filled) + new string('.', width - filled) + $"] {frac * 100,3:0}% ";
        }
        var nums = unit == "B"
            ? (total > 0 ? $"{Human(done)} / {Human(total)}" : Human(done))
            : (total > 0 ? $"{done}/{total} {unit}" : $"{done} {unit}");
        return $"{label} {bar}{nums}" + (detail != null ? "  " + detail : "");
    }

    static string Human(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB"
        : b >= 1 << 20 ? $"{b / (double)(1 << 20):0} MB"
        : $"{b / 1024.0:0} KB";

    static int Width()
    {
        try { return Console.WindowWidth; }
        catch { return 120; }
    }
}

sealed class Args
{
    static readonly HashSet<string> ValueOpts = new(StringComparer.OrdinalIgnoreCase)
    {
        "--from", "--near", "--skip", "--junction", "--optional", "--without", "--root", "--name", "-m", "--message", "--url", "--keep", "--as", "--shared",
        "--repo", "--dir", "--wait-pid", "--relaunch", "-o", "--out", "--into", "--prefix",
    };

    public string? Command;
    public readonly List<string> Pos = new();
    readonly Dictionary<string, List<string>> _opts = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            if (t.StartsWith('-') && t.Length > 1)
            {
                var key = t;
                string? val = null;
                var eq = t.IndexOf('=');
                if (eq > 0) { key = t[..eq]; val = t[(eq + 1)..]; }
                if (TakesValue(key))
                {
                    val ??= i + 1 < argv.Length ? argv[++i] : throw new SgException($"{key} needs a value, for example: {key} libs/tools");
                    if (!_opts.TryGetValue(key, out var l)) _opts[key] = l = new List<string>();
                    l.Add(val);
                }
                else _flags.Add(key);
            }
            else if (Command == null) Command = t;
            else Pos.Add(t);
        }
    }

    /// <summary>
    /// Whether this option eats the word after it. Every option in the list does, except one: --keep
    /// names an external for 'server-branch' and is a plain yes for 'sg shelf restore', where it says
    /// to leave the shelf where it is. Read as a value option there it swallowed the wrong word and
    /// answered no, so the shelf was thrown away by the very flag that asked to keep it. The command
    /// is always the first word that is not an option, so it is known by the time its options are read.
    /// </summary>
    bool TakesValue(string key) =>
        ValueOpts.Contains(key)
        && !(key.Equals("--keep", StringComparison.OrdinalIgnoreCase) && Command is "shelf" or "shelve");

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Get(params string[] keys)
    {
        foreach (var k in keys)
            if (_opts.TryGetValue(k, out var l)) return l[^1];
        return null;
    }

    public List<string> GetAll(string key) => _opts.TryGetValue(key, out var l) ? l : new List<string>();

    public string Arg(int i, string what) => i < Pos.Count ? Pos[i] : throw new SgException("missing " + what);
}
