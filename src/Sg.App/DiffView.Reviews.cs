using System.Text.Json;

namespace Sg.App;

public sealed partial class DiffView
{
    public sealed record ReviewMessage(string Actor, string Action, string Body, string At);
    public sealed record ReviewAnnotation(string Id, string Side, int First, int Last, string State, bool Conflict, ReviewMessage[] Messages);
    public event Action<string, string>? ReviewActionInvoked;
    ReviewAnnotation[] _reviews = [];
    int _reviewDocument;
    string? _pendingReviewReveal;

    public void SetReviewThreads(ReviewAnnotation[] threads)
    {
        _reviews = threads;
        if (!threads.Any(t => t.Id == _pendingReviewReveal)) _pendingReviewReveal = null;
        SendReviews();
    }

    void ClearReviews()
    {
        ++_reviewDocument;
        _pendingReviewReveal = null;
        _reviews = [];
        SendReviews();
    }

    void SendReviews() => Post(new
    {
        command = "reviews", document = _reviewDocument,
        threads = _reviews.Select(t => new
        {
            id = t.Id, side = t.Side, first = t.First, last = t.Last, state = t.State, conflict = t.Conflict,
            messages = t.Messages.Select(m => new { actor = m.Actor, action = m.Action, body = m.Body, at = m.At }),
        }),
    });

    public void RevealReviewThread(string id)
    {
        _pendingReviewReveal = id;
        FlushReviewReveal();
    }

    void FlushReviewReveal()
    {
        var thread = _reviews.FirstOrDefault(t => t.Id == _pendingReviewReveal);
        if (thread == null || _failed || !_ready) return;
        _pendingReviewReveal = null;
        // Show the actual anchor, including original lines and lines hidden in unchanged regions.
        // These temporary view changes do not change the user's saved defaults.
        if (thread.Side == "original") InlineToggle.IsChecked = false;
        CollapsedToggle.IsChecked = false;
        SendLayout();
        Post(new { command = "review-reveal", document = _reviewDocument, id = thread.Id });
    }

    void OnReviewAction(string json)
    {
        try
        {
            using var message = JsonDocument.Parse(json);
            var root = message.RootElement;
            if (root.GetProperty("document").GetInt32() != _reviewDocument) return;
            var id = root.GetProperty("id").GetString();
            var action = root.GetProperty("action").GetString();
            if (id == null || !_reviews.Any(t => t.Id == id)) return;
            if (action is "reply" or "resolve" or "reopen") ReviewActionInvoked?.Invoke(id, action);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { /* Ignore malformed or obsolete browser messages. */ }
    }
}
