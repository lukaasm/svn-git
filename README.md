# sg

Git branches and worktrees over SVN checkouts. SVN stays the master.
The design is in [DESIGN.md](DESIGN.md). This is milestone 1: the core library and the CLI.

## Build

```powershell
dotnet build
dotnet test
dotnet publish src/sg -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "$env:LOCALAPPDATA\sg"
```

The exe lands in `%LOCALAPPDATA%\sg\sg.exe`. That folder is on the user PATH.
Needs git.exe and svn.exe on PATH, and the .NET 10 runtime.

## Updates

GitHub Actions builds every push to `main` and publishes one rolling prerelease, `latest-build`.
It holds `sg-win-x64.zip`: `sg.exe`, `ui\sg-ui.exe`, the `sg-ui.cmd` shim, and `build.json` with the run id and the commit.
The same build is kept as a workflow artifact, and the workflow deletes the artifacts of every older run.

```powershell
sg update --check   # what is installed, what is on GitHub. Exit code 10 means a newer build exists
sg update           # download it and write it over %LOCALAPPDATA%\sg
sg version          # prints the installed build
```

On a machine with no sg on it yet there is nothing to run `sg update` with, so `scripts/install-sg.ps1`
does that first install: it reads the same release, unpacks it into `%LOCALAPPDATA%\sg`, and puts that
folder on the user PATH. Nothing outside the user's own profile is written and nothing asks to elevate, and no token is
needed. `-Force` closes a running `sg-ui` first; `-NoPath` leaves PATH alone; `-Destination` installs somewhere else.

```powershell
irm https://raw.githubusercontent.com/lukaasm/svn-git/main/scripts/install-sg.ps1 | iex
```

`sg update` compares `build.json` next to `sg.exe` with the run id in the release notes.
It replaces `sg.exe` while `sg.exe` runs: Windows allows renaming a running exe, so the old one is left as `sg.exe.sg-old`
and swept on the next update. Point elsewhere with `--repo owner/name`.

The repository is public, so none of this needs a token. `sg` still uses one when it finds one - `SG_GH_TOKEN`,
`GH_TOKEN`, `GITHUB_TOKEN`, then whatever `gh auth token` prints - because an authenticated call gets a far larger share
of GitHub's hourly rate limit. Were the repository made private again, a call without a token would answer 404, which is
also what GitHub says for a release that is not there.

The app checks GitHub every hour (Settings, 0 turns it off) and shows an "Update and restart" bar when a build is ready.
Pressing it closes the app, lets `sg.exe` install the build, and starts the app again.

## First use on the real checkout

```powershell
sg init D:\work
sg checkout add D:\work\monorepo --skip libs/prebuilt --junction libs/prebuilt --optional libs/tools
sg status
```

`--junction` names the folders every worktree shares with the checkout. `--shared` says how: `junction` (the default,
one folder on disk that every worktree looks into), `clone` (a ReFS block clone: a private folder that shares disk with
the checkout until one side writes, so it costs nothing until then), or `copy` (a full copy, on any volume). A clone
needs the checkout and the worktrees on one ReFS volume, like a Dev Drive; sg checks that before it writes anything.
`sg branch x --shared copy` overrides the checkout's setting for one branch.
A clone or copy leaves `.svn` out: a worktree is not an SVN working copy, and `.svn` is most of the bytes. It follows the
checkout one rebase at a time where a junction follows it live: `sg rebase` mirrors it from the checkout again, and
edits made inside it do not survive that.

A checkout that is not on disk yet comes from its URL. Its name is the last part of the URL unless `--name` says otherwise, and it lands in `<root>\<name>` unless a folder is given:

```powershell
sg checkout add --url https://svn.example.com/svn/monorepo/branches/main --skip libs/prebuilt --junction libs/prebuilt
```

The first `checkout add` hashes about 16 GB once. Expect minutes.
It writes one file into the checkout: `D:\work\monorepo\.git` (a one-line pointer to `D:\work\.sg`).
Delete that file and `D:\work\.sg` to undo everything. SVN never notices either of them.

## Daily loop

