# SVN + Git bridge — design, draft 2

Date: 2026-09-03. Draft 1 was a sketch. Draft 2 records every decision from the design interview.

## What it is

A Windows tool in C#. It puts git branches and worktrees on top of your SVN checkouts.
SVN stays the master. Git holds snapshots of SVN plus your local branches.
It has three faces: a core library, a CLI called `sg`, and a WinUI 3 app with Explorer context menu entries.
It replaces TortoiseSVN and TortoiseGit for the daily loop.
It is built for a workflow where AI agents work in worktrees, and you review and push.

Working name `sg`. Change it later.

## Your setup (facts found on 2026-09-03)

- Machine: Windows 11. Git 2.55. SVN 1.14.5 from TortoiseSVN, with `svnmucc`. .NET SDK 10. WebView2 runtime. 768 GB free on D:.
- First target: `D:\fort\monorepo_proto`. A checkout of `riftbreaker_monorepo/branches/fort`. 37 GB on disk. 173k files.
- The root of that checkout is tiny. The content comes from 5 externals. They track branch HEADs in 3 other repositories.

| Local path | Repository path | Size |
|---|---|---|
| `schmetterling` | `schmetterling/branches/fort/dev` | 3.5 GB, 20k files |
| `tools` | `tools/branches/fort_proto` | 69 MB |
| `fort/dev` | `riftbreaker/branches/fort/dev` | 13 MB |
| `fort/builds` | `riftbreaker/branches/fort/builds` | 13 GB |
| `fort/tools` | `riftbreaker/branches/fort/tools` | 19 GB, 7 GB of it is ignored temp |

- Nobody uses `svn:needs-lock`. No locks exist.
- Ignore rules live only in `svn:ignore` on a few folders (`build`, `.vs`, `graphify-out`, `vcpkg.json`) and one `svn:global-ignores` (`.cursor`). The svn client config is stock.
- You keep local edits in the checkout. 21 changed files today.
- Global git config has `core.autocrlf=true`. The tool overrides it in its own repo.
- Branch layouts on the server:
  - `riftbreaker/branches/<name>/` holds only the subfolders the branch needs. `fort` has `builds, dev, tools`. `multiwindow` has `dev`. 57 branches. A nested project `rbs/` has its own `trunk`.
  - `schmetterling/branches/<name>/` is a copy of the whole trunk: `dev, docs, tools`. 90 branches.
  - `tools/branches/fort_proto` is a copy of `tools/trunk/dev`. 6 branches.
  - `riftbreaker_monorepo/branches/<name>` (7 branches) and `fort_monorepo/trunk` carry the externals. `fort_monorepo` points at whole trunks: `schmetterling/trunk`, `tools/trunk`, `fort/trunk/dev` as `game/dev`, `fort/trunk/builds` as `game/builds`.
  - Small repos like `crashboard` are checked out at the repo root. `trunk` and `branches` are folders inside one working copy.
- Other checkouts: `D:\fort\monorepo_tools` is a second full checkout of the same `fort` branch. `D:\fort\monorepo_trunk` is a root checkout of `fort_monorepo`. `D:\fort\asset_src` is 168 GB of art sources. `D:\fort\schmetterling` is the engine trunk.
- The server checks only the commit message length.
- `create_monorepo_branch.bat` in the checkout is from the riftbreaker layout. It does not match `fort`.
- A stray empty file named `nul` sits in `D:\fort\monorepo_proto`. Windows treats it as a device. Delete it: `cmd /c "del \\?\D:\fort\monorepo_proto\nul"`.

## Decisions

