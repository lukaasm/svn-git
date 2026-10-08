using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Test-only executable proxy. A fixture can hold one Git preview read without changing app code.
internal static class UiCommandGate
{
    static async Task<int> Main(string[] args)
    {
        var directory = Environment.GetEnvironmentVariable("SG_UI_COMMAND_GATE")
            ?? throw new InvalidOperationException("The command gate needs its isolated fixture directory.");
        var executable = File.ReadAllText(Path.Combine(directory, "executable.txt")).Trim();
        var matchFile = Path.Combine(directory, "match.txt");
        var match = File.Exists(matchFile) ? File.ReadAllText(matchFile).Trim() : "--show-toplevel";
        if (args.Contains(match))
        {
            string? request = null;
            try { request = File.ReadAllText(Path.Combine(directory, "request.txt")); }
            catch (FileNotFoundException) { }
            if (request != null)
            {
                var parts = request.Trim().Split(':');
                var marker = Path.Combine(directory, parts[1]);
                File.WriteAllText(marker + ".entered", Environment.ProcessId.ToString());
                if (parts[0] == "fail")
                {
                    Console.Error.WriteLine("Intentional preview read failure from the UI test fixture.");
                    return 86;
                }
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!File.Exists(marker + ".release"))
                {
                    if (DateTime.UtcNow >= deadline) return 87;
                    await Task.Delay(20);
                }
            }
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        // Both ways, as the bytes arrive: git cat-file --batch-command lives as long as the app does and
        // answers each request on its own, so holding its output until it exits holds every read with it.
        _ = Pump(Console.OpenStandardInput(), child.StandardInput.BaseStream, closeTarget: true);
        var output = Pump(child.StandardOutput.BaseStream, Console.OpenStandardOutput());
        var error = Pump(child.StandardError.BaseStream, Console.OpenStandardError());
        await child.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return child.ExitCode;
    }

    static async Task Pump(Stream from, Stream to, bool closeTarget = false)
    {
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read));
                await to.FlushAsync();
            }
        }
        catch (IOException) { }
        finally { if (closeTarget) to.Close(); }
    }
}
