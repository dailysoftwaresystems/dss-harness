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
    /// The writers of <see cref="FileWriters"/> that write only where they copy or link to - their last word, or the
    /// directory an option names - and read the rest: what they read is no write.
    /// </summary>
    public static IReadOnlyList<string> CopyWriters { get; } = ["cp", "install", "ln", "rsync", "copy", "Copy-Item", "cpi"];

    /// <summary>The programs that write the files they read where an in-place option is among their words: <c>-i</c> and its kin.</summary>
    public static IReadOnlyList<string> InPlaceEditors { get; } = ["sed", "perl", "ruby"];

    /// <summary>
    /// Every place a command of <paramref name="analyzed"/>'s writes, as it spells it: each file an output redirect names
    /// (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>&lt;&gt;</c>), and - where the program at <paramref name="start"/> is one of
    /// <see cref="FileWriters"/>, or one of <see cref="InPlaceEditors"/> given an in-place option - each of its words that
    /// is no option (of a <see cref="CopyWriters"/> its last alone), a value glued after an option's <c>=</c> or a
    /// PowerShell parameter's <c>:</c> among them, and <c>dd</c>'s <c>of=</c>. A word that is the program's data and no
    /// path - a mode, a script - names a place no refusal holds, so taking it for one refuses nothing that runs.
    /// </summary>
    /// <param name="analyzed">The command, as the analysis read it.</param>
    /// <param name="start">Where its program stands among its words: past its environment, or where a wrapper's program is.</param>
    /// <returns>Each place, as spelled; empty where the command writes no file this reads.</returns>
    public static IReadOnlyList<string> WriteTargets(AnalyzedCommand analyzed, int start)
    {
        ArgumentNullException.ThrowIfNull(analyzed);

        var targets = analyzed.Redirects
            .Where(redirect => redirect.Operator is ">" or ">>" or "<>")
            .Select(redirect => redirect.Target)
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

        var ended = false;
        var copies = Is(program, [.. CopyWriters]);
        string? last = null;

        foreach (var word in arguments)
        {
            if (!ended && word == "--")
            {
                ended = true;
                continue;
            }

            if (ended || !word.StartsWith('-'))
            {
                if (copies)
                {
                    last = word;
                }
                else
                {
                    targets.Add(word);
                }

                continue;
            }

            // An option's value glued to it: --target-directory=x, a PowerShell parameter's -Path:x.
            if (word.IndexOf('=', StringComparison.Ordinal) is var equals and > 0 && equals + 1 < word.Length)
            {
                targets.Add(word[(equals + 1)..]);
            }
            else if (word.IndexOf(':', StringComparison.Ordinal) is var colon and > 1 && colon + 1 < word.Length)
            {
                targets.Add(word[(colon + 1)..]);
            }
        }

        if (last is not null)
        {
            targets.Add(last);
        }

        return targets;
    }
}