| # | Decision |
|---|---|
| 1 | Snapshots, not full history. One git commit per sync. SVN keeps the history. |
| 2 | You are the only user for now. Safety rules are team grade from day one. |
| 3 | SVN stays the master for years. |
| 4 | The game builds from any folder. Worktrees work. |
| 5 | The checkout stays a normal SVN working copy. You can edit and commit there like today. |
| 6 | No lock support. A lock held by someone else makes a push fail with their name. |
| 7 | Several server branches at once. Each is its own checkout with its own snapshot ref. One shared git store. |
| 8 | Push squashes a branch into one SVN commit per repository. No `--each`. |
| 9 | Branches update by rebase, never by merge. |
| 10 | After a push the branch resets to the snapshot. The worktree stays. |
| 11 | Push checks the message length. The minimum is in config. Default 10. |
| 12 | C# for everything. One core library, a CLI, a WinUI 3 app. |
| 13 | The app replaces TortoiseSVN and TortoiseGit for daily work. Classic Explorer context menu, registry based. |
| 14 | Tortoise style from Explorer: right click, one window per action. Inside the app, the shape of Windows Settings: checkouts in a pane, one page per place, a breadcrumb over the page, back and forward. Changed 2026-09-05, it was one window per action everywhere. |
| 15 | One root folder: `D:\fort`. Store in `D:\fort\.sg`. Worktrees in `D:\fort\<branch>`. Existing checkouts stay where they are. |
| 16 | The tool creates server branches. Generic URL rule. Per-repository override in config. |
| 17 | Fixes between server branches use `svn merge` in the checkout, as today. |
| 18 | First target is `monorepo_proto`. |
| 19 | A push that spans repositories makes one SVN commit per repository, in a fixed order. It stops at the first failure. Committed parts stay. The rest stays on the branch as one "not pushed yet" commit. |
| 20 | `fort/builds` is never tracked by git. `fort/tools` is tracked. A worktree can leave it out. |
| 21 | `asset_src` is out of scope. |
| 22 | The diff viewer is built in: WebView2 with the Monaco diff editor. An external tool is a config option. |
| 23 | New server checkout = copy the nearest checkout on disk, then `svn switch`. Only differences download. |
| 24 | Small repos like `crashboard` come in v2. |
| 25 | Humans push. Agents get "branch is ready". A config flag can allow agent pushes later. |
| 26 | Every new worktree gets `CLAUDE.local.md` and `.cursor/rules/sg.mdc`. Both are excluded from git. |
| 27 | v1 GUI: Overview, Sync, New branch, Rebase, Push, Commit with diff, Log, Discard, New server branch, New server checkout, Settings. v2: conflict resolver, SVN commit, SVN log and blame. Never icon overlays. |
| 28 | The commit windows work the way Tortoise's do, one block of a diff at a time. A block is a hunk of the unified patch git or svn wrote, so what a button acts on is exactly what those tools would place. Git commits can hold part of a file, through the index; SVN has no index, so there a block is only reverted. The file itself is edited in the diff and written back. |
| 29 | The branch's own commits can be joined into one, and any of them given a new message. A snapshot of SVN never can: it is what the server said. A rewrite replaces an unbroken run with one commit that has the run's own last tree, so replaying what sat above it cannot conflict; if it somehow does, the rebase is undone and nothing changed. |
| 30 | A push can send the oldest few commits instead of the whole branch. The boundary is a count, not a commit: a push rebases first and that renames every commit. What is left over stays as the commits it was, replayed onto the new snapshot, so the next push can take a few more. Only a push that failed half way still flattens the rest into one commit. |
| 31 | Merging between server branches happens in the checkout, through svn, with the tool driving it: all of a branch, or named revisions, and the same run backwards to take a revision out again. It never commits. What a merge brings in is a local change of the checkout, and the changes window sends it, so a merge and an edit made by hand leave the same way. Changed 2026-09-06; decision 17 left this to be done by hand. |
| 32 | A commit is undone by adding one that takes its changes back out, never by rewriting it. Squash and reword rewrite, and both refuse anything that has left the machine; revert does not, so it is the one that works on a commit somebody else may already have. |
| 33 | Blame answers with the SVN view. A worktree's git history is one commit per sync, so plain `git blame` names the sync and not the change; the lines that came in with a snapshot are handed to `svn blame`, through the original line number git gives for each one, and the lines the branch itself changed keep git's answer. This is the `sg blame` the notes promised. |
| 34 | A push can stop before the server: the same bytes are written into the checkout and left there as local changes, to read, change, or commit by hand. Nothing reaches SVN and the branch does not move, so the push can simply be made again. Until those changes are committed or reverted, a real push refuses to touch the same files, which is the rule that keeps the two ways of sending one change apart. |
| 35 | Changes can be put aside and taken back: a shelf. It is a commit in the store whose parent is what the working copy was against, so the change is a diff between two commits and nothing new has to be invented to read it, show it, or merge it. A checkout needs this because a local edit there makes a push of the same file refuse, and a worktree needs it because a rebase and a push both demand a clean one. Putting one back writes the files where they still hold what they held; a file that moved on, which is the normal end of shelve, push, sync, gets the change merged into it the way an svn update merges, and a merge that cannot settle keeps the shelf. |
| 36 | Work that stops on conflicts is finished, never thrown away. A rebase and an import replaying a patch series leave the same thing behind - files at three stages and a commit waiting to be made - so they are one thing in `Conflicts`: read what stopped, pick a version per file, then carry on, drop the one it stopped on, or put it all back. The import used to give up on the first patch that would not merge and take the rest of the series with it; the whole series now goes to `git am` in one call and is left standing where it stopped, so carrying on finishes the patches queued behind it. `sg resolve` in the CLI, and one Resolve conflicts page in the app that words its two sides from what stopped, because "keep the SVN version" means nothing during an import. Added 2026-09-10. |
| 37 | A patch travels byte for byte or it does not travel. git mailsplit strips the CR off every line it reads, so a patch of a file this repository stores with CRLF - which is most of them - arrived LF only, matched nothing, and stopped the series on the first one with nothing in conflict, because git never got as far as a merge. `git am --keep-cr` is the whole fix. Alongside it the pack now carries the blob every patch starts from rather than only the blob at the snapshot: the second commit to touch one file is cut against what the first one left, a version on no commit the far side will ever have, and without it git cannot three-way at all - it falls back to a plain apply and fails the same silent way. Added 2026-09-10. |
| 38 | A backup is a git repository somewhere else that holds every branch, the changes not yet committed, and the shelves, and never the SVN tree. Each branch goes as a thin history: the snapshot becomes a marker with the empty tree and the SVN revisions in its message, every commit keeps only the files the branch wrote, and the version a file started from goes in a base commit under its first edit, so `git merge-tree` can put each change back onto a checkout that moved on. Nothing the branch did not touch leaves the machine. The URL is in `sg.json` and never a git remote, so `git push` keeps having nowhere to go. A backup mirrors with a lease and never deletes; restore replays the way import does, without a worktree. Built 2026-09-11. |

## The picture

```
 SVN server (master): riftbreaker_monorepo, schmetterling, tools, riftbreaker
      ^
      |  svn update / svn commit / svnmucc        (the tool, or you inside a checkout)
      v
 D:\fort\.sg\               shared git store (bare). Every folder below is a linked worktree of it.
 D:\fort\monorepo_proto\    server checkout (.svn + externals). git HEAD detached on svn/monorepo_proto
 D:\fort\rel-1\             server checkout of another branch. git HEAD detached on svn/rel-1
 D:\fort\feature-x\         branch worktree. branch feature-x, born from svn/monorepo_proto
 D:\fort\agent-task-7\      branch worktree for an agent
```

## Rules

