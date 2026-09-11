using System.Text.RegularExpressions;

namespace Sg.Core;

/// <summary>The one rule for new server branches: the last "trunk" or "branches/x" in a URL becomes "branches/name".</summary>
public static class BranchRule
{
    public static string NewUrl(string url, string name, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (overrides != null)
            foreach (var (prefix, template) in overrides)
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return template.Replace("{name}", name) + url[prefix.Length..];

        var parts = url.Split('/');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i] == "trunk")
                return string.Join("/", parts[..i].Concat(["branches", name]).Concat(parts[(i + 1)..]));
            if (parts[i] == "branches" && i + 1 < parts.Length)
                return string.Join("/", parts[..(i + 1)].Concat([name]).Concat(parts[(i + 2)..]));
        }
        throw new SgException($"no 'trunk' or 'branches/<x>' in {url}. Add a branchUrlOverrides entry to sg.json for it.");
    }
}

/// <summary>Reads and rewrites svn:externals values without losing their layout.</summary>
public static class Externals
{
    static readonly Regex UrlLike = new(@"^(?:[a-z][a-z0-9+.-]*://|\^/|//|/|\.\./)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    sealed record Token(string Text, bool Quoted);

    /// <summary>Applies newUrl to the URL token of every definition line. Comments, blank lines, pegs, and quoting stay as they were.</summary>
    public static string Rewrite(string value, Func<string, string> newUrl) => Rewrite(value, (url, _) => newUrl(url));

    /// <summary>
    /// The same, with the local name of each definition beside its URL, so a caller can tell the
    /// externals apart. A null answer leaves that line exactly as it was, spacing included.
    /// </summary>
    public static string Rewrite(string value, Func<string, string, string?> newUrl)
    {
        var result = new List<string>();
        foreach (var line in value.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { result.Add(line); continue; }
            var tokens = Tokenize(trimmed);
            var idx = tokens.FindIndex(t => UrlLike.IsMatch(SplitPeg(t.Text).Url));
            if (idx < 0) throw new SgException("no URL in this svn:externals line: " + line);
            var (url, peg) = SplitPeg(tokens[idx].Text);
            var replaced = newUrl(url, LocalName(tokens, idx));
            if (replaced == null) { result.Add(line); continue; }
            tokens[idx] = tokens[idx] with { Text = replaced + peg };
            result.Add(string.Join(" ", tokens.Select(t => t.Quoted ? "\"" + t.Text + "\"" : t.Text)));
        }
        return string.Join("\n", result);
    }

    /// <summary>The URL and local name of every definition line.</summary>
    public static List<(string Url, string Name)> Definitions(string value)
    {
        var res = new List<(string, string)>();
        foreach (var line in value.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var tokens = Tokenize(trimmed);
            var idx = tokens.FindIndex(t => UrlLike.IsMatch(SplitPeg(t.Text).Url));
            if (idx < 0) continue;
            res.Add((SplitPeg(tokens[idx].Text).Url, LocalName(tokens, idx)));
        }
        return res;
    }

    /// <summary>The token that is neither the URL nor a -r revision: the folder the external lands in.</summary>
    static string LocalName(List<Token> tokens, int urlIndex) =>
        tokens.Where((t, i) => i != urlIndex && !t.Text.StartsWith("-r") && !(i > 0 && tokens[i - 1].Text == "-r"))
            .Select(t => t.Text).LastOrDefault() ?? "";

    static (string Url, string Peg) SplitPeg(string token)
    {
        var at = token.LastIndexOf('@');
        if (at > 0 && at < token.Length - 1 && token[(at + 1)..].All(char.IsDigit)) return (token[..at], token[at..]);
        return (token, "");
    }

    static List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0) end = line.Length;
                tokens.Add(new Token(line[(i + 1)..end], true));
                i = end + 1;
            }
            else
            {
                var end = i;
                while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
                tokens.Add(new Token(line[i..end], false));
                i = end;
            }
        }
        return tokens;
    }
}
