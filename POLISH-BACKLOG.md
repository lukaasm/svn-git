# UI polish backlog

What the 2026-09-08 audit found and this pass did not fix. 329 agents read every page and every
shared widget, then a second agent tried to refute each finding; 124 survived. All 43 marked high
were fixed, bar the two named at the end. These are the 81 that are left.

## Medium (65)

### AddCheckoutPage.xaml.cs

- **:16** (interactivity) No keyboard submit on any of the four form pages
  - CommitPage.xaml.cs:46, SvnCommitPage.xaml.cs:39 and PushPage.xaml.cs:40 all register `Shortcuts.Add(this, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { if (Button.IsEnabled) ... });` so the primary action is one chord away from the field you are typing in. AddCheckoutPage, NewRootPage, Edit
  - Fix: Register exactly ONE Ctrl+Enter accelerator per page, in the ctor after InitializeComponent, adding `using Windows.System;` where missing. AddCheckoutPage: `Shortcuts.Add(this, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { if (AddButton.IsEnabled) Add_Click(AddButton, null!); });` — same shape with SaveButton/Save_Click on EditCh

### BlamePage.xaml

- **:20** (interactivity) The filter box promises Ctrl+F but nothing registers that accelerator on this page.
  - Every other filter box in the app gets Ctrl+F from ListFilter.AddAccelerator (ListFilter.cs:236), and SettingsPage.xaml:116 documents Ctrl+F as an app-wide shortcut. BlamePage builds its filter by hand and registers only F5 (line 39 of the code-behind), so pressing Ctrl+F here does nothing and the p
  - Fix: In the constructor add `Shortcuts.Add(this, VirtualKey.F, VirtualKeyModifiers.Control, () => { Filter.Focus(FocusState.Programmatic); Filter.SelectAll(); });`.
- **:26** (design) The star column is on the line list instead of the diff, so the splitter turns the layout into a fixed-width grid.
  - LogPage.xaml:20-22 and SvnLogPage.xaml:34-36 both give the left list a fixed width and the diff column the star; this page has it inverted. Two consequences: on a wide window the Monaco diff stays pinned at 420 px while the line list eats every extra pixel, and after the first splitter drag ColumnSp
  - Fix: Keep the reporter's direction — the star must move off LeftCol so the splitter has a column left to absorb the window — but do not shrink the left to 560. In BlamePage.xaml:26-28 write `<ColumnDefinition x:Name="LeftCol" Width="2*" MinWidth="420" />`, leave the `Width="10"` thumb column, and make the third `<ColumnDefinition Width="*" Min

### BlamePage.xaml.cs

