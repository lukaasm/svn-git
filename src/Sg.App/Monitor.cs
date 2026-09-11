using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using Sg.Core;

namespace Sg.App;

/// <summary>One SVN URL the monitor watches.</summary>
public sealed class MonitorItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "General";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public int IntervalMinutes { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public bool Notify { get; set; } = true;
    /// <summary>The newest revision the user has looked at. Everything above it is unread.</summary>
    public long LastSeen { get; set; }
    /// <summary>The newest revision a toast was shown for.</summary>
    public long LastNotified { get; set; }
    public long Head { get; set; }
    public string ReposRoot { get; set; } = "";
    public DateTime? LastChecked { get; set; }
    public string? Error { get; set; }

    [JsonIgnore] public List<SvnLogRevision> Recent { get; set; } = new();
    [JsonIgnore] public int Unread => Recent.Count(r => r.Revision > LastSeen);
    [JsonIgnore] public bool Checking { get; set; }
}

public sealed class MonitorStore
{
    public List<MonitorItem> Items { get; set; } = new();

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sg", "monitor.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static MonitorStore Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<MonitorStore>(File.ReadAllText(FilePath), Opts) ?? new MonitorStore();
        }
        catch { /* start empty */ }
        return new MonitorStore();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }
}

/// <summary>
/// Checks the watched URLs on a timer while the app runs. Keeps the last 60 revisions of each in memory,
/// counts the unread ones, and shows a toast when a repository moves.
/// </summary>
public static class MonitorService
{
    public static MonitorStore Store { get; } = MonitorStore.Load();
    public static event Action? Changed;
    static DispatcherQueue? _queue;
    static DispatcherQueueTimer? _timer;
    static bool _busy;
    const int RecentCount = 60;

    public static int TotalUnread => Store.Items.Sum(i => i.Unread);
    public static IEnumerable<string> Categories => Store.Items.Select(i => i.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase);

    public static void Start(DispatcherQueue queue)
    {
        if (_timer != null) return;
        _queue = queue;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(60);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = CheckDueAsync();
        _timer.Start();
        _ = CheckDueAsync();
    }

    static void Raise()
    {
        if (_queue == null || _queue.HasThreadAccess) Changed?.Invoke();
        else _queue.TryEnqueue(() => Changed?.Invoke());
    }

    public static void Save()
    {
        Store.Save();
        Raise();
    }

    public static async Task CheckDueAsync()
    {
        var now = DateTime.UtcNow;
        var due = Store.Items.Where(i => i.Enabled && (i.LastChecked == null || i.Recent.Count == 0 || now - i.LastChecked >= TimeSpan.FromMinutes(Math.Max(1, i.IntervalMinutes)))).ToList();
        await CheckAsync(due);
    }

    public static Task CheckAllAsync() => CheckAsync(Store.Items.Where(i => i.Enabled).ToList());

    public static Task CheckOneAsync(MonitorItem item) => CheckAsync([item]);

