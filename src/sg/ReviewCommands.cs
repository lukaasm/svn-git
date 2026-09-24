using Sg.Core;
using System.Text.Json;

static class ReviewCommands
{
    public static object Run(SgRoot root, string path, string action, Args args)
    {
        if (action == "inbox")
        {
            if (!int.TryParse(args.Get("--offset") ?? "0", out var inboxOffset)) throw new SgException("Offset must be a nonnegative integer.");
            return ReviewInbox.Query(root, args.Get("--state") ?? "open", args.Get("--search") ?? "", args.Get("--worktree"), inboxOffset);
        }
        path = CodeReview.Worktree(root, args.Get("--worktree") ?? path);
        string Body() => args.Get("--body-file") is { } file ? file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file)
            : args.Get("--body") ?? throw new SgException("Supply --body-file or --body.");
        string Required(string key) => args.Get(key) ?? throw new SgException("Missing " + key);
        var actor = args.Get("--actor") ?? "User";
        switch (action)
        {
            case "files": return CodeReview.Files(root, path);
            case "file": return CodeReview.ReadFile(root, path, args.Arg(1, "file"));
            case "threads":
                var state = args.Get("--state") ?? "open";
                if (state is not ("open" or "resolved" or "all")) throw new SgException("State must be open, resolved or all.");
                if (!int.TryParse(args.Get("--offset") ?? "0", out var offset) || offset < 0) throw new SgException("Offset must be a nonnegative integer.");
                var all = CodeReview.Read(root, path).Threads.Where(t => state == "all" || t.State == state).ToArray();
                return new { schema = 1, total = all.Length, threads = all.Skip(offset).Take(100).ToArray(), nextOffset = offset + 100 < all.Length ? (int?)(offset + 100) : null };
            case "thread": return CodeReview.Context(root, path, args.Arg(1, "thread id"));
            case "comment":
                var range = (args.Get("--lines") ?? "0:0").Split(':');
                if (range.Length > 2 || !int.TryParse(range[0], out var first) || !int.TryParse(range[^1], out var last)) throw new SgException("Use --lines first:last, or 0:0 for the whole file.");
                return CodeReview.Add(root, path, CodeReview.ReadFile(root, path, Required("--file")), args.Get("--side") ?? "modified", first, last, Body(), actor, args.Get("--request-id"));
            case "reply": case "resolve": case "reopen":
                return CodeReview.Address(root, path, args.Arg(1, "thread id"), action, Body(), Required("--expected-revision"), actor, args.Get("--version"), args.Get("--request-id"));
            case "export":
                var json = CodeReview.Encode(CodeReview.Read(root, path));
                if (args.Get("--out", "-o") is { } destination) AtomicFile.WriteAllText(Path.GetFullPath(destination), json);
                return JsonDocument.Parse(json).RootElement.Clone();
            case "handoff": return new { instructions = CodeReview.Handoff(root, path) };
            default: throw new SgException("Unknown review action: " + action);
        }
    }
}
