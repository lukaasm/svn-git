using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// The three ways a worktree can get a folder it shares with its checkout, as one drop-down with a
/// line under it. The line is where the volumes answer: a clone that cannot happen here is greyed
/// out and the reason is written under the box, instead of a branch that fails after every file
/// was written. Three pages ask this question, so the answer is built once.
/// </summary>
public sealed class SharedModeBox : UserControl
{
    const string CloneText = "ReFS clone (copy-on-write)";

    readonly ComboBox _box = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly ComboBoxItem _junction = new() { Content = "Junction into the checkout", Tag = SharedMode.Junction };
    readonly ComboBoxItem _clone = new() { Content = CloneText, Tag = SharedMode.Clone };
    readonly ComboBoxItem _copy = new() { Content = "Full copy", Tag = SharedMode.Copy };

    public SharedModeBox()
    {
        ToolTipService.SetToolTip(_junction, "One folder on disk. Every worktree sees the checkout's copy, and a build in one worktree changes it for all.");
        ToolTipService.SetToolTip(_clone, "A private folder that shares disk with the checkout until one side writes, so it costs nothing until then. "
                                          + "Needs the checkout and the worktrees on one ReFS volume, like a Dev Drive.");
        ToolTipService.SetToolTip(_copy, "A private folder that costs the checkout's folder again. Works on any volume.");
        _box.Items.Add(_junction);
        _box.Items.Add(_clone);
        _box.Items.Add(_copy);
        _box.SelectedIndex = 0;
        _box.SelectionChanged += (_, _) => Changed?.Invoke();
        if (Application.Current.Resources.TryGetValue("CaptionTextBlockStyle", out var style) && style is Style s) _note.Style = s;
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush) && brush is Brush b) _note.Foreground = b;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = new StackPanel { Spacing = 4, Children = { _box, _note } };
    }

    /// <summary>The choice changed. Hosts that enable a Save on it listen here.</summary>
    public event Action? Changed;

    public string Header
    {
        get => _box.Header as string ?? "";
        set => _box.Header = value;
    }

    public SharedMode Mode
    {
        get => _box.SelectedItem is ComboBoxItem i && i.Tag is SharedMode m ? m : SharedMode.Junction;
        set => _box.SelectedItem = value switch
        {
            SharedMode.Clone => _clone,
            SharedMode.Copy => _copy,
            _ => _junction,
        };
    }

    /// <summary>Why a clone is off the table here. Null when it is on, or when the paths are not known yet.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Asks the volumes whether a clone from the checkout into a worktree under worktreeRoot can work,
    /// and sets the clone item and the line under the box from the answer. A blank path means the
    /// question cannot be asked yet, and the item stays on. A clone that was chosen and cannot happen
    /// falls back to a junction, which is what the checkout would have got before this choice existed.
    /// </summary>
    public string? Detect(string? checkoutPath, string? worktreeRoot)
    {
        var known = !string.IsNullOrWhiteSpace(checkoutPath) && !string.IsNullOrWhiteSpace(worktreeRoot);
        Problem = known ? SharedFolders.CloneProblem(checkoutPath!, worktreeRoot!) : null;
        _clone.IsEnabled = Problem == null;
        _clone.Content = Problem == null ? CloneText : CloneText + ", not available here";
        if (!known) _note.Visibility = Visibility.Collapsed;
        else
        {
            var volume = SharedFolders.VolumeOf(checkoutPath!) ?? "";
            _note.Text = Problem ?? $"{volume} is ReFS: a clone shares disk with the checkout until it is written";
            _note.Visibility = Visibility.Visible;
        }
        if (Problem != null && Mode == SharedMode.Clone) Mode = SharedMode.Junction;
        return Problem;
    }
}