    /// <summary>
    /// Every repository is asked at once. Each one costs two round trips to the server, and asking them
    /// one after another added the waits up: a checkout's root and its externals are twenty items, so a
    /// check took twenty times longer than it had to and held the timer for all of it. The answers are
    /// still applied in order, on the UI thread, so the tree fills from the top.
    /// </summary>
    static async Task CheckAsync(List<MonitorItem> items)
    {
        if (_busy || items.Count == 0) return;
        _busy = true;
        try
        {
            var svn = Session.Root?.Svn ?? new Svn("svn", new NullLog());
            // Enough to overlap the waits, few enough that one server is not hammered.
            var gate = new SemaphoreSlim(6);
            var reads = items.Select(item => (Item: item, Read: Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    var info = svn.InfoUrl(item.Url);
                    var log = svn.LogVerbose(null!, item.Url, RecentCount);
                    return (info.LastChangedRev, info.ReposRoot, log);
                }
                finally { gate.Release(); }
            }))).ToList();
            foreach (var item in items) item.Checking = true;
            Raise();

            foreach (var (item, read) in reads)
            {
                try
                {
                    var (head, reposRoot, recent) = await read;
                    item.ReposRoot = reposRoot;
                    item.Recent = recent;
                    if (item.LastSeen == 0) item.LastSeen = head;
                    item.Head = head;
                    item.Error = null;
                    if (item.Notify && item.Unread > 0 && head > item.LastNotified)
                    {
                        var newest = recent.FirstOrDefault(r => r.Revision > item.LastSeen);
                        var first = newest?.Message.Split('\n')[0].Trim() ?? "";
                        Notifications.Show($"{item.Name}: {item.Unread}{(item.Unread >= RecentCount ? "+" : "")} new commit(s)",
                            (newest != null ? $"r{newest.Revision} {newest.Author}: {first}" : item.Url),
                            new Dictionary<string, string> { ["action"] = "monitor", ["id"] = item.Id });
                        item.LastNotified = head;
                    }
                }
                // Every exception, not only ours: an svn that answers with something XDocument cannot read
                // used to end the sweep at whichever repository it happened on, and from Check now - an
                // async void handler - it ended the process. One repository failing is one row with an error.
                catch (Exception ex)
                {
                    item.Error = ex.Message.Split('\n')[0];
                }
                item.LastChecked = DateTime.UtcNow;
                item.Checking = false;
                Raise();
            }
            Store.Save();
        }
        finally
        {
            // Every repository is marked as being checked up front, so anything that ends this early
            // has to clear all of them. One left set spins in the tree for the life of the app.
            _busy = false;
            if (items.Any(i => i.Checking))
            {
                foreach (var i in items) i.Checking = false;
                Raise();
            }
        }
    }

    public static void MarkRead(MonitorItem item)
    {
        item.LastSeen = Math.Max(item.LastSeen, item.Head);
        item.LastNotified = item.LastSeen;
        Save();
    }

    /// <summary>The user read this revision. It and the older ones are read; anything newer stays unread.</summary>
    public static void MarkReadUpTo(MonitorItem item, long revision)
    {
        if (revision <= item.LastSeen) return;
        item.LastSeen = revision;
        item.LastNotified = Math.Max(item.LastNotified, revision);
        Save();
    }

    public static void MarkAllRead()
    {
        foreach (var i in Store.Items) { i.LastSeen = Math.Max(i.LastSeen, i.Head); i.LastNotified = i.LastSeen; }
        Save();
    }

    public static MonitorItem Add(string name, string url, string category, int interval, bool notify)
    {
        var item = new MonitorItem { Name = name, Url = url.TrimEnd('/'), Category = category.Length > 0 ? category : "General", IntervalMinutes = interval, Notify = notify };
        Store.Items.Add(item);
        Save();
        _ = CheckOneAsync(item);
        return item;
    }

    public static void Remove(MonitorItem item)
    {
        Store.Items.Remove(item);
        Save();
    }

    /// <summary>Adds the root and every external of a checkout as items in a category named after the checkout.</summary>
    /// <summary>
    /// The URLs a checkout watches: its own, and one per external, read out of the snapshot. Two git
    /// processes per checkout, so it is split from the write below and belongs on a pool thread.
    /// </summary>
    public static List<(string Name, string Url, string Category)> CheckoutUrls(SgRoot root, CheckoutConfig co)
    {
        var sha = root.Git.RefSha(root.SnapshotRef(co));
        var meta = sha != null ? SnapshotMeta.Parse(root.Git.Body(sha)) : null;
        var urls = new List<(string Name, string Url, string Category)>();
        var rootUrl = (meta?.Url.Length > 0 ? meta.Url : co.Url).TrimEnd('/');
        urls.Add((co.Name + " root", rootUrl, co.Name));
        if (meta != null)
            foreach (var (rel, url) in meta.ExternalUrls) urls.Add((rel, url.TrimEnd('/'), co.Name));
        return urls;
    }

    /// <summary>Adds whatever of those is not watched yet, and starts a check. Touches no repository.</summary>
    public static int AddUrls(IEnumerable<(string Name, string Url, string Category)> urls)
    {
        var added = 0;
        foreach (var (name, url, category) in urls)
        {
            if (Store.Items.Any(i => i.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;
            Store.Items.Add(new MonitorItem { Name = name, Url = url, Category = category });
            added++;
        }
        if (added > 0) Save();
        _ = CheckAllAsync();
        return added;
    }

    public static int AddCheckout(SgRoot root, CheckoutConfig co) => AddUrls(CheckoutUrls(root, co));
}