1. SVN is the master. Git holds snapshots of SVN and your local branches. Nothing else.
2. A server checkout is a normal SVN working copy. You can edit and commit there like today.
3. `svn/<checkout>` is a git ref. It is a snapshot of that checkout at the last sync. Nobody edits it by hand.
4. A branch is born from one `svn/<checkout>` ref. It remembers which one. Each branch has its own worktree folder.
5. Push = sync, rebase, copy only the files the branch changed into the checkout, then `svn commit` per repository.
6. Git never changes bytes. No autocrlf. No filters. Same bytes in, same bytes out.
7. One bridge operation at a time per root folder. A lock file in `.sg` guards it.
8. Push never touches a file that has a local edit in the checkout. It refuses instead.

## Parts

| Part | What it is |
|---|---|
| `Sg.Core` | C# library. Runs `git.exe`, `svn.exe`, `svnmucc.exe`. Holds the model, the snapshot builder, and every operation. Streams progress lines. |
| `sg` | CLI on `Sg.Core`. `--json` output. Non-interactive mode for agents. Clear exit codes. |
| `Sg.App` | WinUI 3 app in the shape of Windows Settings: the checkouts in a pane, one page per place, a breadcrumb and back/forward over them. The Explorer menu opens one window per action, around the same page. Cards are the Community Toolkit settings controls. Monaco diff in a WebView2. |
| `Sg.Shell` | Install script. Copies to `%LOCALAPPDATA%\sg`. Writes registry keys for a cascading `sg` context menu on folders and folder backgrounds. |
| tests | xUnit. Fixtures make local repos with `svnadmin create` and `file:///` URLs. Externals across 3 repos, like the monorepo. |

Stack: .NET 10, the current Windows App SDK, unpackaged, CommunityToolkit.Mvvm, System.CommandLine.
Config: `D:\fort\.sg\sg.json`. It holds checkouts, skip lists, push order, message length, diff tool, the agent push flag, and branch rule overrides.

## CLI

| Command | Run in | What it does |
|---|---|---|
| `sg init <root>` | anywhere | Makes the store. Registers existing checkouts under the root. Builds their first snapshots. |
| `sg checkout add <folder>` | root | Registers one more existing checkout. |
| `sg checkout add --url <url> [<folder>]` | root | `svn checkout` the URL first, into `<root>\<name>` by default, then registers it. The name is the last part of the URL unless `--name` says otherwise. |
| `sg sync [checkout]` | anywhere | `svn update` in the checkout. New snapshot. Moves `svn/<checkout>`. |
| `sg branch <name> [--from <checkout>] [--no-tools]` | anywhere | New branch and worktree from `svn/<checkout>`. Writes the agent notes. |
| `sg rebase` | worktree | Rebases the branch on the latest snapshot of its checkout, then mirrors the shared folders of a clone or copy worktree from the checkout. |
| `sg push [-m msg]` | worktree | Sync, rebase, commit per repository. Human only, unless config allows agents. |
| `sg rm <name>` | anywhere | Removes a worktree and its branch. |
| `sg status [--json]` | anywhere | Revisions, worktrees, ahead counts, "needs rebase", pending pushes. |
| `sg server-branch <name> [--from <checkout>] [--keep <ext>]... [--as <ext>=<name>]...` | anywhere | Copies the branch on the server with the generic URL rule. An external can stay on its branch, or get a name of its own. |
| `sg server-checkout <branch> [--near <checkout>]` | anywhere | New checkout by copy and switch. Registers it. |
| `sg blame <path>` | worktree | `svn blame` on the checkout copy. |
| `sg shelve [-m <title>] [<path>...]` | checkout or worktree | Takes the named local changes out and keeps them. Everything changed there when no path is named. |
| `sg shelf [list\|show\|restore\|drop]` | anywhere | What is on the shelf, one of them in full, one written back, one thrown away. |
| `sg export [<worktree>] [-o <file>]` | worktree | Packs the branch's own commits into one file, with the SVN revisions it was cut from. |
| `sg import <file> [--name n] [--into c] [--show]` | anywhere | Puts one back against a checkout of the same repository, merging across whatever this one is at. |
| `sg backup [--check] [--force]` | anywhere | Every worktree's branch, the uncommitted changes, and the shelves, rewritten thin and pushed to the backup URL. `--check` prints what would go up and what is on the remote only. |
| `sg backup set <url> [--prefix p] [--no-uncommitted]` | anywhere | Where backups go. |
| `sg backup list` | anywhere | What the remote holds: each branch, the checkout it was cut from as URL and revision, its commits, its wip and shelves, and how far the checkouts here have drifted. |
| `sg backup restore <branch> [--name n] [--into c] [--wip]` | anywhere | Makes the branch here and replays it, the way import does. `--wip` brings the uncommitted changes back through the shelf. |
| `sg backup prune [--yes]` | anywhere | Lists what is on the remote and not here, and deletes it with `--yes`. |

Plain `git` does everything else in a worktree. `git push` does not work. `sg push` replaces it.

## How it works inside

### Store and worktrees

The store is a bare git repo in `D:\fort\.sg`. Every checkout and every branch worktree is a linked worktree of it.
A checkout gets a `.git` file that points into the store. Its HEAD is detached on its snapshot.
`git worktree add` refuses a folder that has files. So the tool adds the worktree in an empty temp folder, moves the `.git` file into the real folder, and runs `git worktree repair`.

Git config in the store: `core.autocrlf=false`, `core.safecrlf=false`, `core.filemode=false`, `core.longpaths=true`, `core.symlinks=false`, `core.fsmonitor=true`, `core.untrackedCache=true`, `feature.manyFiles=true`, `core.bigFileThreshold=1m`, `core.looseCompression=0`, `gc.auto=0`.

A checkout can be a subfolder of a working copy, like `monorepo_trunk\trunk`. All svn commands then run on that subfolder.

### The snapshot

A snapshot is the exact content of the checkout as SVN has it, at the revisions of the root and of every external. Local edits are left out. Skipped paths are left out.

