using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Sg.App;

/// <summary>
/// A filter box over a commit list. A branch that has been worked on for a month lists forty commits,
/// and the one that touched the thing you are looking for is found by a word of its message, not by
/// scrolling. The box narrows the list; the page decides what a narrowed list means for the rest of
/// what it shows, so this only says which rows match and how many did.
///
/// Ctrl+Shift+F reaches it. Ctrl+F is the file list's on every page that has one, and the two lists
/// sit on the same page.
/// </summary>
public sealed class CommitFilter
{
    readonly TextBox _box;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(140) };

    public CommitFilter(TextBox box, FrameworkElement page)
    {
        _box = box;
        // One rebuild per pause in the typing, the way the file filter does it.
        _box.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };
        Shortcuts.Add(page, VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () =>
        {
            _box.Focus(FocusState.Programmatic);
            _box.SelectAll();
        });
    }

    /// <summary>The text settled. The page rebuilds its list from what Apply hands back.</summary>
    public event Action? Changed;

    /// <summary>The words in the box. Every one of them has to be found somewhere on a row for it to stay.</summary>
    string[] Words => _box.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Something is typed, so the list on screen is not the whole list.</summary>
    public bool Active => Words.Length > 0;

    /// <summary>The rows that match, in the order they came. All of them when nothing is typed.</summary>
    public List<CommitRow> Apply(IEnumerable<CommitRow> rows)
    {
        var words = Words;
        return words.Length == 0 ? rows.ToList() : rows.Where(r => Matches(r, words)).ToList();
    }

    /// <summary>
    /// A word matches the message, the author or the date anywhere, and the sha from its start: "a3f"
    /// is the beginning of a sha, not three letters that happen to be inside one.
    /// </summary>
    static bool Matches(CommitRow row, string[] words) =>
        words.All(w => row.Subject.Contains(w, StringComparison.OrdinalIgnoreCase)
                       || row.Author.Contains(w, StringComparison.OrdinalIgnoreCase)
                       || row.Date.Contains(w, StringComparison.OrdinalIgnoreCase)
                       || row.Sha.StartsWith(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>"showing 5 of 45", for the header over the list while the filter is on.</summary>
    public static string Showing(int shown, int all) => $"showing {shown} of {all}";
}
