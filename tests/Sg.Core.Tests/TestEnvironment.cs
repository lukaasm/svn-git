using System.Runtime.CompilerServices;

namespace Sg.Core.Tests;

/// <summary>
/// Every test runs against git as a fresh CI runner has it: with no global config. A developer's own
/// ~/.gitconfig - a name and an email, autocrlf, aliases - let tests pass on the machine they were
/// written on and fail on CI, where a commit made with no identity is refused.
/// </summary>
static class TestEnvironment
{
    [ModuleInitializer]
    internal static void NoGlobalGitConfig()
    {
        var empty = Path.Combine(Path.GetTempPath(), "sg-tests-empty.gitconfig");
        if (!File.Exists(empty)) File.WriteAllText(empty, "");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", empty);
    }
}