- **:55** (stale-state) A stale load hides the skeleton out from under the load that is still running.
  - Hide() happens before the generation check. Press F5 twice (or Refresh while the first read is still out): the first read returns, hides the skeleton, and the second read continues with no progress indication at all — an empty card that looks finished. LogPage.xaml.cs:177 gets this right with the co
  - Fix: Split the guard instead of moving Hide() past all of it, so the newest load clears the skeleton whether it succeeded or failed: replace lines 55-56 with `if (gen != _generation) return;` then `LinesSkeleton.Hide();` then `if (result == null) return;`. (Optionally also guard the F5 path at line 39 against re-entry so two loads cannot overl
- **:124** (design) A line svn could not date is reported as "Not committed yet", which is the wrong answer.
  - This branch is reached whenever Revision is null and Sha is null — which Blame.cs:103 produces for a line that came in with a snapshot and that svn blame could not answer (the file is not in the checkout, or svn blame failed; Blame.cs:91-93 raise a warning for both). Those lines are committed in SVN
  - Fix: Do not string-match the prose sentence in the page (it lives in another assembly, is pinned by nothing, and a reword of Blame.cs:103 would silently restore the false message). Put the discriminator in the model: in Sg.Core/Blame.cs add `public const string CameWithSnapshot = "came in with a snapshot";` to Blame, use it at line 103, and gi
- **:134** (stale-state) While an SVN revision loads, the detail card still describes the revision picked before it.
  - The diff header instantly says r1234 and the loading bar starts, but the card above it keeps showing the author, date and message of the previous line's revision for as long as the svn log takes — the two halves of the pane disagree about which revision the user is looking at. SvnLogPage.xaml.cs:173
  - Fix: Before `Diff.BeginLoading(title)` set `DetailHead.Text = $"r{revision}   {line.Author}   {line.Date}"; DetailMessage.Text = "";` from the row's own data, then refine them when the log arrives.
- **:135** (stall) Three svn round trips run one after another for every SVN line picked.
  - The URL never changes for the life of the page yet is read again on every pick, and the two reads that actually need the server — a 200-entry verbose log (with every changed path of every one of those revisions) and the revision diff — run in sequence when they only depend on the URL. Sg.Core ships 
  - Fix: Keep the info read inside the selection handler and pair the log against the info→diff chain, so nothing moves to load time and the not-in-checkout case still only fails the pick, not the page. Replace the lambda body at BlamePage.xaml.cs:135-140 with: `var (log, diff) = Fan.Two(() => root.Svn.LogVerbose(co.Path, _path, 200).FirstOrDefaul

### CheckoutPage.xaml.cs

- **:70** (design) With a root open and no checkout to show, the page body is completely blank — no state, no action
  - noRoot is false here, so NoRoot stays collapsed and Scroll stays visible while CheckoutHeader, CheckoutCard and WorktreesHeader were all just set to Collapsed — an empty white page. It is reached at startup from MainWindow.xaml.cs:68 before the first status, and it is what is left behind whenever th
  - Fix: Put the new empty state inside the ScrollViewer's StackPanel right after OverviewSkeleton (CheckoutPage.xaml:32-34), the way NoWorktrees sits at line 137, so it flows with the skeleton instead of overlaying it: `<local:EmptyState x:Name="NoCheckout" Visibility="Collapsed" Glyph="&#xE8B7;" Title="No checkout to show" Text="This root has no

### CommitPage.xaml

- **:44** (design) The six-button action row is a non-wrapping StackPanel in a column whose minimum is 320 px, contradicting its own comment
  - All / None / Stage / Unstage / Discard / Shelve are roughly 490 px of buttons. The column starts at 560 (LeftCol, line 31) and ColumnSplitter.Attach clamps it down to 320 (Controls.cs:59), and a horizontal StackPanel never wraps — so as soon as the splitter is dragged in, Discard and Shelve leave th
  - Fix: Swap the StackPanel for the app's own wrapping panel, which exists for this and is currently unused: `<local:WrapRow Grid.Row="1" Spacing="6">` (Controls.cs:85).

### CommitPage.xaml.cs

- **:71** (stale-state) A read started by the Not staged / Staged switch is never dropped, so it can draw over a newer selection
  - The side switch calls ShowFileAsync with `node` defaulted to null, so the stillWanted predicate is `node == null || ...` — always true. Click "Staged" and then pick another file before the three git reads return, and the switch's answer lands last: the diff, the title, `_shownPath` and `_shownPatch`
  - Fix: No new parameter is needed: strengthen the guard in place at CommitPage.xaml.cs:247, where the effective side is already known. Replace `() => node == null || _filter.IsCurrent(node)` with `() => (node != null ? _filter.IsCurrent(node) : ReferenceEquals(_filter.Selected&lt;ChangeRow&gt;(), row)) && _showStaged == staged` — `staged` is the
- **:219** (animation) The diff's action row and side switch are torn down before every read and rebuilt after, so the header blinks and the diff jumps on each selection
  - ShowFileAsync calls Clear() before the reads, which empties the action list; DiffView.SyncActions then collapses the whole ActionRow (DiffView.xaml.cs:355) and the Extras panel with the Not staged / Staged pair goes with it. The buttons only come back at line 254 after the git reads return. So movin
  - Fix: Split Clear() into two: `ClearShown()` keeping only the `_shownPath/_shownPatch/_shownModified/_shownOnDisk` reset, and `ClearHeader()` keeping `Diff.SetActions(Array.Empty<...>())` plus the `_workingSide/_stagedSide` collapse. ShowFileAsync (219) calls `ClearShown()` alone so the action row stays put across a file-to-file move, while lin
- **:387** (design) The summary always claims untracked files are unchecked, including right after All checked them
  - SyncCommitButton recomputes this on every tick, so the sentence is live — but it is a constant. Press All (whose own tooltip says "Check every change, untracked files too") and the line under the list still insists untracked files are unchecked. On a worktree with no untracked files at all it names 
  - Fix: The proposed count is right (`var untracked = _rows.Count(r => !r.Checked && r.Entry.Untracked);`) but "instead of the fixed clause" puts it BEFORE the existing `partly` clause, whose text is `$"  {partly} of them {(partly == 1 ? "goes" : "go")} in as staged only."` — "of them" would then read as referring to the untracked files, which is
- **:446** (interactivity) Stage and Unstage do nothing and say nothing when nothing is checked, while Discard and Shelve on the same toolbar explain themselves
  - The four action buttons are always enabled. Pressing Stage with nothing ticked, or Unstage when nothing ticked is staged (Unstage_Click filters to `.Staged` at line 499), returns at line 446/453 with no dialog, no status line and no button state change — the press reads as a hang or a broken button.
  - Fix: Put the guard in the click handlers, not in the shared task, so the two empty cases are told apart. In Stage_Click (CommitPage.xaml.cs:495-496) test `Checked()` first and show `await Dialogs.Info(this, "Nothing checked", "Check the files to stage.")` when it is empty. In Unstage_Click (498-499) take `var picked = Checked();` and branch tw

### ConflictPage.xaml

- **:39** (interactivity) The ticked file tree has no All / None buttons, unlike the two sibling pages that use the same template
  - This page draws the same `CheckedFileTreeTemplate` tree as CommitPage and SvnCommitPage, and its four actions all work on what is ticked, but there is no way to tick or untick everything at once. A rebase that stops on forty files across a dozen folders has to be ticked folder by folder, and there i
  - Fix: Add `<Button Content="All" Click="All_Click" ToolTipService.ToolTip="Tick every file in conflict." />` and `<Button Content="None" Click="None_Click" ToolTipService.ToolTip="Untick everything." />` at the head of the StackPanel, with handlers setting `foreach (var r in _filter.Rows<FileRow>()) r.Checked = true/false;`.

### ConflictPage.xaml.cs

- **:15** (interactivity) No F5 to refresh, on the one page whose whole job is re-reading state you changed outside the app
  - The Refresh tooltip (ConflictPage.xaml:72) tells the user this is for "after you edited files outside" — the exact workflow where a keyboard refresh matters, because the user is alt-tabbing back from an editor. Every sibling binds it: MergePage.xaml.cs:34, CommitPage.xaml.cs:47, SvnCommitPage.xaml.c
  - Fix: The proposed fix is right; the only addition is to keep the tooltip wording identical to the siblings. In ConflictPage.xaml.cs add `using Windows.System;` and, in the constructor next to the existing `Shortcuts.DiffNavigation(this, Diff);` (line 22), add `Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync());`. Then append " F5 does t
- **:23** (interactivity) Right-clicking a conflicted file offers no way to pick a side, though the buttons above do
  - The `extend` parameter of FileActions.Attach (FileActions.cs:20) is left null, so the context menu on a conflicted row is only Open folder / Edit file / Copy path. The four things a user actually comes to this page to do — Keep SVN, Keep branch, Edit, Mark resolved — exist only as buttons above the 
  - Fix: First make the two actions take an explicit set: change `TakeAsync` to `async Task TakeAsync(bool ours, string label, List<string>? paths = null)` and open it with `paths ??= Picked();`, and extract the body of `MarkResolved_Click` into `async Task MarkResolvedAsync(List<string> paths)`, leaving the handler as `Busy.During(sender, () => M
- **:100** (interactivity) Keep SVN and Keep branch overwrite files on disk with no confirmation, while the harmless Abort asks
  - Git.TakeSide (Sg.Core/Git.cs:665) runs `git checkout --ours/--theirs` then `git add`, which rewrites the working file and destroys any hand-editing already done between the conflict markers — irrecoverably, short of aborting the whole rebase. One click, no dialog, and via a folder checkbox it can hi
  - Fix: Confirm before the busy wrapper, the way Abort_Click already does: change TakeAsync to take the paths and a title (`async Task TakeAsync(List<string> paths, bool ours, string label)`, dropping its own Picked() call) and rewrite the handlers as `async void TakeSvn_Click(object sender, RoutedEventArgs e) => await TakeClick(sender, true, "Ke
- **:112** (interactivity) Edit does nothing at all, silently, when nothing is ticked or selected
  - With no tick and no selection, Picked() (line 82) returns an empty list and the loop simply does not execute: the button visibly depresses and nothing happens, with no dialog, no log line and no hint about what was missing. Its two immediate neighbours on the same StackPanel both handle it — TakeAsy
  - Fix: Make the handler async in the same call, not in a separate finding: change line 112 to `async void OpenFile_Click(object sender, RoutedEventArgs e)`, then make the guard the first statement -- `var paths = Picked(); if (paths.Count == 0) { await Dialogs.Info(this, "Nothing picked", "Tick one or more files first, or select one."); return; 

### Controls.cs

- **:59** (interactivity) The pane splitter has no resize cursor, no name and no tooltip, so a 4 px line is its only affordance
  - Attach wires DragDelta and nothing else, and the Splitter style (Templates.xaml:124-129) sets only Width, Margin, BorderThickness and Background. Grepping the app for ProtectedCursor/InputSystemCursor returns nothing, so the pointer never turns into a west-east resize arrow over any of the ten split
  - Fix: Keep the wrapper, but teach Attach to climb to it first, and land the Attach change before the XAML change so nothing breaks in between. In Controls.cs add `public sealed class SplitterGrip : ContentControl { public SplitterGrip() { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Siz

### Dialogs.cs

- **:136** (interactivity) Neither code-built input dialog moves focus into its first field
  - NewBranch (line 136) and ServerCheckout (line 161) both open with focus on whatever ContentDialog picks -- in practice the Create button, because DefaultButton is Primary -- so the user has to click or Shift+Tab into the name box before typing. The sibling MessageDialog.cs:54 does it properly: `Open
  - Fix: In NewBranch, between the ContentDialog initializer (ends line 144) and `await d.ShowAsync()` (line 145), add `d.Opened += (_, _) => name.Focus(FocusState.Programmatic);`. In ServerCheckout, between line 169 and line 170, add `d.Opened += (_, _) => target.Focus(FocusState.Programmatic);` — the first field there is `target` (line 153), not
- **:171** (interactivity) The server checkout dialog has the same always-enabled Create button and the same silent no-op
  - Same defect as NewBranch: DefaultButton is Primary (line 168) and PrimaryButtonText is "Create" with no gate, so Enter on an empty 'Server branch name' box closes the dialog and returns null. The user sees the dialog disappear and no checkout appear, with no explanation.
  - Fix: Add `IsPrimaryButtonEnabled = false,` to the ContentDialog initializer (Dialogs.cs:161-169), then insert the gate after line 169, before `ShowAsync()`, since the lambdas capture `d`: `void Gate() => d.IsPrimaryButtonEnabled = target.Text.Trim().Length > 0 && near.SelectedItem is string; target.TextChanged += (_, _) => Gate(); near.Selecti

### DiffView.xaml.cs

- **:48** (stale-state) The diff keeps its boot-time light/dark theme when Windows switches theme
  - Assets/monaco.html reads the theme once at startup (`var dark = window.matchMedia('(prefers-color-scheme: dark)').matches;`) and picks `sg-dark`/`sg-light` from it, and DiffView subscribes to nothing. Switch Windows to light mode with a commit page open and the whole app repaints — Controls.cs:292 a
  - Fix: Same idea, two corrections. (1) C#: SendTheme must copy the guard the sibling senders use - SendLayout (DiffView.xaml.cs:391) and SendActions (line 370) both open with `if (!_ready) return;`. Write `void SendTheme() { if (!_ready) return; Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { command = "theme", dark = Actual
- **:66** (animation) The WebView2 has no default background colour, so a white rectangle flashes on every navigation to a diff page in dark mode
  - WebView2 paints white until the document loads and monaco.html's `document.body.style.background = dark ? '#1e1e1e' : '#ffffff'` runs. Because NavHost drops and remakes pages on every visit, that white flash across the whole right-hand pane happens each time the user opens Commit, Log, Push or Monit
  - Fix: Set the WebView2 to the colour Monaco itself will paint, not the card colour, so there is no second step when the document loads. In OnLoaded, immediately before `await Web.EnsureCoreWebView2Async();` (DiffView.xaml.cs:66), add: `Web.DefaultBackgroundColor = ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 0x1e, 0x1e, 0x1
- **:90** (stale-state) The header's timing is recomputed off a stopwatch that is never stopped, so a later toggle prints a nonsense duration
  - `_clock` is only ever Restarted, never Stopped, and Monaco posts "shown" again from `onDidUpdateDiff` every time the diff is recomputed — which is what WhitespaceToggle_Click and CollapsedToggle_Click cause via SendLayout's `ignoreTrimWhitespace` / `hideUnchangedRegions`. Press "Ignore space" a minu
  - Fix: Do not touch the stopwatch. Add a one-shot flag: declare `bool _timed;` beside `long _readMs;` (line 40), set `_timed = false;` in Present() right after `_readMs = _clock.ElapsedMilliseconds;` (line 247), and open the "shown" branch with `if (_timed) return; _timed = true;` before line 90. That reports exactly the first draw after each Pr
- **:322** (interactivity) Action buttons stay live and show no busy state while their git/svn command runs
  - `ActionInvoked` is a fire-and-forget `Action<string>`, so the button is never disabled and never gets the spinner every other button in the app gets. Pressing Stage twice while the first `git apply` is still running fires it twice — the second run fails with a raw "patch does not apply" in the statu
  - Fix: Guard at the single choke point in DiffView, not at the button, so the Monaco context menu is covered too. Add `public event Func<string, Task>? ActionInvokedAsync;` and a `bool _acting;` field, plus one router: `async void Invoke(string id) { if (_acting) return; _acting = true; try { _buttons.TryGetValue(id, out var b); await Busy.Durin

### EditCheckoutPage.xaml

- **:17** (stale-state) The rename box is unvalidated, so a taken or empty name fails only after the write is attempted
  - Save sends `NameBox.Text.Trim()` straight into Ops.CheckoutEdit (EditCheckoutPage.xaml.cs:131) with no check that the name is non-empty, unique in the root, or a legal git ref — and the page's own header text says a rename moves the snapshot ref svn/<name> and re-points every branch born from it (Ed
  - Fix: Fix the identity lookup first: drop `readonly` from `_name` (EditCheckoutPage.xaml.cs:31), make CheckoutConfig() resolve only by `_name` (delete the `NameBox.Text.Trim()` predicate at line 61), and set `_name = NameBox.Text.Trim();` after a successful save (before line 142) so a rename is still followed — that alone routes a taken name in

### EditCheckoutPage.xaml.cs

- **:70** (stall) The externals list reads svn with no skeleton, no bar and no empty state — the card is a blank box
  - Runner.Quiet deliberately shows nothing in the StatusStrip (no Begin/Arm, so no progress bar and no Cancel — Session.cs:203-208), and NoExternals starts Collapsed (EditCheckoutPage.xaml:65), so from page open until svn answers the Externals card is an empty rectangle that looks like a checkout with 
  - Fix: Add `<local:Skeleton x:Name="ExternalsSkeleton" RowCount="3" Badges="False" Visibility="Collapsed" />` to the Grid at EditCheckoutPage.xaml:52-67, beside NoExternals. In LoadExternalsAsync, after the root/co null guards, call `if (Externals.Items.Count == 0) ExternalsSkeleton.Show();` before the await on line 70 and `ExternalsSkeleton.Hid
- **:168** (stale-state) 'Back to declared' spins the Switch button and leaves itself pressable during its own run
  - SwitchTo is reached from both Switch_Click (line 147) and Revert_Click (line 149-152) but always marks SwitchButton busy. Press "Back to declared" and the ring appears on the wrong button while RevertButton stays enabled and focused, so a second Enter or click starts a second svn switch on the same 
  - Fix: Do not add a `Control button` parameter — follow the sibling pattern and wrap at the handler with `sender`: leave `SwitchTo(string url)` free of Busy and write `async void Switch_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => SwitchTo(BranchBox.Text.Trim()));` plus the same for Revert_Click, as ConflictPage.xam

### ListFilter.cs

- **:208** (stale-state) Every filter keystroke clears the selected file even when it is still in the filtered list
  - Apply() nulls `_picked` and then hands the ListView a brand new collection (`_lines = new ObservableCollection<TreeNode>(...); _list.ItemsSource = _lines;` at lines 216-217), which clears SelectedItem. Type one letter into the filter and the file you were reading is deselected and `HasPick` flips to
  - Fix: In Apply(), before the Detach loop capture `var keep = _picked?.Row;`, and after line 217 restore only when there is a row to restore: `if (keep != null && Walk(_roots).FirstOrDefault(n => ReferenceEquals(n.Row, keep)) is { } back) { _picked = back; _list.SelectedItem = back; _list.ScrollIntoView(back); }` — assigning `_picked` before Sel

### LogPage.xaml

- **:42** (design) The line that explains why Squash is off is the first thing to vanish when the pane is narrowed.
  - PickHint carries the only reason the disabled buttons give — "A snapshot of SVN is picked. Those cannot be rewritten.", "They have to sit next to each other." — and it sits fourth in a non-wrapping horizontal StackPanel. Squash/Reword/Revert take roughly 250 px, and the left column is draggable down
  - Fix: Change line 31 to `<local:WrapRow Grid.Row="1" Spacing="6">` (closing `</local:WrapRow>` at line 44) — WrapRow (Controls.cs:85) measures each child against the real available width, so at 320 px the hint drops to a second line instead of being clipped, and the Grid row is already Auto so it grows. On PickHint, delete both `VerticalAlignme

### LogPage.xaml.cs

- **:24** (interactivity) No F5 and no Reload button: the log cannot be refreshed at all once it is open.
  - The constructor binds F7 for diff navigation and stops. A commit made in another window, a sync that lands a new snapshot, a rewrite done from the command line — none of it reaches this page, and there is no gesture that asks for the list again. F5 is bound on BlamePage.xaml.cs:39, CommitPage.xaml.c
  - Fix: The F5 half is right: add `Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync(_currentSha.Length > 0 ? _currentSha : null));` after LogPage.xaml.cs:24 (plus `using Windows.System;`) — passing the current sha matches what RewriteAsync already does at line 286, and LoadAsync clears `_currentSha` at line 66 so the re-selection re-raises 
- **:47** (animation) The commits skeleton is shown unconditionally, so a reload pulses grey bars on top of the rows that are still there.
  - Skeleton has no background and IsHitTestVisible="False" (Skeleton.xaml:5,17) and pulses its opacity 0.35→1.0 forever, so on the reload after a squash or a reword the bars sit over the old commit rows and both are legible at once — it reads as a rendering fault, not as loading. Every sibling guards i
  - Fix: Change line 47 to `if (Commits.ItemsSource == null) CommitsSkeleton.Show();`.
- **:190** (stall) The whole commit patch is split and counted on the UI thread, right after it was read off it.
  - DiffStats.Parse (Sg.Core/DiffStats.cs:29) does `unified.Split('\n')` and allocates a string per line. The patch handed to it is git.CommitPatch of the selected row, and on this page a selectable row can be a snapshot of SVN — the diff of an entire sync across the monorepo, tens of megabytes. That sp
  - Fix: Move the parse into the background read: return the stats alongside details/files/patch from the Task.Run (or Runner.Quiet) at line 176, and pass the finished dictionary to SetStats here.

### MainWindow.xaml.cs

- **:394** (animation) The skeleton is not depth-counted the way the busy bar is, so overlapping refreshes hide it early
  - CheckoutPage.SetBusy counts depth (`_busyDepth += on ? 1 : -1;`) precisely because RefreshAsync overlaps itself — it is called from F5, from CheckoutPage.OnShown(returning: true), from SyncAsync, NewBranchAsync, ServerCheckoutAsync and OpenRootAsync. ShowSkeleton is a plain visibility toggle, so on 
  - Fix: Leave ShowSkeleton as a plain toggle and make only the winning refresh clear it: in RefreshAsync's finally (MainWindow.xaml.cs:400) change `if (first) Overview.ShowSkeleton(false);` to `if (first && generation == _generation) Overview.ShowSkeleton(false);`, hoisting the generation out of RefreshCoreAsync (e.g. `var generation = ++_generat
- **:443** (stale-state) The fast refresh path never re-applies the pane badges, so cleared counts keep showing the old numbers
  - The rebuild path calls ApplyRemoteBadge/ApplyLocalBadge from the dictionaries (lines 493-494); this path does not, and relies entirely on CheckRemotesAsync landing. So after RefreshAllAsync clears `Remote` (line 386) or after ShowSvnChanges' Left callback removes a LocalEdits entry (line 596), the p
  - Fix: Inside this loop add `ApplyRemoteBadge(row!, Remote.GetValueOrDefault(c.Name)); ApplyLocalBadge(row!, LocalEdits.TryGetValue(c.Name, out var known) ? known : null);` so the badges follow the dictionaries the same way the rebuild path does.

### MergePage.xaml.cs

- **:56** (stale-state) Refresh and F5 silently reset the Into and From choices back to the first entry
  - The button's own tooltip is "Read the branches and their revisions again", so the user presses it to refresh what they are looking at. Instead the target snaps back to the root, the source snaps back to trunk, the revision list is rebuilt for a branch they did not ask about, and their pick is gone. 
  - Fix: Capture inside each loader, not both in `LoadTargetsAsync` — `src` captured there is a local that `LoadSourcesAsync` cannot see, and `LoadSourcesAsync` is also reached from `Target_Changed` (`:226`). In `LoadTargetsAsync`, before line 55: `var keep = Target;` then `TargetBox.SelectedIndex = _targets.Count == 0 ? -1 : Math.Max(0, keep == n
- **:103** (animation) The skeleton is shown on every reload, pulsing on top of the rows that are already there
  - Skeleton sits in the same Grid cell as the list (MergePage.xaml lines 41-45) and only overlays it — it does not clear the rows. Every source-box change and every post-merge reload at line 307 therefore paints eight pulsing grey bars over the revisions still on screen, then flicks back. For a reload 
  - Fix: Don't hide the skeleton — clear what it is standing on, so it stands in for nothing instead of over something. In `LoadRevisionsAsync`, immediately before `RevisionsSkeleton.Show();` at MergePage.xaml.cs:105, add `_binding = true; Revisions.ItemsSource = null; _binding = false; _rows = new List<SvnRevRow>();` (the `_binding` guard around 
- **:112** (stall) Merge.Problems is run once per pair in sequence while the line above it fans out
  - Every Merge.Problems call does an svn InfoUrl against the server plus an svn status of that working copy. A merge of the checkout root pairs the root with every external, so on this monorepo that is a run of server round trips one after another, all inside the same load the user is waiting on. The c
  - Fix: Replace with `Problems = Fan.Map(pairs, p => Merge.Problems(root, _co, p.Target, p.SourceUrl)).SelectMany(x => x).Distinct().ToList(),` (Fan.Map preserves input order, so the message order stays stable).
- **:115** (stall) A stale load hides the skeleton belonging to the load that is still running
  - The skeleton is hidden before the generation check. Change the From branch twice quickly: the first read returns, hides the skeleton, then drops its result — and the second read carries on with no skeleton at all, leaving an empty card that looks like a branch with no revisions until it finishes. Se
  - Fix: Split the guard so only the stale case returns early, in MergePage.xaml.cs lines 117-119: replace with `if (gen != _generation) return;` (a newer read owns the skeleton and the buttons — touch nothing), then `RevisionsSkeleton.Hide(); _reading = false;`, then `if (read == null) { SyncButtons(); return; }`. That keeps the skeleton and _rea
- **:145** (design) The whole right-hand half of the page is a blank diff with no hint until Test merge is pressed
  - The load path never touches Diff. The only Diff.ShowText on the page is line 80, on the no-sources dead end. So after a normal load the biggest panel on screen — the pane titled "What a merge would do" — is an empty Monaco view with an empty title bar, and nothing tells the user that Test merge is w
  - Fix: Give LoadRevisionsAsync a `bool keepDiff = false` parameter and pass `keepDiff: true` from the post-merge reload at MergePage.xaml.cs:315; then after the `ShowRevisions();` at line 148 add `if (!keepDiff) { OutcomeHeader.Text = "What a merge would do"; Diff.ShowText("", "press Test merge to see what it would change"); }`. Put it at the en

### Monitor.cs

- **:99** (stall) A repository that failed its check is re-read every 60 seconds forever, ignoring its interval.
  - On failure the catch sets item.Error but leaves item.Recent empty, so `i.Recent.Count == 0` makes that item due on every single timer tick. One unreachable URL therefore launches two svn processes a minute for the life of the app and fires two Raise() calls each time, which rebuilds the monitor tree
  - Fix: Keep the startup-repopulation behaviour and make it session-scoped instead of inferring it from an empty list. Add `[JsonIgnore] public DateTime? CheckedThisRun { get; set; }` to MonitorItem (beside Monitor.cs:29), set it alongside the persisted stamp at Monitor.cs:160 — `item.CheckedThisRun = item.LastChecked = DateTime.UtcNow;` — and re

### MonitorPage.xaml.cs

- **:120** (interactivity) A toast selects a repository in the tree but never scrolls it into view.
  - This is the path a toast press takes (MainWindow.xaml.cs:219-224 with the id from the toast arguments). Every external of a checkout is one row, so the named repository is routinely below the fold; the user presses the toast, the right-hand pane changes to a repository they cannot see in the tree, a
  - Fix: The proposed fix is right; it only needs one addition to also cover the page-creation path. In SelectById capture the node: `var node = nodes.FirstOrDefault(n => n.Item?.Id == id); Tree.SelectedItem = node; if (node != null) Tree.ScrollIntoView(node);`. Because the Loaded handler (line 66-72) calls SelectById in the same pass that Rebuild
- **:323** (interactivity) The Add/Edit repository dialog opens with focus on the button, not on the Name box.
  - The ContentDialog is built with DefaultButton = Primary (line 344) and nothing ever focuses a field, so "Watch a repository" opens with the Add button focused: the user types and nothing happens until they Tab into the form. Both sibling dialogs in this app do focus the first field on open — `Opened
  - Fix: Add `d.Opened += (_, _) => name.Focus(FocusState.Programmatic);` before `await d.ShowAsync()`.

### NewRootPage.xaml.cs

- **:20** (interactivity) No form page puts focus in its first field when it opens
  - The page opens with focus nowhere, so a keyboard user must Tab past the header and the breadcrumb to reach RootBox, the one field the page is about; the same is true of AddCheckoutPage (Fields' folder box), EditCheckoutPage (NameBox) and ServerBranchPage (NameBox). The app already does this the othe
  - Fix: Focus once, on the right control, and stop the focused box from eating Escape. (a) In NewRootPage, EditCheckoutPage and ServerBranchPage add a guarded handler — `bool _focused; Loaded += (_, _) => { if (_focused) return; _focused = true; RootBox.Focus(FocusState.Programmatic); };` (NameBox on the other two; fold it into ServerBranchPage's

### OutputWindow.xaml.cs

- **:51** (interactivity) Every arriving log line wipes the text the user selected in the log window
  - Assigning TextBox.Text collapses the selection. Because Fill runs on every appended line (see OnChanged above), a user cannot select a failing git command out of the log while anything is still running — the highlight disappears the instant the next line lands, so copying a message out of a live ope
  - Fix: In Fill(), keep the previous text in a field and only restore the selection when the update is provably a pure append, then stop the follow jump from undoing it. Concretely: `var start = LogText.SelectionStart; var len = LogText.SelectionLength; var appended = text.Length >= _prev.Length && text.StartsWith(_prev, StringComparison.Ordinal)
- **:53** (stale-state) Follow sets the caret but never scrolls, so the newest line does not come into view
  - A TextBox only brings its caret into view when it has focus. LogText is IsReadOnly and is the last tab stop in the grid (OutputWindow.xaml:28, after FollowToggle and Clear), so on an opened log window focus sits on the Follow toggle, not in the box — setting SelectionStart moves an invisible caret a
  - Fix: In OutputWindow.xaml.cs, resolve the TextBox's template ScrollViewer lazily (never cache a null) and force layout before scrolling. Add a `ScrollViewer? _scroller;` field and a small `static IEnumerable<DependencyObject> Descendants(DependencyObject)` walk like the one already in MainWindow.xaml.cs:93-102, then make Fill(): `LogText.Text 

### PageWindow.xaml.cs

- **:39** (interactivity) A page window shows a back button but no forward affordance, though Alt+Right works
  - Sync mirrors only CanGoBack. NavHost binds Alt+Right and the second mouse thumb button in every host (Navigation.cs:205, 216), so forward navigation exists in a PageWindow, but nothing on screen says so: a user who backs out of a diff by mistake has no way to discover the way forward. The sibling ho
  - Fix: Do not use RightHeader — WindowHelper.cs:79 overwrites it with the Log button after InitializeComponent. Put the button in the unused TitleBar.LeftHeader instead: in PageWindow.xaml add &lt;TitleBar.LeftHeader&gt;&lt;Button x:Name="ForwardButton" Visibility="Collapsed" Click="Forward_Click" Style="{StaticResource QuietButton}" VerticalAli

### PushPage.xaml

- **:110** (design) The page that puts changes on the shelf offers no way to reach the shelf
  - The collision check's "Shelve them" button creates a shelf from this page, and the result then tells the reader to go and find it: "Push now, then put them back from Shelved changes on the checkout card." (PushPage.xaml.cs:382-383). Both siblings carry the destination on the page itself — CommitPage
  - Fix: In PushPage.xaml, widen the action row at line 107 to `ColumnDefinitions="Auto,Auto,Auto,*,Auto"`, insert `<local:IconButton Grid.Column="2" x:Name="ShelfButton" Glyph="&#xE7B8;" Text="Shelved changes" Click="Shelf_Click" ToolTipService.ToolTip="What is already put aside from this checkout, and the button that writes it back." />` after t

### PushPage.xaml.cs

- **:40** (interactivity) No F5 refresh accelerator, though every sibling page has one
  - CommitPage.xaml.cs:47 and SvnCommitPage.xaml.cs:40 both register `Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync(_shownPath));`, and ShelfPage.xaml.cs:46 does too; CommitPage's Refresh tooltip even advertises it ("Read the changes in the worktree again. F5 does the same."). On the push page 
  - Fix: Add `Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync());` beside the Ctrl+Enter registration and say "F5 does the same." in both Refresh tooltips.
- **:114** (stale-state) A reload always throws away the file the user was reading and re-reads the whole-branch patch
  - ListFilter.SetItems rebuilds the tree and clears the selection (`_picked = null;`, ListFilter.cs:208), so after any reload `_filter.HasPick` is false. The guard at line 144 (`if (_filter.HasPick) return;`) therefore never fires and every Refresh, every Fix, and every commit-boundary change drops the
  - Fix: Adopt the sibling pattern, but include the reset the proposal omits. Add `string? _shownPath;`; set it only after a successful file read at the end of OnPicked (after the `await Diff.ShowFileAsync(...)` on PushPage.xaml.cs:195-199, guarded by `_filter.IsCurrent(node)`), and set `_shownPath = null;` at the top of the folder branch (PushPag
- **:399** (stale-state) The result bar keeps the action button from the previous operation
  - Apply_Click sets `ResultBar.ActionButton = ok ? ChangesButton() : null;` (line 299), but PushAsync (lines 399-421) and ShelveCollisionsAsync (lines 381-384) set Severity, Message and IsOpen without touching ActionButton. So after an apply, a later push shows "Pushed. <branch> now equals svn/<checkou
  - Fix: Clear it wherever the bar is reset, not only in the two methods named. Add `ResultBar.ActionButton = null;` beside BOTH `ResultBar.IsOpen = false;` lines — line 281 in Apply_Click (this covers the `r == null` early return at 284-291 that the proposed fix omits, while line 299 still assigns explicitly on the normal path) and line 399 in Pu

### Rows.cs

- **:11** (stale-state) Status, repo and blame brushes are snapshotted from the current theme and never repaint
  - These keys live in ThemeDictionaries (Templates.xaml:8-110), so the indexer returns whichever theme's Brush object was current at the moment of the call, and Templates.xaml binds the result with no Mode — `Background="{x:Bind CodeBackground}"` (line 166), `Foreground="{x:Bind CodeBrush}"` (line 168)
  - Fix: Keep the brush-valued properties but stop handing out the dictionary's brush objects: give StatusColors (Rows.cs:11-12) and the Res helpers (Rows.cs:508, 531-532, 660-661) a small cache of app-owned SolidColorBrush instances keyed by resource name, and on one ActualThemeChanged subscribed on the window's root FrameworkElement re-assign ea

### ServerBranchPage.xaml.cs

- **:188** (design) Dry run is enabled with an empty form and answers by typing a sentence into the read-only Plan box
  - ShowPlan writes into Plan, a read-only monospace TextBox inside PlanCard (ServerBranchPage.xaml:71-74), so a validation error about the field at the top of the page appears as if it were server output at the right of the page — and it replaces the NoPlan empty state, which already says the same thin
  - Fix: Give the Dry run IconButton `x:Name="DryRunButton" IsEnabled="False"` in ServerBranchPage.xaml:17-18 (empty NameBox on load means disabled is the correct initial state), and extend its existing tooltip with "Needs a branch name and a checkout." — matching CreateButton beside it (ServerBranchPage.xaml:20, "Needs a dry run first."); do not 
- **:204** (stall) The whole form stays live while the branch is being created on the server
  - Busy.During disables only CreateButton. During a run that makes one revision per repository and then checks a copy out — minutes — NameBox, FromBox, MessageBox, the Dry run button and every per-external toggle stay live. Changing FromBox mid-run calls From_SelectionChanged -> LoadPartsAsync (line 13
  - Fix: In ServerBranchPage.xaml add `x:Name="DryRunButton"` to the Dry run IconButton (it currently has none). In Create_Click, capture the checkbox up front — `var noCheckout = NoCheckout.IsChecked == true;` before the confirm — and use that local at line 209 instead of reading the live control after the await; then set `NameBox.IsEnabled = Fro

### SettingsPage.xaml.cs

- **:99** (stale-state) The Start-with-Windows switch keeps showing the state the registry refused to take
  - Startup.IsOn is read from the registry only once, in the constructor (line 32, Shell.IsStartup()). When SetStartup throws, the message goes to Status but the switch stays where the user put it, so the page shows 'on' for a Run entry that does not exist - and every later Save() retries the same faili
  - Fix: Re-read the registry in the catch, but guard the read so it cannot throw out of a synchronous event handler: replace line 99 with `catch (Exception ex) { Status.Text = "startup entry failed: " + ex.Message; _filling = true; try { Startup.IsOn = Shell.IsStartup(); } catch { } _filling = false; return; }`. The inner try matters because Shel

### ShelfActions.cs

- **:32** (design) Files a shelve left behind are reported only to the log window while the page says success
  - StatusStrip.Append writes to LogStore only (StatusStrip.xaml.cs:37) - it does not even reach the strip, let alone the page. All three callers then post a plain Success InfoBar that never mentions the leftovers: CommitPage.xaml.cs:355-357, SvnCommitPage.xaml.cs:363-365, PushPage.xaml.cs:379. So a she
  - Fix: Apply the change to SvnCommitPage.xaml.cs:363-365 and PushPage.xaml.cs:381-384 only — NOT CommitPage.xaml.cs:355-357, because CommitPage passes `_worktree`, which routes Shelf.Save (Shelf.cs:152-158) to FromWorktree, and FromWorktree returns `new ShelfSaveResult(info, new List<string>())` at Shelf.cs:247, so its LeftBehind can never be no

### ShelfPage.xaml

- **:34** (interactivity) The action row has neither Refresh nor Close, which every sibling page carries
  - The shelf list is the one list in the app another window changes behind your back (any Shelve on CommitPage, SvnCommitPage or PushPage adds to it), yet the only way to reread it is the invisible F5 bound at ShelfPage.xaml.cs:46. CommitPage.xaml:74 and PushPage.xaml:108 both put a Refresh IconButton 
  - Fix: Leave the StackPanel at line 34 alone and copy the family's footer instead. Give `Filled` (line 22) `RowDefinitions="*,Auto"`, move its existing three-column content into `Grid.Row="0"`, and add at `Grid.Row="1"` a footer mirroring SvnCommitPage.xaml:69-72: `<Grid ColumnDefinitions="Auto,Auto,*" ColumnSpacing="8" Margin="0,4,0,0"><local:I

### ShelfPage.xaml.cs

- **:69** (stall) Both skeletons are hidden before the staleness check, so a stale read blanks the newer read's feedback
  - Press F5 while the first load is still running, or click a second shelf while the first is still reading: the older read returns, hides the skeleton, and only then discovers it is stale. The newer read is still in flight but its skeleton is gone, so the card sits empty and finished-looking until it 
  - Fix: Split the two conditions instead of swapping the lines, and cover the null-pick path. In LoadAsync replace lines 69-70 with `if (gen != _generation) return;   // a newer read owns the skeleton now` / `ShelvesSkeleton.Hide();` / `if (all == null) return;`, and in ShowPicked replace lines 108-109 with `if (gen != _pick) return;` / `FilesSke
- **:106** (animation) The files skeleton is shown unconditionally, so grey bars pulse on top of the previous shelf's rows
  - FilesSkeleton sits in the same Grid cell as the Files tree (ShelfPage.xaml:66-71) with no background of its own, so switching from one shelf to another paints pulsing placeholder bars over the file names that are still listed - two lists drawn on top of each other. The same method guards the other s
  - Fix: Make it the one-line sibling guard and nothing else: change ShelfPage.xaml.cs:106 to `if (_filter.Count == 0) FilesSkeleton.Show();`, matching LogPage.xaml.cs:175. Do not add `_filter.Clear("Files")` before the read - the previous shelf's rows should stay until line 110 replaces them, exactly as LogPage keeps the previous commit's files, 
- **:119** (stall) Picking a shelf runs two independent git processes one after the other
  - Shelf.Changes is `git diff --name-status` and Shelf.PatchText is `git diff` over the same two commits (Sg.Core/Shelf.cs:134-139); neither needs the other's answer, yet the patch only starts once the name-status has come back, so the wait for a diff is the sum of the two. CommitPage runs exactly this
  - Fix: Do not merge the two awaits. Start the patch before awaiting the name-status and await the already-running task where line 119 is now: after `var root = Session.Require();` (:105) add `var patchTask = Task.Run(() => Shelf.PatchText(root, shelf));`, leave lines 106-118 untouched so the tree still fills and the skeleton still hides on the c

### Skeleton.xaml.cs

- **:59** (stale-state) The skeleton caches its brush at build time and never repaints on a theme change
  - Build() runs from the constructor and from the RowCount/Badges property callbacks only. SkeletonBrush has three different values per theme (Templates.xaml:24, :58, :92 -- #33FFFFFF for dark, #26000000 for light, SystemColorGrayTextColor for high contrast), so after the user switches theme every skel
  - Fix: Add `ActualThemeChanged += (_, _) => Build();` to the constructor beside the existing Loaded/Unloaded hooks, matching StatusChip.xaml.cs:72.

### StatusStrip.xaml.cs

- **:87** (animation) The status strip toggles the progress bar's Visibility, so the whole page resizes every time an operation starts and ends
  - Bar sits in Grid.Row 1 of the strip with Margin="0,6,0,0" (StatusStrip.xaml:19), and the strip itself is the Auto row of every page's `RowDefinitions="Auto,*,Auto"` (e.g. CommitPage.xaml:6 and :96). Collapsed the row is 0 high, Visible it is ~10 px, so the content area above shrinks and grows on eve
  - Fix: In StatusStrip.xaml:19 drop `Visibility="Collapsed"` and add `Style="{StaticResource LoadingBar}"` so Bar keeps its 4 px + 6 px margin permanently at Opacity 0, exactly as DiffView.xaml:41 does. Then replace all five Visibility writes in StatusStrip.xaml.cs — lines 41, 59, 87, 95 and 103 — with one private helper `void ShowBar(bool on) { 

### SvnLogPage.xaml.cs

- **:101** (stale-state) Reload re-reads the server but never re-reads the working copies, so the marks and the "newer on the server" line stay frozen
  - `_wcs` — and with it every `WcRevision` and `SnapshotRevision` — is read exactly once, in InitAsync at page construction. Reload only re-runs LoadAsync, so every derived piece of state is computed against stale numbers: `Mark` (line 138) still paints revisions as Behind, `pending`/`behind` still cla
  - Fix: Two edits in SvnLogPage.xaml.cs. First, change line 53 from `RevisionsSkeleton.Show();` to the same guarded form LoadAsync uses — `if (Revisions.ItemsSource == null) RevisionsSkeleton.Show();` — so a re-run refreshes in place instead of flashing the skeleton over live rows (the first run still shows it, since ItemsSource is null then). Th
- **:110** (stall) Two LoadAsync runs can be in flight at once and both write the list; the reference guard catches nothing
  - `_wcs` is only ever assigned in InitAsync (line 73), so this guard never fires for a second Reload. Busy.During disables only the button that was pressed — and there are two Reload buttons wired to the same handler (SvnLogPage.xaml:15 and :25) plus the constructor's own unattributed InitAsync run. R
  - Fix: The generation approach is right; only the placement needs adjusting. Add `int _generation;` beside `_wcs`, and take the ticket *after* the existing empty check so a click made while InitAsync is still reading (when `_wcs` is still empty) does not bump it: `if (_wcs.Count == 0) return; var mine = ++_generation;` at lines 101-102. Then rep
- **:188** (stale-state) The stale-result guard compares only the revision number, on the one page that merges several repositories into one list
  - This page deliberately interleaves the root and every external — separate SVN repositories with independent revision numbering (line 143-145). r4210 in the root and r4210 in an external are different commits, so a slow diff for the first can land after the user has clicked the second, pass the guard
  - Fix: Fix line 188 only, and fix it by identity rather than by adding a second comparison: change `if (_rev?.Revision != rev) return;` to `if (!ReferenceEquals(_rev, row.Entry)) return;` — row.Entry is unique per row, so one check covers both the revision and the working copy it came from (equivalently, capture `var wc = _wc;` beside `var url =

## Low (16)

### CheckoutFields.xaml.cs

- **:112** (stale-state) The three folder pickers are refilled by three unawaited walks that can land out of order
  - OfferFoldersOf walks two directory levels on a pool thread and then assigns Candidates.ItemsSource (PathPicker.xaml.cs:56-60) with no check that the folder is still the one being offered. Typing through two paths that both exist — D:\work then D:\workspace — starts two walks; if the first finishes la
  - Fix: Fix it inside PathPicker so it is self-contained and covers all three pickers at once. In src/Sg.App/PathPicker.xaml.cs add a field `string _offering = "";` and change OfferFoldersOf (lines 56-60) to: `_offering = checkoutPath; var found = await Task.Run(() => Folders(checkoutPath)); if (!string.Equals(checkoutPath

### CheckoutPage.xaml.cs

- **:131** (interactivity) Expand(branch) opens a card that may be off screen and never scrolls to it
  - This is what the branch crumb calls (MainWindow.xaml.cs:121, `ShowOverview(co); Overview.Expand(br);`). With several worktrees the named card is often below the fold, so clicking the branch in the breadcrumb appears to do nothing: the page does not move, and the card that opened is out of view.
  - Fix: In CheckoutPage.xaml.cs `Expand` (lines 127-132), keep `row.IsExpanded = true;` and queue the reveal at Low priority — the codebase's own post-layout convention (DiffView.xaml.cs:52) — with an index fallback and the app's motion switch: `if (row == null) return; row.IsExpanded = true; DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.Di

### CommitPage.xaml.cs

- **:136** (stale-state) A load that lost the race hides the skeleton the newer load is still showing
  - Hide() runs before the generation check. Open the page and press F5 (or Refresh) before the first status walk returns: the first read comes back, hides the skeleton and then drops its data, and the second read runs on with an empty card and no sign that anything is loading — exactly the "empty card 
  - Fix: Reorder to `if (gen != _generation) return; FilesSkeleton.Hide(); if (data == null) return;`.
- **:143** (interactivity) Amend is disabled with nothing on screen saying why
  - On a branch whose HEAD has no parent the Amend checkbox goes grey and the empty state's "Reword the last commit" button vanishes (144), while the checkbox's tooltip still describes what amending does rather than why it is off — and WinUI does not surface tooltips on disabled controls anyway. The use
  - Fix: Use the app's own wrapper pattern from MainWindow, not Summary. In CommitPage.xaml wrap the Amend CheckBox (line 79) in `<Border x:Name="AmendTip" Grid.Column="3" Background="Transparent">` (move `Grid.Column="3"` off the CheckBox onto the Border) and move the existing tooltip from the CheckBox to the Border. Then beside line 143 add: `To

### ConflictPage.xaml

- **:36** (design) The filter tooltip drops the sibling pages' warning that filtering does not change what is ticked
  - Picked() acts on `_filter.Rows<FileRow>()`, which is every row the page loaded, not the filtered subset — so a filter narrows what you can see but not what "Keep SVN" will overwrite. Both sibling pages that carry this exact hazard spell it out in the tooltip: CommitPage.xaml:41 ends "It changes the 
  - Fix: Edit the ToolTipService.ToolTip on ConflictPage.xaml:37 to keep the siblings' exact shape and speak only about ticks, which genuinely do survive Apply(): "Show only the rows whose path contains this text. It changes the list, never what is ticked: a ticked file the filter hides is still one that Keep SVN or Keep branch overwrites." Do not

### FileActions.cs

- **:136** (design) A failed open or clipboard copy is reported only to the log window, so the click looks like it did nothing
  - Same at line 84 for CopyText. A file with no registered handler, a folder on a disconnected share, or a locked clipboard produces a context-menu click that visibly does nothing while the reason sits in a window the user has not opened. The app's own pattern for a failure is StatusStrip.Error (Status
  - Fix: Do not touch Attach's signature. In `Start` (FileActions.cs:133-137) and `CopyText` (78-85), keep the `Session.Log.Warn(...)` line and add, right after it, `OutputWindow.Show("cannot open " + path + ": " + ex.Message.Split('\n')[0]);` (and `OutputWindow.Show("cannot copy to the clipboard: " + ex.Message.Split('\n')[0]);` respectively) — t

### MainWindow.xaml

- **:116** (stale-state) Breadcrumb colours are snapshotted from app resources and bound OneTime, so a theme switch leaves them wrong
  - `Application.Current.Resources[...]` hands back the brush object for whichever theme was active at the moment the Crumb was built, and x:Bind defaults to OneTime so it is never re-read. Switch Windows between light and dark while sg is open and the path over the page keeps the old theme's foreground
  - Fix: Keep the two named brushes — do not substitute a hardcoded opacity, which would make the breadcrumb the only secondary text in the app not using TextFillColorSecondaryBrush (App.xaml:23, Templates.xaml:146/152/172/206/241) and would be visibly wrong in dark, where that brush is 77% not 60%. Instead apply the pattern the codebase already u

### MergePage.xaml.cs

- **:141** (design) The revisions header carries no count, unlike every other list header in the app
  - The user cannot tell how much is on offer without counting rows, and the number is the first thing that answers "is there anything to merge here?". SvnLogPage writes `$"Revisions, newest first ({rows.Count})"`, ServerBranchPage writes `$"Externals ({_rows.Count})"`, and ListFilter appends the count 
  - Fix: Move the header into `ShowRevisions()` (MergePage.xaml.cs:155) so it also refreshes when the merged-revisions fold is toggled, source it from the `Source` property and `_pairs.Count > 1` instead of the `source`/`many` locals that do not exist there, and mark the 200-row cap the way SvnLogPage does. After `var open = ...` add: `var n = $"{
- **:231** (interactivity) Arrowing through the list toggles the merged fold as soon as focus lands on it
  - Because the toggle hangs off SelectionChanged, a keyboard user walking the list with Down arrow opens the fold merely by passing over it, which rebuilds the list under them and drops focus back to the top; there is no Enter or Space path to open it deliberately, and no double-click. RowTreeView (Con
  - Fix: Drop the `IsFold` branch at MergePage.xaml.cs:239 so SelectionChanged only calls SyncButtons()/ShowPickedText(), and toggle from a single `Tapped` handler on `Revisions` — `if ((e.OriginalSource as FrameworkElement)?.DataContext is SvnRevRow { IsFold: true } f) f.Toggle();` — NOT Tapped plus DoubleTapped, since Tapped fires first and the 

### OutputWindow.xaml

- **:28** (design) The log window has no empty state
  - Opening the log before anything has run — or straight after pressing Clear — shows an empty bordered card with no text, which reads as a window that failed to load rather than one with nothing to say. The app has a control for exactly this and uses it in the sibling chrome: MainWindow.xaml:59 puts `
  - Fix: Mirror the Plan pane. Add `xmlns:local="using:Sg.App"` to OutputWindow.xaml's root (after line 4). Replace row 2 with a `<Grid Grid.Row="2" Margin="16,0,16,16">` containing the existing Border renamed `x:Name="LogCard"` with `Visibility="Collapsed"` (keeping `Style="{StaticResource Card}" Padding="12,8"` and LogText inside it), plus a sib

### PathPicker.xaml.cs

- **:56** (stale-state) A late folder enumeration overwrites the candidate list of a newer path
  - CheckoutFields.Revalidate fires this for every path the user types that happens to exist (CheckoutFields.xaml.cs:109-114), so several walks are in flight at once with nothing ordering them. A slow walk of `D:\` finishing after a fast walk of `D:\work` leaves the drop-down offering the wrong checkout
  - Fix: Keep an `int _generation` in PathPicker: `var gen = ++_generation;` before the await, `if (gen != _generation) return;` before assigning ItemsSource.

### PushPage.xaml.cs

- **:41** (interactivity) The file tree offers no page actions on right click, unlike both sibling pages
  - FileActions.Attach takes an `extend` callback for exactly this (FileActions.cs:18), and both siblings use it: CommitPage.xaml.cs:48 and SvnCommitPage.xaml.cs:41 pass ExtendMenu, which adds Blame for a single tracked file among other things. Here the same file rows, listed against the same worktree, 
  - Fix: At PushPage.xaml.cs:41 pass an inline extend delegate in the shape of LogPage.xaml.cs:27-34: `FileActions.Attach(Files, n => PathUtil.Join(_worktree, n.FullPath), (menu, node) => { if (node.Row is not FileRow row || row.Status == 'D') return; var item = new MenuFlyoutItem { Text = "Blame", Icon = new FontIcon { Glyph = "" } }; ToolTipSer
- **:300** (design) Apply finishes silently in the background while Push on the same page raises a toast
  - PushAsync ends with `if (!WindowHelper.IsForeground(this)) Notifications.Show(...)` (lines 426-427). Apply runs the same sync, rebase and per-working-copy write (Push.Run with PushFinish.LeaveInCheckout) and takes just as long, but when the user has switched away nothing tells them it landed — the t
  - Fix: Add the same two lines to Apply_Click before `await LoadAsync();`: `if (!WindowHelper.IsForeground(this)) Notifications.Show(ok ? "Apply done" : "Apply stopped", ResultBar.Message);`

### ShelfPage.xaml

- **:52** (design) The Details card has no height cap and no scroller, so a long path squeezes the Files tree
  - DetailWhere wraps a two-line string ending in the full shelf path and GoneBar can open under it, and the card is in an Auto row above the `*` row the Files tree lives in, so a deep path or a warning eats the file list's height. The three pages built from the identical list/details/files column all c
  - Fix: Keep the sibling cap but pin the warning outside the scroller. Add `MaxHeight="180"` to the Border at ShelfPage.xaml:52 and replace its StackPanel with `<Grid RowDefinitions="*,Auto" RowSpacing="4">` holding a `<ScrollViewer VerticalScrollBarVisibility="Auto">` (wrapping a `<StackPanel Spacing="4">` with only DetailHead and DetailWhere) i

### SvnCommitPage.xaml

- **:17** (interactivity) Neither Refresh button mentions F5, although F5 is wired on this page and both of the sibling's do
  - SvnCommitPage.xaml.cs line 40 registers Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync(_shownPath)), but neither the empty-state Refresh (line 17) nor the bottom-bar Refresh (line 71) says so, so the key is undiscoverable. CommitPage's two Refresh buttons both end their tooltip with 'F5 does
  - Fix: Append ' F5 does the same.' to the ToolTipService.ToolTip on both Refresh buttons, lines 17 and 71, matching the sibling's wording exactly.

### SvnLogPage.xaml

- **:57** (design) The commit message is set in the proportional font; the sibling log renders the same content monospaced
  - LogPage.xaml:57 is the identical control in the identical Details card and reads `<TextBlock x:Name="DetailMessage" FontFamily="Cascadia Mono, Consolas" FontSize="12" ... />`. SVN log messages carry the same hand-wrapped lines, bullet lists and pasted paths as git ones, and here they lose their alig
  - Fix: Add `FontFamily="Cascadia Mono, Consolas"` to the DetailMessage TextBlock, matching LogPage.xaml:57.

## The two high findings left open

- **MainWindow.xaml.cs:637** - Open root does not say when the folder picked is not a root. The
  verifier showed the obvious fix would fire a false modal on the legitimate case, where Find walks
  up from a checkout or a worktree. It needs a different signal, not a different message.
- **DiffView.xaml.cs:236** - picking another file in a changes page still throws away unsaved editor
  text without asking. Refresh now asks; the file switch needs the previous selection put back when
  the answer is no, which is a re-entrancy dance through ListFilter and was left for its own change.
