# Worktree code review

Implementation update: the first delivery adds the review page, shared durable comments, CLI addressing, a 31-tool MCP server, readiness integration, and worktree-scoped backup/restore of review events and saved code context. See [the shipped behavior and setup](MCP.md). This document remains the broader UX roadmap; gutter threads, richer navigation/scope filters, rename tracking, and review rounds are not yet implemented. Backup was brought forward from the later phase at the user's request.

Status: proposed design and feature set. This document does not describe a shipped feature.

## Outcome

A user opens a worktree, selects code, and leaves a comment. An agent reads the same thread through `sg`, changes the code, replies with what it did, and marks the thread resolved. The user can inspect the result and reopen it. Comments survive application restarts and retain their original context when files change or commits are rebased.

Comments are annotations stored in SG metadata, not text inserted into source files. The initial release is local to one SG root; it does not launch an agent or publish comments to GitHub, SVN, or a backup remote.

## Main interaction

1. Expand a worktree and choose **Code review**, beside the existing code/history actions. If there are open threads, show a compact comment icon and count on the collapsed worktree card. Selecting the badge opens those threads.
2. The review opens immediately with a file list, code area, and **Comments** pane. Show the previous local summary while the file inventory loads. The default scope is **All worktree changes**: the branch's recorded SVN base through its current working files, including staged and unstaged edits and eligible untracked files.
3. Select one or more lines and choose **Add comment** from the gutter, toolbar, or context menu. The composer names the file, side, and line range above a short text field. `Ctrl+Enter` submits; Escape closes the composer while retaining the draft.
4. The saved thread appears at that code location and in the Comments pane. Only the selected thread expands. Replies, **Resolve**, and **Reopen** use the same controls in both places.
5. Choose **Prepare agent handoff** to preview the selected open threads, then copy instructions or export them. The agent uses the CLI to retrieve current state, reply, and resolve individual threads.
6. Agent updates appear without reloading the whole page. Selecting a resolved thread shows its original comment, resolution explanation, and any linked local commit or check result. **View current code** and **View original code** distinguish the two versions.

## Page design

```text
Fort > features > Code review
All worktree changes ▾       3 open · 2 resolved       Prepare agent handoff
┌────────────────────┬────────────────────────────────┬─────────────────────┐
│ Files              │ src/Cache.cs                   │ Comments            │
│ Search files       │ Base → working files           │ Open | Resolved | All│
│ Cache.cs       2   │                                │                     │
│ CacheTests.cs  1   │  42  if (entry == null)        │ Cache.cs:42–44      │
│                    │  43      return null;          │ You                 │
│ Files with comments│      [Add comment]             │ Handle a missing    │
│                    │                                │ entry explicitly.   │
│                    │                                │                     │
│                    │                                │ Reply…     Resolve  │
└────────────────────┴────────────────────────────────┴─────────────────────┘
Tasks · ongoing operations remain visible
```

- Reuse SG's navigation, Fluent icons, accent color, status chips, diff preferences, and spacing. Open uses an outlined comment icon; resolved uses a check icon and green; location uncertainty uses an amber warning plus text. Color is never the only indicator.
- Keep pane widths stable when threads expand, reply text wraps, or filters change. Grow thread bodies vertically; never cap header height around badges. Preserve selected file, thread, scroll, and drafts across navigation.
- At narrower widths, switch the right pane to a **Code / Comments** choice and collapse the file list behind **Files**. Do not compress three panes until code becomes unreadable. The inline thread and pane display the same state, not independent copies.
- Base menu: scope, thread filters, add comment, prepare handoff. Put export format, resolved history, metadata, and future review policies under **More**. No agent configuration is required to leave a comment.
- Empty: **No comments yet — select lines to start a review**. No diff: **No changed files**, with **Browse tracked files** and retained threads still available. Load failures retain useful content and offer **Retry** / **View log**.
- Binary or oversized files allow a file-level comment. If Monaco fails, retain the native thread pane and provide an explicit side/line-range composer against the displayed text. Code and comments remain readable without the embedded editor.

## Feature set

