using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

public sealed partial class DiffView
{
    /// <summary>Start a different file at the top while retaining this page's Back-navigation state.</summary>
    public bool RestartOnFileChange { get; set; }
    DiffReadingState _readingPositions = new();
    int _readingVisit;
    string _readingKey = "";

    void AttachReadingPositions()
    {
        for (DependencyObject? parent = this; parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is SgPage page)
            {
                _readingPositions = page.ReadingPositions;
                break;
            }
        _readingVisit = _readingPositions.Visit;
    }

    string ReadingKey(string kind, string? title) => string.IsNullOrEmpty(title) ? "" : Name + "\n" + kind + "\n" + title;

    void OnReadingPosition(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                && value.TryGetProperty("state", out var state))
                _readingPositions.Put(_readingVisit, key.GetString()!, state);
        }
        catch (JsonException) { /* Ignore an incomplete browser report. */ }
    }

    async Task CaptureReadingPositionAsync()
    {
        if (!_ready || Web.CoreWebView2 is not { } core) return;
        try
        {
            // Drain the last scroll/caret change before disposal without delaying navigation itself.
            var json = await core.ExecuteScriptAsync("window.sgCaptureReading && window.sgCaptureReading()").AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
            OnReadingPosition(json);
        }
        catch (Exception) { /* A closing or unresponsive browser must not hold navigation open. */ }
    }
}
