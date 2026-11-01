namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// One pathspec a git command is given, as git reads it: the path it names past its magic, from the repository's root
/// where <see cref="Top"/> says so - <c>:/</c>, <c>:(top)</c> - and a pattern git expands itself unless
/// <see cref="Literal"/> says it is not, quoted or not, its wildcards matching names that begin with a dot.
/// </summary>
/// <param name="Word">Which word of the command it is.</param>
/// <param name="Path">The path past its magic.</param>
/// <param name="Top">Whether it is read from the repository's root rather than where the command stands.</param>
/// <param name="Literal">Whether git reads it as the path it spells, no pattern (<c>:(literal)</c>).</param>
/// <param name="Glob">Whether git reads its pattern as a shell's (<c>:(glob)</c>): a star within a name, <c>**</c> across them; else a star and a question mark match a separator too.</param>
internal sealed record GitPathspec(int Word, string Path, bool Top, bool Literal, bool Glob = false);

public static partial class CommandAnalysis
{
    /// <summary>
    /// The git subcommands that read only what git tracks - its history, its index, the files it tracks - each word they
    /// are given naming only what git tracks, never a file it ignores: none of them reaches what a directory holds past
    /// that. <c>diff</c> and <c>grep</c> are read so only without <c>--no-index</c> (and <c>--untracked</c>).
    /// </summary>
    private static readonly string[] TrackedReaders =
    [
        "status", "diff", "log", "show", "ls-files", "ls-tree", "grep", "blame", "annotate", "shortlog", "whatchanged",
        "diff-tree", "diff-index", "diff-files", "rev-list", "rev-parse", "describe", "cat-file", "check-ignore", "check-attr",
        "reflog", "show-ref", "name-rev", "for-each-ref", "cherry", "range-diff",
    ];

    /// <summary>
    /// Whether the program at <paramref name="start"/> is git running a subcommand that reads only what git tracks
    /// (<see cref="TrackedReaders"/>): every word of it names only what git tracks.
    /// </summary>
    internal static bool ReadsTrackedOnly(IReadOnlyList<string> words, int start)
    {
        if (start < 0 || start >= words.Count || GitWords([.. words.Skip(start)]) is not { } git)
        {
            return false;
        }

        var at = GitSubcommandAt(git);

        return at < git.Count
            && TrackedReaders.Contains(git[at], StringComparer.Ordinal)
            && !git.Skip(at + 1).TakeWhile(word => word != "--").Any(word => word is "--no-index" or "--untracked");
    }

    /// <summary>
    /// The pathspecs of the git subcommand at <paramref name="start"/> where it reaches what git does not track - a file
    /// it ignores among them: <c>add</c> with <c>-f</c>, <c>grep</c> with <c>--no-index</c> or <c>--untracked</c>,
    /// <c>clean</c>, <c>stash</c> that keeps untracked files (<c>-u</c>, <c>-a</c>), <c>diff --no-index</c> - each read as
    /// git reads it (<see cref="GitPathspec"/>), an excluding one (<c>:!</c>, <c>:^</c>, <c>:(exclude)</c>) left out, for
    /// it reaches nothing. None for every other git command.
    /// </summary>
    /// <param name="words">The command's words.</param>
    /// <param name="start">Where its program stands among them.</param>
    internal static IReadOnlyList<GitPathspec> UntrackedPathspecs(IReadOnlyList<string> words, int start)
    {
        if (start < 0 || start >= words.Count || GitWords([.. words.Skip(start)]) is not { } git)
        {
            return [];
        }

        // A dashed command's words are one more than the command's own: each word's place among the command's is kept.
        var shift = start - (git.Count - (words.Count - start));
        var at = GitSubcommandAt(git);

        if (at >= git.Count)
        {
            return [];
        }

        var subcommand = git[at];
        IReadOnlyList<string> rest = [.. git.Skip(at)];
        var options = rest.Skip(1).TakeWhile(word => word != "--").ToList();

        bool Given(string letter, params string[] names)
            => options.Any(word => names.Contains(word, StringComparer.Ordinal)
                || (word.Length > 1 && word[0] == '-' && word[1] != '-' && word.Contains(letter, StringComparison.Ordinal)));

        var (reaches, table, firstIsPattern) = subcommand switch
        {
            "add" => (Given("f", "--force"), AddOptions, false),
            "grep" => (options.Any(word => word is "--no-index" or "--untracked"), GrepOptions, true),
            "clean" => (true, CleanOptions, false),
            "diff" => (options.Contains("--no-index", StringComparer.Ordinal), DiffOptions, false),
            "stash" => (Given("u", "--include-untracked") || Given("a", "--all"), StashOptions, false),
            _ => (false, AddOptions, false),
        };

        if (!reaches)
        {
            return [];
        }

        var (given, operands, _) = Getopt(rest, table);
        IEnumerable<(string Word, int At)> paths = operands;

        // stash's own subcommand, and grep's pattern where no -e or -f gives one, are no pathspecs.
        if (subcommand == "stash")
        {
            paths = paths.SkipWhile(operand => operand.Word is "push" or "save");
        }

        // grep's pattern is its first operand before a '--': one after it is a pathspec, the command given no pattern.
        var ended = Enumerable.Range(0, rest.Count).FirstOrDefault(at => rest[at] == "--", -1);

        if (firstIsPattern && !given.Any(option => option.Option is "e" or "f" or "regexp" or "file") && paths.FirstOrDefault() is { Word: not null } first && (ended < 0 || first.At < ended))
        {
            paths = paths.Skip(1);
        }

        var pathspecs = new List<GitPathspec>();

        foreach (var (word, place) in paths)
        {
            if (Pathspec(word) is { } read)
            {
                pathspecs.Add(read with { Word = place + at + shift });
            }
        }

        return pathspecs;
    }

