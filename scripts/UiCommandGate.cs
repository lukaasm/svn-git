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
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        Console.Out.Write(await output);
        Console.Error.Write(await error);
        return child.ExitCode;
    }
}
