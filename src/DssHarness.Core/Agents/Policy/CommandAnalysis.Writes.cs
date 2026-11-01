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
        "chgrp", "rsync", "copy", "move", "del", "erase", "ren", "rename", "md", "rd", "mkfifo", "mknod", "Set-Content", "Add-Content",
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

    /// <summary>
    /// The programs that write a file one of their options or words names besides what they print, each read by its own
    /// way of naming it (<see cref="OutputsOf"/>): curl's and wget's downloads, an archive tar, zip or 7-Zip makes and the
    /// directory tar (its <c>-C</c> and its <c>--one-top-level</c>), unzip and 7-Zip extract into, sort's and patch's
    /// output and the directory patch writes in (its <c>-d</c>), xxd's output file, sed's <c>w</c>, robocopy's and xcopy's
    /// destination, a build's output (dotnet, a compiler, cmake), git's <c>--output</c>, a key ssh-keygen and openssl write,
    /// script's typescript, a file a compressor rewrites, and PowerShell's downloads, archives and exports. What an archive
    /// or a patch writes from its own contents - each member's or each file's own name - is not read.
    /// </summary>
    public static IReadOnlyList<string> OutputWriters { get; } =
    [
        "curl", "wget", "tar", "gtar", "bsdtar", "unzip", "zip", "7z", "7za", "7zr", "sort", "patch", "xxd", "sed", "gsed", "robocopy",
        "xcopy", "dotnet", "gcc", "g++", "cc", "c++", "clang", "clang++", "ld", "rustc", "go", "javac", "cmake", "git", "ssh-keygen",
        "openssl", "script", "gzip", "gunzip", "bzip2", "bunzip2", "xz", "unxz", "lzma", "unlzma", "zstd", "unzstd", "lz4", "unlz4",
        "compress", "uncompress", "pigz", "unpigz", "brotli", "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm",
        "Expand-Archive", "Compress-Archive", "Export-Csv", "epcsv", "Export-Clixml", "Tee-Object", "Start-Transcript",
    ];

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
    /// (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>&lt;&gt;</c>), each an output writer's option or word names (<see cref="OutputsOf"/>),
    /// and - where the program at <paramref name="start"/> is one of <see cref="FileWriters"/>, or one of
    /// <see cref="InPlaceEditors"/> given an in-place option - each path its words name (<see cref="PathsNamed"/>): every
    /// path of a word that is no option, and every value an option's word gives - glued after its <c>=</c>, a PowerShell
    /// parameter's <c>:</c> or a short option's letter. A copy writer's own are where it copies to alone
    /// (<see cref="CopyDestinations"/>); <c>dd</c>'s is its <c>of=</c>. A word that is the program's data and no path - a
    /// mode, a script - names a place no refusal holds, so taking it for one refuses nothing that runs.
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
        var command = words.Skip(start).ToList();
        var arguments = command.Skip(1).ToList();

        if (Is(program, "dd"))
        {
            targets.AddRange(arguments.Where(word => word.StartsWith("of=", StringComparison.Ordinal)).Select(word => word["of=".Length..]));
            return targets;
        }

        targets.AddRange(OutputsOf(program, command));

        var inPlace = Is(program, [.. InPlaceEditors])
            && arguments.Any(word => word.StartsWith("--in-place", StringComparison.Ordinal) || (word.StartsWith('-') && !word.StartsWith("--", StringComparison.Ordinal) && word.Contains('i', StringComparison.Ordinal)));

        if (!inPlace && !Is(program, [.. FileWriters]))
        {
            return targets;
        }

        if (Is(program, [.. CopyWriters]))
        {
            targets.AddRange(CopyDestinations(program, command).SelectMany(word => PathsNamed(word).Where(path => path.Whole).Select(path => path.Path)));
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
    /// The words a copy writer copies or links to, among <paramref name="command"/>'s, by its own way of naming them: GNU's
    /// <c>cp</c>, <c>install</c> and <c>ln</c> the directory <c>-t</c> or <c>--target-directory</c> names in every form -
    /// its own word or the next, after an <c>=</c>, glued to the letter, at the end of a cluster of short options - else
    /// their last word that is no option nor an option's value, each option read by the writer's own table, so a long
    /// option's value in the next word (<c>--suffix x</c>) is never taken for the destination; <c>rsync</c> its last such
    /// word. Where a long option this does not know stands without its value glued, the word after it may be its value or
    /// a path: every word that is no option but the first is read as a destination. PowerShell's <c>Copy-Item</c> and
    /// <c>cpi</c>, and the <c>copy</c> both PowerShell and cmd.exe name, the value of <c>-Destination</c> (any prefix
    /// PowerShell takes, glued after a <c>:</c> too), else every word that is no parameter nor a parameter's value but the
    /// first, which is the path copied - every one of them where <c>-Path</c> or <c>-LiteralPath</c> names that.
    /// </summary>
    /// <param name="program">The copy writer's name.</param>
    /// <param name="command">Its words, its name first.</param>
    private static IReadOnlyList<string> CopyDestinations(string program, IReadOnlyList<string> command)
    {
        if (Is(program, [.. PowerShellCopies]))
        {
            return PowerShellCopyDestinations([.. command.Skip(1)]);
        }

        var table = Is(program, "cp") ? CpOptions : Is(program, "install") ? InstallOptions : Is(program, "ln") ? LnOptions : RsyncOptions;
        var (options, operands, unknown) = Getopt(command, table);
        var named = options.Where(option => option.Option is "t" or "target-directory" && !Is(program, "rsync") && option.Value is not null).Select(option => option.Value!).ToList();
        var written = Is(program, "rsync")
            ? options.Where(option => option.Option is "log-file" or "write-batch" or "only-write-batch" or "backup-dir" or "partial-dir" or "temp-dir" or "T" && option.Value is not null).Select(option => option.Value!)
            : [];

        IEnumerable<string> destinations = named.Count > 0 ? named
            : unknown ? operands.Skip(1).Select(operand => operand.Word)
            : operands.Count > 0 ? [operands[^1].Word]
            : [];

        return [.. destinations, .. written];
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

    /// <summary>curl's short options that take a value.</summary>
    private static readonly OptionTable CurlOptions = new(
        "AbCcDdEeFHKmoPQrTtUuwXxYyz",
        string.Empty,
        ["output", "output-dir", "dump-header", "cookie-jar", "trace", "trace-ascii", "stderr", "etag-save", "libcurl", "hsts", "alt-svc",
         "user-agent", "cookie", "continue-at", "data", "data-binary", "data-raw", "data-urlencode", "data-ascii", "json", "cert",
         "referer", "form", "form-string", "header", "config", "max-time", "ftp-port", "quote", "range", "upload-file", "telnet-option",
         "proxy-user", "user", "write-out", "request", "proxy", "speed-limit", "speed-time", "time-cond", "url", "connect-timeout",
         "retry", "retry-delay", "retry-max-time", "max-redirs", "resolve", "connect-to", "cacert", "capath", "key", "pass",
         "oauth2-bearer", "variable", "expand-url", "expand-data", "expand-header"],
        [],
        ["remote-name", "remote-name-all", "silent", "show-error", "location", "fail", "insecure", "verbose", "include", "head",
         "compressed", "create-dirs", "remote-header-name", "progress-bar", "no-progress-meter", "get", "globoff", "http1.1",
         "http2", "ipv4", "ipv6", "list-only", "netrc", "no-buffer", "no-keepalive", "path-as-is", "raw", "help", "version"]);

    /// <summary>wget's short options that take a value.</summary>
    private static readonly OptionTable WgetOptions = new(
        "aABDeiIlOoPQRtTUwX",
        string.Empty,
        ["output-document", "directory-prefix", "output-file", "append-output", "save-cookies", "warc-file", "execute", "input-file",
         "base", "tries", "timeout", "wait", "quota", "user-agent", "level", "accept", "reject", "domains", "exclude-domains",
         "include-directories", "exclude-directories", "header", "post-data", "post-file", "body-data", "body-file", "method",
         "user", "password", "http-user", "http-password", "load-cookies", "limit-rate", "cut-dirs", "default-page", "bind-address"],
        [],
        ["recursive", "mirror", "quiet", "verbose", "no-verbose", "continue", "timestamping", "no-clobber", "no-parent",
         "page-requisites", "convert-links", "adjust-extension", "no-check-certificate", "content-disposition", "server-response",
         "spider", "force-directories", "no-directories", "no-host-directories", "help", "version"]);

    /// <summary>sort's short options that take a value; its <c>-o</c> names its output.</summary>
    private static readonly OptionTable SortOptions = new(
        "kotST",
        string.Empty,
        ["key", "output", "field-separator", "buffer-size", "temporary-directory", "parallel", "batch-size", "files0-from", "sort",
         "compress-program", "random-source"],
        [],
        ["ignore-leading-blanks", "dictionary-order", "ignore-case", "general-numeric-sort", "human-numeric-sort",
         "ignore-nonprinting", "month-sort", "numeric-sort", "random-sort", "reverse", "version-sort", "check", "merge", "stable",
         "unique", "zero-terminated", "debug", "help", "version"]);

    /// <summary>The short options that take a value of the programs read by a plain getopt reading alone.</summary>
    private static OptionTable ShortValued(string valued) => new(valued, string.Empty, [], [], []);

    /// <summary>
    /// The files and directories an output writer (<see cref="OutputWriters"/>) writes, among <paramref name="command"/>'s
    /// words, as each is spelled: an option's value or a word, by the program's own way of naming it.
    /// </summary>
    /// <param name="program">The program's name.</param>
    /// <param name="command">Its words, its name first.</param>
    private static IEnumerable<string> OutputsOf(string program, IReadOnlyList<string> command)
    {
        if (!Is(program, [.. OutputWriters]))
        {
            return [];
        }

        IEnumerable<string> Values(IEnumerable<GivenOption> options, params string[] named)
            => options.Where(option => named.Contains(option.Option, StringComparer.Ordinal) && option.Value is { Length: > 0 }).Select(option => option.Value!);

        switch (program)
        {
            case var _ when Is(program, "curl", "wget"):
            {
                var curl = Is(program, "curl");
                var (options, operands, _) = Getopt(command, curl ? CurlOptions : WgetOptions);
                var files = Values(options, curl ? ["o", "output", "D", "dump-header", "c", "cookie-jar", "trace", "trace-ascii", "stderr", "etag-save", "libcurl", "hsts", "alt-svc"] : ["O", "output-document", "o", "output-file", "a", "append-output", "save-cookies", "warc-file"]).ToList();
                var directories = Values(options, curl ? ["output-dir"] : ["P", "directory-prefix"]).ToList();

                // A download named by its address's last segment: curl's -O, and wget's every download without its -O.
                var named = curl ? options.Any(option => option.Option is "O" or "remote-name" or "remote-name-all")
                    : !options.Any(option => option.Option is "O" or "output-document");
                var remote = named ? operands.Select(operand => UrlName(operand.Word)).OfType<string>().ToList() : [];

                return [.. files, .. directories, .. remote, .. directories.SelectMany(directory => files.Concat(remote).Select(file => directory + "/" + file)),
                        .. PowerShellValues(command, ["outfile"], 2)];
            }

            case var _ when Is(program, "tar", "gtar", "bsdtar"):
            {
                var (read, _) = TarStyled(command);
                var options = Getopt(read, TarOptions).Options;
                var creates = options.Any(option => option.Option is "c" or "r" or "u" or "A" or "create" or "append" or "update" or "catenate" or "concatenate" or "delete");
                var extracts = options.Any(option => option.Option is "x" or "extract" or "get");

                // --one-top-level extracts into the directory it names, under -C's where both are given.
                var into = extracts ? Values(options, "C", "directory").ToList() : [];
                var top = extracts ? Values(options, "one-top-level").ToList() : [];

                return [.. creates ? Values(options, "f", "file") : [], .. into, .. top, .. into.SelectMany(directory => top.Select(level => directory + "/" + level)),
                        .. Values(options, "index-file", "volno-file", "listed-incremental", "g")];
            }

            case var _ when Is(program, "unzip"):
                return Values(Getopt(command, ShortValued("dPx")).Options, "d");

            case var _ when Is(program, "zip"):
            {
                var (options, operands, _) = Getopt(command, ShortValued("bnOPsZt"));

                return [.. operands.Take(1).Select(operand => operand.Word), .. Values(options, "O")];
            }

            case var _ when Is(program, "7z", "7za", "7zr"):
                return [.. command.Skip(1).Where(word => word.StartsWith("-o", StringComparison.Ordinal) && word.Length > 2).Select(word => word[2..]),
                        .. command.Skip(1).Where(word => !word.StartsWith('-')).Skip(1).Take(1)];

            case var _ when Is(program, "sort"):
                return Values(Getopt(command, SortOptions).Options, "o", "output");

            case var _ when Is(program, "patch"):
            {
                // The directory it moves to (-d) is where it writes each file the patch names, and its output and rejects
                // are named from there.
                var options = Getopt(command, PatchOptions).Options;
                var into = Values(options, "d", "directory").ToList();
                var named = Values(options, "o", "output", "r", "reject-file").ToList();

                return [.. into, .. named, .. into.SelectMany(directory => named.Select(file => directory + "/" + file))];
            }

            case var _ when Is(program, "xxd"):
            {
                // Its second operand is the file it writes, the dump or, with -r, the bytes the dump spells. xxd names an
                // option by its first letter, its long name's rest optional (-c, -cols; -p, -ps): one that takes a value
                // takes the next word unless its value is glued to its letter.
                var operands = new List<string>();

                for (var at = 1; at < command.Count; at++)
                {
                    var word = command[at];

                    if (word == "--")
                    {
                        operands.AddRange(command.Skip(at + 1));
                        break;
                    }

                    if (word.Length < 2 || word[0] != '-')
                    {
                        operands.Add(word);
                    }
                    else if ("cglosnR".Contains(word[1], StringComparison.Ordinal) && word[2..].All(char.IsAsciiLetter))
                    {
                        at++;
                    }
                }

                return operands.Skip(1).Take(1);
            }

            case var _ when Is(program, "sed", "gsed"):
                try
                {
                    return SedOf(command, []) is { } sed ? sed.Written : [];
                }
                catch (NotSupportedException)
                {
                    // A script this cannot read is refused where the command is read; another program's words name no write.
                    return [];
                }

            case var _ when Is(program, "robocopy", "xcopy"):
                return [.. command.Skip(1).Where(word => !word.StartsWith('/')).Skip(1).Take(1),
                        .. command.Skip(1).Where(word => word.StartsWith('/') && word.Contains(':', StringComparison.Ordinal)
                            && word.Split(':', 2)[0].TrimEnd('+').ToUpperInvariant() is "/LOG" or "/UNILOG").Select(word => word.Split(':', 2)[1])];

            case var _ when Is(program, "dotnet"):
                return [.. Values(Getopt(command, new OptionTable("acefoprsv", string.Empty, ["output", "results-directory", "diag", "configuration", "framework", "runtime", "arch", "os", "verbosity", "source", "property", "project", "settings", "logger", "filter", "collect", "environment"], [], [])).Options, "o", "output", "results-directory", "diag"),
                        .. command.Skip(1).Where(word => word.Length > 1 && word[0] is '-' or '/' && word.Contains(':', StringComparison.Ordinal)
                            && word[1..].Split(':', 2)[0].ToLowerInvariant() is "bl" or "binarylogger").Select(word => word.Split(':', 2)[1])];

            case var _ when Is(program, "gcc", "g++", "cc", "c++", "clang", "clang++", "ld", "rustc", "go"):
                return Values(Getopt(command, ShortValued("oIDLlx")).Options, "o");

            case var _ when Is(program, "javac"):
                return Values(Getopt(command, ShortValued("dsh")).Options, "d");

            case var _ when Is(program, "cmake"):
                return Values(Getopt(command, new OptionTable("BSDCGTAUj", string.Empty, ["build", "install-prefix", "preset", "target", "config", "parallel"], [], [])).Options, "B", "build");

            case var _ when Is(program, "git"):
            {
                var subcommand = GitSubcommandAt(command) is var at and > 0 && at < command.Count ? command[at] : string.Empty;
                var options = Getopt(command, new OptionTable(subcommand is "format-patch" or "archive" ? "o" : string.Empty, string.Empty, ["output", "output-directory"], [], [])).Options;

                return Values(options, "output", "output-directory", "o");
            }

            case var _ when Is(program, "ssh-keygen"):
                return Values(Getopt(command, ShortValued("abCDEFfGIJjKMmNnOPRrSsTtVwYZz")).Options, "f");

            case var _ when Is(program, "openssl"):
                return command.Skip(1).Select((word, at) => (word, at)).Where(each => each.word is "-out" or "-keyout")
                    .Select(each => each.at + 2 < command.Count ? command[each.at + 2] : null).OfType<string>();

            case var _ when Is(program, "script"):
            {
                var (options, operands, _) = Getopt(command, new OptionTable("cEIOBTm", string.Empty, ["command", "echo", "log-in", "log-out", "log-io", "log-timing", "logging-format", "output-limit"], [], []));

                return [.. operands.Take(1).Select(operand => operand.Word), .. Values(options, "O", "log-out", "I", "log-in", "B", "log-io", "T", "log-timing")];
            }

            case var _ when Is(program, "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm"):
                return PowerShellValues(command, ["outfile"], 2);

            case var _ when Is(program, "Expand-Archive", "Compress-Archive"):
            {
                var given = PowerShellValues(command, ["destinationpath"], 1);

                return given.Count > 0 ? given : command.Skip(1).Where(word => !word.StartsWith('-')).Skip(1).Take(1);
            }

            case var _ when Is(program, "Export-Csv", "epcsv", "Export-Clixml", "Tee-Object", "Start-Transcript"):
            {
                var given = PowerShellValues(command, ["path", "literalpath", "filepath", "outputdirectory"], 1);

                return given.Count > 0 ? given : command.Skip(1).Where(word => !word.StartsWith('-')).Take(1);
            }

            default:
                // A compressor rewrites the files it is given, where it is not printing what it makes.
                return command.Skip(1).Any(word => word is "-c" or "--stdout" or "--to-stdout" or "-l" or "--list" or "-t" or "--test")
                    ? []
                    : command.Skip(1).Where(word => !word.StartsWith('-'));
        }
    }

    /// <summary>The name a download takes from the address <paramref name="word"/> names: its path's last segment, or null where it is no address or names none.</summary>
    private static string? UrlName(string word)
    {
        if (!word.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var path = word.Split(['?', '#'], 2)[0];
        var slash = path.IndexOf('/', path.IndexOf("://", StringComparison.Ordinal) + 3);

        return slash < 0 || slash + 1 >= path.Length ? null : path[(path.LastIndexOf('/') + 1)..] is { Length: > 0 } name ? name : null;
    }

    /// <summary>
    /// The values PowerShell binds to the parameters <paramref name="names"/> (in lower case) among <paramref name="command"/>'s
    /// words: each by any prefix of at least <paramref name="shortest"/> letters, its value glued after a <c>:</c> or the
    /// next word.
    /// </summary>
    private static IReadOnlyList<string> PowerShellValues(IReadOnlyList<string> command, IReadOnlyList<string> names, int shortest)
    {
        var values = new List<string>();

        for (var at = 1; at < command.Count; at++)
        {
            var word = command[at];

            if (word.Length < 2 || word[0] != '-' || word.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var spelled = word[1..].Split(':', 2);
            var parameter = spelled[0].ToLowerInvariant();

            if (parameter.Length >= shortest && names.Any(name => name.StartsWith(parameter, StringComparison.Ordinal)))
            {
                if ((spelled.Length == 2 ? spelled[1] : at + 1 < command.Count ? command[++at] : null) is { Length: > 0 } value)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    /// <summary>
    /// What a command of <paramref name="analyzed"/>'s removes or moves whole, with all a directory of it holds, as it
    /// spells each: a recursive <c>rm</c>'s words (<c>-r</c>, <c>-R</c>, <c>--recursive</c>), PowerShell's
    /// <c>Remove-Item</c> and its aliases where PowerShell reads them, cmd.exe's <c>rd /s</c> and <c>del /s</c>, what a move
    /// or a rename moves (<c>mv</c> but its destination,
    /// <c>Move-Item</c>, <c>Rename-Item</c>), the destination an <c>rsync</c> deletes from (<c>--delete</c> and its kind)
    /// and the paths a <c>git clean</c> of untracked directories and ignored files cleans (<c>-d</c> with <c>-x</c> or
    /// <c>-X</c>), its working directory where it names none.
    /// </summary>
    /// <param name="analyzed">The command, as the analysis read it.</param>
    /// <param name="start">Where its program stands among its words.</param>
    /// <param name="powerShell">Whether PowerShell reads the command, whose <c>rm</c>, <c>del</c> and <c>rd</c> are its own <c>Remove-Item</c>.</param>
    public static IReadOnlyList<string> RemovalTargets(AnalyzedCommand analyzed, int start, bool powerShell)
    {
        ArgumentNullException.ThrowIfNull(analyzed);

        var words = analyzed.Words;

        if (start < 0 || start >= words.Count)
        {
            return [];
        }

        var program = Name(words[start]);
        var command = words.Skip(start).ToList();
        var arguments = command.Skip(1).ToList();

        IEnumerable<string> Plain(IEnumerable<string> each) => each.Where(word => word.Length > 0 && word[0] is not '-' and not '/');

        if (Is(program, "Remove-Item", "ri") || (powerShell && Is(program, "rm", "del", "erase", "rd", "rmdir")))
        {
            return [.. Plain(arguments), .. PowerShellValues(command, ["path", "literalpath"], 1)];
        }

        if (Is(program, "rd", "rmdir", "del", "erase") && arguments.Any(word => word.Equals("/s", StringComparison.OrdinalIgnoreCase)))
        {
            return [.. Plain(arguments)];
        }

        if (Is(program, "rm"))
        {
            var (options, operands, _) = Getopt(command, RmOptions);

            return options.Any(option => option.Option is "r" or "R" or "recursive") ? [.. operands.Select(operand => operand.Word)] : [];
        }

        if (Is(program, "mv"))
        {
            var (options, operands, _) = Getopt(command, new OptionTable("St", string.Empty, ["suffix", "target-directory"], ["backup", "update"], ["force", "interactive", "no-clobber", "strip-trailing-slashes", "no-target-directory", "verbose", "exchange", "help", "version"]));

            return options.Any(option => option.Option is "t" or "target-directory")
                ? [.. operands.Select(operand => operand.Word)]
                : [.. operands.SkipLast(1).Select(operand => operand.Word)];
        }

        if (Is(program, "Move-Item", "mi", "Rename-Item", "rni") || (powerShell && Is(program, "move", "ren", "rename")))
        {
            var named = PowerShellValues(command, ["path", "literalpath"], 1);

            return named.Count > 0 ? named : [.. Plain(arguments).Take(1)];
        }

        if (Is(program, "move", "ren", "rename"))
        {
            return [.. Plain(arguments).Take(1)];
        }

        if (Is(program, "rsync"))
        {
            var (options, operands, _) = Getopt(command, RsyncOptions);

            return options.Any(option => option.Option.StartsWith("delete", StringComparison.Ordinal) || option.Option == "remove-source-files") && operands.Count > 0
                ? [operands[^1].Word]
                : [];
        }

        if (Is(program, "git") && GitSubcommandAt(command) is var subcommand and > 0 && subcommand < command.Count && command[subcommand] == "clean")
        {
            var (options, operands, _) = Getopt([.. command.Skip(subcommand)], new OptionTable("e", string.Empty, ["exclude"], [], ["dry-run", "force", "interactive", "quiet"]));

            return options.Any(option => option.Option == "d") && options.Any(option => option.Option is "x" or "X") && !options.Any(option => option.Option is "n" or "dry-run")
                ? operands.Count > 0 ? [.. operands.Select(operand => operand.Word)] : ["."]
                : [];
        }

        return [];
    }
}
