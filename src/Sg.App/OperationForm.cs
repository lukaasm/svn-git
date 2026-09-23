using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Sg.App;

/// <summary>Owns a submitted request and input locking without holding navigation or task progress.</summary>
internal sealed class OperationForm<TRequest>(params Control[] inputs) where TRequest : class
{
    public bool Running { get; private set; }
    public bool RetryAvailable { get; private set; }
    public TRequest? Submitted { get; private set; }
    public bool IsRetry(TRequest request) => RetryAvailable && EqualityComparer<TRequest>.Default.Equals(Submitted, request);

    public async Task<TResult?> Run<TResult>(TRequest request, Func<TRequest, Task<TResult?>> work, object? sender = null) where TResult : class
    {
        if (Running) return null;
        Running = true;
        RetryAvailable = false;
        Submitted = request;
        var enabled = inputs.Select(input => input.IsEnabled).ToArray();
        var help = inputs.Select(AutomationProperties.GetHelpText).ToArray();
        foreach (var input in inputs)
        {
            AutomationProperties.SetHelpText(input, "Inputs are locked while the submitted operation runs. Follow progress or cancel it in Tasks.");
            input.IsEnabled = false;
        }
        TResult? result = null;
        try
        {
            result = await Busy.During(sender, () => work(request), restoreEnabled: false);
            return result;
        }
        finally
        {
            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i].IsEnabled = enabled[i];
                AutomationProperties.SetHelpText(inputs[i], help[i]);
            }
            RetryAvailable = result == null;
            Running = false;
        }
    }
}

internal sealed record ImportRequest(string File, string Name, string Checkout);
internal sealed record RestoreRequest(string Source, string Name, string Checkout, bool WithEdits, bool Replace, IReadOnlyDictionary<string, string>? ExpectedRefs = null);
