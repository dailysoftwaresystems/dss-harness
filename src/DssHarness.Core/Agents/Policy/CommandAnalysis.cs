namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// Reads a command whole before any rule is matched against it: what one client hands as an
/// argument list and another as a shell string is flattened into commands, each a list of words,
/// however the command hides its program - and what it cannot read whole it refuses to read, so
/// the policy denies it.
/// </summary>
/// <remarks>
/// <para>
/// A shell string is split as the system's own shell splits it: on <c>;</c>, <c>&amp;&amp;</c>,
/// <c>||</c>, <c>|</c>, <c>&amp;</c>, line ends and a closing parenthesis nothing opened (a
/// <c>case</c> pattern's); the body of a command substitution (<c>$(...)</c>, backticks) and of a
/// subshell is read as commands of their own, and the shell's structural words - <c>if</c>,
/// <c>then</c>, <c>do</c>, <c>done</c> and the rest - never join a command; a line a backslash
/// continues is one line, <c>$'...'</c> is read with its escapes decoded, and a redirect's file is
/// a word with its operator before it (<c>&gt;out.txt</c>). The <c>NAME=value</c> words before a
/// program stay the command's own, its environment, and <c>env</c>'s pairs become them; wrappers
/// (<c>env</c>, <c>nohup</c>, <c>time</c>, <c>timeout</c>, <c>nice</c>, <c>xargs</c>,
/// <c>command</c>, <c>builtin</c>, <c>exec</c>, <c>eval</c> and PowerShell's
/// <c>Invoke-Expression</c>) are stripped with their own options; it goes into
/// <c>sh</c>/<c>bash</c>/<c>zsh -c</c> - and reads the words after the script as scripts too, for
/// the script may run them - into <c>cmd /c</c> and <c>/k</c> by cmd's own quoting, and into
/// <c>powershell</c>/<c>pwsh</c>'s <c>-Command</c> and a decoded <c>-EncodedCommand</c>, each by
/// any prefix PowerShell takes, by PowerShell's quoting; and a shell another program runs with
/// its script among its words (<c>find -exec sh -c ...</c>) is gone into as well.
/// </para>
/// <para>
/// What it cannot read whole it refuses, by a <see cref="NotSupportedException"/> naming why, and
/// the policy denies what it was refused: a script nested past <see cref="Depth"/> levels of going
/// into, parentheses nested past <see cref="Nesting"/>, a shell that reads its script from its
/// input, a program named by an expansion - a variable, a substitution, a pattern, a brace - and an
/// <c>xargs</c> that hands a shell its script from its input. What it does not see is said in
/// help, not guessed at: the inside of a script file a command runs, and a program another program
/// runs from words that do not name it.
/// </para>
/// <para>
/// A program is matched by its name as this reads it - its last segment, a Windows program's end
/// (<c>.exe</c>, <c>.com</c>, <c>.cmd</c>, <c>.bat</c>, <c>.ps1</c>) folded away - without regard
/// to case, so a wrapper or a shell named in either case is read as one: what this reads as a
/// command that is not one can only meet a rule that would refuse it, never lose one.
/// </para>
/// </remarks>
public static partial class CommandAnalysis
{
    /// <summary>How deep one command goes into the scripts its words name: a shell's <c>-c</c>, a decoded encoded command; a script nested deeper is refused.</summary>
    public const int Depth = 4;

    /// <summary>How deep the parentheses of one script nest - its substitutions and subshells - before it is refused.</summary>
    public const int Nesting = 32;

    /// <summary>The shells whose <c>-c</c> takes a script.</summary>
    private static readonly string[] Shells = ["sh", "bash", "zsh", "dash", "ksh"];

