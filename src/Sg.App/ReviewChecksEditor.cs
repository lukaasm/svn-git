using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>Edits argument arrays directly: spaces, quotes, and empty arguments stay literal.</summary>
internal sealed class ReviewChecksEditor : UserControl
{
    readonly List<ReviewCheckConfig> _checks;
    readonly StackPanel _rows = new() { Spacing = 16 };
    readonly InfoBar _validation = new() { IsClosable = false, Severity = InfoBarSeverity.Informational };
    public event Action? Changed;
    public bool IsValid => _checks.All(c => !string.IsNullOrWhiteSpace(c.Executable));

    public ReviewChecksEditor(IEnumerable<ReviewCheckConfig> checks)
    {
        _checks = checks.Select(Clone).ToList();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "Checks run in list order, in the branch folder. Saving this list does not run commands.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_rows);
        body.Children.Add(_validation);
        body.Children.Add(Button("Add check", "\uE710", "AddReviewCheck", () =>
        {
            _checks.Add(new());
            Render();
        }));
        Content = body;
        Render();
    }

    public List<ReviewCheckConfig> Snapshot()
    {
        if (!IsValid) throw new SgException("Every check needs an executable.");
        return Draft();
    }
    public List<ReviewCheckConfig> Draft() => _checks.Select(Clone).ToList();

    static ReviewCheckConfig Clone(ReviewCheckConfig check) => new() { Name = check.Name, Executable = check.Executable, Arguments = check.Arguments.ToList() };

    void Validate()
    {
        var missing = _checks.FindIndex(c => string.IsNullOrWhiteSpace(c.Executable));
        _validation.IsOpen = missing >= 0;
        _validation.Message = missing >= 0 ? $"Check {missing + 1}: enter a program name or executable path before saving." : "";
        Changed?.Invoke();
    }

    void Render()
    {
        _rows.Children.Clear();
        if (_checks.Count == 0) _rows.Children.Add(new TextBlock { Text = "No checks configured. Add a check to get started.", TextWrapping = TextWrapping.Wrap });
        for (var i = 0; i < _checks.Count; i++)
        {
            var index = i;
            var check = _checks[i];
            var card = new StackPanel { Spacing = 8 };
            card.Children.Add(new TextBlock { Text = $"Check {i + 1}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            card.Children.Add(Field("Name (optional)", check.Name, $"ReviewCheckName_{i}", text => check.Name = text));
            card.Children.Add(Field("Executable", check.Executable, $"ReviewCheckExecutable_{i}", text => check.Executable = text));
            card.Children.Add(new TextBlock { Text = "Arguments — one value per field; spaces and quotes are passed literally.", TextWrapping = TextWrapping.Wrap });
            for (var a = 0; a < check.Arguments.Count; a++)
            {
                var argument = a;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(Field($"Argument {a + 1}", check.Arguments[a], $"ReviewCheckArgument_{i}_{a}", text => check.Arguments[argument] = text));
                var remove = Button("Remove argument", "\uE74D", $"RemoveReviewArgument_{i}_{a}", () => { check.Arguments.RemoveAt(argument); Render(); });
                remove.VerticalAlignment = VerticalAlignment.Bottom;
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                card.Children.Add(row);
            }
            card.Children.Add(Button("Add argument", "\uE710", $"AddReviewArgument_{i}", () => { check.Arguments.Add(""); Render(); }));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var up = Button("Move up", "\uE74A", $"MoveReviewCheckUp_{i}", () => { (_checks[index - 1], _checks[index]) = (_checks[index], _checks[index - 1]); Render(); });
            up.IsEnabled = i > 0;
            var down = Button("Move down", "\uE74B", $"MoveReviewCheckDown_{i}", () => { (_checks[index + 1], _checks[index]) = (_checks[index], _checks[index + 1]); Render(); });
            down.IsEnabled = i + 1 < _checks.Count;
            actions.Children.Add(up); actions.Children.Add(down);
            actions.Children.Add(Button("Remove check", "\uE74D", $"RemoveReviewCheck_{i}", () => { _checks.RemoveAt(index); Render(); }));
            card.Children.Add(actions);
            _rows.Children.Add(card);
        }
        Validate();
    }

    TextBox Field(string label, string value, string id, Action<string> changed)
    {
        var box = new TextBox { Header = label, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(box, id);
        box.TextChanged += (_, _) => { changed(box.Text); Validate(); };
        return box;
    }
    static IconButton Button(string text, string glyph, string id, Action click)
    {
        var button = new IconButton { Text = text, Glyph = glyph };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => click();
        return button;
    }
}