```powershell
sg branch my-feature              # worktree D:\work\my-feature, branch my-feature, agent notes inside
cd D:\work\my-feature             # work with plain git, or let an agent work here
sg rebase                         # put the branch on the latest SVN state
sg resolve                        # when a rebase or an import stops: see it, pick a side, continue
sg push -m "what and why"         # one svn commit per repository, then the branch resets to the snapshot
sg rm my-feature                  # when done
sg sync                           # svn update the checkout and refresh svn/monorepo
sg status --json                  # for agents and the GUI
```

## The app

`sg-ui` is a WinUI 3 app with Windows 11 looks. Build and publish it with:

```powershell
dotnet publish src/Sg.App -c Release -r win-x64 -o "$env:LOCALAPPDATA\sg\ui"
```

Start it with `sg-ui` from any terminal (a small `sg-ui.cmd` shim sits next to `sg.exe`), or `sg-ui <action> <folder>`.
Actions: `overview`, `sync`, `branch`, `commit`, `log`, `rebase`, `push`, `server-branch`, `server-checkout`, `settings`.

The app is shaped like Windows Settings. The checkouts sit in the pane on the left. The page on the right is the selected checkout.
Only one checkout can be selected, so the checkout is the page's toolbar rather than a card on it, and it stays put while the worktrees
scroll: the first row is its name, its revision, its URL, the badges that say what is true, and the one thing to do about that (Commit
when the checkout has edits, otherwise Sync); the second row is everything else it can do - New worktree first, then Shelved changes,
SVN log, Merge, Open folder, Server branch, Import, Edit checkout. The bar folds what will not fit into an overflow of its own and takes
it back when the window widens, so the "..." says how wide the window is rather than what matters. Both rows are always on screen, so
nothing appears twice across them. Commit is the only way to the changes made directly in the checkout: it stays in one place and greys
out when none are waiting, rather than trading places with a second button that opened the same page. Sync is the only button about the
server: it syncs outright when there is nothing to bring in,
and opens the incoming revisions first when there is, because syncing blind over a server that moved is the thing worth reading first;
its tooltip says which of the two it is about to do and what the server last said. "Shelved changes" greys out when the shelf is empty.
Refresh sits beside the path at the top of the window, next to Forward: reading everything again is about the root rather than about the
page you are on, and F5 is the same thing. Under it, one card per worktree, in branch-name order so a card never moves under the pointer
when its state changes (its badges, what to do next, the button that does it, and behind the chevron
Commit, Log, Push to SVN, Rebase, Open, Remove). Every action opens as a page under a breadcrumb, `monorepo > feature-x > Commit`, with back
and forward over it (Alt+Left, Alt+Right, the mouse thumb buttons, Esc). The Explorer menu opens the same page in a window of its own.

Pages: Commit (pick files, diff, message, discard), Push to SVN (commits, files per repository, checks, message), Log (branch commits and
snapshots, details, files, diffs), Resolve conflicts (whatever stopped, a rebase or an import: keep one side per file, continue, skip that one, abort), Changes in the checkout (edits made
directly in the checkout: diff, revert, commit per working copy), SVN log and Incoming changes (per working copy, server diffs),
New server branch (dry run, then create), Edit checkout, Add checkout, New root, Project monitor, Settings (every setting writes itself
on change). Picking a folder in any file tree shows one patch of everything under it. A page with nothing to show says so in one sentence.

The Checkouts row in the pane has three icon buttons: open an existing root, make a new one, and new server checkout.
**New root** is a wizard: it runs `sg init` on a folder you pick, then offers to register the first SVN checkout,
with the skip, shared and optional lists as plain per-line boxes, and a drop-down that says how worktrees get the shared
folders: junction, ReFS clone, or full copy. The clone choice is greyed out, with the reason under it, when the checkout
and the worktrees are not on one ReFS volume. The second step is optional and can take minutes.
A button that cannot do anything is disabled, and its tooltip says why.

The overview asks the SVN server for new commits every few minutes and shows a toast when there are some.
It asks GitHub for a newer sg build on its own schedule and shows an "Update and restart" bar when one is ready.

