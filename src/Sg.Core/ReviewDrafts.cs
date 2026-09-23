using System.Text.Json;

namespace Sg.Core;

/// <summary>Unpublished UI text, kept separately from shared review history and backups.</summary>
public sealed record ReviewDraft(string Id, string File, string Body, string Side, double? First, double? Last, string SourceVersion);
public sealed record ReviewDraftVersion(ReviewDraft? Draft, string? Revision);

/// <summary>Per-user draft files. Compare-and-write preserves another window's edits; failed writes retain the previous version.</summary>
public sealed class ReviewDrafts(string directory)
{
    const int MaxBytes = 160_000;
    sealed record Document(int Schema, string Key, ReviewDraft Draft);
    static bool Valid(ReviewDraft draft) => Guid.TryParseExact(draft.Id, "N", out _)
        && draft.Body is { Length: <= 32000 } && draft.File != null && draft.SourceVersion != null
        && draft.Side is "original" or "modified";
    string Folder(string scope) => Path.Combine(directory, WorkspaceVersion.Hash(scope));
    string FileFor(string scope, string key) => Path.Combine(Folder(scope), WorkspaceVersion.Hash(key) + ".json");
    static Document Decode(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new SgException("Review draft is too large; the original file is retained.");
        Document? doc;
        try { doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path)); }
        catch (JsonException) { throw new SgException("Cannot read this review draft; the original file is retained."); }
        if (doc is not { Schema: 1, Draft: not null } || doc.Key == null || !Valid(doc.Draft)) throw new SgException("Unsupported review draft; the original file is retained.");
        return doc;
    }
    public ReviewDraftVersion Read(string scope, string key)
    {
        var path = FileFor(scope, key);
        if (!File.Exists(path)) return new(null, null);
        var doc = Decode(path);
        if (doc.Key != key) throw new SgException("Review draft identity does not match.");
        return new(doc.Draft, WorkspaceVersion.Hash(JsonSerializer.Serialize(doc)));
    }
    public IReadOnlyDictionary<string, ReviewDraft> List(string scope)
    {
        var folder = Folder(scope);
        if (!Directory.Exists(folder)) return new Dictionary<string, ReviewDraft>();
        return Directory.EnumerateFiles(folder, "*.json").Select(Decode).ToDictionary(d => d.Key, d => d.Draft, StringComparer.Ordinal);
    }
    public ReviewDraftVersion Write(string scope, string key, ReviewDraft? draft, string? expectedRevision)
    {
        var path = FileFor(scope, key);
        Directory.CreateDirectory(Folder(scope));
        using var gate = Acquire(path + ".lock");
        if (Read(scope, key).Revision != expectedRevision) throw new SgException("This draft changed in another window. Your text is still here; copy it before reopening the latest draft.");
        if (draft == null) { File.Delete(path); return new(null, null); }
        if (!Valid(draft)) throw new SgException("Invalid review draft. Your text has not been discarded.");
        var text = JsonSerializer.Serialize(new Document(1, key, draft));
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new SgException("Review draft is too large. Your text has not been discarded.");
        AtomicFile.WriteAllText(path, text);
        return new(draft, WorkspaceVersion.Hash(text));
    }
    static FileStream Acquire(string path)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            Cancellation.ThrowIfRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); }
        }
    }
}
