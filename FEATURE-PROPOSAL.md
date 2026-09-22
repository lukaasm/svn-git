# Next features for sg

Approved design · 2026-09-16 · based on `2cec33e`. Implementation added 2026-09-17. The original design below explains the intended workflow; the implementation notes here state its current boundaries.

## Implemented entry points

- Branch cards: **Update from SVN**, **Review readiness**, and **Backup coverage**.
- Navigation: **Activity** (including attention count and older Git replay discovery) and **Storage**.
- Resolver: source-specific choices, file-level conflict explanations, original patch and staged-result review.
- CLI: `branch-update`, `activity`, `review`, `storage`, and `handoff` (see `sg --help`). `sg update` remains the application updater.
- Core: `Operations`, `WorkspaceVersion`, `Review`, `Backup.Coverage/TestRestore`, and `Storage`. Records are atomic JSON under `.sg`; checkpoint refs remain reachable independently of working folders.

## Current boundaries

- Guided update plans show the server revisions observed at planning time. Sync may fetch newer revisions. Requests to stop wait for a durable step boundary.
- Branch edits and checkout edits are separate protected shelves. Partial index staging, SVN property/directory changes, and linked changes block the plan before mutation. Ignored files are left untouched. Private copies of shared folders are not refreshed by guided update; junctions continue to follow the checkout.
- An interrupted restoration is never repeated automatically. Activity opens the saved shelves for manual comparison; closing the operation retains current files and all shelves. Git replay cancellation cannot undo an SVN sync or published revisions.
- Backup receipts record category paths and exclusions, historical uploads, checked ref hashes, local version/configuration, and the exact rehearsal base. Restore tests replay thin objects into a separate retained branch. Handoff JSON contains no executable instructions; imported notes are text. Receipts are point-in-time evidence and need revalidation after remote changes.
- Review covers the complete branch through its HEAD, snapshot, working/index content, and local check configuration. Review does not authorize publication. Checks run only on explicit action; their executable and argument arrays live in the local root configuration.
- Archive is deliberately conservative: it preserves a commit checkpoint and removes only a clean worktree without ignored or linked content. Generated ignored files also count as real files. Unsupported content leaves the worktree in place. Storage can remove explicitly selected stale temporary directories after rechecking age, inventory and pending operations. Physical savings remain unknown; automatic retention is disabled.
- SVN publication records identify attempted work and returned revision evidence. An interrupted attempt with no revision receipt remains an unknown outcome requiring repository inspection; Activity never reruns publication commands.
- Existing backup/import operations retain their own replay recovery metadata. Activity bridges those records and discovers older paused Git operations.

## Validation

`WorkflowTests` exercises dirty updates, changed plans, partial staging, durable shelf protection, process reopening at conflicts, interrupted restoration, review invalidation, backup ref changes and isolated rehearsal, clean archive recovery, archive refusal, and temporary cleanup revalidation. The native app and CLI are built as part of verification. Interactive Windows inspection was attempted but blocked by an app-approval timeout; no interactive visual pass is claimed.

Final verification: 375 tests passed in the full regression run; focused final runs passed all 12 new workflow tests, including receipt-write failure and forced-restore protection. Release builds of the native app and CLI passed. CLI smoke tests exercised guided update, persisted Activity, review invalidation and failed-check exit status, archive refusal, backup coverage, handoff preview, and isolated restoration. Existing compiler/XAML warnings remain.

## Product direction

Make the daily loop easier to complete: understand incoming work, update a branch without manually juggling shelves, review a known version, and recover confidently after interruption.

The app already has shelves, backup reconciliation, conflict resolution, reusable Git resolutions, push checks, partial push results, project monitoring, and a command palette. Extend those capabilities rather than adding parallel versions of them.

Keep the current WinUI layout and branch cards. Add contextual actions there; reserve global navigation for Activity and Storage. SVN remains authoritative, and publishing stays an explicit action.

## Priorities

| Order | Feature | User benefit | Relative scope |
|---|---|---|---|
| 1 | Guided branch update | One action handles saving edits, syncing, rebasing, and restoring edits | Large; central workflow |
| 2 | Activity and recovery | Every interrupted operation has a durable next action | Medium–large; shares the first feature's foundation |
| 3 | Backup coverage and handoff | See exactly what another machine can recover | Medium |
| 4 | Review readiness for a specific version | Know whether checks and review still describe today's branch | Medium–large |
| 5 | Conflict explanations and resolution review | Understand why a replay stopped and what each choice changes | Medium |
| 6 | Worktree archive and storage cleanup | Recover space without guessing whether work is safe | Medium–large |

These are relative scope estimates, not delivery dates. Build the first two together in vertical slices.

