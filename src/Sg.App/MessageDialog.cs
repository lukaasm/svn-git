using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// The commit message, asked for in a dialog over the middle of the window. Commit, Commit to SVN and
/// Push to SVN all use this one: the same box, the same count under the same rule, Ctrl+Enter to send
/// it, Escape or Cancel to keep it for later. It sits in the page's tree, takes no room until it is
/// shown, and holds the text between showings. The rule is shown while you type instead of after you
/// press the button, and it counts what actually gets committed, so comment lines and trailing blanks
/// do not pad it.
/// </summary>
public sealed class MessageDialog : ContentDialog
{
    readonly TextBlock _header = new() { Style = Res<Style>("BodyStrongTextBlockStyle") };
    readonly TextBlock _count = new() { Style = Res<Style>("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Bottom };
    readonly TextBox _box = new() { MinHeight = 120, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly Button _recent = new() { Content = "Recent", FontSize = 12, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Bottom };
    readonly ContentPresenter _extra = new();
    int _minimum = 1;
    bool _ready;
    bool _asking;
    bool _confirmed;

    public MessageDialog()
    {
        CloseButtonText = "Cancel";
        PrimaryButtonStyle = Res<Style>("AccentButtonStyle");
        // A dialog stops at 548 wide by default, and a message box that wide is a slit.
        Resources["ContentDialogMaxWidth"] = 760.0;
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_count, 1);
        Grid.SetColumn(_recent, 2);
        head.Children.Add(_header);
        head.Children.Add(_count);
        head.Children.Add(_recent);
        _recent.Click += (_, _) => ShowRecent();
        ToolTipService.SetToolTip(_recent, "The messages that went through before. Picking one puts it in the box; nothing is sent by it.");
        var panel = new StackPanel { Spacing = 6, Width = 620 };
        panel.Children.Add(head);
        panel.Children.Add(_box);
        panel.Children.Add(_extra);
        Content = panel;
        _box.TextChanged += (_, _) => Sync();
        // The caret goes after whatever is in the box: the prefix the page put there, or the message
        // being amended. At the start it sat in front of the text, and the first letter typed went there.
        Opened += (_, _) =>
        {
            _box.Focus(FocusState.Programmatic);
            _box.SelectionStart = _box.Text.Length;
        };
        // Ctrl+Enter inside is the dialog's own button. The dialog is a popup of its own, so the key sits
        // on its panel, where it reaches it while the box has the focus. Hide answers None, so the yes
        // travels in _confirmed.
        Shortcuts.Add(panel, VirtualKey.Enter, VirtualKeyModifiers.Control, () =>
        {
            if (!IsPrimaryButtonEnabled) return;
            _confirmed = true;
            Hide();
        });
        Sync();
    }

    /// <summary>The line over the box: "Commit message", or "Commit message for SVN".</summary>
    public string Header { get => _header.Text; set => _header.Text = value; }

    /// <summary>The tooltip on the count: why there is a minimum.</summary>
    public string CountTip { get => ToolTipService.GetToolTip(_count) as string ?? ""; set => ToolTipService.SetToolTip(_count, value); }

    /// <summary>What sits under the box: the push's list of working copies. Null for the plain commits.</summary>
    public UIElement? Extra { get => _extra.Content as UIElement; set => _extra.Content = value; }

    /// <summary>The shortest text the button accepts. The server's minimum for SVN; 1 for git, a commit needs a message and nothing more.</summary>
    public int Minimum { get => _minimum; set { _minimum = Math.Max(0, value); Sync(); } }

    /// <summary>The page's own gate: something is ticked, the push is ready. Off, and the button stays off whatever the text.</summary>
    public bool Ready { get => _ready; set { _ready = value; Sync(); } }

    /// <summary>The shared text is needed unless this says no: a push where every working copy has its own. Null means always.</summary>
    public Func<bool>? Needed { get; set; }

    /// <summary>What the extra content demands on top: every own message long enough. Null means nothing.</summary>
    public Func<bool>? ExtraOk { get; set; }

    /// <summary>A question asked after the message and before the work, for what everyone can see. Null skips it.</summary>
    public Func<string>? Confirm { get; set; }

    public string Text { get => _box.Text; set => _box.Text = value; }

    /// <summary>What actually gets committed: comment lines and trailing blanks stripped.</summary>
    public string Clean => Push.CleanMessage(_box.Text);

    /// <summary>The text meets the minimum.</summary>
    public bool Ok => Clean.Length >= _minimum;

    /// <summary>
    /// The messages that went through before, newest first. Picking one puts the whole of it in the box,
    /// where it can be edited; it does not send anything. The button hides itself until there are some.
    /// </summary>
    void ShowRecent()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        foreach (var message in Session.Settings.RecentMessages.Take(AppSettings.MaxRecentMessages))
        {
            var first = message.Split('\n')[0].TrimEnd('\r');
            var item = new MenuFlyoutItem { Text = first.Length > 80 ? first[..79] + "…" : first };
            ToolTipService.SetToolTip(item, message);
            var whole = message;
            item.Click += (_, _) => { _box.Text = whole; _box.SelectionStart = whole.Length; _box.Focus(FocusState.Programmatic); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.ShowAt(_recent);
    }

    /// <summary>Called when a commit went through, so the message is offered again next time.</summary>
    public static void Remember(string message) => Session.Settings.RememberMessage(message);

    void SyncRecent() => _recent.Visibility = Session.Settings.RecentMessages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The count under the rule, and the button after the whole of it. Called for anything the rule reads.</summary>
    public void Sync()
    {
        var n = Clean.Length;
        _count.Text = n >= _minimum ? $"{n} characters" : $"{n} of {_minimum} characters";
        _count.Foreground = Res<Brush>(n >= _minimum ? "TextFillColorTertiaryBrush" : "SystemFillColorCautionBrush");
        var needed = Needed?.Invoke() ?? true;
        IsPrimaryButtonEnabled = _ready && (!needed || Ok) && (ExtraOk?.Invoke() ?? true);
    }

    /// <summary>
    /// Shows the dialog and waits for the answer: true when its button or Ctrl+Enter said yes, and the
    /// question after it, if there is one, was answered yes too. False at once while it is already up,
    /// so a second Ctrl+Enter in between does nothing.
    /// </summary>
    public async Task<bool> AskAsync()
    {
        if (_asking) return false;
        _asking = true;
        try
        {
            _confirmed = false;
            SyncRecent();
            var result = await ShowAsync();
            if (result != ContentDialogResult.Primary && !_confirmed) return false;
            if (Confirm == null) return true;
            return await Dialogs.Confirm(this, Title as string ?? "", Confirm(), PrimaryButtonText);
        }
        finally { _asking = false; }
    }

    static T Res<T>(string key) => (T)Application.Current.Resources[key];
}