1. `svn update` on the checkout. This also updates the externals.
2. `svn status --xml` on the checkout. It covers the externals. Collect: unversioned, added, modified, deleted, missing, conflicted, replaced.
3. Write a temp exclude file: `.svn/`, the skip list (`fort/builds/`), every unversioned and added path, and every path with a reserved Windows name.
4. `git add -A` with that exclude file. The index stat cache and fsmonitor make this incremental.
5. Overlay: for each modified, deleted, missing, conflicted, or replaced file, put the pristine copy in the index. `svn cat -r BASE <path>` gives it. This works offline, also inside externals.
6. `git write-tree`. `git commit-tree` with parent = the previous snapshot. Message: first line `monorepo_proto r266`, then one line per external with its revision, then the SVN log messages since the last sync, then trailers `svn-rev` and `svn-url`.
7. `git update-ref refs/remotes/svn/<checkout>`. Then `git update-ref HEAD` in the checkout worktree. Files are not touched.

The first snapshot of a checkout has no parent.

Ignore rules come from the skip list and every `svn:ignore` and `svn:global-ignores` property in the checkout and its externals. `svn:ignore` on folder `d` with pattern `p` becomes `/d/p`. `svn:global-ignores` becomes `/d/**/p`. The rules are cached in `.sg/ignores/<checkout>.gitignore`.
Every new worktree gets that file as its `.gitignore`, so tools that read only `.gitignore` see the rules too. The file is not tracked. A `.gitignore` that the SVN tree itself tracks is left alone.
The store's `info/exclude` holds the same rules for all checkouts, plus `.svn/`, `/.gitignore`, `CLAUDE.local.md`, `.cursor/rules/sg.mdc`.

### New branch and worktree

1. `git worktree add --no-checkout -b <name> D:\fort\<name> refs/remotes/svn/<checkout>`.
2. With `--no-tools`: `git sparse-checkout set` in cone mode, everything except `fort/tools`.
3. `git checkout`.
4. If config says so, `fort\builds` comes from the checkout, the way `shared` in sg.json says, or `--shared` on the branch:
   a junction into the checkout's folder (no admin rights needed), a ReFS block clone (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`
   file by file, so the folder shares disk with the checkout until one side writes; needs both on one ReFS volume, and
   sg refuses up front when they are not), or a full copy. `.svn` is left out of a clone or copy. `branch.<name>.sgShared`
   records which one the worktree got, and a rebase mirrors a clone or copy from the checkout again.
5. Write `CLAUDE.local.md` and `.cursor/rules/sg.mdc`. They say: use `sg`, never `git push`, rebase before you ask for a push.
6. Record `branch.<name>.sgBase = <checkout>` in git config.

Disk: a worktree costs about 4 GB without tools, about 16 GB with tools, plus its own `build` folder.

### Push

Pre-checks:

1. The worktree is clean.
2. Sync the checkout of the branch. Rebase the branch on the new snapshot. Stop on conflict.
3. Changed paths: `git diff --name-status -M svn/<checkout>..<branch>`.
4. Refuse if a changed path is in the skip list or has a reserved name.
5. Refuse if a changed path has a local edit in the checkout.
6. Refuse if the message is shorter than the minimum.

Group the changed paths by working copy. Each external is its own working copy. Order: the root first, then the externals in config order. Default is alphabetical.

For each group, in the checkout:

1. Renames: `svn mv --parents old new`.
2. Added and modified files: `git checkout <branch> -- <paths>`. This writes only those files.
3. Deleted files: delete from disk.
4. `svn add --parents` for added files. `svn rm --force` for deleted files.
5. `svn commit -F message <paths of this group>`. Only these paths. Local edits elsewhere stay local.

If a group fails: `svn revert -R` its paths, restore its files from the snapshot with `git checkout svn/<checkout> -- <paths>`, delete its added files, and stop. Earlier groups are already in SVN.

After the loop:

1. `svn update`. New snapshot. `svn/<checkout>` moves.
2. All groups done: in the worktree, `git reset --hard svn/<checkout>`. The branch now equals SVN, including any eol or keyword normalizing SVN did.
3. Some groups failed: build one commit on top of the new snapshot. Its tree is the snapshot tree, with the paths of the failed groups taken from the old branch tip. In the worktree, `git reset --hard` onto it. The branch is now "snapshot plus one not-pushed-yet commit". The old tip stays in the reflog. Report which repositories got a commit and which did not.

### New server branch

Input: a source checkout and a new name. Each external can also get a name of its own, or be kept: then it is not copied, and the `svn:externals` line that brings it in stays as it is. An external inside a kept one is kept with it. A repository with nothing left to copy gets no revision.

1. Collect the root URL and every `svn:externals` line in the checkout, also nested ones like the one on `fort/`.
2. Rule for each URL: find the last `/trunk` segment or the last `/branches/<x>` segment. Replace it with `/branches/<new>`. Keep the rest. If neither exists, stop and ask for an override in config.
3. Group by repository. One `svnmucc` transaction per repository: `mkdir` the branch folder if needed, then `cp HEAD <src> <dst>` for each subtree. For the monorepo repository, also `propset svn:externals` with the rewritten lines on the new branch root and on nested folders.
4. Message: `- Creating branch: <new>`.
5. Then run "new server checkout" for it.

For `fort` this makes 4 revisions in 4 repositories: riftbreaker_monorepo, schmetterling, tools, riftbreaker.

### New server checkout

1. Pick the nearest existing checkout of the same monorepo repository.
2. Copy its folder to `D:\fort\<branch>` with robocopy. Skip unversioned and ignored items.
3. `svn switch <new root URL>` on the copy. SVN switches each external in place when the repository is the same. It downloads only differences.
4. Register the checkout. Add it as a linked worktree. Build its first snapshot.

### Context menu