## 1. Guided branch update

**Entry:** the branch card offers **Update from SVN** when its base is behind. A dirty branch remains eligible. Clicking opens a plan, not a refusal asking the user to shelve manually.

The plan names the branch and checkout, shows the incoming revision range per working copy, the local commit count, and the files that must be saved temporarily. One primary action: **Save edits and update**, or **Update branch** when clean. Expandable details show the exact file coverage and ordered steps.

### Workflow

1. Inspect the branch, checkout edits, existing operations, and incoming revisions.
2. Save branch edits and relevant checkout edits as separate recovery shelves, recording staged state where supported. If an edit cannot be preserved, stop before mutation and name it.
3. Sync the SVN checkout and record the resulting snapshot and per-working-copy revisions.
4. Rebase this branch onto that snapshot.
5. Restore each shelf to its original owner. A restoration conflict is a separate recovery step, not a failed rebase.
6. Show a receipt: commits replayed, files restored, shelves still requiring review, and a link to the original branch checkpoint.

Ignored files are not silently described as protected by a shelf. The plan must distinguish files preserved by the operation from files left untouched. A destructive replacement requires a complete worktree recovery copy, as forced restore now does.

### One recovery screen, distinct entry actions

**Update from SVN**, **Get changes from backup**, and **Import export file** retain their own names and plans. They share the operation timeline, pause states, and completion receipt. Backup retrieval does not implicitly sync SVN; adding that step must be visible in its plan. Import names both the source file and destination branch.

### Stop states and actions

| State | Primary action | Other available action |
|---|---|---|
| Files conflict | Review first unresolved file | Finish later |
| All files resolved | Continue update/import | Finish later |
| Commit produces no changes | Review original commit | Skip this commit after review |
| Patch did not apply | Review patch | Apply what fits; skip after review |
| Commits done, edits pending | Recover local edits | Finish later |
| Restoring edits was interrupted | Review saved edits | Keep current files |
| Fully complete | Back to branch | View operation receipt |

An empty step is not automatically labeled “already included”: the result may also be caused by a manual resolution or a patch that failed to apply.

**Cancellation:** during execution, **Stop after current step** requests a safe boundary. At a conflict, **Finish later** leaves everything resumable. **Cancel replay** states exactly what Git will undo. Once SVN has synced, reversing the branch operation does not reverse the shared checkout's SVN update or any server commits.

**Acceptance:** start dirty, encounter a conflict, close the app, reopen, resolve, and recover both sets of edits. Repeat with an empty commit, an unapplied patch, an interruption during edit restoration, and a changed destination between planning and execution.

## 2. Activity and recovery

**Entry:** one **Activity** item for the current root, plus a small count of operations needing attention. Branch cards link to the same operation; they do not create a second recovery flow.

Each row shows the action, branch, last completed step, time, and one applicable action: **Resume**, **Review saved edits**, or **View result**. Completed entries list concrete effects. A partial SVN push records successful repository/revision pairs and what remains unsent.

Selecting an entry opens its timeline and preserved state. A checkpoint describes its coverage: branch commits, staged/unstaged edits, untracked files, or complete worktree. Avoid a generic “Everything is safe” badge.

**Restore checkpoint** defaults to a separate recovery branch if current work has changed. The UI must never imply that restoring a local checkpoint undoes published SVN revisions. Pruning is explicit and cannot remove a checkpoint referenced by a pending operation.

**Implementation boundary:** a persistent operation record under the root owns identity, source, destination, expected refs, phase, completed effects, shelf/checkpoint references, and error details. The core exposes `Plan`, `Run`, `Resume`, and `AvailableActions`; WinUI and CLI consume those results. These names describe the proposed boundary, not a requirement to introduce a generic workflow framework.

Keep records free of credentials. Write them atomically. On startup, reconcile the journal with Git/SVN state: a process can stop after an external command succeeds but before sg records completion. Never repeat an SVN commit solely because the journal says it is unfinished; reconcile against recorded revision evidence or require review when the outcome is uncertain.

**Acceptance:** process interruption at every mutation boundary produces either a correct next action or an explicit uncertain outcome with preserved work. Two callers cannot execute mutations for the same root concurrently.

## 3. Backup coverage and handoff

Extend the current Backup page with a coverage view per branch:

| Category | Example status | Action |
|---|---|---|
| Local commits | 4 of 4 uploaded at the recorded branch version | View receipt |
| Uncommitted edits | 2 included; 1 excluded by size | Review excluded file |
| Shelves | 2 uploaded | View shelves |
| Ignored/shared content | Outside backup coverage | View exclusions |

Keep **Uploaded**, **Remote refs checked**, and **Restore tested** distinct. A successful push does not prove that all current files were included or that a future restore will succeed.