    /// <summary>
    /// The commands an argument list runs: the list as it is, and a script one of its words names
    /// gone into, its environment first where it carries one.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    /// <exception cref="NotSupportedException">The command holds what this does not read, named.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Commands(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        return [.. FromWords(argv, 1, []).Where(words => words.Count > 0)];
    }

    /// <summary>The commands a shell string runs, however it hides them.</summary>
    /// <param name="shell">The command as Claude Code's shell tool hands it: one string the system's own shell would run.</param>
    /// <exception cref="NotSupportedException">The command holds what this does not read, named.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Commands(string shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        return [.. FromScript(shell, 1, []).Where(words => words.Count > 0)];
    }

    /// <summary>
    /// Whether <paramref name="word"/> is an assignment the shell reads before a command -
    /// <c>NAME=value</c>, a name of letters, digits and underscores that begins with no digit -
    /// which gives the command its environment.
    /// </summary>
    public static bool IsAssignment(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var equals = word.IndexOf('=', StringComparison.Ordinal);

        return equals > 0
            && (char.IsAsciiLetter(word[0]) || word[0] == '_')
            && word[..equals].All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    /// <summary>How many of a command's first words are its environment: the assignments before its program.</summary>
    /// <param name="words">One command's words, as <see cref="Commands(string)"/> or <see cref="Commands(IReadOnlyList{string})"/> flattened it.</param>
    public static int EnvironmentOf(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var at = 0;

        while (at < words.Count && IsAssignment(words[at]))
        {
            at++;
        }

        return at;
    }

    /// <summary>
    /// The programs a command of which is decided as a web request too, by each address it names
    /// (the user, 2026-10-19): <c>curl</c>, <c>wget</c>, PowerShell's <c>Invoke-WebRequest</c> and
    /// <c>Invoke-RestMethod</c> with their aliases <c>iwr</c> and <c>irm</c>, the <c>http</c> and
    /// <c>https</c> of its own name, and <c>gh api</c>, whose endpoint is <c>api.github.com</c>'s.
    /// A program this list does not name, and what a script does inside, are not seen.
    /// </summary>
    public static IReadOnlyList<string> WebPrograms { get; } =
        ["curl", "wget", "Invoke-WebRequest", "Invoke-RestMethod", "iwr", "irm", "http", "https"];

    /// <summary>
    /// The environment names a fetcher reaches the network through: a proxy any of them names is a
    /// host the request reaches, as its own address is.
    /// </summary>
    private static readonly string[] ProxyNames = ["http_proxy", "https_proxy", "all_proxy", "ftp_proxy"];

    /// <summary>
    /// The web addresses one command names, where it runs a program which fetches from the network:
    /// the URLs of <c>curl</c> and <c>wget</c> - any word that is not an option or an option's
    /// value, and a <c>--url</c>'s - with every host curl reaches besides (a proxy, a
    /// <c>--connect-to</c>'s and a <c>--resolve</c>'s target, a <c>--doh-url</c>), the proxy its
    /// environment names, the first address PowerShell's fetchers take by position or by
    /// <c>-Uri</c>, the first address of an <c>http</c> or <c>https</c>, and the endpoint of
    /// <c>gh api</c>, which is <c>api.github.com</c>'s unless a <c>--hostname</c> names another.
    /// Empty where the command's program is none of them, and an address that is only a flag's
    /// value or an option's shape names nothing.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one, as <see cref="Commands(string)"/> or <see cref="Commands(IReadOnlyList{string})"/> flattened it.</param>
    public static IReadOnlyList<string> WebAddresses(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var environment = EnvironmentOf(words);

        if (environment == words.Count)
        {
            return [];
        }

        var program = Name(words[environment]);
        var arguments = words.Skip(environment + 1).ToList();
        var proxies = words.Take(environment)
            .Select(assignment => assignment.Split('=', 2))
            .Where(pair => ProxyNames.Contains(pair[0], StringComparer.OrdinalIgnoreCase) && pair[1].Length > 0)
            .Select(pair => pair[1])
            .ToList();

        if (program.Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count == 0 || !arguments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var host = "api.github.com";
            string? endpoint = null;

            // The api's own options that take a value: what follows one is its value, never the endpoint.
            for (var at = 1; at < arguments.Count; at++)
            {
                var word = arguments[at];

                if (word.StartsWith("--hostname=", StringComparison.OrdinalIgnoreCase) && word["--hostname=".Length..] is { Length: > 0 } glued)
                {
                    host = glued;
                }
                else if (word.Equals("--hostname", StringComparison.OrdinalIgnoreCase) && at + 1 < arguments.Count)
                {
                    host = arguments[++at];
                }
                else if (GhApiValueTaking.Contains(word, StringComparer.Ordinal) && at + 1 < arguments.Count)
                {
                    at++;
                }
                else if (!word.StartsWith('-') && endpoint is null)
                {
                    endpoint = word;
                }
            }

            return [.. proxies, endpoint is { } path && LooksLikeAddress(path) ? path : host];
        }

        if (program.Equals("curl", StringComparison.OrdinalIgnoreCase))
        {
            var addresses = new List<string>(proxies);

            for (var at = 0; at < arguments.Count; at++)
            {
                var word = arguments[at];

                if (Valued(word, at, arguments, ["--url"], out var url, out var taken))
                {
                    addresses.Add(url);
                    at += taken;
                }
                else if (Valued(word, at, arguments, ["-x", "--proxy", "--preproxy"], out var proxy, out taken))
                {
                    addresses.Add(proxy);
                    at += taken;
                }
                else if (Valued(word, at, arguments, ["--doh-url"], out var doh, out taken))
                {
                    addresses.Add(doh);
                    at += taken;
                }
                else if (Valued(word, at, arguments, ["--connect-to"], out var connect, out taken))
                {
                    // HOST1:PORT1:HOST2:PORT2 - the request reaches HOST2, where one is named.
                    if (connect.Split(':') is { Length: >= 3 } parts && parts[2].Length > 0)
                    {
                        addresses.Add(parts[2].Trim('[', ']'));
                    }

                    at += taken;
                }
                else if (Valued(word, at, arguments, ["--resolve"], out var resolve, out taken))
                {
                    // [+]HOST:PORT:ADDRESS[,ADDRESS]... - the request reaches each address.
                    if (resolve.Split(':', 3) is { Length: 3 } parts)
                    {
                        addresses.AddRange(parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(address => address.Trim('[', ']')));
                    }

                    at += taken;
                }
                else if (CurlValueTaking.Contains(word, StringComparer.Ordinal) && at + 1 < arguments.Count)
                {
                    at++;
                }
                else if (!word.StartsWith('-') && LooksLikeAddress(word))
                {
                    addresses.Add(word);
                }
            }

            return [.. addresses];
        }

        if (program.Equals("wget", StringComparison.OrdinalIgnoreCase))
        {
            var addresses = new List<string>(proxies);

            for (var at = 0; at < arguments.Count; at++)
            {
                var word = arguments[at];

                if (WgetValueTaking.Contains(word, StringComparer.Ordinal) && at + 1 < arguments.Count)
                {
                    at++;
                }
                else if (!word.StartsWith('-') && LooksLikeAddress(word))
                {
                    addresses.Add(word);
                }
            }

            return [.. addresses];
        }

        if (WebPrograms.Contains(program, StringComparer.OrdinalIgnoreCase))
        {
            // PowerShell's fetchers take the address by position or by -Uri, a proxy by -Proxy; http
            // and https take it by position alone. One address each: the first they name.
            var addresses = new List<string>(proxies);

            for (var at = 0; at < arguments.Count; at++)
            {
                if (arguments[at].StartsWith("-Proxy:", StringComparison.OrdinalIgnoreCase) && arguments[at]["-Proxy:".Length..] is { Length: > 0 } glued)
                {
                    addresses.Add(glued);
                }
                else if (arguments[at].Equals("-Proxy", StringComparison.OrdinalIgnoreCase) && at + 1 < arguments.Count)
                {
                    addresses.Add(arguments[++at]);
                }
            }

            for (var at = 0; at < arguments.Count; at++)
            {
                if (arguments[at].StartsWith("-Uri:", StringComparison.OrdinalIgnoreCase) && LooksLikeAddress(arguments[at]["-Uri:".Length..]))
                {
                    return [.. addresses, arguments[at]["-Uri:".Length..]];
                }

                if (arguments[at].Equals("-Uri", StringComparison.OrdinalIgnoreCase) && at + 1 < arguments.Count && LooksLikeAddress(arguments[at + 1]))
                {
                    return [.. addresses, arguments[at + 1]];
                }
            }

            return arguments.FirstOrDefault(word => !word.StartsWith('-') && LooksLikeAddress(word)) is { } positional
                ? [.. addresses, positional]
                : [.. addresses];
        }

        return [];
    }

    /// <summary>
    /// The option of a fetcher command that reads its addresses or its settings from a file this
    /// does not read - curl's <c>--config</c>, wget's <c>--input-file</c>, <c>--execute</c> and
    /// <c>--config</c> - so where the web is held to rules, what it reaches cannot be told; null
    /// where it names none.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one.</param>
    public static string? WebAddressesUnread(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var environment = EnvironmentOf(words);

        if (environment == words.Count)
        {
            return null;
        }

        var program = Name(words[environment]);
        string[] unread = program.Equals("curl", StringComparison.OrdinalIgnoreCase) ? ["-K", "--config"]
            : program.Equals("wget", StringComparison.OrdinalIgnoreCase) ? ["-i", "--input-file", "-e", "--execute", "--config"]
            : [];

        return words.Skip(environment + 1)
            .FirstOrDefault(word => unread.Any(option => word == option || word.StartsWith(option + "=", StringComparison.Ordinal)
                || (option.Length == 2 && word.StartsWith(option, StringComparison.Ordinal) && word.Length > 2)));
    }

    /// <summary>The options of <c>gh api</c> that take a value in the next word.</summary>
    private static readonly string[] GhApiValueTaking =
        ["-X", "--method", "-H", "--header", "-f", "--raw-field", "-F", "--field", "-p", "--preview", "-q", "--jq", "-t", "--template", "--input", "--cache"];

    /// <summary>
    /// The options of curl that take a value, whose value is no address: an output file
    /// (<c>-o out.json</c>) or a header (<c>-H 'Host: x'</c>) would otherwise be read as a host by
    /// its shape. What this table does not name is said in help, as the inside of a script file is.
    /// </summary>
    private static readonly string[] CurlValueTaking =
    [
        "-o", "--output", "--data", "--data-raw", "--data-binary", "--data-urlencode", "-d", "-H", "--header",
        "-A", "--user-agent", "-u", "--user", "-m", "--max-time", "-e", "--referer",
        "-b", "--cookie", "-c", "--cookie-jar", "-F", "--form", "-T", "--upload-file",
        "--cert", "--key", "--cacert", "--capath", "--ciphers", "--connect-timeout", "--content-type",
        "--dns-servers", "--interface", "--ip-address", "--limit-rate", "--mail-from", "--mail-rcpt",
        "--netrc-file", "--oauth2-bearer", "--pass", "--proxy-user", "--pubkey", "-Q", "--quote",
        "--random-file", "-r", "--range", "-t", "--telnet-option", "-U", "--write-out", "-w",
    ];

    /// <summary>The options of wget that take a value in the next word, whose value is no address it fetches.</summary>
    private static readonly string[] WgetValueTaking =
    [
        "-O", "--output-document", "-o", "--output-file", "-a", "--append-output", "-P", "--directory-prefix",
        "-U", "--user-agent", "--header", "--post-data", "--post-file", "--body-data", "--body-file", "--method",
        "--user", "--password", "--http-user", "--http-password", "--proxy-user", "--proxy-password",
        "-t", "--tries", "-T", "--timeout", "-w", "--wait", "-l", "--level", "-A", "--accept", "-R", "--reject",
        "-D", "--domains", "-B", "--base", "--referer", "--load-cookies", "--save-cookies", "--ca-certificate",
        "--certificate", "--private-key", "-Q", "--quota", "--limit-rate", "--bind-address", "-X",
        "--exclude-directories", "-I", "--include-directories",
    ];

    /// <summary>
    /// Whether <paramref name="word"/> is one of <paramref name="options"/> with its value - in the
    /// next word, glued after an <c>=</c>, or glued to a short option - and the value it gives.
    /// </summary>
    private static bool Valued(string word, int at, IReadOnlyList<string> arguments, string[] options, out string value, out int taken)
    {
        foreach (var option in options)
        {
            if (word == option && at + 1 < arguments.Count)
            {
                value = arguments[at + 1];
                taken = 1;
                return true;
            }

            if (word.StartsWith(option + "=", StringComparison.Ordinal) && word.Length > option.Length + 1)
            {
                value = word[(option.Length + 1)..];
                taken = 0;
                return true;
            }

            if (option.Length == 2 && word.Length > 2 && word.StartsWith(option, StringComparison.Ordinal))
            {
                value = word[2..];
                taken = 0;
                return true;
            }
        }

        value = string.Empty;
        taken = 0;
        return false;
    }

    /// <summary>
    /// Whether a word names an address a fetcher would ask: a URL of any scheme that names a host -
    /// a file's names none - or a bare <c>host.tld[:port][/path]</c>, which curl and wget read as
    /// <c>http://</c> - localhost among the bare hosts, for a loopback port is the one an agent's own
    /// machine serves. A word with no dot in its host is a flag's value, a file, or an endpoint path -
    /// not an address.
    /// </summary>
    private static bool LooksLikeAddress(string word)
    {
        if (word.StartsWith('-') || word.Length == 0)
        {
            return false;
        }

        if (word.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(word, UriKind.Absolute, out var absolute) && !absolute.IsFile && absolute.Host.Length > 0;
        }

        var authority = word.Split('/')[0].Split(':');

        if (authority.Length is not (1 or 2))
        {
            return false;
        }

        if (authority[0].Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return authority[0].Split('.', StringSplitOptions.RemoveEmptyEntries).Length >= 2
            && authority[0].All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    /// <summary>
    /// The words of a command the rules are matched against: its environment left out, git's own
    /// options dropped before its subcommand, so <c>git -C x push</c> meets a rule on
    /// <c>git push</c> - and a git command whose configuration is set where it runs is the
    /// <c>git config</c> it is: a <c>-c</c> or a <c>--config-env</c> that defines an alias or sets a
    /// key naming a program git runs, and any command that sets git's configuration or such a
    /// program through the environment (<c>GIT_CONFIG_COUNT</c> and its pairs, <c>GIT_SSH_COMMAND</c>
    /// and the rest), for <c>git -c alias.x=commit x</c> would otherwise pass every rule on
    /// <c>commit</c>.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one.</param>
    /// <param name="comparison">How git's own name compares: names compare as this platform compares paths.</param>
    public static IReadOnlyList<string> ForRules(IReadOnlyList<string> words, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(words);

        // Setting git's configuration or a program it runs through the environment is configuring
        // git, whatever runs with it: an export alone, an env, the words before git itself.
        if (words.FirstOrDefault(SetsGitConfiguration) is { } setting && words.Count > 0)
        {
            var name = setting.TrimStart('$').Split('=', 2)[0];

            return ["git", "config", name, .. words.Skip(EnvironmentOf(words))];
        }

        var environment = EnvironmentOf(words);

        if (environment == words.Count || !string.Equals(Name(words[environment]), "git", comparison))
        {
            return environment == 0 ? words : [.. words.Skip(environment)];
        }

        var config = false;
        var at = environment + 1;

        while (at < words.Count && words[at].StartsWith('-'))
        {
            var option = words[at];

            // -c and --config-env, the setting glued into them or the next word: an alias or a key
            // naming a program git runs makes the command the git config it is.
            if (option is "-c" or "--config-env")
            {
                if (at + 1 < words.Count && NamesAProgram(words[at + 1]))
                {
                    config = true;
                }

                at += 2;
                continue;
            }

            if (option.StartsWith("--config-env=", StringComparison.Ordinal) || (option.StartsWith("-c", StringComparison.Ordinal) && option.Contains('=', StringComparison.Ordinal)))
            {
                if (NamesAProgram(option.StartsWith("--", StringComparison.Ordinal) ? option["--config-env=".Length..] : option[2..]))
                {
                    config = true;
                }

                at++;
                continue;
            }

            // --exec-path given a directory is where git finds the programs of its subcommands.
            if (option.StartsWith("--exec-path=", StringComparison.Ordinal))
            {
                config = true;
                at++;
                continue;
            }

            // -C, its path glued into it or the next word; an option with its value in the same
            // word stands as one word too. The options that take a value in the next word are the
            // few git names for them, and every other option takes none - dropping a word that was
            // the subcommand would read the command as one it is not.
            at += (option.StartsWith("-C", StringComparison.Ordinal) && option.Length > 2)
                || option.Contains('=', StringComparison.Ordinal)
                || ValueLess(option) ? 1
                : (option == "-C" || TakesNextWord(option)) && at + 1 < words.Count ? 2 : 1;
        }

        return at >= words.Count ? [.. words.Skip(environment)]
            : config ? ["git", "config", .. words.Skip(at)]
            : ["git", .. words.Skip(at)];
    }

    /// <summary>git's global options that take their value as the next word, read before its subcommand.</summary>
    private static bool TakesNextWord(string option)
        => option is "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix";

    /// <summary>
    /// Whether a <c>git -c</c> value defines an alias or sets a key naming a program git runs: either
    /// makes the command the <c>git config</c> it is.
    /// </summary>
    private static bool NamesAProgram(string setting)
    {
        var key = setting.Split('=', 2)[0];

        return key.StartsWith("alias.", StringComparison.OrdinalIgnoreCase)
            || ProgramKeys.Any(suffix => key.Equals(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The keys whose value git runs as a program: set through <c>-c</c>, each would run a command of
    /// the setter's choosing outside every rule on git's subcommand.
    /// </summary>
    private static readonly string[] ProgramKeys =
    [
        "core.sshcommand",
        "core.hookspath",
        "core.editor",
        "core.sequenceeditor",
        "core.pager",
        "core.askpass",
        "core.fsmonitor",
        "credential.helper",
    ];

    /// <summary>
    /// The environment names through which git reads its configuration (every <c>GIT_CONFIG</c>
    /// name - the count and its keys and values, the parameters, the files it reads) or a program it
    /// runs; set for a command, each is a <c>git -c</c> of the setter's choosing.
    /// </summary>
    private static readonly string[] GitProgramNames =
        ["GIT_SSH", "GIT_SSH_COMMAND", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "GIT_PAGER", "GIT_ASKPASS", "GIT_EXTERNAL_DIFF", "GIT_EXEC_PATH", "GIT_TEMPLATE_DIR"];

    /// <summary>
    /// Whether <paramref name="word"/> sets git's configuration or a program git runs through the
    /// environment: <c>NAME=value</c> or PowerShell's <c>$env:NAME=value</c> of such a name.
    /// </summary>
    private static bool SetsGitConfiguration(string word)
    {
        var assignment = word.StartsWith("$env:", StringComparison.OrdinalIgnoreCase) ? word["$env:".Length..] : word;

        if (!IsAssignment(assignment))
        {
            return false;
        }

        var name = assignment.Split('=', 2)[0];

        return name.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase)
            || GitProgramNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>git's options that take no value, read before its subcommand.</summary>
    private static bool ValueLess(string option)
        => option is "--bare" or "--no-optional-locks" or "--literal-pathspecs" or "--glob-pathspecs"
            or "--noglob-pathspecs" or "--icase-pathspecs" or "--no-replace-objects" or "--html-path"
            or "--man-path" or "--info-path" or "--exec-path" or "-p" or "--paginate" or "--no-pager";

    /// <summary>The ends of a Windows program's name that are folded away: what Windows finds a command by.</summary>
    private static readonly string[] ProgramEnds = [".exe", ".com", ".cmd", ".bat", ".ps1"];

    /// <summary>The word as a program's name: its last segment, with a Windows program's end folded away.</summary>
    internal static string Name(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return word;
        }

        // Either separator ends a directory, whichever system this runs on: a Windows path is read
        // on Linux too.
        var name = word.TrimEnd('/', '\\').Replace('\\', '/').Split('/')[^1];

        return ProgramEnds.FirstOrDefault(end => name.Length > end.Length && name.EndsWith(end, StringComparison.OrdinalIgnoreCase)) is { } folded
            ? name[..^folded.Length]
            : name;
    }

    /// <summary>
    /// The words <paramref name="word"/> stands for once the shell expands its braces - <c>{a,b}c</c>
    /// is <c>ac</c> and <c>bc</c> - so a root action spelled in braces is still one; the word alone
    /// where it holds none, and every word it spells up to a bound.
    /// </summary>
    internal static IReadOnlyList<string> BraceExpansions(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var results = new List<string>();
        var pending = new Stack<string>([word]);

        while (pending.Count > 0 && results.Count < 64)
        {
            var current = pending.Pop();

            if (BraceGroup(current) is not { } group)
            {
                results.Add(current);
                continue;
            }

            foreach (var alternative in group.Alternatives)
            {
                pending.Push(current[..group.Open] + alternative + current[(group.Close + 1)..]);
            }
        }

        return results;
    }

    /// <summary>The first brace group of a word that holds a comma at its own level, and its alternatives; null where it holds none.</summary>
    private static (int Open, int Close, IReadOnlyList<string> Alternatives)? BraceGroup(string word)
    {
        for (var open = word.IndexOf('{', StringComparison.Ordinal); open >= 0; open = word.IndexOf('{', open + 1))
        {
            var depth = 0;
            var start = open + 1;
            var alternatives = new List<string>();

            for (var at = open; at < word.Length; at++)
            {
                if (word[at] == '{')
                {
                    depth++;
                }
                else if (word[at] == '}' && --depth == 0)
                {
                    if (alternatives.Count == 0)
                    {
                        break;
                    }

                    alternatives.Add(word[start..at]);
                    return (open, at, alternatives);
                }
                else if (word[at] == ',' && depth == 1)
                {
                    alternatives.Add(word[start..at]);
                    start = at + 1;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The commands the words run, the words' own command among them: its environment kept before
    /// it, wrappers stripped, a script one of the words names gone into - at the program, and at a
    /// shell another program runs - and an <c>eval</c> read as the script it is.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> FromWords(IReadOnlyList<string> words, int depth, IReadOnlyList<string> environment)
    {
        // The assignments before the program are the command's environment, carried to what it runs.
        var assigned = EnvironmentOf(words);

        if (assigned > 0)
        {
            environment = [.. environment, .. words.Take(assigned)];
            words = [.. words.Skip(assigned)];
        }

        if (words.Count == 0)
        {
            // Assignments alone run nothing; they stay a command of their own, so what they name is
            // still met by every rule on a path.
            if (environment.Count > 0)
            {
                yield return environment;
            }

            yield break;
        }

        var name = Name(words[0]);

        // A script the command names, gone into: sh -c 'a && b' and the words after its script,
        // cmd /c a & b, powershell -Command and a decoded -EncodedCommand.
        if (Into(name, words) is { } scripts)
        {
            foreach (var command in GoneInto(scripts, depth, environment))
            {
                yield return command;
            }

            yield break;
        }

        if (ReadsItsInput(name, words))
        {
            throw new NotSupportedException($"'{string.Join(' ', words)}' reads the script it runs from its input, which this does not read");
        }

        // env -S takes a command line of its own, which it splits into the words of the command it
        // runs, the words after it among them.
        if (Is(name, "env") && SplitString(words) is { } split)
        {
            if (depth > Depth)
            {
                throw Nested(words);
            }

            foreach (var command in FromWords([.. split.Pairs, .. Script(split.Script, 0).SelectMany(command => command), .. split.Tail], depth + 1, environment))
            {
                yield return command;
            }

            yield break;
        }

        // eval and PowerShell's Invoke-Expression join their arguments into one script and run that.
        if (Is(name, "eval", "iex", "Invoke-Expression"))
        {
            if (words.Count > 1)
            {
                var script = string.Join(' ', words.Skip(1));

                foreach (var command in GoneInto([Is(name, "eval") ? script : PowerShellAsPosix(script)], depth, environment))
                {
                    yield return command;
                }
            }

            yield break;
        }

        // Wrappers, with their own options and values, stripped until a command is left; env's
        // pairs become the command's environment.
        if (Stripped(name, words) is { } stripped)
        {
            if (Is(name, "xargs") && stripped.Count > 0 && (Is(Name(stripped[0]), [.. Shells, "cmd", "powershell", "pwsh", "eval", "env"])))
            {
                throw new NotSupportedException($"'{string.Join(' ', words)}' hands '{stripped[0]}' words from its input, which this does not read");
            }

            foreach (var command in FromWords(stripped, depth, environment))
            {
                yield return command;
            }

            yield break;
        }

        // The program itself: one named by an expansion the shell makes is not read at all.
        if (Expansion(words[0]) is { } expansion)
        {
            throw new NotSupportedException($"'{words[0]}' names the program it runs by {expansion}, which this does not read");
        }

        yield return environment.Count == 0 ? words : [.. environment, .. words];

        // A shell another program runs with its script among the command's words - find -exec,
        // ssh, watch - is gone into too: what it runs is a command of its own.
        for (var at = 1; at < words.Count; at++)
        {
            var rest = words.Skip(at).ToList();

            if (Into(Name(words[at]), rest) is { } inner)
            {
                foreach (var command in GoneInto(inner, depth, environment))
                {
                    yield return command;
                }
            }
            else if (Is(Name(words[at]), "env") && SplitString(rest) is { } innerSplit)
            {
                foreach (var command in GoneInto([innerSplit.Script], depth, environment))
                {
                    yield return command;
                }
            }
        }
    }

    /// <summary>The commands each script runs, one level further in: refused past <see cref="Depth"/>.</summary>
    private static IEnumerable<IReadOnlyList<string>> GoneInto(IReadOnlyList<string> scripts, int depth, IReadOnlyList<string> environment)
    {
        if (depth > Depth)
        {
            throw Nested(scripts);
        }

        foreach (var script in scripts)
        {
            foreach (var command in FromScript(script, depth + 1, environment))
            {
                yield return command;
            }
        }
    }

    /// <summary>The refusal of a script nested past <see cref="Depth"/> levels: a root action inside it would not be seen.</summary>
    private static NotSupportedException Nested(IReadOnlyList<string> words)
        => new($"'{string.Join(' ', words)}' is a script nested past {Depth} levels of going into, which this does not read");

    /// <summary>
    /// What expansion names the word, where the shell would make one before it runs it: a variable
    /// (<c>$X</c>, cmd's <c>%X%</c>), a pattern (<c>*</c>, <c>?</c>, <c>[</c>) or a brace
    /// (<c>{a,b}</c>, <c>{a..b}</c>); null where it names none.
    /// </summary>
    private static string? Expansion(string word)
        => word.Contains('$', StringComparison.Ordinal) || word.Contains('%', StringComparison.Ordinal) ? "a variable"
            : word.IndexOfAny(['*', '?']) >= 0 || (word.IndexOf('[', StringComparison.Ordinal) is var open and >= 0 && word.IndexOf(']', open + 1) > open + 1) ? "a pattern"
            : word.Contains('{', StringComparison.Ordinal) && (word.Contains(',', StringComparison.Ordinal) || word.Contains("..", StringComparison.Ordinal)) ? "a brace"
            : null;

    /// <summary>
    /// The command line <c>env -S</c> (<c>--split-string</c>) takes, the pairs before it and the
    /// words that follow it: null where the words name none. Read before any word of the command
    /// proper, as env reads it.
    /// </summary>
    private static (IReadOnlyList<string> Pairs, string Script, IReadOnlyList<string> Tail)? SplitString(IReadOnlyList<string> words)
    {
        var pairs = new List<string>();

        for (var at = 1; at < words.Count; at++)
        {
            var option = words[at];

            if (option == "-S" || option == "--split-string")
            {
                return at + 1 < words.Count
                    ? (pairs, words[at + 1], [.. words.Skip(at + 2)])
                    : null;
            }

            // Glued to its value: '-Ssudo x', '--split-string=sudo x'.
            if (option.StartsWith("-S", StringComparison.Ordinal) && option.Length > 2)
            {
                return (pairs, option[2..], [.. words.Skip(at + 1)]);
            }

            if (option.StartsWith("--split-string=", StringComparison.Ordinal))
            {
                return (pairs, option["--split-string=".Length..], [.. words.Skip(at + 1)]);
            }

            // Before the option: the pairs it gives, and the names it takes away again.
            if (IsAssignment(option))
            {
                pairs.Add(option);
                continue;
            }

            if ((option == "-u" || option == "--unset") && at + 1 < words.Count)
            {
                at++;
                continue;
            }

            if (option is "-i" or "-0" or "--ignore-environment" or "--null")
            {
                continue;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// The scripts <paramref name="name"/> would run with <paramref name="words"/>, each ready for
    /// the shell reader, where it names one; null where it names none.
    /// </summary>
    private static IReadOnlyList<string>? Into(string name, IReadOnlyList<string> words)
    {
        if (Is(name, Shells))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (option == "--" || !(option.StartsWith('-') || option.StartsWith('+')))
                {
                    return null;
                }

                // An option that takes a value in the next word: the value is no script and no option.
                if (ShellValueTaking.Contains(option, StringComparer.Ordinal))
                {
                    at++;
                    continue;
                }

                // The option that carries c takes the next word as its script, alone (-c) or glued
                // into a short cluster (-lc); a long option (--norc among them) is never a short
                // cluster, however many letters it holds. The words after the script are its own
                // $0, $1 and on, which the script may run: read as scripts too.
                if (option == "-c"
                    || (!option.StartsWith("--", StringComparison.Ordinal) && option.Length > 1 && option.Contains('c', StringComparison.Ordinal)))
                {
                    return at + 1 < words.Count ? [.. words.Skip(at + 1)] : null;
                }
            }

            return null;
        }

        if (Is(name, "cmd"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                // /k runs what it is given as /c does, and stays open after: what it runs is what
                // the rules must meet. cmd takes the command glued to either too.
                if (words[at].StartsWith("/c", StringComparison.OrdinalIgnoreCase) || words[at].StartsWith("/k", StringComparison.OrdinalIgnoreCase))
                {
                    var glued = words[at][2..];
                    var script = string.Join(' ', (glued.Length > 0 ? [glued] : Array.Empty<string>()).Concat(words.Skip(at + 1)));

                    return script.Length > 0 ? [CmdAsPosix(script)] : null;
                }
            }

            return null;
        }

        if (Is(name, "powershell", "pwsh"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (!option.StartsWith('-') && !option.StartsWith('/'))
                {
                    // Windows PowerShell takes its command by position; pwsh takes a script file so.
                    return Is(name, "powershell") ? [PowerShellAsPosix(string.Join(' ', words.Skip(at)))] : null;
                }

                var parameter = option[1..].ToLowerInvariant();

                if (parameter.Length == 0)
                {
                    return null;
                }

                if (parameter == "ec" || "encodedcommand".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 < words.Count ? [PowerShellAsPosix(Decode(words[at + 1]))] : null;
                }

                if (parameter is "cwa" or "commandwithargs" || "command".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 < words.Count ? [PowerShellAsPosix(string.Join(' ', words.Skip(at + 1)))] : null;
                }

                if ("file".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return null;
                }

                if (PowerShellValueTaking.Any(taking => taking.StartsWith(parameter, StringComparison.Ordinal) && parameter.Length >= 2))
                {
                    at++;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>The shells' own options that take a value in the next word.</summary>
    private static readonly string[] ShellValueTaking = ["-o", "+o", "-O", "+O", "--rcfile", "--init-file"];

    /// <summary>PowerShell's own parameters that take a value in the next word, by their whole names: a prefix of one is read as it.</summary>
    private static readonly string[] PowerShellValueTaking =
        ["executionpolicy", "ep", "windowstyle", "version", "inputformat", "outputformat", "configurationname", "configurationfile", "settingsfile", "workingdirectory", "wd", "custompipename", "psconsolefile", "encodedarguments", "ea"];

    /// <summary>
    /// Whether a shell this goes into, run with <paramref name="words"/>, reads its script from its
    /// input: a shell named with neither a script nor a file (or <c>-s</c>), a cmd with neither
    /// <c>/c</c> nor <c>/k</c>, a PowerShell with neither a command nor a file, or one told <c>-</c> -
    /// what it runs is what an earlier command printed. One that only says its version or its help
    /// reads nothing.
    /// </summary>
    private static bool ReadsItsInput(string name, IReadOnlyList<string> words)
    {
        if (words.Skip(1).Any(word => word is "--version" or "--help" or "-?" or "/?" or "-h"
            || word.Equals("-Version", StringComparison.OrdinalIgnoreCase) || word.Equals("-Help", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Is(name, Shells))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var word = words[at];

                if (word is "-s" or "-" || (word.StartsWith('-') && !word.StartsWith("--", StringComparison.Ordinal) && word.Contains('s', StringComparison.Ordinal)))
                {
                    return true;
                }

                if (word == "--")
                {
                    return at + 1 >= words.Count;
                }

                if (ShellValueTaking.Contains(word, StringComparer.Ordinal))
                {
                    at++;
                    continue;
                }

                if (!word.StartsWith('-') && !word.StartsWith('+'))
                {
                    return false;
                }
            }

            return true;
        }

        if (Is(name, "cmd"))
        {
            return true;
        }

        if (Is(name, "powershell", "pwsh"))
        {
            return words.Skip(1).Any(word => word == "-") || words.Skip(1).All(word => word.StartsWith('-') || word.StartsWith('/'));
        }

        return false;
    }

    /// <summary>A decoded <c>-EncodedCommand</c>: base64 of the script as PowerShell writes it, UTF-16LE.</summary>
    private static string Decode(string encoded)
    {
        try
        {
            return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            // Not base64: read it as the script it would have been, a word at a time.
            return encoded;
        }
    }

    /// <summary>
    /// A cmd script spelled for the shell reader: cmd quotes with double quotes alone and escapes
    /// with a caret, while a backslash, a single quote, a dollar and a backtick are its ordinary
    /// characters - each escaped here, so the reader splits the script where cmd does.
    /// </summary>
    private static string CmdAsPosix(string script)
    {
        var builder = new System.Text.StringBuilder(script.Length);
        var quoted = false;

        for (var at = 0; at < script.Length; at++)
        {
            var character = script[at];

            if (character == '"')
            {
                quoted = !quoted;
                builder.Append(character);
            }
            else if (!quoted && character == '^' && at + 1 < script.Length)
            {
                builder.Append('\\').Append(script[++at]);
            }
            else if (character is '\\' or '$' or '`' || (!quoted && character == '\''))
            {
                builder.Append('\\').Append(character);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A PowerShell script spelled for the shell reader: PowerShell escapes with a backtick, keeps a
    /// backslash as an ordinary character and doubles a single quote inside single quotes - each
    /// rewritten here, so the reader splits the script, and reads its names, where PowerShell does.
    /// </summary>
    private static string PowerShellAsPosix(string script)
    {
        var builder = new System.Text.StringBuilder(script.Length);
        var quote = '\0';

        for (var at = 0; at < script.Length; at++)
        {
            var character = script[at];

            if (quote == '\'')
            {
                if (character == '\'' && at + 1 < script.Length && script[at + 1] == '\'')
                {
                    builder.Append("'\\''");
                    at++;
                }
                else
                {
                    if (character == '\'')
                    {
                        quote = '\0';
                    }

                    builder.Append(character);
                }

                continue;
            }

            if (character == '`' && at + 1 < script.Length)
            {
                var escaped = script[++at];

                // Inside double quotes the reader escapes only what is special there; anything else
                // is the character itself.
                if (quote == '"' && escaped is not ('"' or '$' or '`' or '\\' or '\n'))
                {
                    builder.Append(escaped);
                }
                else
                {
                    builder.Append('\\').Append(escaped);
                }

                continue;
            }

            if (character == '\\')
            {
                builder.Append("\\\\");
                continue;
            }

            if (character == '"')
            {
                quote = quote == '"' ? '\0' : '"';
            }
            else if (character == '\'' && quote == '\0')
            {
                quote = '\'';
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The command with its wrapper stripped - <paramref name="name"/> and its own options and
    /// values gone, so <c>nohup git push</c> is the <c>git push</c> every rule is matched against,
    /// and <c>env A=1 git push</c> the <c>A=1 git push</c> it is - or null where
    /// <paramref name="name"/> wraps nothing.
    /// </summary>
    private static IReadOnlyList<string>? Stripped(string name, IReadOnlyList<string> words)
    {
        if (!Is(name, "env", "nohup", "time", "timeout", "nice", "xargs", "command", "builtin", "exec"))
        {
            return null;
        }

        var at = 1;

        // The wrapper's own spelling from here on: its options are read in the one case each of its
        // kinds writes them.
        name = name.ToLowerInvariant();

        if (name == "env")
        {
            // NAME=value pairs, kept as the command's environment, and names taken away again with
            // -u, before the command. -S and --split-string, a command line of env's own, are read by
            // the flattener above this.
            var pairs = new List<string>();

            while (at < words.Count)
            {
                if (IsAssignment(words[at]))
                {
                    pairs.Add(words[at]);
                    at++;
                }
                else if ((words[at] == "-u" || words[at] == "--unset") && at + 1 < words.Count)
                {
                    at += 2;
                }
                else if (words[at] is "-i" or "-0" or "--ignore-environment" or "--null")
                {
                    at++;
                }
                else
                {
                    break;
                }
            }

            return [.. pairs, .. words.Skip(at)];
        }

        if (name == "timeout")
        {
            // Its options, each with its value where it takes one, then the duration the command waits.
            while (at < words.Count && words[at].StartsWith('-'))
            {
                at += words[at] is "--foreground" or "-f" or "-p" || words[at].Contains('=', StringComparison.Ordinal) ? 1
                    : words[at] is "-k" or "-s" or "--kill-after" or "--signal" && at + 1 < words.Count ? 2
                    : 1;
            }

            if (at + 1 < words.Count && Duration().IsMatch(words[at]))
            {
                at++;
            }
        }
        else if (name == "nice")
        {
            if (at < words.Count && (words[at] == "-n" || words[at] == "--adjustment") && at + 1 < words.Count)
            {
                at += 2;
            }
            else if (at < words.Count && Niceness().IsMatch(words[at]))
            {
                at++;
            }
        }
        else if (name == "time")
        {
            if (at < words.Count && words[at] == "-p")
            {
                at++;
            }
        }
        else if (name == "xargs")
        {
            // Its options, each a word of its own and each that takes a value with it; the command
            // is the first word that is none.
            while (at < words.Count && words[at].StartsWith('-'))
            {
                at += words[at] is "-I" or "-d" or "-P" or "-n" or "-s" or "-E" or "-j" or "-L" or "-l" or "-o" or "-S"
                    && at + 1 < words.Count ? 2 : 1;
            }
        }
        else if (name is "command" or "builtin")
        {
            while (at < words.Count && words[at] is "-v" or "-V" or "-p")
            {
                at++;
            }
        }

        return [.. words.Skip(at)];
    }

    /// <summary>A duration as timeout takes it: digits, with a unit and a fraction.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(\.\d+)?[smhd]?$")]
    private static partial System.Text.RegularExpressions.Regex Duration();

    /// <summary>Whether <paramref name="name"/> is one of <paramref name="programs"/>, without regard to case, as a program's name is read here.</summary>
    private static bool Is(string name, params string[] programs)
        => programs.Any(program => name.Equals(program, StringComparison.OrdinalIgnoreCase));

    /// <summary>A niceness as nice takes it: a dash and digits, one word.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^-\d+$")]
    private static partial System.Text.RegularExpressions.Regex Niceness();

    /// <summary>
    /// The commands a script runs, read as the system's own shell reads it: split on its
    /// separators, its command substitutions and subshells read as commands of their own, each
    /// command then flattened again - its own words may name a script or carry a wrapper.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> FromScript(string script, int depth, IReadOnlyList<string> environment)
    {
        foreach (var words in Script(script, 0))
        {
            foreach (var flat in FromWords(words, depth, environment))
            {
                yield return flat;
            }
        }
    }

    /// <summary>
    /// The shell's structural words, read past as separators: a loop's and a branch's own words
    /// never join the commands its body runs, and neither does a lone brace or a negation.
    /// </summary>
    private static readonly HashSet<string> Structural =
    [
        "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done", "case", "esac", "in", "!", "{", "}",
    ];

    /// <summary>
    /// The commands the script spells, word by word: quotes read, the body of a substitution or a
    /// subshell read as commands of their own, a redirect's file kept as a word with its operator,
    /// and the structural words never part of a command. A substitution that names the program a
    /// command runs - standing where its program does, or glued to it - is refused, for what it
    /// would print is the program.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <param name="nesting">How deep in parentheses the script stands: refused past <see cref="Nesting"/>.</param>
    private static IEnumerable<IReadOnlyList<string>> Script(string script, int nesting)
    {
        if (nesting > Nesting)
        {
            throw new NotSupportedException($"its parentheses nest past {Nesting} levels, which this does not read");
        }

        List<string>? words = null;
        var at = 0;

        // Whether a double quote is open, carried across the words the script spells: a quote a
        // command substitution interrupts stays open over it, as the shell reads it.
        var quoted = false;

        // Whether a subshell stood where the command's program does: a word after it would be run
        // as the program its output names, as PowerShell's '& (...)' runs one.
        var led = false;

        // Where the last word read ended: a substitution that begins there is glued to it.
        var endOfLast = -1;

        while (at < script.Length)
        {
            var character = script[at];

            // Inside an open double quote the shell's structure is not structure: a separator, a
            // parenthesis and a subshell are ordinary characters until the quote closes, while a
            // backtick and a $(...) substitute there as anywhere.
            if (quoted && !(character == '$' && at + 1 < script.Length && script[at + 1] == '(') && character != '`')
            {
                var inside = Word(script, ref at, ref quoted);

                if (inside.Length > 0)
                {
                    (words ??= []).Add(inside);
                    endOfLast = at;
                }

                continue;
            }

            var substitution = (character == '$' && at + 1 < script.Length && script[at + 1] == '(') || character == '`';

            // A substitution that stands where the program does, or is glued to the program's word,
            // names the program by what it prints.
            if (substitution && (ProgramPending(words) || (endOfLast == at && words is { } held && held.Count - EnvironmentOf(held) == 1)))
            {
                throw new NotSupportedException("a substitution names the program a command runs, which this does not read");
            }

            // A command substitution or a subshell: its body is commands of their own, and it gives
            // the command it stands in no word, for what it would give is what it printed.
            if ((character == '$' && at + 1 < script.Length && script[at + 1] == '(') || character == '(')
            {
                var open = character == '$' ? at + 1 : at;

                if (Matching(script, open) is not { } end)
                {
                    throw new NotSupportedException("a parenthesis in it does not close, which this does not read");
                }

                if (character == '(' && ProgramPending(words))
                {
                    led = true;
                }

                foreach (var command in Script(script[(open + 1)..end], nesting + 1))
                {
                    yield return command;
                }

                at = end + 1;
                continue;
            }

            // A backtick substitution: read to its closing backtick, as a command of its own.
            if (character == '`')
            {
                var closing = script.IndexOf('`', at + 1);

                if (closing < 0)
                {
                    throw new NotSupportedException("a backtick in it does not close, which this does not read");
                }

                foreach (var command in Script(script[(at + 1)..closing], nesting + 1))
                {
                    yield return command;
                }

                at = closing + 1;
                continue;
            }

            // Separators: each ends the command before it and starts one after it. A closing
            // parenthesis nothing opened ends a case pattern, and separates as one does.
            if (character is ';' or '\n' or '\r' or '|' or '&' or ')')
            {
                at++;
                led = false;
                endOfLast = -1;

                if (words is not null)
                {
                    yield return words;
                    words = null;
                }

                continue;
            }

            if (character is ' ' or '\t')
            {
                at++;
                continue;
            }

            var word = Word(script, ref at, ref quoted);

            if (word.Length == 0)
            {
                continue;
            }

            if (Structural.Contains(word))
            {
                led = false;
                endOfLast = -1;

                if (words is not null)
                {
                    yield return words;
                    words = null;
                }

                continue;
            }

            if (led && !IsAssignment(word) && !IsRedirect(word))
            {
                throw new NotSupportedException("a subshell names the program a command runs, which this does not read");
            }

            (words ??= []).Add(word);
            endOfLast = at;
        }

        if (words is not null)
        {
            yield return words;
        }
    }

    /// <summary>Whether the command read so far has no program yet: no word, or its environment alone.</summary>
    private static bool ProgramPending(List<string>? words)
        => words is null || EnvironmentOf(words) == words.Count;

    /// <summary>Whether <paramref name="word"/> is a redirect as the reader keeps one: its operator, then the file it names.</summary>
    internal static bool IsRedirect(string word)
        => word.Length > 0 && (word[0] is '<' or '>' || (char.IsDigit(word[0]) && word.TrimStart("0123456789".ToCharArray()) is [ '<' or '>', ..]));

    /// <summary>
    /// One word of a script: single quotes read literally, <c>$'...'</c> with its escapes decoded, a
    /// backslash escaping whatever follows it - inside double quotes only a quote, a backtick, a
    /// dollar, itself and a line end - and a line it ends continued; a redirect operator kept before
    /// the file it names (<c>&gt;out.txt</c>), so the path a command writes is one the policy is
    /// asked about and no host is read from it. Whether a double quote is open when the word ends is
    /// given back, for a quote a command substitution interrupts stays open over it, as the shell
    /// reads it.
    /// </summary>
    private static string Word(string script, ref int at, ref bool quoted)
    {
        var word = new System.Text.StringBuilder();

        // A redirect, its file glued to it or not: '>file', '>> file', '2>file'. A numbered redirect
        // to a number ('2>&1') names no file: the whole operator is consumed and the word is empty.
        // Inside an open double quote no operator is an operator, and the characters are the word's
        // own.
        var opens = !quoted
            && (script[at] is '<' or '>'
                || (char.IsDigit(script[at]) && at + 1 < script.Length && script[at + 1] is '<' or '>'));

        if (opens)
        {
            // The operator's own characters, a doubled one among them ('>>', '2>>'), kept whole
            // before the file it names.
            var operatorEnd = at + 1;

            while (operatorEnd < script.Length && script[operatorEnd] is '<' or '>' or '|')
            {
                operatorEnd++;
            }

            if (operatorEnd < script.Length && script[operatorEnd] == '&')
            {
                at = Math.Min(operatorEnd + 2, script.Length);
                return string.Empty;
            }

            word.Append(script[at..operatorEnd]);
            at = operatorEnd;

            while (at < script.Length && script[at] is ' ' or '\t')
            {
                at++;
            }
        }

        while (at < script.Length)
        {
            var character = script[at];

            // Inside double quotes a separator is an ordinary character - a quoted ';' is an
            // argument, not a second command - while a backtick and a $(...) substitute there too,
            // as the shell reads them. Outside them a redirect's operator begins a word of its own.
            if (!quoted && (character is ';' or '\n' or '\r' or '|' or '&' or '(' or ')' or ' ' or '\t'
                || (character is '<' or '>' && word.Length > 0 && !opens)))
            {
                break;
            }

            if (character == '`')
            {
                break;
            }

            if (character == '\'' && !quoted)
            {
                var end = script.IndexOf('\'', at + 1);

                if (end < 0)
                {
                    word.Append(script[(at + 1)..]);
                    at = script.Length;
                }
                else
                {
                    word.Append(script[(at + 1)..end]);
                    at = end + 1;
                }

                // The segment read whole, quotes included: a word may follow it, its own.
                continue;
            }

            // $'...', read with its escapes decoded: '$'\x73udo'' is sudo. $"..." is a double quote.
            if (character == '$' && !quoted && at + 1 < script.Length && script[at + 1] == '\'')
            {
                word.Append(AnsiC(script, ref at));
                continue;
            }

            if (character == '$' && !quoted && at + 1 < script.Length && script[at + 1] == '"')
            {
                at++;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                at++;
                continue;
            }

            // A backslash before a line end continues the line: both go, and the word goes on.
            if (character == '\\' && at + 1 < script.Length && script[at + 1] is '\n' or '\r')
            {
                at += script[at + 1] == '\r' && at + 2 < script.Length && script[at + 2] == '\n' ? 3 : 2;
                continue;
            }

            // A backslash escapes whatever follows it, and inside double quotes only a quote, a
            // backtick, a dollar and itself - the shell's own rule, which leaves a Windows path
            // spelled with backslashes itself inside quotes and lets no spelling of a root action
            // escape by a backslash before one of its letters.
            if (character == '\\' && at + 1 < script.Length
                && (!quoted || script[at + 1] is '"' or '`' or '$' or '\\'))
            {
                word.Append(script[at + 1]);
                at += 2;
                continue;
            }

            if (character == '$' && at + 1 < script.Length && script[at + 1] == '(')
            {
                break;
            }

            word.Append(character);
            at++;
        }

        return word.ToString();
    }

    /// <summary>
    /// The text of a <c>$'...'</c> at <paramref name="at"/>, its escapes decoded as bash decodes
    /// them - the named ones, octal, hexadecimal and Unicode - and <paramref name="at"/> moved past
    /// its closing quote.
    /// </summary>
    private static string AnsiC(string script, ref int at)
    {
        var text = new System.Text.StringBuilder();
        at += 2;

        while (at < script.Length && script[at] != '\'')
        {
            if (script[at] != '\\' || at + 1 >= script.Length)
            {
                text.Append(script[at++]);
                continue;
            }

            var escape = script[at + 1];
            at += 2;

            switch (escape)
            {
                case 'a': text.Append('\a'); break;
                case 'b': text.Append('\b'); break;
                case 'e' or 'E': text.Append('\u001b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'v': text.Append('\v'); break;
                case 'x': text.Append(Coded(script, ref at, 2, 16)); break;
                case 'u': text.Append(Coded(script, ref at, 4, 16)); break;
                case 'U': text.Append(Coded(script, ref at, 8, 16)); break;
                case 'c' when at < script.Length: text.Append((char)(script[at++] & 0x1f)); break;
                case >= '0' and <= '7':
                    at--;
                    text.Append(Coded(script, ref at, 3, 8));
                    break;
                default: text.Append(escape); break;
            }
        }

        at = Math.Min(at + 1, script.Length);
        return text.ToString();
    }

    /// <summary>The character a run of up to <paramref name="most"/> digits of <paramref name="radix"/> at <paramref name="at"/> codes, the digits consumed.</summary>
    private static string Coded(string script, ref int at, int most, int radix)
    {
        var value = 0L;
        var read = 0;

        while (read < most && at < script.Length && Uri.IsHexDigit(script[at]) && Uri.FromHex(script[at]) < radix)
        {
            value = (value * radix) + Uri.FromHex(script[at]);
            at++;
            read++;
        }

        // A value no character has - past the last, or half of one - is the replacement character.
        return read == 0 ? string.Empty
            : value is > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) ? "�"
            : char.ConvertFromUtf32((int)value);
    }

    /// <summary>
    /// The index of the parenthesis that closes the one at <paramref name="open"/>, counting the ones
    /// inside and passing over what quotes hold - a substitution's body its own again, as the shell
    /// reads it; null where none closes it.
    /// </summary>
    private static int? Matching(string script, int open)
    {
        // The quote open around each parenthesis met, restored when it closes: a $(...) inside
        // double quotes begins quoting afresh.
        var around = new Stack<char>();
        var quote = '\0';

        for (var at = open; at < script.Length; at++)
        {
            var character = script[at];

            if (quote == '\'')
            {
                if (character == '\'')
                {
                    quote = '\0';
                }

                continue;
            }

            if (character == '\\')
            {
                at++;
                continue;
            }

            if (character == '$' && at + 1 < script.Length && script[at + 1] == '(')
            {
                around.Push(quote);
                quote = '\0';
                at++;
                continue;
            }

            if (quote == '"')
            {
                if (character == '"')
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '(')
            {
                around.Push(quote);
                quote = '\0';
            }
            else if (character == ')' && around.Count > 0)
            {
                quote = around.Pop();

                if (around.Count == 0)
                {
                    return at;
                }
            }
        }

        return null;
    }
}