| Feature | First release | Later extension |
| --- | --- | --- |
| Review scope | All worktree changes; committed changes; uncommitted changes; browse a tracked file | Selected commits/ranges and named review rounds |
| Comments | Line, line range, or file; both original and modified sides; multiline text | Suggestions with explicit apply-and-preview |
| Threads | Replies, Open/Resolved, reopen, author and timestamps, retained resolution history | Optional assignment and temporary agent claims |
| Navigation | File counts, Open/Resolved/All, literal search, next/previous open thread, file/line links | Root-wide review inbox |
| Agent access | CLI JSON, stable IDs, reply/resolve/reopen, handoff text/export | Explicitly launched agent runs in Tasks |
| Code movement | Durable original context; conservative relocation; visible outdated/missing states | Richer rename tracking and review-round comparisons |
| Readiness | Open threads shown in existing Review readiness; version-bound checks remain separate | Configurable mandatory review policy |
| Storage | Local durable metadata; atomic writes; concurrent UI/CLI safety; local export | Selected-worktree backup, import, and cross-machine merge |
| Feedback | Native thread controls, staged loading, retained drafts, live local updates | Notifications and optional activity grouping |

The complete first release includes the UI and CLI together. Shipping only a comment editor would leave agents unable to finish the workflow.

## Thread and version behavior

Keep workflow state small: **Open ⇄ Resolved**. A reply does not implicitly reopen a thread. A resolution records who resolved it, when, an explanation, and the code version inspected; an agent resolution requires an explanation. A user may resolve directly. Reopening retains the previous resolution in history.

Location is independent of workflow state:

| Location | Presentation and behavior |
| --- | --- |
| Current | Original anchor still identifies the same content at the same location. |
| Relocated | A unique, verified mapping finds the unchanged commented content at another location. Show current line and original location. |
| Changed | The target still exists, but the commented range changed. Show the saved excerpt and offer original/current comparison. |
| Missing or ambiguous | File was removed, rename cannot be verified, or multiple locations match. Keep the thread in the list and do not attach it to a guessed line. |

Never resolve a thread because its line disappeared, a test passed, a commit was made, or a rebase completed. Resolved threads stay resolved after later edits but carry **Code changed since resolution** when applicable; the user can reopen. Agent resolution is visible as **Resolved by <agent label>**, not a claim that SG verified the fix.

Each anchor records the worktree identity, repository-relative path, original/modified side, inclusive 1-based line range, base and head IDs, captured file-content IDs, selected text, and surrounding context. Keep immutable captured file versions for commented text files in a content-addressed local store, including dirty files and deleted-side text. Deduplicate identical content. Apply a documented size limit and use file-level comments when a full text capture exceeds it. Retained context must not depend on an unreferenced Git object surviving garbage collection.

Mapping tries the same path and content first, then a verified file rename and an unambiguous line mapping. Similar names or the nearest line number are insufficient. Explicit **Attach to current selection** appends a new anchor event and retains the previous one. Case-only renames, repeated code, CRLF, non-ASCII text, and deletion are required cases.

The code shown in the editor carries a read token. If the file changes before submission, save against that displayed version and flag the thread as changed; never attach the user's words to unseen code. If an agent resolves against a stale thread revision or an outdated inspected file version, return a conflict and the new state. The agent rereads before deciding what to do.

## Agent interface

Extend the existing `sg review` command family without changing `status`, `run`, or `ready`. Proposed commands below are not available yet. Run them from the target worktree; also support `--worktree <registered-name-or-path>` for root-level tooling.

```text
sg review threads --state open --json
sg review thread <id> --json
sg review comment --file src/Cache.cs --side modified --lines 42:44 --body-file comment.md
sg review reply <id> --body-file reply.md --expected-revision 3
sg review resolve <id> --body-file resolution.md --expected-revision 4 --version <read-token>
sg review reopen <id> --body-file reason.md --expected-revision 5
sg review export --state open --format markdown --output review.md
```