**Check restore** fetches the recorded objects, checks their completeness, and rehearses restoration separately against a compatible locally available snapshot. Record the exact backup refs and base tested. If no compatible base exists, show that limitation rather than offering a green result.

**Continue on another machine** creates a receipt containing the branch, backup refs, source snapshot identity, coverage, and optional human note. The receiving machine previews the differences before restoring. A new change after the receipt was made marks it out of date. Source-machine automation does not mutate the receiving machine.

**Acceptance:** excluded large files remain prominently excluded after a successful upload; a removed remote ref invalidates verification; restore testing leaves the current branch and working files untouched.

## 4. Review readiness for a specific version

Add a small review summary to the existing branch and Push pages: **Not reviewed**, **Ready for this version**, or **Changed since review**. Avoid a separate review application.

The review view combines the existing commit diff and repository groups with configured local checks. A **Mark ready** action records the branch tip, snapshot identity, included commit boundary, reviewed file set, and check results. Checks show the command, exit status, time, and captured output.

Changing the relevant commits, staged/working content included in checks, base snapshot, or check configuration invalidates readiness. A rebase does not inherit readiness just because its subjects match. Configure commands locally; importing an export or backup must not cause bundled commands to run automatically.

An agent can provide a summary and request review. Publishing remains a separate human action. Before SVN publication, revalidate the plan and server state per repository; there is no promise of an atomic commit across SVN repositories.

**Acceptance:** a successful check for one version never appears as current for a different version. Partial pushes preserve the result for completed repositories and identify the remaining work.

## 5. Conflict explanations and resolution review

Extend the current resolver rather than replacing it. Above the diff, show the operation source, current commit, changed paths, and one accurate explanation: both sides changed the same lines, rename/delete conflict, no changes after resolution, or patch not applied.

Use source-specific choices: **Keep branch change**, **Keep updated SVN change**, or **Keep incoming backup change**. Avoid “ours/theirs” in the primary UI. For mixed source changes, show the actual source identity rather than forcing one generic label.

Git already remembers resolutions. Surface when a remembered resolution or agent edit was used, show its resulting diff, and allow review before continuing. Do not invent confidence percentages or infer semantic correctness from missing conflict markers. Keep file-resolution actions separate from skipping an entire commit.

**Acceptance:** every paused state provides an explanation and an applicable visible action even when there are zero conflicted files. A user can compare an automatically resolved file before staging/continuing.

## 6. Worktree archive and storage cleanup

**Entry:** **Storage** for the current root. Group active worktrees, recovery copies, shelves, and obsolete temporary data. Show logical size separately from estimated reclaimable space; shared ReFS blocks make directory size an unreliable savings estimate.

**Archive branch** first produces a durable receipt for unique commits and covered edits, then previews exactly which worktree will be removed. If ignored or unsupported files are present, offer a complete recovery copy or leave the worktree in place. “Uploaded commits” alone is insufficient permission to remove a directory containing other work.

The preview names exclusions, pending-operation references, and any unresolved verification. Start with manual cleanup of completed recovery copies and stale temporary data. Automatic retention should arrive only after those predicates are tested.

**Acceptance:** unfinished operations and their checkpoints cannot be pruned; an untracked or ignored file never disappears under an “already backed up” assumption; unknown reclaimable size is labeled unknown.

## Delivery sequence

1. Implement the persistent operation record and one complete dirty-branch SVN update flow. Reuse shelves, the root lock, the existing resolver, and atomic writes.
2. Add startup reconciliation and Activity. Route backup and sgexport recovery through the same action policy while preserving source-specific wording.
3. Add backup coverage receipts, then restoration rehearsal. This supplies the evidence needed for archive cleanup.
4. Add version-bound review readiness and conflict explanation polish.
5. Add manual archive/cleanup, followed by optional retention.

Useful measures are abandoned operations, manual recovery commands needed, duplicate conflict-resolution attempts, and time from opening a branch to completing an update. Establish a baseline locally before setting targets; no telemetry service is required.

## Small improvements to ship alongside these slices

- Consistent Ctrl+Enter for a page's primary action, and predictable focus after loading or resolving a file.
- One primary action per state, with the same wording on branch cards, pages, notifications, and CLI output.
- A shared asynchronous selection guard for diff/read views, extending the recent Commit-page fix.
- Progress with a named phase, actual counts when known, and cancellation semantics specific to that phase.
- Refresh the polish backlog as fixes land; do not treat its historical counts as a current inventory.

Avoid adding automatic publication, an opaque “fix everything” button, another command palette, or a second conflict engine. The biggest benefit is fewer manual transitions between the tools sg already has.
