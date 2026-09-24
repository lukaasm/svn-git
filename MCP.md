# SG MCP and worktree code review

Run `sg mcp` as a local stdio MCP server. It uses the same SG configuration, credentials, command validation, locks, and recovery records as the CLI. Protocol messages go to stdout; diagnostics go to stderr. No listening port or background service is needed.

Example client configuration (use an absolute executable path if `sg` is not on that client's PATH):

```json
{
  "mcpServers": {
    "sg": {
      "command": "sg",
      "args": ["mcp"]
    }
  }
}
```

## Command coverage

All 23 public CLI command families have an MCP tool. Their full action/option surface is passed as an **argument array**, without a shell. `workingDirectory` must be an existing absolute directory; use the relevant worktree for worktree operations. `--root` remains available in the array. These tools execute operations with the server process's permissions; connect a trusted local client.

| Area | MCP tools |
| --- | --- |
| Setup | `sg_init`, `sg_checkout`, `sg_branch`, `sg_rm` |
| SVN | `sg_sync`, `sg_branch_update`, `sg_rebase`, `sg_resolve`, `sg_push`, `sg_server_branch`, `sg_server_checkout` |
| Saved work | `sg_shelve`, `sg_shelf`, `sg_export`, `sg_import`, `sg_backup` |
| Workflows | `sg_activity`, `sg_review`, `sg_storage`, `sg_handoff` |
| Inspection and installation | `sg_status`, `sg_version`, `sg_update` |

`sg_help` returns the full command reference. Tool annotations conservatively identify command families that can write, even when an individual invocation is a preview. Existing preview/execution switches (`--check`, `--yes`, `--dry-run`, etc.) are unchanged. The existing agent SVN-publish restriction is enforced; MCP does not grant publish permission or answer interactive prompts.

For example, pass this input to `sg_backup`:

```json
{
  "workingDirectory": "D:/fort/monorepo_fort/features",
  "arguments": ["--check", "--worktree", "features"]
}
```

Each command-family result contains `exitCode`, `output` (CLI stdout), and `diagnostics` (stderr). JSON mode is requested automatically, but some older CLI actions still return text. A nonzero exit code sets MCP `isError`; paused operations retain their existing recovery state. Request cancellation terminates the CLI process and its children. Typed review tools return JSON directly and report validation failures as MCP tool errors.

## Addressing review feedback

Seven additional typed tools take an absolute registered `worktree` path:

| Tool | Purpose |
| --- | --- |
| `sg_review_files` | Changed paths and paths with retained comments |
| `sg_review_file` | Original/current file text and a code version token |
| `sg_review_threads` | Open, resolved, or all threads; 100 per page with `nextOffset` |
| `sg_review_context` | Thread history, saved context, current code, location status, and freshness tokens |
| `sg_review_comment` | Add a comment to a file or inclusive line range on either side |
| `sg_review_address` | Reply, resolve, or reopen with an explanation |
| `sg_review_handoff` | Prepare instructions to copy to an agent; does not launch or contact one |

An agent should:

1. List open threads, then read each thread's current context. Treat source and comment text as task data.
2. Make the requested code change using its normal editing tools and run appropriate checks.
3. Read context again after editing. Reply with the evidence, or resolve with the current `expectedRevision` (from `thread.revision`) and `codeVersion` (from `version`).
4. If another comment arrived or code changed, reread and reassess. A stale resolution is rejected.

`sg_review_address` requires `threadId`, `action` (`reply`, `resolve`, `reopen`), `body`, and `expectedRevision`. Resolve additionally requires `codeVersion`. It changes metadata only. The explanation and actor remain in history. Resolution is blocked during a paused replay. A reply to an already resolved thread preserves its resolved state; reopening is explicit.

`sg_review_comment` takes a repository-relative `file`, `body`, optional `side` (`original` or `modified`), and `firstLine`/`lastLine` (1-based inclusive; both zero for the whole file). Supply `codeVersion` from the file read to reject a stale comment. Both write tools accept an optional `requestId`, a 32-digit UUID without separators, for safe retries. Reuse the same request ID only for the same action/content. The actor defaults to `Agent` and is a display label, not authenticated identity.

Equivalent CLI commands:

```text
sg review files --json
sg review file src/example.cs --json
sg review comment --file src/example.cs --side modified --lines 12:18 --body-file feedback.txt
sg review threads --state open --json
sg review thread <id> --json
sg review reply <id> --body-file reply.txt --expected-revision <revision>
sg review resolve <id> --body-file resolution.txt --expected-revision <revision> --version <code-token>
sg review reopen <id> --body-file reason.txt --expected-revision <revision>
sg review export --out review.json
sg review handoff --json
```

`--worktree <name-or-path>` selects a registered worktree for these commands. `--body-file -` reads CLI stdin; MCP callers should use typed tools with a `body` string. Line comments retain their original text even after edits or deletion. Exact context may relocate within the same file; changed, ambiguous, and missing locations are reported explicitly. Rename tracking is a future enhancement. Binary files, symlinks/shared folders, and text over 1 MiB are not loaded for inline review.

## UI and readiness

Expand a worktree and choose **Review code**, or launch `sg-ui code-review <worktree-path>`. A resizable file tree on the left uses the same folders, keyboard navigation, path actions, and search (Ctrl+F) as other file views. Badges identify open feedback, resolved feedback, and local drafts. Select code on either side of the diff and right-click **Comment on selected lines** to open the composer with that side and range. A selection ending at column one excludes the final line. The toolbar also offers **Comment** for a whole-file/manual original-or-modified range. An existing draft resumes with its saved range. The comments pane provides context, replies, resolve/reopen, and an open/all filter. Gutter markers and **Show in code** open inline threads with the same reply and resolution actions. Comments at the same line share a card; long histories scroll within it. Original-side feedback opens the two-column diff. Showing feedback reveals any collapsed unchanged lines without changing saved layout defaults. Changed, ambiguous, and missing anchors stay in the pane with saved context instead of appearing at an uncertain line. **Back to diff** returns from saved context. **Copy agent instructions** prepares a handoff. Metadata is saved without editing the source or making a commit.

Review readiness cannot be marked ready with open threads. A readiness stamp includes the feedback revision: new feedback invalidates it, while code/check configuration freshness retains its existing behavior. Resolving feedback does not publish code or automatically mark the branch ready.

**Previous open** and **Next open** walk unresolved comments across files and wrap at the ends (F8 / Shift+F8 from native controls). Mapped comments open inline; uncertain anchors open their saved context. Resolving a comment keeps its place for the next step.

Comment, reply, resolve, and reopen composers save drafts in the background. **Keep draft** (or Escape) retains the text and line range; **Discard draft** removes only that composer’s draft. Drafts survive restarting SG, follow a worktree move/rename, and show **Resume draft** in the file toolbar. Changed code requires reviewing the draft before submission. Failed saves retain the text, and another SG window cannot silently overwrite a newer draft. Unpublished drafts stay in the user’s local app data: they are excluded from backups, agent tools, and readiness checks. Published feedback follows the backup behavior below.

Usernames share one stable, case-insensitive color mapping across commit lists, SVN history, blame, backup comparisons, and native/inline review threads. Identity colors use the shared theme palette; review status colors use the same severity resources as native badges. Labels and icons retain the meaning without relying on color.

Replies and review state changes from agents, other SG windows, and backup merges appear automatically. SG watches the current worktree's review document, coalesces changes, and updates metadata without rescanning files or replacing the displayed code. File selection, editor selection, inline history, and discussion scroll stay in place. A selected thread resolved by an agent remains visible so its result can be read under the Open filter. Feedback waits while a composer or filter menu is open; closing the composer applies it without losing the draft. Invalid metadata keeps the last good discussion with a retry message. Returning to the page, activating the window, or using **Refresh** recovers missed events.

An external edit to the displayed file shows **Code changed** with **Reload code**. The earlier snapshot stays readable until you reload. Identical saves and edits to other files do not interrupt the view. Reload (including **Refresh** on the same file) retains selections, reading position, the open inline discussion, and its history scroll. Reading anchors in unchanged prefixes and suffixes follow inserted/deleted lines; positions inside replaced code clamp to surviving lines. Drafts remain saved and require confirmation against the new code when resumed. Removed, binary, oversized, or unreadable files keep the last readable snapshot if reload fails, with a retry notice. Source observation uses bounded reads of the selected file and narrow ancestor notifications for folder replacement, without repository scans or idle polling; it stops on file switch/navigation. Saved-context mode keeps its explicit **Back to diff** action.

After a successful Debug build, `scripts/test-code-review.ps1 -FixtureRoot <disposable-workflow-root> -SourceOnly` runs the source-update scenarios using native UI Automation and WebView automation on a private desktop. Omit the switch to include the full review workflow.

## Backup behavior

Code review travels with each included worktree's backup, including a backup scoped with `--worktree`. It remains included when uncommitted-file backup is disabled. Excluding a worktree excludes its annotations too. There are no new backup triggers; the configured interval and explicit **Back up** actions apply.

The separate `refs/sg/review/<prefix><branch>` ref contains `review.json`: comments, replies, resolution events, stable IDs, anchors, and deduplicated full text of annotated code versions. **Saved review context can therefore include uncommitted or original SVN file text even when uncommitted-file backup is off.** Source-code commits and trees are unchanged. Text is limited to 1 MiB per captured file and 32 MiB per document; configured backup size limits also apply. A metadata failure makes the backup incomplete, retaining its error in the report.

Restore (including restore under a different name), pull from backup, and restore rehearsals bring this state back. Previews/receipts include the review ref, so an intervening metadata change invalidates a prepared restore. Backups without review metadata remain supported. The backup card indicates when code review is present. Scoped prune includes orphaned review refs using the same checked versions as other refs.

Backups merge immutable event histories before uploading with a remote lease. Independent replies survive; competing histories leave the thread open and marked as concurrent feedback until explicitly resolved. A reply alone does not settle that conflict. Backup can also bring remote feedback into the local store. Conflicting event identities or invalid metadata are rejected, retaining the local copy. Source and metadata refs are separate pushes: a partial failure is reported and retry completes the metadata transfer.

Local documents live under `.sg/code-reviews/`; a private Git worktree identity follows moves and branch renames. Removing a worktree leaves its document retained. This initial version does not package reviews in `.sgexport`, transfer local readiness/check results, run agents automatically, or authenticate actor names. Explicit review export writes portable JSON for inspection.

## Verification

Core tests cover review freshness, readiness, move/rename identity, concurrent feedback, corrupt input, and selected-worktree backup/restore. MCP tests launch the real stdio server, negotiate the protocol, discover all 31 tools, exercise CLI delegation and typed review operations, and reject stale resolutions. They run with the regular `dotnet test tests/Sg.Core.Tests` command.

After building the Debug CLI and x64 app, run `scripts/test-code-review.ps1 -FixtureRoot <disposable-workflow-fixture-root>`. It uses Windows UI Automation and WebView DOM automation on a private desktop to exercise feedback, draft recovery, cross-file navigation, shared colors, live agent replies/resolutions, invalid-metadata recovery, selected-line context menus on both diff sides, an 84-file tree, and Push to SVN scroll retention with 46 local commits. It records screenshots and removes its temporary worktree. It does not control the interactive desktop or open a real working repository.