Registry keys under `HKCU\Software\Classes\Directory\shell\sg` and `Directory\Background\shell\sg`, with a cascading submenu. Items: Overview, Sync, New branch, Rebase, Push, Commit, Log, Discard, New server branch, New server checkout, Settings.
A classic menu cannot change per folder. Each dialog checks where it was opened: root, checkout, or worktree. It says so when the action does not fit there.

### Agents

- Agents work in worktrees with plain git. `sg status --json` and `sg rebase` are for them too. `sg push` refuses in non-interactive mode unless config allows it.
- The versioned `.claude` folder arrives in every worktree through the snapshot.
- Several agents can run in several worktrees at the same time. The root lock serializes bridge operations.

### Shelves

A shelf is `refs/sg/shelf/<time>-<name>`: a commit whose parent is the working copy's base, the snapshot
for a checkout and the branch tip for a worktree, and whose tree is that tree with the picked changes in
it. It is built in an index of its own, so the checkout's own index, kept warm for the next snapshot,
is never disturbed. The commit message carries what git cannot: which working copy it came from, its
path, and what svn called each file, because svn schedules an add and a delete where git only sees content.

Making one: read the changes (`svn status` in a checkout, `git status` in a worktree), refuse what a
shelf cannot hold (a conflicted file, a skipped path, a reserved name), build the commit, then put the
working copy back. `svn revert` for the versioned ones, delete for the files SVN never had; a worktree
gets `git checkout HEAD --` and a delete for the untracked ones.

Putting one back: for each file, what it held when the shelf was made, what the shelf holds, and what is
on disk now. Equal to the first means write over it. Equal to the second means it is already back.
Anything else moved on since, and gets `git merge-file`: the three way merge git uses for a merge and svn
for an update. A file that cannot be merged that way, because the shelf adds or deletes it, or because
it is binary, is named and left alone. Then svn is told: the files that were scheduled for adding are
added again, the ones scheduled for deleting are deleted again. A restore that left a conflict keeps the
shelf, so the only copy of that work is never the one being sorted out.

### Backup remote

A backup is a second copy of everything this machine has that SVN does not: the branches, the changes not yet
committed, and the shelves. It lives in a git repository somewhere else - a bare folder on a share, a GitHub or
GitLab repository - and it never holds the SVN tree. The snapshot is 16 GB of bytes the server already keeps, and
on a hosted remote it would be the whole product in someone else's hands. What goes is what the export file
carries, as commits instead of a zip: the branch's own commits cut down to the files they touch, and the version
each of those files started from.

**What a branch becomes.** A branch is a run of commits above one snapshot. The backup rewrites that run into a
thin history of three kinds of commit, told apart by an `sg-thin` trailer:

- The snapshot becomes a *marker*: a commit with the empty tree, whose message is the snapshot's first line and
  its `svn-rev`, `svn-url` and `svn-external` trailers, and `sg-thin: marker`. The SVN log messages the snapshot
  quotes stay behind. Its author, committer and dates are the snapshot's, so two branches cut from one snapshot
  share one marker: the same inputs make the same commit.
- Each commit of the branch becomes one *change* commit. Its tree holds only the paths the branch has written and
  that still exist: the paths of its thin parent's tree, plus the paths this commit changed, each at this commit's
  version. Author, committer, both dates and the message are the real commit's, with `sg-thin: change` and
  `sg-source: <sha>` added under it.
- Under a change commit that writes a path the thin history has not seen, a *base* commit puts in the version that
  path had in the real parent, so the file is there before it is edited, as it is in the branch. A path the branch
  adds gets no base, and neither does one it deleted and adds again, because the real parent has none. It carries
  the change commit's author and dates, and `sg-thin: base`.

The diff between a change commit and what is under it is exactly the diff of the real commit, with the blob every
hunk starts from present in the base commit under it. So `git merge-tree --write-tree --merge-base=<what is under it>
<the tip here> <the change commit>` puts it back: a path in neither the base nor the change is the tip's, untouched; a
path absent from the base is an add; a path absent from the change is a delete; and a path in all three is a three
way merge. That runs in the store with no worktree, and a rename is a delete and an add of the same content, which
git pairs up as it does anywhere. So a thin history is the export file kept as git objects, on a remote that
`git log` can read and a web page can show.

Nothing the branch never touched is in any thin tree: not the blob, not the tree object that would name its
siblings, not the name. A touched file goes twice at most, base and branch version, and a pack deltifies those
against each other. A branch of a few files comes to a few hundred kilobytes, the export's size. The trees are
built in an index of the store's own, like a shelf: `git ls-tree -r <commit> -- <paths>` into it, `write-tree`,
then `commit-tree` with the author, committer and dates set, so the checkout's warm index is never touched.

**Uncommitted work and shelves.** The rewrite works on any commit above a snapshot, so it covers the rest of what
is only here. `Shelf.Wip` is the shelf builder's first half alone: a commit of the changes not yet committed - in a
worktree, and for the local edits of a checkout - without touching the working copy, and with the base commit's
dates rather than the clock, so the same changes make the same commit twice and "still the same" costs no push.
That commit goes as `refs/sg/wip/<branch>`, one base and one change above the branch's thin tip, and a checkout's
local edits as `refs/sg/edits/<checkout>`, above the marker. Two namespaces, because a branch may carry a checkout's
name when the worktrees live elsewhere, and git cannot hold a ref beside a folder of the same name at all. A shelf
goes as `refs/sg/shelf/<id>` the same way, above whatever its parent became. A worktree that is clean again takes its wip off the remote with the next backup, so a restore never
brings back work that was committed since. `--no-uncommitted` on `sg backup set` sends only what was committed.

