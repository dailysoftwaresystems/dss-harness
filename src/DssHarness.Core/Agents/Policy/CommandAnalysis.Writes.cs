namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>
    /// The programs whose arguments name the files they write - make, change, move into, delete - each by its name in any
    /// case: the file-writing commands of the shells and of PowerShell, and its aliases of them. What one of them names
    /// is a write the policy decides as a file tool's write would be decided by the refusals every ground keeps
    /// (<see cref="WriteTargets"/>).
    /// </summary>
    public static IReadOnlyList<string> FileWriters { get; } =
    [
        "tee", "cp", "mv", "install", "ln", "touch", "truncate", "rm", "rmdir", "unlink", "shred", "mkdir", "chmod", "chown",
        "chgrp", "rsync", "copy", "move", "del", "erase", "ren", "rename", "md", "rd", "Set-Content", "Add-Content",
        "Clear-Content", "Out-File", "New-Item", "Remove-Item", "Copy-Item", "Move-Item", "Rename-Item", "sc", "ac", "clc", "ni",
        "ri", "cpi", "mi", "rni",
    ];

    /// <summary>
    /// The writers of <see cref="FileWriters"/> that write only where they copy or link to, and read the rest: what they
    /// read is no write. Each names where it writes in its own way (<see cref="CopyDestinations"/>).
    /// </summary>
    public static IReadOnlyList<string> CopyWriters { get; } = ["cp", "install", "ln", "rsync", "copy", "Copy-Item", "cpi"];

    /// <summary>The programs that write the files they read where an in-place option is among their words: <c>-i</c> and its kin.</summary>
    public static IReadOnlyList<string> InPlaceEditors { get; } = ["sed", "perl", "ruby"];

    /// <summary>The copy writers that name a target directory by GNU's <c>-t</c> and <c>--target-directory</c>.</summary>
    private static readonly string[] TargetDirectoryWriters = ["cp", "install", "ln"];

    /// <summary>
    /// The short options of GNU's copy writers that take a value besides <c>-t</c>: <c>cp</c>'s and <c>ln</c>'s suffix,
    /// <c>install</c>'s group, mode, owner and suffix. A value-taking option takes the rest of its cluster, or the next word.
    /// </summary>
    private const string GnuValued = "gmoSt";

    /// <summary>The PowerShell copy cmdlets, and the <c>copy</c> both PowerShell and cmd.exe name.</summary>
    private static readonly string[] PowerShellCopies = ["Copy-Item", "cpi", "copy"];

    /// <summary>
    /// The parameters of PowerShell's copy cmdlets that take a value, in lower case, the common parameters' among them: a
    /// word after one is its value, never a path the cmdlet copies or copies to.
    /// </summary>
    private static readonly string[] PowerShellCopyValued =
    [
        "path", "literalpath", "pspath", "lp", "destination", "filter", "include", "exclude", "credential", "fromsession", "tosession",
        "erroraction", "ea", "warningaction", "wa", "informationaction", "infa", "errorvariable", "ev", "warningvariable", "wv",
        "informationvariable", "iv", "outvariable", "ov", "outbuffer", "ob", "pipelinevariable", "pv", "progressaction", "proga",
    ];

    /// <summary>
    /// Every place a command of <paramref name="analyzed"/>'s writes, as it spells it: each file an output redirect names
    /// (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>&lt;&gt;</c>), and - where the program at <paramref name="start"/> is one of
    /// <see cref="FileWriters"/>, or one of <see cref="InPlaceEditors"/> given an in-place option - each path its words
    /// name (<see cref="PathsNamed"/>): every path of a word that is no option, and every value an option's word gives -
    /// glued after its <c>=</c>, a PowerShell parameter's <c>:</c> or a short option's letter. A copy writer's own are
    /// where it copies to alone (<see cref="CopyDestinations"/>); <c>dd</c>'s is its <c>of=</c>. A word that is the
    /// program's data and no path - a mode, a script - names a place no refusal holds, so taking it for one refuses
    /// nothing that runs.
    /// </summary>
    /// <param name="analyzed">The command, as the analysis read it.</param>
    /// <param name="start">Where its program stands among its words: past its environment, or where a wrapper's program is.</param>
    /// <returns>Each place, as spelled; empty where the command writes no file this reads.</returns>
    public static IReadOnlyList<string> WriteTargets(AnalyzedCommand analyzed, int start)
    {
        ArgumentNullException.ThrowIfNull(analyzed);

        var targets = analyzed.Redirects
            .Where(redirect => redirect.Operator is ">" or ">>" or "<>")
            .SelectMany(redirect => PathsNamed(redirect.Target).Where(path => path.Whole).Select(path => path.Path))
            .ToList();

        var words = analyzed.Words;

        if (start < 0 || start >= words.Count)
        {
            return targets;
        }

        var program = Name(words[start]);
        var arguments = words.Skip(start + 1).ToList();

        if (Is(program, "dd"))
        {
            targets.AddRange(arguments.Where(word => word.StartsWith("of=", StringComparison.Ordinal)).Select(word => word["of=".Length..]));
            return targets;
        }

        var inPlace = Is(program, [.. InPlaceEditors])
            && arguments.Any(word => word.StartsWith("--in-place", StringComparison.Ordinal) || (word.StartsWith('-') && !word.StartsWith("--", StringComparison.Ordinal) && word.Contains('i', StringComparison.Ordinal)));

        if (!inPlace && !Is(program, [.. FileWriters]))
        {
            return targets;
        }

        if (Is(program, [.. CopyWriters]))
        {
            targets.AddRange(CopyDestinations(program, arguments).SelectMany(word => PathsNamed(word).Where(path => path.Whole).Select(path => path.Path)));
            return targets;
        }

        var ended = false;

        foreach (var word in arguments)
        {
            if (!ended && word == "--")
            {
                ended = true;
                continue;
            }

            // A word that is no option names its paths; an option's word names the values it gives, never itself. A word of
            // text a writer is handed (Set-Content -Value 'see .git/x') is what it writes, not where.
            targets.AddRange(PathsNamed(word)
                .Where(path => (ended || !word.StartsWith('-') || path.Path != word) && (path.Whole || !word.Any(char.IsWhiteSpace)))
                .Select(path => path.Path));
        }

        return targets;
    }

    /// <summary>
    /// The words a copy writer copies or links to, among <paramref name="arguments"/>, by its own way of naming them:
    /// GNU's <c>cp</c>, <c>install</c> and <c>ln</c> the directory <c>-t</c> or <c>--target-directory</c> names in every
    /// form - its own word or the next, after an <c>=</c>, glued to the letter, at the end of a cluster of short options -
    /// else their last word that is no option nor an option's value; PowerShell's <c>Copy-Item</c> and <c>cpi</c>, and the
    /// <c>copy</c> both PowerShell and cmd.exe name, the value of <c>-Destination</c> (any prefix PowerShell takes,
    /// glued after a <c>:</c> too), else every word that is no parameter nor a parameter's value but the first, which
    /// is the path copied - every one of them where <c>-Path</c> or <c>-LiteralPath</c> names that; <c>rsync</c> its last
    /// word that is no option.
    /// </summary>
    /// <param name="program">The copy writer's name.</param>
    /// <param name="arguments">Its words after its name.</param>
    private static IReadOnlyList<string> CopyDestinations(string program, IReadOnlyList<string> arguments)
    {
        if (Is(program, [.. PowerShellCopies]))
        {
            return PowerShellCopyDestinations(arguments);
        }

        var gnu = Is(program, [.. TargetDirectoryWriters]);
        var named = new List<string>();
        var plain = new List<string>();
        var ended = false;

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (!ended && word == "--")
            {
                ended = true;
                continue;
            }

            if (ended || !word.StartsWith('-') || word == "-")
            {
                plain.Add(word);
                continue;
            }

            if (!gnu)
            {
                continue;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                // --target-directory, by any prefix getopt takes of it - none of the three has another long option
                // beginning '--t' - its value glued after an '=' or the next word.
                var name = word.Split('=', 2);

                if (name[0].Length >= 3 && "--target-directory".StartsWith(name[0], StringComparison.Ordinal))
                {
                    if (name.Length == 2)
                    {
                        named.Add(name[1]);
                    }
                    else if (at + 1 < arguments.Count)
                    {
                        named.Add(arguments[++at]);
                    }
                }

                continue;
            }

            // A cluster of short options, read as getopt reads one: the first that takes a value takes the rest of the
            // cluster, or the next word where it ends the cluster.
            for (var letter = 1; letter < word.Length; letter++)
            {
                if (!GnuValued.Contains(word[letter], StringComparison.Ordinal))
                {
                    continue;
                }

                var value = letter + 1 < word.Length ? word[(letter + 1)..] : at + 1 < arguments.Count ? arguments[++at] : null;

                if (word[letter] == 't' && value is not null)
                {
                    named.Add(value);
                }

                break;
            }
        }

        return named.Count > 0 ? named : plain.Count > 0 ? [plain[^1]] : [];
    }

    /// <summary>
    /// Where PowerShell's <c>Copy-Item</c> copies to, among <paramref name="arguments"/>: <c>-Destination</c>'s value, else
    /// the words it binds to <c>-Destination</c> by place - every word that is no parameter nor a parameter's value, but
    /// the first where no <c>-Path</c> or <c>-LiteralPath</c> names the path copied. A <c>/</c> word is one of cmd.exe's
    /// own switches, which its <c>copy</c> takes before its paths.
    /// </summary>
    private static IReadOnlyList<string> PowerShellCopyDestinations(IReadOnlyList<string> arguments)
    {
        var destinations = new List<string>();
        var positional = new List<string>();
        var pathNamed = false;

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (word.StartsWith('/') && word.Length == 2)
            {
                continue;
            }

            if (word.Length < 2 || word[0] != '-')
            {
                positional.Add(word);
                continue;
            }

            var spelled = word[1..].Split(':', 2);
            var parameter = spelled[0].ToLowerInvariant();
            var glued = spelled.Length == 2 ? spelled[1] : null;
            var value = glued;

            // A parameter by any prefix PowerShell takes: -Destination's from '-des', as '-d' and '-de' are Debug's too.
            var takes = PowerShellCopyValued.Where(name => name.StartsWith(parameter, StringComparison.Ordinal)).ToList();

            if (takes.Count == 0)
            {
                continue;
            }

            if (value is null && at + 1 < arguments.Count)
            {
                value = arguments[++at];
            }

            if (parameter.Length >= 3 && "destination".StartsWith(parameter, StringComparison.Ordinal))
            {
                if (value is not null)
                {
                    destinations.Add(value);
                }
            }
            else if (takes.Any(name => name is "path" or "literalpath" or "pspath" or "lp"))
            {
                pathNamed = true;
            }
        }

        return destinations.Count > 0 ? destinations
            : pathNamed ? positional
            : [.. positional.Skip(1)];
    }
}