- JSON returns a schema version, worktree/review/thread IDs, revision, state, anchor status, original excerpt, current location if verified, replies, resolution, and code read token. Lists are paged with a cursor; exports intentionally include the selected complete set. IDs remain stable across sorting and rebases.
- `--body-file` preserves multiline text without shell interpolation; `-` accepts standard input. Errors use a consistent nonzero exit code with a structured error when `--json` is present. Define a distinct revision-conflict code during implementation without reusing existing workflow semantics accidentally.
- Mutations accept an idempotency key so a timed-out client can retry without duplicate replies. Updates are optimistic: a stale revision produces a conflict rather than replacing another actor's change.
- Actor labels distinguish a user from an agent instance but are local attribution, not authenticated identity. Optional commit/check evidence is verified against local data before becoming a clickable link; arbitrary prose remains prose.
- Handoff contains scope, worktree path, current thread IDs, excerpts, and commands to reread/reply/resolve. It tells the agent to handle only the selected feedback and preserve SG's existing SVN publication rules. Prepare/export does not start a process or send a message externally.
- Update generated agent notes with a short discover/read/resolve workflow. Existing worktrees get an explicit **Update SG agent instructions** action that updates SG-owned generated content without overwriting user-authored guidance.
- Review text and imported snippets are task data. CLI output/export must delimit them from SG's operating instructions; no comment text is interpreted as a shell command or executed by SG.

Example handoff loop: read open threads → inspect original and current code → make the change → run relevant checks → reply with evidence → resolve using the latest thread revision and inspected-code token. A reply without a code change is valid when it explains why no change is needed; the user can reopen.

## Persistence and lifecycle

Use a new `.sg/code-reviews/` namespace rather than mixing thread records into the existing `.sg/reviews/<branch-hash>.json` check results. Assign a persistent worktree review ID through a registry owned by the review module; branch labels and folder paths are locators, not identity.

- Moving a registered worktree or renaming its branch preserves the ID. SG-managed removal archives the mapping and retains its threads. Recreating the same branch name gets a new ID. If external Git operations make identity uncertain, show an unlinked review and require an explicit association rather than guessing.
- Keep one atomic record per thread, including its bounded event history and a monotonic revision. Store immutable text versions separately by content hash. A small summary index supplies counts without reading code. The index is rebuildable; thread records are authoritative.
- Reuse `AtomicFile` for publication. Serialize review metadata mutations across processes with a short review-specific lock; combine this with revision checks to prevent lost updates. Never hold it while reading a large diff, waiting for an agent, or executing a check.
- The existing root lock still protects Git/SVN operations and identity-changing mutations. Establish one lock order: root before review metadata when both are needed. Metadata-only replies do not take the root lock. Revalidate identity and displayed code tokens before writes that depend on them.
- Browsing and replying remain available during a long operation. Adding/reanchoring/resolving requires a stable captured version; during rebase, restore, removal, or an uncertain mapping, explain why those actions are unavailable and link to the running task. Composing and preserving a draft remains available.
- Corrupt or newer-schema records are reported and left intact. Drafts persist separately from submitted threads and never appear in agent exports. Retention cleanup is explicit and cannot remove a blob referenced by a retained thread.
- First-release UI clearly labels review data **Stored on this machine**. Existing backups and sgexport files do not silently claim to include it. A future portable format must carry selected threads and their context with a format version, worktree mapping, explicit inclusion preview, and conflict handling for concurrent edits.

## Fit with existing SG architecture

| Existing code | Design use |
| --- | --- |
| `src/Sg.Core/Review.cs` | Retain local checks and readiness stamps; expose thread summary alongside them. |
| `src/Sg.Core/WorkspaceVersion.cs` | Keep full workspace fingerprints for explicit readiness/check operations. Do not compute one per comment keystroke or card render. |
| `src/Sg.Core/AtomicFile.cs` | Reuse atomic metadata publication. |
| `src/Sg.Core/AgentNotes.cs`, `src/sg/Program.cs` | Add agent discovery instructions and review subcommands. |
| `src/Sg.App/DiffView.xaml.cs`, `Assets/monaco.html` | Extend selection messages with side, model identity, and read token; add comment markers and reveal-line actions. Current selection support covers only the modified side. |
| `src/Sg.App/CheckoutPage.xaml`, `WorkflowPages.cs` | Add the worktree entry/count and link Code review to existing Review readiness. |
| `src/Sg.App/PageReads.cs`, `Navigation.cs` | Reuse cancellation, stale-result suppression, and navigation view-state retention. |
| `src/Sg.Core/TaskQueue.cs` | Reuse persistent task presentation for expensive exports and any future explicit agent run. |