Tray: closing the overview hides it into the notification area and the app keeps running, so the monitor keeps checking.
Right click the tray icon for Open, Project monitor, Check now, Settings, Exit. Settings has "Start with Windows", which
starts sg hidden in the tray at login. Only one sg-ui runs at a time: a context menu click opens its window in the running one.

Project monitor: watch any SVN URLs, sorted into categories, each on its own interval. New commits give a badge in the tree
and a toast. Pick a repository to read the incoming revisions, their messages, changed paths, and diffs from the server.
"Add my checkouts" watches the root and every external of a checkout. The list lives in `%LOCALAPPDATA%\sg\monitor.json`.

Settings has "Install" for the Explorer context menu: right click a folder, "Show more options", then "sg".
It is the classic menu, registry only, current user only. The menu points at the exe it was installed from.
The diff viewer is the Monaco editor, with syntax colouring per file type. The app ships its own copy,
so diffs colour with no internet and no CDN. `monacoUrl` in `app.json` is only the fallback for a build without the bundle.
After a clone, run `scripts/fetch-monaco.ps1` once to put that copy in place; the build workflow runs it on every build.

**Diff colours** in Settings picks the palette it reads in: sg's own, and ten people already have in their editor —
One Dark Pro, Dracula, Monokai, Nord, Gruvbox Dark, Tokyo Night, GitHub Dark, Solarized Dark, and GitHub and Solarized
in light. They are written into the app rather than downloaded, so a machine with no internet has all of them.
A theme colours the syntax, the editor, and the two washes over a changed line, and it colours the status letters
beside a file name with the same green and the same red — the letter and the line stand for the same thing, and a
palette that stopped at the editor would leave the list next to it disagreeing. Changing it repaints every diff
already open.

Right click any row in a file list, in Push, Commit, Log, Resolve conflicts or Checkout changes:
**Open folder** shows it in Explorer with the file selected, **Edit file** opens it in whatever Windows uses for its type.
Every file list has a filter box above it; the header then reads `showing 12 of 340`. The commit message box counts what
actually gets committed and the button stays off until it passes the minimum. A worktree can be opened in a terminal or
in the editor named in Settings.

Every file list shows the status as a coloured letter: amber M for modified, green A for added, red D for deleted,
purple R for renamed, orange C for a conflict, grey ? for a file version control does not know. Push and Checkout changes
group the rows by working copy, one header per SVN commit that would go out. Right click a row in Checkout changes for
**Revert**, or in Commit for **Discard changes**. Click inside a change in the diff of a modified file and press
**Revert chunk** to put that block back the way BASE (or HEAD) has it; the rest of the file stays, like TortoiseSVN.
In the Project monitor, clicking a commit marks it and the older ones as read; newer ones stay unread.
Lists show grey bars while they load, a button that started a long operation shows a ring until it finishes, and rows
cascade in instead of popping in.

Each worktree card shows what it costs on disk, and the header shows the total. The walk measures the shared folders
on their own: a junction costs nothing, a ReFS clone costs nothing until one side writes, and a full copy costs the
folder's size again. Only the copies are added to the total, and a worktree carrying more than a gigabyte of them gets
a **"12.4 GB copied"** badge naming the folders, because a junction or a clone would not have cost that. The badge fires
on a worktree whose sharing was set to `copy`, and on one whose junction was skipped because the folder was already
there. A worktree with nothing to push that nobody has touched for two weeks is marked idle, so it is obvious which
branch to remove.

Any long operation can be cancelled from the log pane. Cancel ends the running git or svn command; a snapshot writes
nothing until the last step, and a cancelled `checkout add` unregisters itself and takes its `.git` pointer back out.

Push shows its six pre-checks before you press the button: worktree clean, no rebase in progress, something to push,
change types SVN takes, paths sg may write, and no local edit in the checkout on the same file. All passing is one line;
any failing one names the paths. `sg push --check` prints the same list and exits 10 when one fails.

Keyboard: `Ctrl+F` filter, `Ctrl+Enter` commit or push, `F7` and `Shift+F7` next and previous change in the diff,
`F5` refresh the overview, `Esc` close the window unless you are typing. The list is in Settings.

