namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>
    /// The options a program takes, as getopt and getopt_long read them: the short letters that take a value - the rest of
    /// their cluster, or the next word - and those that take one glued alone, the long names that take a value - after an
    /// <c>=</c>, or the next word - those that take one after an <c>=</c> alone, and those that take none. A long option is
    /// named by its whole name or by any prefix that begins it alone.
    /// </summary>
    /// <param name="ShortValued">The short letters that take a value.</param>
    /// <param name="ShortOptional">The short letters that take a value glued to them alone.</param>
    /// <param name="LongValued">The long names that take a value, without their dashes.</param>
    /// <param name="LongOptional">The long names that take a value after an <c>=</c> alone.</param>
    /// <param name="LongFlags">The long names that take none.</param>
    private sealed record OptionTable(string ShortValued, string ShortOptional, string[] LongValued, string[] LongOptional, string[] LongFlags);

    /// <summary>One option a reading of a command's words found: its letter or its long name, its value, and the word that holds the value.</summary>
    /// <param name="Option">The short letter, or the long name the word names in full - the word's own spelling where it names none this knows.</param>
    /// <param name="Value">Its value, or null where it takes none.</param>
    /// <param name="Word">The word that holds its value, or the option's own where it takes none.</param>
    private sealed record GivenOption(string Option, string? Value, int Word);

    /// <summary>
    /// A command's words read as getopt_long reads them: its options, in order, each with its value, and its operands -
    /// every word that is no option nor an option's value, an option met past an operand read as one all the same, and
    /// every word after a <c>--</c> an operand. Unknown is whether a long option this does not know stands without an
    /// <c>=</c>: the word after it may be its value or an operand, which this cannot tell.
    /// </summary>
    /// <param name="words">The words, the program's name first.</param>
    /// <param name="table">The program's options.</param>
    private static (List<GivenOption> Options, List<(string Word, int At)> Operands, bool Unknown) Getopt(IReadOnlyList<string> words, OptionTable table)
    {
        var options = new List<GivenOption>();
        var operands = new List<(string Word, int At)>();
        var unknown = false;
        var ended = false;

        for (var at = 1; at < words.Count; at++)
        {
            var word = words[at];

            if (ended || word.Length < 2 || word[0] != '-')
            {
                operands.Add((word, at));
                continue;
            }

            if (word == "--")
            {
                ended = true;
                continue;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = word.IndexOf('=', StringComparison.Ordinal);
                var spelled = equals < 0 ? word[2..] : word[2..equals];
                var glued = equals < 0 ? null : word[(equals + 1)..];

                switch (LongOption(spelled, table))
                {
                    case (var name, true, _):
                        options.Add(glued is not null ? new GivenOption(name, glued, at)
                            : at + 1 < words.Count ? new GivenOption(name, words[at + 1], ++at)
                            : new GivenOption(name, null, at));
                        break;
                    case (var name, false, true):
                        options.Add(new GivenOption(name, glued, at));
                        break;
                    case (var name, false, false):
                        options.Add(new GivenOption(name, glued, at));
                        unknown |= glued is null && !table.LongFlags.Contains(name, StringComparer.Ordinal);
                        break;
                }

                continue;
            }

            for (var letter = 1; letter < word.Length; letter++)
            {
                var option = word[letter].ToString();

                if (table.ShortValued.Contains(word[letter], StringComparison.Ordinal))
                {
                    options.Add(letter + 1 < word.Length ? new GivenOption(option, word[(letter + 1)..], at)
                        : at + 1 < words.Count ? new GivenOption(option, words[at + 1], ++at)
                        : new GivenOption(option, null, at));
                    break;
                }

                if (table.ShortOptional.Contains(word[letter], StringComparison.Ordinal))
                {
                    options.Add(new GivenOption(option, letter + 1 < word.Length ? word[(letter + 1)..] : null, at));
                    break;
                }

                options.Add(new GivenOption(option, null, at));
            }
        }

        return (options, operands, unknown);
    }

    /// <summary>
    /// The long option <paramref name="spelled"/> names in <paramref name="table"/>: its whole name, or the one name it is a
    /// prefix of alone; whether it takes a value, and whether it takes one after an <c>=</c> alone. One it names none of,
    /// or more than one of, is itself and takes nothing this knows.
    /// </summary>
    private static (string Name, bool Valued, bool Optional) LongOption(string spelled, OptionTable table)
    {
        var all = table.LongValued.Select(name => (name, Valued: true, Optional: false))
            .Concat(table.LongOptional.Select(name => (name, Valued: false, Optional: true)))
            .Concat(table.LongFlags.Select(name => (name, Valued: false, Optional: false)))
            .ToList();

        if (all.FirstOrDefault(option => option.name == spelled) is { name: not null } exact)
        {
            return exact;
        }

        var prefixed = all.Where(option => spelled.Length > 0 && option.name.StartsWith(spelled, StringComparison.Ordinal)).ToList();

        return prefixed.Count == 1 ? prefixed[0] : (spelled, false, false);
    }

    /// <summary>GNU cp's options.</summary>
    private static readonly OptionTable CpOptions = new(
        "St",
        string.Empty,
        ["no-preserve", "sparse", "suffix", "target-directory"],
        ["backup", "preserve", "reflink", "update", "context"],
        ["archive", "attributes-only", "copy-contents", "dereference", "force", "interactive", "link", "no-dereference", "no-clobber",
         "parents", "recursive", "remove-destination", "strip-trailing-slashes", "symbolic-link", "no-target-directory",
         "one-file-system", "verbose", "debug", "keep-directory-symlink", "help", "version"]);

    /// <summary>GNU install's options; its <c>--strip-program</c> names a program it runs.</summary>
    private static readonly OptionTable InstallOptions = new(
        "gmoSt",
        string.Empty,
        ["group", "mode", "owner", "suffix", "target-directory", "strip-program"],
        ["backup", "context"],
        ["compare", "directory", "preserve-timestamps", "strip", "verbose", "no-target-directory", "debug", "preserve-context", "help", "version"]);

    /// <summary>GNU ln's options.</summary>
    private static readonly OptionTable LnOptions = new(
        "St",
        string.Empty,
        ["suffix", "target-directory"],
        ["backup"],
        ["directory", "force", "interactive", "logical", "no-dereference", "physical", "relative", "symbolic", "no-target-directory",
         "verbose", "help", "version"]);

    /// <summary>rsync's options: <c>-e</c>'s and <c>--rsh</c>'s value is the command it connects by.</summary>
    private static readonly OptionTable RsyncOptions = new(
        "efTBM@",
        string.Empty,
        ["rsh", "rsync-path", "exclude", "include", "exclude-from", "include-from", "files-from", "filter", "compare-dest", "copy-dest",
         "link-dest", "backup-dir", "suffix", "temp-dir", "partial-dir", "log-file", "log-file-format", "out-format", "password-file",
         "chmod", "chown", "usermap", "groupmap", "bwlimit", "timeout", "contimeout", "port", "sockopts", "max-size", "min-size",
         "max-delete", "max-alloc", "modify-window", "block-size", "checksum-choice", "compress-choice", "compress-level",
         "skip-compress", "info", "debug", "stderr", "outbuf", "remote-option", "address", "iconv", "only-write-batch", "write-batch",
         "read-batch", "protocol", "checksum-seed", "copy-as", "early-input", "stop-after", "stop-at", "trust-sender"],
        [],
        ["archive", "recursive", "verbose", "quiet", "progress", "partial", "delete", "delete-before", "delete-during", "delete-delay",
         "delete-after", "delete-excluded", "delete-missing-args", "remove-source-files", "checksum", "compress", "dry-run",
         "human-readable", "links", "perms", "times", "group", "owner", "devices", "specials", "update", "inplace", "append",
         "dirs", "mkpath", "relative", "hard-links", "acls", "xattrs", "executability", "sparse", "whole-file", "one-file-system",
         "existing", "ignore-existing", "ignore-times", "size-only", "stats", "itemize-changes", "list-only", "numeric-ids",
         "no-motd", "safe-links", "copy-links", "copy-dirlinks", "keep-dirlinks", "copy-unsafe-links", "backup", "cvs-exclude",
         "prune-empty-dirs", "force", "help", "version", "8-bit-output", "fuzzy", "omit-dir-times", "omit-link-times",
         "super", "fake-super", "atimes", "crtimes", "open-noatime", "preallocate", "secluded-args", "blocking-io", "ipv4", "ipv6"]);

    /// <summary>
    /// GNU tar's options, its old style's letters among them: <c>-C</c> moves where it reads and extracts, <c>-f</c> names
    /// its archive, and <c>-I</c>, <c>-F</c>, <c>--to-command</c>, <c>--checkpoint-action</c>, <c>--rsh-command</c> and
    /// the volume scripts name programs it runs.
    /// </summary>
    private static readonly OptionTable TarOptions = new(
        "bCfFgHIKLNTVX",
        string.Empty,
        ["file", "directory", "blocking-factor", "format", "use-compress-program", "starting-file", "tape-length", "newer",
         "after-date", "newer-mtime", "files-from", "label", "exclude-from", "exclude", "listed-incremental", "info-script",
         "new-volume-script", "to-command", "owner", "group", "mode", "mtime", "transform", "xform", "strip-components", "suffix",
         "checkpoint-action", "rsh-command", "volno-file", "record-size", "sparse-version", "pax-option", "quoting-style",
         "index-file", "level", "hole-detection", "warning", "set-mtime-format", "exclude-tag", "exclude-tag-under",
         "exclude-tag-all", "exclude-ignore", "exclude-ignore-recursive", "add-file", "owner-map", "group-map", "acls", "selinux"],
        ["backup", "checkpoint", "occurrence", "atime-preserve", "one-top-level", "sort"],
        ["create", "extract", "get", "list", "append", "update", "catenate", "concatenate", "delete", "diff", "compare", "test-label",
         "gzip", "gunzip", "ungzip", "bzip2", "xz", "lzip", "lzma", "lzop", "zstd", "compress", "uncompress", "auto-compress",
         "verbose", "keep-old-files", "overwrite", "preserve-permissions", "same-permissions", "no-same-owner", "same-owner",
         "absolute-names", "dereference", "hard-dereference", "recursion", "no-recursion", "wildcards", "no-wildcards",
         "anchored", "no-anchored", "totals", "help", "version", "usage", "verify", "remove-files", "skip-old-files",
         "keep-newer-files", "unlink-first", "recursive-unlink", "ignore-zeros", "ignore-failed-read", "numeric-owner",
         "show-transformed-names", "show-stored-names", "no-null", "null", "interactive", "confirmation", "multi-volume",
         "sparse", "seek", "no-seek", "utc", "full-time", "touch", "preserve-order", "same-order", "posix", "old-archive",
         "portability", "check-device", "no-check-device", "check-links", "no-unquote", "unquote", "ignore-case", "no-ignore-case",
         "exclude-backups", "exclude-caches", "exclude-caches-under", "exclude-caches-all", "exclude-vcs", "exclude-vcs-ignores",
         "no-acls", "no-selinux", "xattrs", "no-xattrs", "delay-directory-restore", "no-delay-directory-restore",
         "restrict", "force-local", "to-stdout", "no-overwrite-dir", "overwrite-dir", "show-defaults", "show-omitted-dirs",
         "block-number", "check-links", "clamp-mtime", "no-auto-compress", "read-full-records", "one-file-system", "preserve",
         "no-same-permissions", "keep-directory-symlink", "verbatim-files-from", "no-verbatim-files-from"]);

    /// <summary>GNU make's options: <c>-C</c> and <c>--directory</c> move where it works.</summary>
    private static readonly OptionTable MakeOptions = new(
        "CfIoWE",
        "jlO",
        ["directory", "file", "makefile", "include-dir", "old-file", "assume-old", "what-if", "new-file", "assume-new", "eval"],
        ["jobs", "load-average", "max-load", "output-sync", "debug", "shuffle", "jobserver-style"],
        ["always-make", "environment-overrides", "ignore-errors", "keep-going", "just-print", "dry-run", "recon", "question",
         "no-builtin-rules", "no-builtin-variables", "print-directory", "no-print-directory", "silent", "quiet", "stop",
         "no-keep-going", "touch", "trace", "version", "help", "check-symlink-times", "warn-undefined-variables", "print-data-base"]);

    /// <summary>ninja's options: <c>-C</c> moves where it works.</summary>
    private static readonly OptionTable NinjaOptions = new("Cfjkldtw", string.Empty, [], [], ["version", "verbose", "quiet"]);

    /// <summary>GNU patch's options: <c>-d</c> and <c>--directory</c> move where it works, <c>-o</c> and <c>-r</c> name files it writes.</summary>
    private static readonly OptionTable PatchOptions = new(
        "BdDFgioprVYz",
        string.Empty,
        ["prefix", "directory", "ifdef", "fuzz", "get", "input", "output", "strip", "reject-file", "version-control", "basename-prefix",
         "suffix", "backup-if-mismatch", "reject-format", "quoting-style", "read-only"],
        [],
        ["backup", "binary", "context", "dry-run", "ed", "force", "forward", "normal", "posix", "quiet", "silent", "remove-empty-files",
         "reverse", "unified", "verbose", "help", "version", "batch", "merge", "follow-symlinks", "no-backup-if-mismatch"]);

    /// <summary>
    /// The directories a program that works elsewhere than where it is started is moved to by its own options, each with
    /// the word that names it: tar's, make's, ninja's and git's <c>-C</c> (tar's and make's <c>--directory</c>), patch's
    /// <c>-d</c> and <c>--directory</c>, each in every form - its own word or the next, after an <c>=</c>, glued to its
    /// letter, at the end of a cluster, a letter of tar's old style taking the words after its letters in turn - and
    /// none where the program is none of these.
    /// </summary>
    /// <param name="words">The words, the program's name first.</param>
    private static List<(string Directory, int Word)> OwnDirectories(IReadOnlyList<string> words)
    {
        var name = Name(words[0]);

        if (Is(name, "git"))
        {
            // git's own options before its subcommand: -C alone takes the next word, each from where the one before moved.
            var moves = new List<(string Directory, int Word)>();
            var subcommand = GitSubcommandAt(words);

            for (var at = 1; at + 1 < subcommand; at++)
            {
                if (words[at] == "-C")
                {
                    moves.Add((moves.Count == 0 ? words[at + 1] : Path.Combine(moves[^1].Directory, words[at + 1]), at + 1));
                }

                at += words[at] is "-C" or "-c" || GitTakesValue(words[at]) ? 1 : 0;
            }

            return moves;
        }

        var (table, letter, named) = name switch
        {
            _ when Is(name, "tar", "gtar", "bsdtar") => (TarOptions, "C", "directory"),
            _ when Is(name, "make", "gmake", "bmake") => (MakeOptions, "C", "directory"),
            _ when Is(name, "ninja") => (NinjaOptions, "C", "-"),
            _ when Is(name, "patch") => (PatchOptions, "d", "directory"),
            _ => (null, string.Empty, string.Empty),
        };

        if (table is null)
        {
            return [];
        }

        var (read, map) = Is(name, "tar", "gtar", "bsdtar") ? TarStyled(words) : (words, null);

        return [.. Getopt(read, table).Options
            .Where(option => (option.Option == letter || option.Option == named) && option.Value is not null)
            .Select(option => (option.Value!, map is null ? option.Word : map[option.Word]))];
    }

    /// <summary>
    /// Where git's subcommand stands among <paramref name="words"/> (git's name first): past git's own options and their
    /// values (<c>-C</c>, <c>-c</c> and the long ones that take the next word); the words' count where none stands.
    /// </summary>
    private static int GitSubcommandAt(IReadOnlyList<string> words)
    {
        var at = 1;

        while (at < words.Count && words[at].StartsWith('-'))
        {
            at += words[at] is "-C" or "-c" || GitTakesValue(words[at]) ? 2 : 1;
        }

        return Math.Min(at, words.Count);
    }

    /// <summary>
    /// tar's words with an old style's first word - its letters with no dash, each that takes a value taking the next of
    /// the words after it in turn - spelled as options of their own, each with the word of <paramref name="words"/> it
    /// came from; the words as they are where the first is no such word, with no map.
    /// </summary>
    private static (IReadOnlyList<string> Words, IReadOnlyList<int>? Map) TarStyled(IReadOnlyList<string> words)
    {
        if (words.Count < 2 || words[1].Length == 0 || words[1][0] == '-' || !words[1].All(char.IsAsciiLetter))
        {
            return (words, null);
        }

        var spelled = new List<string> { words[0] };
        var map = new List<int> { 0 };
        var next = 2;

        foreach (var letter in words[1])
        {
            spelled.Add("-" + letter);
            map.Add(1);

            if (TarOptions.ShortValued.Contains(letter, StringComparison.Ordinal) && next < words.Count)
            {
                spelled.Add(words[next]);
                map.Add(next++);
            }
        }

        for (; next < words.Count; next++)
        {
            spelled.Add(words[next]);
            map.Add(next);
        }

        return (spelled, map);
    }
}