A `CodeReview` core module owns identity, version capture, anchors, thread transitions, concurrency, and export. Its small interface provides browse/read, capture/create, reply, set state, reattach, and export operations. UI and CLI cross the same interface. Storage, anchor matching, and indexing remain internal; neither caller recreates them.

In the app, a reusable thread presenter serves the Comments pane and inline location. Extend `DiffView` with typed review messages rather than allowing the WebView to write metadata. Validate file/model tokens and payloads on the native side. Later, Commit and Log pages can open the same thread presenter with their own explicit code versions.

Readiness and discussion remain distinguishable. **Mark this version ready** requires current passing checks and no open threads in that worktree review. Its stamp records the review revision as well as the existing code/configuration inputs; a new or reopened thread invalidates the stamp. Resolving every thread does not automatically mark ready. Existing worktrees without threads keep their current check flow. Push remains advisory by default: show unresolved feedback and a link, without adding an unrequested mandatory publishing gate.

## Responsiveness and accessibility

- Render the page frame and cached thread counts before scanning worktree changes. Load file metadata next, then only the selected file's content. Create Monaco on demand. Keep the native comments pane usable while it starts.
- Use bounded/virtualized file and thread lists. Fetch original context lazily. Cancel obsolete reads on file selection and navigation; late results cannot replace the current selection or draft.
- Watch review metadata for external CLI changes, coalesce bursts, and refresh affected threads only. Recover from missed watcher events on activation or explicit refresh. Avoid repeated full repository hashing or polling when idle.
- A save shows **Saving…**, then **Saved** after durable publication. On failure, retain the text and offer Retry. Do not show a success count until the write succeeded. Short saves use inline feedback; long operations use Tasks.
- Initial targets to validate on the real large repository: visible frame within 100 ms, cached comments within 200 ms, native local save feedback within 100 ms and durable completion normally within 500 ms. These are targets, not measurements; report slow disk/lock waits instead of hiding them.
- Native controls expose thread IDs, author, state, range, and actions through UI Automation. Every gutter action has a keyboard/toolbar equivalent. Test normal and maximized windows, narrow layouts, text scaling, high contrast, and multiple wrapped badges.

## Delivery and acceptance

1. **Core and CLI:** durable identity/threads, captured context, conservative anchors, concurrency, handoff export, and tests. Preserve all existing `sg review` behavior.
2. **Complete first release:** Code review page, both-side inline comments, shared thread presenter, draft retention, agent updates, worktree badges, and readiness integration. Verify end to end with background UI Automation on a disposable repository.
3. **Follow-up:** review inbox, selected-worktree review backup/import, then optional explicit agent execution. Suggestions, assignments, and mandatory approval policy remain separate additions.

Acceptance scenarios:

- User selects lines, saves a comment, navigates away, restarts, and finds the same text and original code. Source bytes and Git status are unchanged by commenting.
- Agent reads JSON, replies, resolves, and the UI updates without losing the user's selected file or draft. The user reopens and the agent sees it.
- New lines, changed targets, verified rename, case-only rename, deletion, repeated text, and rebase never silently misplace or resolve a thread. Original context remains readable after Git cleanup.
- Concurrent user/agent replies, stale resolve, duplicate retry, save failure, and process interruption neither lose nor duplicate accepted comments.
- Worktree move/rename retains review identity; archive retains feedback; same-name replacement does not inherit unrelated comments.
- New feedback invalidates readiness; resolving does not grant readiness; check freshness rules still apply. No command publishes to SVN as a side effect.
- UI Automation exercises comment creation, reply, CLI resolution, reopen, drafts, both diff sides, fallback mode, and layout stability on a private desktop. Core integration tests validate persistence and repository bytes directly.
- Large fixture and read-only real-repository profiling confirm staged hydration, bounded rendering, cancellation, and stable input latency. Product code changes begin only after this design stage.
