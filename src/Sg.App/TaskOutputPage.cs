using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>Reads a finished task's retained output without depending on the current repository.</summary>
internal sealed class TaskOutputPage : WorkflowPage
{
    readonly TaskSnapshot _task;
    readonly SearchableOutput _output;

    public TaskOutputPage(TaskSnapshot task) : base("Task output")
    {
        _task = task;
        Subtitle = task.Title;
        _output = new(task.Log, "TaskOutputText", "Retained task output");
        Shortcuts.Add(this, VirtualKey.F, VirtualKeyModifiers.Control, _output.FocusSearch);
    }

    protected override Task Reload()
    {
        Body.Children.Clear();
        var severity = _task.State switch {
            TaskState.Succeeded => ChipSeverity.Success,
            TaskState.Failed => ChipSeverity.Critical,
            _ => ChipSeverity.Caution
        };
        Status(_task.StatusLabel, severity, severity == ChipSeverity.Success ? "\uE73E" : "\uE7BA");
        Text(_task.Detail);
        var repository = new TextBlock { Text = _task.Root, IsTextSelectionEnabled = true, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(repository, "TaskOutputRepository");
        Body.Children.Add(repository);
        Text($"Started {_task.Started.LocalDateTime:g} · finished {_task.Finished?.LocalDateTime:g}");
        Text("Retained output from this task. Earlier lines may have been omitted from long logs.");
        if (string.IsNullOrWhiteSpace(_task.Log)) Text("No output was captured.");
        Body.Children.Add(_output);
        return Task.CompletedTask;
    }
}