    /// <summary>
    /// A pathspec as git reads it: its long magic (<c>:(top,icase,glob,literal,exclude)</c>) or its short magic (<c>:/</c>,
    /// <c>:!</c>, <c>:^</c>), then its path; null for one that excludes what it names.
    /// </summary>
    private static GitPathspec? Pathspec(string word)
    {
        if (!word.StartsWith(':'))
        {
            return new GitPathspec(0, word, Top: false, Literal: false);
        }

        if (word.StartsWith(":(", StringComparison.Ordinal) && word.IndexOf(')', StringComparison.Ordinal) is var close and > 1)
        {
            var magic = word[2..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            return magic.Any(name => name is "exclude")
                ? null
                : new GitPathspec(0, Named(word[(close + 1)..]), Top: magic.Contains("top"), Literal: magic.Contains("literal"), Glob: magic.Contains("glob"));
        }

        // Short magic: its letters to a ':' or the first that is none.
        var at = 1;
        var top = false;

        while (at < word.Length && word[at] is '/' or '!' or '^')
        {
            if (word[at] is '!' or '^')
            {
                return null;
            }

            top = true;
            at++;
        }

        if (at < word.Length && word[at] == ':')
        {
            at++;
        }

        return new GitPathspec(0, Named(word[at..]), top, Literal: false);

        // A pathspec that names nothing past its magic names where it is read from.
        static string Named(string path) => path.Length == 0 ? "." : path;
    }

    /// <summary>git add's options.</summary>
    private static readonly OptionTable AddOptions = new(string.Empty, string.Empty, ["chmod", "pathspec-from-file"], [], ["force", "all", "update", "verbose", "dry-run", "intent-to-add", "refresh", "ignore-errors", "ignore-missing", "renormalize", "no-all", "no-ignore-removal", "sparse", "patch", "interactive", "edit", "pathspec-file-nul"]);

    /// <summary>git grep's options.</summary>
    private static readonly OptionTable GrepOptions = new("efABCm", "O", ["regexp", "file", "after-context", "before-context", "context", "max-count", "max-depth", "threads"], ["open-files-in-pager", "color"], ["no-index", "untracked", "cached", "recurse-submodules", "text", "ignore-case", "word-regexp", "invert-match", "full-name", "extended-regexp", "basic-regexp", "perl-regexp", "fixed-strings", "line-number", "column", "files-with-matches", "files-without-match", "null", "count", "heading", "break", "show-function", "function-context", "and", "or", "not", "all-match", "quiet", "only-matching", "exclude-standard", "no-exclude-standard"]);

    /// <summary>git clean's options.</summary>
    private static readonly OptionTable CleanOptions = new("e", string.Empty, ["exclude"], [], ["force", "dry-run", "quiet", "interactive"]);

    /// <summary>git diff's options that take the next word.</summary>
    private static readonly OptionTable DiffOptions = new(string.Empty, "UMCBlS", ["output", "src-prefix", "dst-prefix", "line-prefix", "relative", "anchored", "diff-algorithm", "word-diff-regex", "color-words", "ignore-matching-lines", "inter-hunk-context", "stat-width", "stat-name-width", "stat-count", "stat-graph-width", "abbrev", "diff-filter", "orderfile", "rotate-to", "skip-to"], ["stat", "dirstat", "unified", "color", "word-diff", "submodule", "ignore-submodules", "find-renames", "find-copies", "break-rewrites"], ["no-index", "cached", "staged", "name-only", "name-status", "patch", "no-patch", "raw", "quiet", "exit-code"]);

    /// <summary>git stash's options.</summary>
    private static readonly OptionTable StashOptions = new("m", string.Empty, ["message", "pathspec-from-file"], [], ["include-untracked", "all", "keep-index", "no-keep-index", "patch", "staged", "quiet", "pathspec-file-nul", "only-untracked"]);

    /// <summary>
    /// The words of <paramref name="words"/> that name what a program leaves out rather than reads: an exclude's or an
    /// include's pattern - <c>--exclude</c>, <c>--exclude-dir</c>, <c>--include</c>, <c>--include-dir</c> - glued after an
    /// <c>=</c> or the word after it. An exclude read from a file (<c>--exclude-from</c>) is a read of that file.
    /// </summary>
    internal static IReadOnlyList<int> ExcludeWords(IReadOnlyList<string> words)
    {
        var excludes = new List<int>();

        for (var at = 0; at < words.Count; at++)
        {
            var name = words[at].Split('=', 2)[0];

            if (name is not ("--exclude" or "--exclude-dir" or "--include" or "--include-dir"))
            {
                continue;
            }

            if (words[at].Contains('=', StringComparison.Ordinal))
            {
                excludes.Add(at);
            }
            else if (at + 1 < words.Count)
            {
                excludes.Add(++at);
            }
        }

        return excludes;
    }
}
