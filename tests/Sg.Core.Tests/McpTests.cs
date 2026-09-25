using System.Diagnostics;
using System.Text.Json;

namespace Sg.Core.Tests;

// The stdio deadline measures the server, not contention from parallel Git/SVN fixture processes.
[CollectionDefinition("MCP process integration", DisableParallelization = true)]
public sealed class McpProcessCollection;

[Collection("MCP process integration")]
public sealed class McpTests
{
    sealed class Client : IDisposable
    {
        readonly Process process;
        readonly Task<string> errors;
        int nextId;
        public Client(bool apphost)
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder != null && !File.Exists(Path.Combine(folder.FullName, "src", "sg", "sg.csproj"))) folder = folder.Parent;
            Assert.NotNull(folder);
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var dll = Path.Combine(folder.FullName, "src", "sg", "bin", configuration, "net10.0", "sg.dll");
            Assert.True(File.Exists(dll), "CLI build missing: " + dll);
            var start = new ProcessStartInfo(apphost ? Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null) : "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            if (!apphost) start.ArgumentList.Add(dll);
            start.ArgumentList.Add("mcp");
            process = Process.Start(start)!;
            errors = process.StandardError.ReadToEndAsync();
        }
        public async Task<JsonElement> Call(string method, object parameters)
        {
            var id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (true)
            {
                string? line;
                try { line = await process.StandardOutput.ReadLineAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(true); await process.WaitForExitAsync();
                    throw new InvalidOperationException("MCP timeout in " + method + ": " + JsonSerializer.Serialize(parameters) + "\n" + await errors);
                }
                Assert.True(line != null, "MCP exited: " + (errors.IsCompleted ? await errors : "no diagnostics"));
                using var doc = JsonDocument.Parse(line!); // Any stdout noise is a protocol failure.
                var message = doc.RootElement;
                if (!message.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                Assert.False(message.TryGetProperty("error", out _), line);
                return message.GetProperty("result").Clone();
            }
        }
        public async Task Init()
        {
            var result = await Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "sg-tests", version = "1" } });
            Assert.Equal("sg", result.GetProperty("serverInfo").GetProperty("name").GetString());
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.FlushAsync();
        }
        public async Task<JsonElement> Tool(string name, object arguments, bool error = false)
        {
            var result = await Call("tools/call", new { name, arguments });
            Assert.Equal(error, result.TryGetProperty("isError", out var flag) && flag.GetBoolean());
            if (error) return result;
            return JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
        }
        public void Dispose()
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(3000)) { process.Kill(true); process.WaitForExit(); }
            process.Dispose();
        }
    }

    [Fact]
    public async Task Transfer_and_rename_require_current_preview_tokens_through_stdio()
    {
        using var fixture = new Fixture(); fixture.Setup();
        using var client = new Client(false); await client.Init();
        Fixture.Put(fixture.Checkout, "CMakeLists.txt", "project(transferred)\n");
        var args = new[] { "received", "--from", fixture.Co.Name, "--new" };
        var preview = await client.Tool("sg_transfer", new { workingDirectory = fixture.RootDir, arguments = args });
        using var plan = JsonDocument.Parse(preview.GetProperty("output").GetString()!);
        Assert.True(plan.RootElement.GetProperty("canApply").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(fixture.RootDir, "received")));
        await client.Tool("sg_transfer", new { workingDirectory = fixture.RootDir, arguments = args.Concat(new[] { "--yes", "--version", "stale" }).ToArray() }, error: true);
        var token = plan.RootElement.GetProperty("token").GetString()!;
        await client.Tool("sg_transfer", new { workingDirectory = fixture.RootDir, arguments = args.Concat(new[] { "--yes", "--version", token }).ToArray() });
        var received = Path.Combine(fixture.RootDir, "received");
        Assert.Equal("project(transferred)\n", File.ReadAllText(Path.Combine(received, "CMakeLists.txt")));
        var rename = await client.Tool("sg_rename", new { workingDirectory = fixture.RootDir, arguments = new[] { "received", "renamed" } });
        using var renamePlan = JsonDocument.Parse(rename.GetProperty("output").GetString()!);
        await client.Tool("sg_rename", new { workingDirectory = fixture.RootDir, arguments = new[] { "received", "renamed", "--yes" } }, error: true);
        Assert.True(Directory.Exists(received));
        await client.Tool("sg_rename", new { workingDirectory = fixture.RootDir, arguments = new[] { "received", "renamed", "--yes", "--version", renamePlan.RootElement.GetProperty("token").GetString()! } });
        Assert.False(Directory.Exists(received));
        Assert.Equal("project(transferred)\n", File.ReadAllText(Path.Combine(fixture.RootDir, "renamed", "CMakeLists.txt")));
        Assert.Equal("project(transferred)\n", File.ReadAllText(Path.Combine(fixture.Checkout, "CMakeLists.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stdio_exposes_every_command_family_and_preserves_review_freshness(bool apphost)
    {
        using var fixture = new Fixture(); fixture.Setup();
        var path = Ops.Branch(fixture.Root, "mcp-review", fixture.Co).Path;
        using var client = new Client(apphost); await client.Init();
        var list = await client.Call("tools/list", new { });
        var names = list.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        foreach (var command in "init checkout sync branch transfer rename branch-update activity review storage handoff rebase resolve push rm shelve shelf export import backup status server-branch server-checkout update version".Split(' '))
            Assert.Contains("sg_" + command.Replace('-', '_'), names);
        Assert.Equal(34, names.Count);
        Assert.Contains("sg_review_inbox", names);
        var version = await client.Tool("sg_version", new { workingDirectory = path });
        Assert.Equal(0, version.GetProperty("exitCode").GetInt32());
        await client.Tool("sg_status", new { workingDirectory = "relative" }, error: true);
        await client.Tool("sg_review_file", new { worktree = path, file = "../outside.txt" }, error: true);
        var file = await client.Tool("sg_review_file", new { worktree = path, file = "CMakeLists.txt" });
        var comment = await client.Tool("sg_review_comment", new { worktree = path, file = "CMakeLists.txt", body = "Explain this name", firstLine = 1, lastLine = 1, codeVersion = file.GetProperty("version").GetString() });
        var id = comment.GetProperty("id").GetString()!;
        var revision = comment.GetProperty("revision").GetString()!;
        var context = await client.Tool("sg_review_context", new { worktree = path, threadId = id });
        Fixture.Put(path, "CMakeLists.txt", "project(updated)\n");
        await client.Tool("sg_review_address", new { worktree = path, threadId = id, action = "resolve", body = "Fixed", expectedRevision = revision, codeVersion = context.GetProperty("version").GetString() }, error: true);
        context = await client.Tool("sg_review_context", new { worktree = path, threadId = id });
        var resolved = await client.Tool("sg_review_address", new { worktree = path, threadId = id, action = "resolve", body = "Updated and checked", expectedRevision = revision, codeVersion = context.GetProperty("version").GetString() });
        Assert.Equal("resolved", resolved.GetProperty("state").GetString());
        var threads = await client.Tool("sg_review_threads", new { worktree = path });
        Assert.Equal(0, threads.GetProperty("total").GetInt32());
        var inbox = await client.Tool("sg_review_inbox", new { workingDirectory = fixture.RootDir, state = "resolved", query = "review", worktree = "mcp-review" });
        Assert.Equal(id, Assert.Single(inbox.GetProperty("threads").EnumerateArray()).GetProperty("thread").GetProperty("id").GetString());
        await client.Tool("sg_review_inbox", new { workingDirectory = fixture.RootDir, state = "invalid" }, error: true);
        var inboxCli = await client.Tool("sg_review", new { workingDirectory = fixture.RootDir, arguments = new[] { "inbox", "--state", "resolved" } });
        Assert.Contains(id, inboxCli.GetProperty("output").GetString());
        Assert.Equal("project(updated)\n", File.ReadAllText(Path.Combine(path, "CMakeLists.txt")));
        // The CLI family reaches the same store and keeps malformed user input a tool error.
        var cli = await client.Tool("sg_review", new { workingDirectory = path, arguments = new[] { "threads", "--state", "all" } });
        Assert.Contains(id, cli.GetProperty("output").GetString());
        await client.Tool("sg_review", new { workingDirectory = path, arguments = new[] { "threads", "--offset", "invalid" } }, error: true);
    }
}
