namespace Sg.Core;

public sealed record ReviewSnapshot(CodeReviewData Data, string Revision);

/// <summary>Follow one review document, including atomic replacement. Notifications are hints; Read is authoritative.
/// No repository scans or polling occur here. Callbacks run on watcher threads; dispose when the view leaves.</summary>
public sealed class ReviewFeed : IDisposable
{
    readonly string _file;
    readonly FileObservation _observation;
    public string Identity => Path.GetFileNameWithoutExtension(_file);
    public string? WatchError => _observation.Error;
    public event Action? Changed { add => _observation.Changed += value; remove => _observation.Changed -= value; }

    internal ReviewFeed(string file) { _file = file; _observation = new(file); }

    /// <summary>Retry an unavailable watcher on activation or explicit refresh. Reads remain usable without it.</summary>
    public void Reconnect() => _observation.Reconnect();

    public ReviewSnapshot Read() => ReadSnapshot(_file);
    internal static ReviewSnapshot ReadSnapshot(string file)
    {
        string text;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > CodeReview.MaxDataBytes) throw new SgException("Review data exceeds the 32 MB limit.");
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return new(new(), "missing"); }
        // Decode before publication: corrupt or incomplete external writes must not replace the last good UI.
        return new(CodeReview.Decode(text), WorkspaceVersion.Hash(text));
    }
    public void Dispose() => _observation.Dispose();
}