**Where it goes.** `sg.json` gets `backup: { url, prefix, uncommitted }`. The URL is never registered as a git
remote, on purpose: a `git push` typed in a worktree keeps having nowhere to go, where a remote named `backup`
would have sent the real branch, snapshot and all, the first time someone typed `git push backup`. sg pushes with
`git push <url> <thin sha>:refs/heads/<branch>` and fetches with the URL the same way; credential helpers and
URL-scoped git config work on a URL as they do on a name. `prefix` puts every ref on the remote under `<prefix>/`,
for two machines that back up into one repository.

On the remote: `refs/heads/<branch>` for each worktree, `refs/sg/wip/<branch>`, `refs/sg/edits/<checkout>`,
`refs/sg/shelf/<id>`. A branch is not keyed by its checkout: one root has one store and one branch namespace, so two
checkouts of a root cannot own two worktrees of one name, and the marker under every branch says which repository
and revision it was cut from - restore matches on that, never on a name. Two roots go under two prefixes. In the
store, `refs/sg/backup/heads/<branch>`, `refs/sg/backup/wip/<branch>`, `refs/sg/backup/edits/<checkout>` and
`refs/sg/backup/shelf/<id>` are what this root last pushed: read for the lease and for the incremental rewrite. What a look at the remote fetched goes under
`refs/sg/fetched/` instead, kept apart on purpose: had a look written the pushed refs, a branch another machine
wrote would have passed the lease on the next push, which is the one thing the lease is for.

**Pushing.** `sg backup` takes the root lock and, for every worktree:

1. The base is `git merge-base` of the branch and its checkout's snapshot ref. That is a snapshot commit whether
   the branch was rebased or not, which is why it is not simply the snapshot ref.
2. If the `sg-source` of the last pushed thin tip is still an ancestor of the branch, the rewrite continues from
   that tip: only the commits above it are rewritten, and the thin parent's tree says which paths are already
   present. Otherwise - a rebase, a squash, a push to SVN that reset the branch - the rewrite starts at the
   marker. It is deterministic, so the part that did not change makes the same objects again and the pack is thin.
3. One `git push --porcelain` for everything, each ref under `--force-with-lease=<ref>:<last pushed sha>`, empty
   when nothing was ever pushed, so the ref must not exist. A rewrite from the marker is what the lease is for;
   nothing local is ever overwritten by a push.
4. A ref the remote holds a version of that this root did not push last - another machine, or another root under
   no prefix - is reconciled rather than refused outright. Those refs are fetched in one call, and the real commit
   the remote's thin history stands for (the `sg-source` of its last change) is read against the tip here. If that
   commit is still in this store and an ancestor of the tip, the remote is an older copy of the same history and the
   push carries it forward, under a lease on what was just read so a race still rejects; it is reported `reconciled`.
   If the tip here is the ancestor instead, the remote is the newer one: nothing is sent and the ref is reported
   `behind`, for a restore to bring that work here. Only when neither is an ancestor of the other - or the remote's
   commit is not in this store, two machines whose commits never met over SVN - is it a real divergence: that ref is
   reported `rejected`, the rest still go, and the exit code is 10. `--force` writes over whatever is there.
5. The wip commits and the shelves go in the same push. A worktree with no commits of its own and nothing
   uncommitted pushes the marker alone, so the remote knows the worktree exists and restore can make it again.

`sg backup` never deletes on the remote. A branch removed with `sg rm` stays there until `sg backup prune`, which
lists those and, with `--yes`, deletes them. `--check` prints what would go up, what is already there, and what is
on the remote only. `sg status` says per worktree when it was last backed up and how many commits have not been;
`--json` carries the same.

The app runs it on a timer - Settings, "Back up every N minutes", 15 by default, 0 turns it off - and a few seconds
after coming back from any page that may have changed a branch, and after a rebase. A worktree card carries a small
badge on the left of the branch name - grey when the backup is current, amber when commits wait or none ever went -
with the words ("backed up 3 min ago", "2 commits not backed up", "not backed up") in its tooltip and on the Backup
page, so a changing sentence never grows the header row. A conflict or a newer-remote is a lasting state, not a fresh
event, so the timer raises its toast once when a name first hits it, not every tick. "Backup" on the checkout toolbar
opens the page: what the remote holds, Restore, Back up now, Prune.

**Restoring.** `sg backup list` fetches the refs and says what is there: each branch, the checkout it was cut from
as URL and revision, its commits, whether a wip and shelves exist, and when. Drift against the checkouts here is
reported as import reports it. `sg backup restore <branch>`:

1. Fetch the thin branch into `refs/sg/backup/heads/<branch>`. Match the checkout by the marker's URL the way
   import matches an export - as written, then with the reaching taken off, then by the path; `--into` says
   otherwise. Refuse a thin history whose trailers this sg does not read, and a branch that exists here unless
   `--name` gives another or `--force` writes over it. Force removes the branch and its worktree first, uncommitted
   changes in it and all, then makes it again from the backup: the way to take the newer version a divergent backup holds.
2. When every `sg-source` sha is still in the store, the branch is set to the last one and nothing is replayed:
   the store still has the real commits, only the ref was gone. This is `sg rm` undone.
3. Otherwise make the branch the way `sg branch` does, then merge each change commit onto the tip so far with
   `git merge-tree`, oldest first, and commit the tree it wrote with the change's author and message. One at a
   time: a change that will not merge stops the run, keeps the ones before it, names the files, and leaves the
   rest for a hand. The worktree is written once, at the end, with `git reset --hard` onto what was built.
4. `--wip` merges the uncommitted changes the same way, registers the tree as a shelf whose parent is the restored
   tip, and takes it back at once, so a file that moved on is merged and one that cannot be is named and stays on
   the shelf. The branch's shelves are made again against the restored tip through the same merge, and stay on
   the shelf: a shelf was put aside on purpose.