## Shelves

Changes you are not ready to commit can be put aside and taken back later. This is what a local edit in the
checkout needs: a push refuses to write a file the checkout has edited, and a shelf takes that edit out of
the way without throwing it away. A worktree needs it too, because a rebase and a push both want a clean one.

```powershell
sg shelve -m "half a fix"          # everything changed here, put aside
sg shelve -m "just the engine" engine   # only these paths
sg shelf                           # what is waiting
sg shelf restore 20260906-101500-half-a-fix    # write it back, and take it off the shelf
sg shelf restore <id> --keep       # write it back and leave it there
sg shelf drop <id>                 # throw one away
```

A shelf is a commit in the shared store under `refs/sg/shelf/`, so nothing is stored twice and nothing is
kept outside git. Putting one back writes over a file that still holds what it held when the shelf was made,
and merges the change into one that moved on since, the way an svn update merges. A file it cannot merge is
named, and the shelf is kept.

In the app: "Shelved changes" on the checkout toolbar and on every worktree card, a Shelve button on both
changes pages and in the right click menu of their file lists, and, when a push refuses because the checkout
has an edit on the same file, a "Shelve them" button on the check that puts exactly those edits aside.

The three pages that write - Commit, Commit to SVN and Push to SVN - all end in the same accent split button.
The left half does what the page is for. The right half drops a menu holding the other things to do with the
same changes: on the commit pages, shelve them instead, and what is already on the shelf; on Push, apply the
changes into the checkout without committing, and what is already on the shelf.

## Carrying a branch to another machine

A branch can leave as one file and arrive on a machine that has nothing in common with this one but the SVN
repository. Nothing goes through the server, so this works for a branch that is not ready to be pushed, and
for a machine that cannot reach the same network share.

```powershell
sg export                          # this worktree's branch, into <branch>.sgexport here
sg export ..\gui -o D:\gui.sgexport
sg import D:\gui.sgexport --show   # what is in it: the branch, the commits, the revisions it was cut from
sg import D:\gui.sgexport          # make the branch here and replay them
sg import D:\gui.sgexport --name gui-from-laptop --into monorepo
```

The file holds the commits the branch has that its snapshot does not, as a git patch series, and the base it
was cut from as SVN revisions: the URL and revision of the checkout and of every external. It does **not**
hold a git sha, because the same revision builds the same tree on both machines but not the same commit -
the parent, the message and the date all differ - so a sha from here would mean nothing there. Uncommitted
work is not in it either; commit it, or shelve it, first. A branch of a few files comes to a few hundred kilobytes.

Import builds the branch on whatever revision this checkout is at, which is rarely the one the export was
cut from, so every commit is merged rather than only applied and every revision that differs is named. The
versions each change starts from travel in the file for exactly this reason: without them git can only
apply a patch onto the tree it was cut from. A commit that will not merge does not end the import: it stops
it and stands there, with the commits behind it still queued, so a branch of twenty does not go back to
nothing because the nineteenth needed a hand. Finish it with `sg resolve`, below. An external that points at
another branch here is reported as that, not as a revision to compare.

In the app: "Export the branch" on every worktree card, "Import a branch" in the checkout toolbar's overflow. Import opens
a page that reads the file first - the branch, the commits, and this checkout's revision beside the one the
export was cut from, line by line - and only then offers the button.

## When it stops on conflicts

A rebase and an import both replay commits, and both stop when one of them touches a line something else
already changed. They stop the same way - the files at three stages, the rest of the run waiting behind -
so one command finishes either of them, and none of its verbs names git.

```powershell
sg resolve                         # what stopped, how far it got, and what is in conflict
sg resolve theirs src\app.cpp      # keep the incoming version of one file
sg resolve ours                    # keep the version already here, for every file in conflict
sg resolve resolved src\app.cpp    # after editing it by hand between the markers
sg resolve continue                # go on. The next commit may stop too, and then this repeats
sg resolve skip                    # drop the one it stopped on, and go on with the rest
sg resolve abort                   # put it back
```

