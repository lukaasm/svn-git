using System.Collections.Concurrent;

namespace Sg.Core;

/// <summary>
/// The git.exe sg starts. On Windows, "git" on PATH is Git for Windows' cmd\git.exe: a small wrapper
/// that sets a few variables and starts mingw64\bin\git.exe as a second process. sg starts git hundreds
/// of times in one operation, and the wrapper was 40% of each start (56 ms against 33 ms measured), so
/// sg starts the real one itself, with the same variables the wrapper sets. Anything else - a git
/// elsewhere, another platform - is started as configured.
/// </summary>
public static class GitExecutable
{
    public sealed record Resolved(string Exe, IReadOnlyDictionary<string, string> Env);

    static readonly ConcurrentDictionary<string, Resolved> Known = new(StringComparer.OrdinalIgnoreCase);
    static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>The platform folders Git for Windows ships git.exe in, with the MSYSTEM each one is.</summary>
    static readonly (string Dir, string System)[] Platforms =
        [(@"mingw64\bin", "MINGW64"), (@"clangarm64\bin", "CLANGARM64"), (@"mingw32\bin", "MINGW32")];

    public static Resolved Resolve(string configured) => Known.GetOrAdd(configured, Find);

    static Resolved Find(string configured)
    {
        if (!OperatingSystem.IsWindows()) return new(configured, None);
        try
        {
            var found = OnPath(configured);
            if (found == null) return new(configured, None);
            var dir = Path.GetDirectoryName(found)!;
            // The wrapper lives in <top>\cmd or <top>\bin; the real git in <top>\<platform>\bin.
            var top = Path.GetFileName(dir) is "cmd" or "bin" && !IsPlatformBin(dir) ? Path.GetDirectoryName(dir) : TopOfPlatformBin(dir);
            if (top == null || !Directory.Exists(Path.Combine(top, "usr", "bin"))) return new(configured, None);
            foreach (var (platform, system) in Platforms)
            {
                var real = Path.Combine(top, platform, "git.exe");
                if (File.Exists(real)) return new(real, Environment(top, platform, system));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new(configured, None);
    }

    static bool IsPlatformBin(string dir) => TopOfPlatformBin(dir) != null;

    static string? TopOfPlatformBin(string dir)
    {
        foreach (var (platform, _) in Platforms)
            if (dir.EndsWith(Path.DirectorySeparatorChar + platform, StringComparison.OrdinalIgnoreCase))
                return dir[..^(platform.Length + 1)];
        return null;
    }

    /// <summary>What the wrapper sets before it starts git: the platform and its tools first on PATH, the system, a home.</summary>
    static Dictionary<string, string> Environment(string top, string platform, string system)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Path.Combine(top, platform) + ";" + Path.Combine(top, "usr", "bin") + ";" + System.Environment.GetEnvironmentVariable("PATH"),
        };
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("MSYSTEM"))) env["MSYSTEM"] = system;
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("PLINK_PROTOCOL"))) env["PLINK_PROTOCOL"] = "ssh";
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("HOME")))
        {
            var drive = System.Environment.GetEnvironmentVariable("HOMEDRIVE");
            var path = System.Environment.GetEnvironmentVariable("HOMEPATH");
            var home = !string.IsNullOrEmpty(drive) && !string.IsNullOrEmpty(path) && Directory.Exists(drive + path)
                ? drive + path : System.Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(home)) env["HOME"] = home;
        }
        return env;
    }

    /// <summary>The file a bare name would start: the first match on PATH, with .exe when it has no extension.</summary>
    static string? OnPath(string exe)
    {
        if (Path.IsPathRooted(exe)) return File.Exists(exe) ? Path.GetFullPath(exe) : null;
        if (exe.Contains(Path.DirectorySeparatorChar) || exe.Contains(Path.AltDirectorySeparatorChar)) return null;
        var name = Path.HasExtension(exe) ? exe : exe + ".exe";
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim().Trim('"'), name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