**What it does not protect.** The checkout itself: SVN has that, and `sg checkout add --url` gets it again. The
store: `sg init` and `checkout add` rebuild it in minutes. Skipped folders like `fort/builds`, and anything
`svn:ignore` hides: never in git, so never in a backup. And it is a mirror, not a history: the remote holds what
the machine last held, and a branch reset by hand is backed up as reset on the next timer. The reflog in the
store is the protection against that, as it is today.

**Tests.** The fixture gets a bare git repository as the URL. A backup of a branch that touched `game.cpp` leaves
the remote with no blob of `engine.cpp` and an empty tree on the marker. A second fixture root with a checkout of
the same repository restores it to the same files; after the second root syncs past a server commit, restore
merges and reports the drift. A rebase pushes with the lease. A remote rolled back to an older commit of the same
history is reconciled and written forward with no force; a second machine's own branch of the same name, on commits
this store never saw, diverges and is refused with exit 10 until `--force`; a remote left holding a strict
descendant of the tip here is reported behind and nothing is sent. `--force` on restore writes over a branch that
is already here. Wip and shelves go up and come back. Prune lists before it deletes.

## Hard parts

- **Big binaries.** Git keeps one copy of each file version in the store. `fort/builds` never enters it. The store starts at about 16 GB and grows only with changed files.
- **Externals.** Their files are in the snapshot. Each external is its own working copy, so a push commits per repository. A push is not atomic across repositories. The tool says so in every report.
- **Local edits in the checkout.** Snapshots skip them. Push refuses to touch them. Sync may give you SVN conflicts on them, like today.
- **Line endings and keywords.** SVN expands them in the checkout. Git stores the expanded bytes. Push writes them back. SVN normalizes on commit. The branch resets to the snapshot after a push, so it matches SVN.
- **Empty folders.** Git has none. SVN keeps them. Push does not add or remove empty folders.
- **Reserved names.** Files like `nul` or `con` cannot exist in git on Windows. The snapshot skips them and warns.
- **Speed on 173k files.** fsmonitor, untracked cache, index v4, no gc. The first snapshot of `monorepo_proto` hashes about 16 GB. Expect minutes.
- **History.** `git log` shows one commit per sync with the SVN messages inside. `git blame` is coarse, so the Blame window gives the SVN view: see decision 33.
- **Windows.** Long paths on. Symlinks off. Case-insensitive file system. `fort/builds` comes as a junction, a ReFS clone, or a copy.

## Not in v1

- Full history backfill. Same data model. Add later.
- `--each`: one SVN commit per git commit.
- Small root-checkout repos like `crashboard`.
- SVN commit dialog, SVN log and blame in the GUI.
- Resolving an SVN conflict in the checkout, the kind `svn update` and `svn merge` leave. Those are files on disk with
  markers in them, not an index at three stages, so they need `svn resolve` and a reader of their own. The resolver
  here covers the git side: a rebase, and an import.
- The new Windows 11 context menu. It needs a packaged app and a certificate.
- Icon overlays. Windows has 15 slots. TortoiseSVN and TortoiseGit overlays keep working, because the folders stay real checkouts and real worktrees.
- Agent pushes. Behind a config flag.
- An MCP server. The CLI with `--json` is the agent interface.

## Why not git-svn

- It imports all history first. Days and hundreds of GB for this repo.
- It cannot see externals. Here the externals are the whole content.
- It talks to the server with its own code. This tool uses your normal `svn.exe`, so auth and config just work.

## Milestones