Sometimes it stops with **nothing** in conflict. That is not "all resolved": it is a patch git would not
take at all, or a commit that changes nothing here any more. Continuing then has nothing to commit, so it
is refused with the reason rather than left to fail. Skip drops that one. For an import there is one more
way on:

```powershell
sg resolve force                   # write what still fits, leave every refused hunk as <file>.rej
```

Then put the refused hunks in by hand, delete the `.rej` files, `sg resolve resolved <path>...`, and
`sg resolve continue`. The same button is on the page in the app.

`ours` and `theirs` mean different things in the two: rebasing, ours is the SVN version and theirs is your
branch's; importing, ours is the branch as it stands here and theirs is what came in the file. `sg resolve`
says which is which every time it prints, and so does the page in the app.

Abort costs different things too. A rebase goes back exactly as it was. An import comes off whole, the
commits that already went in included, because git undoes a patch series as one thing - the export file
still holds them all, so it can simply be run again.

In the app this is the Resolve conflicts page: the files on the left with a side-by-side diff of the two
versions, four buttons to pick a side or mark one done by hand, and Continue, Skip and Abort under them.
It opens from the worktree card whenever something is stopped there, and from the import page when an
import stops.
## Backup

Everything this machine has that SVN does not - the branches, the changes not yet committed, the shelves - can be
copied to a git repository somewhere else: a bare folder on a share, or a hosted repository. It never holds the SVN
tree. Each branch goes as a *thin history*: the snapshot becomes a marker with an empty tree and the SVN revisions in
its message, every commit keeps only the files the branch wrote, and the version a file started from travels under
its first edit. A branch of a few files is a few hundred kilobytes, and nothing the branch did not touch leaves the
machine. The URL is kept in `sg.json` and is never a git remote, so `git push` in a worktree keeps having nowhere to go.

```powershell
sg backup set \\nas\git\fort-backup.git     # or any git URL. --prefix laptop keeps two machines apart in one repository
sg backup                                   # every branch, the uncommitted changes, the shelves. Only what changed goes
sg backup --check                           # what would go, and what is on the remote only
sg backup list                              # what is there, and how far each checkout here has drifted from it
sg backup restore feature-x --wip           # make the branch here again, merged onto this checkout's snapshot
sg backup prune --yes                       # delete from the remote what is not here any more
```

A backup mirrors with a lease: a branch the remote holds another version of - another machine wrote it - is refused
and named, exit code 10, and `--force` takes it over. Nothing is ever deleted by a backup; `prune` does that, and
lists first. Restore matches the checkout by URL the way import does, merges every commit across whatever revision
this checkout is at, and when the store still has the real commits simply points the branch at them again. Uncommitted
changes come back through the shelf: written into the worktree when they merge, left on the shelf when one does not.

In the app: Settings has the URL, the prefix, whether uncommitted changes go, and "Back up every N minutes" (15 by
default). The app also backs up a few seconds after a commit, a shelve or a rebase made in it. Every worktree card
says "backed up 3 min ago" or "2 commits not backed up", and **Backup** on the checkout toolbar opens the page: what
the remote holds, Restore, Back up now, Prune.

## Server branches

```powershell
sg server-branch rel-1 --dry-run  # print the plan: copies and rewritten externals, one revision per repository
sg server-branch rel-1            # do it, then make D:\work\rel-1 by copying the nearest checkout and svn switch
sg server-branch rel-1 --keep libs/prebuilt --as engine=rel-1-engine
                                  # builds stays on its branch, no copy; the engine external gets a name of its own
sg server-checkout stable         # checkout of a branch that already exists, same copy-and-switch trick
sg branch hotfix --from rel-1     # a worktree can start from any checkout
sg status                         # lists every checkout and worktree
```

The rule: the last `trunk` or `branches/<x>` in every URL becomes `branches/<name>`. Externals are rewritten the same way.
A repository that breaks the rule gets an entry in `branchUrlOverrides` in `sg.json`: key = URL prefix, value = replacement with `{name}` in it.

`sg branch x --minimal` leaves out `libs/tools` (4 GB instead of 16 GB).
`sg push` refuses to run without an interactive terminal unless `allowAgentPush` is true in `D:\work\.sg\sg.json`.