1. `Sg.Core` + `sg`: init, sync, branch, rebase, push, rm, status. Tests with local repos. Try it on `monorepo_proto`. Done 2026-09-03.
2. Server branch and server checkout. Done 2026-09-03, dry run checked against the real checkout.
3. `Sg.App`: Overview, Push, Commit with Monaco diff, Log, Discard, Sync, New branch, Rebase, New server branch, Settings. Context menu installer. Done 2026-09-03. Built on Windows App SDK 2.4, .NET 10, unpackaged, Mica, TitleBar, cards, Fluent icons.
4. Agent notes, `--json`, settings, polish. Notes and `--json` done. Left: an "sg-ui" icon, and SVN commit and log from the checkout in the GUI.
5. The Settings shape. Done 2026-09-05. Every window became a page (`SgPage`) shown in a `NavHost` with a back and forward stack; the overview shows them behind its pane under a breadcrumb, the Explorer menu shows one in a `PageWindow`. A checkout and each worktree are `SettingsExpander` cards: closed, the one thing to do next; open, every other row. Settings write themselves on change. A page with nothing to show is one sentence and the buttons that still apply (`EmptyState`). Picking a folder in a file tree shows one patch of everything under it.
6. The commit windows, the way Tortoise does them. Done 2026-09-06. `Patch` in the core reads a unified diff as blocks, writes a patch out of any subset of them, and un-applies one against the file on disk; both commit windows and `git apply --cached` sit on it. Git: a block is staged, unstaged or discarded, a file can be part staged (`MM` in the list), a commit takes the staged version of a part staged file and the working tree version of every other, and nothing staged for a file nobody picked ever reaches it. Amend replaces the last commit. SVN: a block goes back to BASE, unversioned names go into `svn:ignore` on their folder, a versioned file is deleted through svn. Both: the modified side of the diff is editable and Ctrl+S writes it in the file's own encoding, the last messages are offered again, whitespace-only changes can be hidden, and every action is in the editor's own right click menu as well as over the diff.
7. Squash and reword, and a pass over what every window waits for. Done 2026-09-06. `Ops.Squash` and `Ops.Reword` replace a run of the branch's own commits with one built by `commit-tree`, keeping the first one's author, and put the commits above it back with `rebase --onto`; the log window picks the run and refuses a gap, a snapshot, or a dirty worktree before it asks for a message. Performance: the commit window reads the status and HEAD side by side instead of four processes in a row, the status brings the branch name back with it, the numbers beside each row come from `--numstat` rather than the whole patch, that patch is only read when it is the thing on screen, nothing waits for the counts, and the filter box rebuilds its tree once per pause in the typing instead of once per letter.
8. Partial pushes, merging between server branches, and reverting a commit. Done 2026-09-06. Push: the commit list picks where the push stops, the preview and the diff follow the pick, and the commits left behind stay separate. Merge: a window over `svn merge` picks a working copy, a branch of the same repository and any set of its revisions, tests the merge without writing, and takes revisions back out again the same way; what it brings in is committed through the SVN changes window. Revert: the log window adds a commit that undoes any of the branch's own commits, adjacent or not.
9. Blame. Done 2026-09-06. `Blame` in the core answers per line: svn for a file of the checkout, and for a worktree file git for the lines the branch changed and svn for the lines that came in with a snapshot, paired through the original line number git reports. The window lists the file with the revision, the author and the line, one colour per revision so a change reads as a block, and picking a line shows what that revision said and what else it did to this file. Reached from the file lists of the commit and log windows, and from `sg-ui blame <file>`.
10. A push that stops in the checkout. Done 2026-09-06. `PushFinish.LeaveInCheckout` runs the same sync, rebase and refusals a push runs, writes each working copy's share of the change with `svn add`, `svn rm` and `svn mv` exactly as a push would, and then stops: no commit, no new snapshot, no reset of the branch. A working copy that refuses puts back everything already written, because a half applied checkout is worse than one that was never touched. The Push window offers it beside the push itself, asks for no message, and points at the changes window afterwards.
11. Shelves. Done 2026-09-06. `Shelf` in the core saves, lists, reads and restores; the CLI has `sg shelve` and `sg shelf`; `sg status` counts them per checkout and per branch. In the app they are a page of their own, reached from the checkout card, from every worktree card, from both changes pages, and from the Explorer menu; the two changes pages and the right click menu of their file lists make one out of what is checked, and the Push window's collision check offers "Shelve them", which puts exactly the colliding edits aside and reads the push again.
12. Carrying a branch to another machine. Done 2026-09-08. `Export` in the core writes a zip: the commits between
    the snapshot and the branch as a `git format-patch` series, the blobs every one of those changes starts from as a
    pack, and a manifest naming the checkout's URL and revision and those of every external. The base is named by SVN
    and never by a git sha, because the same revision builds the same tree on both machines but not the same commit,
    so a bundle prerequisite could never resolve on the far side. Import matches the checkout by URL, makes the branch
    the way `sg branch` does, unpacks the blobs so a three way merge has what it needs, and replays the whole series
    onto whatever revision this checkout is at, stopping and waiting on a patch that needs a hand. It reports every
    working copy whose revision differs, and an external that points at another branch as that rather than as a number
    to compare. `sg export` and `sg import` in the CLI; on the worktree card and the checkout card in the app, where
    import is a page that reads the file before it offers the button.
13. Resolving what stopped, whatever stopped it. Done 2026-09-10. `Git.ReplayInProgress` tells a rebase from an import
    - git keeps both in `rebase-apply`, and only the `applying` file inside it says which - and `Git.Progress` reads
    how far a series got and what it stopped on out of the same folder. `Conflicts` in the core is the whole surface:
    `State`, `Continue`, `Skip`, `Abort` over either of them, with `TakeSide` and `MarkResolved` underneath where they
    already were. `Export.Import` hands `git am` the entire series at once and leaves it standing where it stopped
    instead of aborting it, which is what makes a patch that needs a hand cost a hand rather than the branch. `sg
    resolve` does the same verbs in the CLI without naming git. The app's Resolve conflicts page names its buttons
    from what stopped, adds Skip beside Continue and Abort, and says what abort costs before it asks, because taking a
    whole import back off is not the same as undoing a rebase. The overview and every refusal say "import" where they
    used to say "rebase".
14. Making the import actually land. Done 2026-09-10, after a real 45 commit branch stopped on its second patch with
    nothing in conflict and no way on. Two causes, both silent, both the same shape - git could not merge at all, so
    it left nothing to merge. First: `git mailsplit` strips a trailing CR from every line, and this repository stores
    its files with CRLF, so the patches arrived LF only and matched nothing; `git am --keep-cr --quoted-cr=nowarn`
    fixes it, and a test asserts a CRLF file survives an export and an import byte for byte. Second: `PackBases`
    packed the blob each changed path had at the snapshot, but the second patch to touch a file is cut against what
    the first one left - an intermediate blob on no commit the far side has - so git fell back to a plain apply;
    it now walks commit by commit and packs what every patch starts from. Third, and the reason it read as a dead
    end: stopped with nothing staged and nothing in conflict is not "every file is resolved", so `ConflictState.Stuck`
    tells them apart, Continue refuses with the reason instead of failing in words about `git add`, and the page says
    what happened and offers the two things that work. `Conflicts.ApplyWhatFits` is the new one: it writes what still
    fits of a stuck patch into the worktree and leaves every refused hunk beside its file as `.rej`, to finish by
    hand - `sg resolve force`, and a button on the page.
15. A backup remote. Done 2026-09-11. `Thin` in the core rewrites a run of commits above a snapshot into a marker, base
    and change commits that hold only the files the run wrote, deterministically, so a backup pushed once is what the next
    one is compared against; `Backup` pushes every branch, every folder's uncommitted changes (`Shelf.Wip`) and every
    shelf in one push under per-ref leases, lists what the remote holds, restores a branch by `git merge-tree` onto
    whatever the checkout here is at - or by pointing at the real commits when the store still has them - and prunes.
    `sg backup`, `sg backup set|list|restore|prune`, and `sg status` says per branch how far it is from its backup. In
    the app: a Backup section in Settings, a timer and a backup a few seconds after a change, a chip on every worktree
    card, and a Backup page on the checkout toolbar with Restore, Back up now and Prune.
