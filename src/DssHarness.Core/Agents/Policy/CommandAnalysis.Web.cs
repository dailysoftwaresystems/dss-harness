namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
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
    /// The web addresses one command names, where it runs a program which fetches from the network:
    /// every word curl and wget take as a URL - each that is no option and no option's value, the
    /// short options of one word read letter by letter as they read them - with every host they
    /// reach besides (curl's proxies, a <c>--connect-to</c>'s and a <c>--resolve</c>'s target, a
    /// <c>--doh-url</c>, wget's base), the proxy the command's environment names, each address
    /// PowerShell's fetchers take by position, by <c>-Uri</c> or by <c>-Proxy</c> - a parameter by
    /// any prefix PowerShell takes - the URL of an <c>http</c> or <c>https</c>, and the endpoint of
    /// <c>gh api</c>, which is <c>api.github.com</c>'s unless a <c>--hostname</c> or the
    /// environment's <c>GH_HOST</c> names another. Empty where the command's program is none of
    /// them. A word of an option this does not know is read as the address it may be, never
    /// passed over. A host named in quotes is a host of that name: read as one only where the
    /// shell would expand it, by the word the address stands in - never by another word that
    /// spells it alike.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one, as <see cref="AnalyzedCommand.Words"/> holds them.</param>
    /// <param name="quoted">How each word's characters stand to the shell, kept in step with <paramref name="words"/>, or null where none was kept.</param>
    /// <exception cref="NotSupportedException">An address's host is named by an expansion, which this does not read.</exception>
    public static IReadOnlyList<string> WebAddresses(IReadOnlyList<string> words, IReadOnlyList<string>? quoted = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        quoted ??= [];

        // A file URL reads the machine's own files, no network: it is a path, as the policy reads it.
        var reached = Fetched(words, quoted).Addresses.Where(each => !each.Address.StartsWith("file:", StringComparison.OrdinalIgnoreCase)).ToList();

        // The host an address reaches, read where the shell would expand it: an address named in quotes is an address
        // of that name, its host included.
        static string? HostExpansion(Reach each)
            => AuthorityOf(each.Address) is var (start, length)
                && Expansion(each.Address.Substring(start, length), each.Quoted is { } mask && start + length <= mask.Length ? mask.Substring(start, length) : null) is { } expansion
                ? expansion
                : null;

        if (reached.FirstOrDefault(each => HostExpansion(each) is not null) is { Address: { } expanded } named)
        {
            throw new NotSupportedException($"'{expanded}' names the host it reaches by {HostExpansion(named)}, which this does not read");
        }

        return [.. reached.Select(each => each.Address)];
    }

    /// <summary>
    /// An address a fetcher command reaches, and how its characters stand to the shell where the word it stands in kept
    /// its spelling: the word's mask cut to the address. Null where the spelling was not kept, or the address is made
    /// rather than spelled - a <c>--connect-to</c>'s target, an <c>http</c>'s <c>localhost</c> - which reads as wholly
    /// open.
    /// </summary>
    /// <param name="Address">The address.</param>
    /// <param name="Quoted">How each of its characters stands to the shell, or null.</param>
    private readonly record struct Reach(string Address, string? Quoted);

    /// <summary>
    /// The option of a fetcher command that reads its addresses or its settings from a file this
    /// does not read - curl's <c>--config</c>, wget's <c>--input-file</c>, <c>--execute</c> and
    /// <c>--config</c>, each in any spelling the program takes - so where the web is held to rules,
    /// what it reaches cannot be told; null where it names none.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one.</param>
    public static string? WebAddressesUnread(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        return Fetched(words, []).Unread;
    }

    /// <summary>The environment names a fetcher reaches the network through: a proxy any of them names is a host the request reaches.</summary>
    private static readonly string[] ProxyNames = ["http_proxy", "https_proxy", "all_proxy", "ftp_proxy"];

    /// <summary>
    /// What one fetcher command reaches: its addresses, each with how it stands to the shell, and the option that reads
    /// them from a file this does not read, where one does.
    /// </summary>
    private static (IReadOnlyList<Reach> Addresses, string? Unread) Fetched(IReadOnlyList<string> words, IReadOnlyList<string> quoted)
    {
        var environment = EnvironmentOf(words);

        if (environment == words.Count)
        {
            return ([], null);
        }

        var program = Name(words[environment]);
        var arguments = words.Skip(environment + 1).ToList();
        var argumentQuoted = quoted.Skip(environment + 1).ToList();

        // Each assignment's name and value, the value's mask cut from its word's where the word kept its spelling.
        var assigned = words.Take(environment)
            .Select((assignment, at) => (Name: AssignedName(assignment), Value: new Reach(assignment.Split('=', 2)[1], Cut(quoted, at, assignment.IndexOf('=', StringComparison.Ordinal) + 1, assignment.Split('=', 2)[1].Length))))
            .ToList();
        var proxies = assigned
            .Where(pair => ProxyNames.Contains(pair.Name, StringComparer.OrdinalIgnoreCase) && pair.Value.Address.Length > 0)
            .Select(pair => pair.Value)
            .ToList();

        if (program.Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            var host = assigned.LastOrDefault(pair => pair.Name == "GH_HOST" && pair.Value.Address.Length > 0) is { Name: not null } given ? given.Value : new Reach("api.github.com", null);
            return (GhApi(arguments, host, argumentQuoted) is { } endpoint ? [.. proxies, endpoint] : [], null);
        }

        if (program.Equals("curl", StringComparison.OrdinalIgnoreCase))
        {
            return Options(arguments, argumentQuoted, proxies, Curl);
        }

        if (program.Equals("wget", StringComparison.OrdinalIgnoreCase))
        {
            return Options(arguments, argumentQuoted, proxies, Wget);
        }

        if (Is(program, "Invoke-WebRequest", "Invoke-RestMethod", "iwr", "irm"))
        {
            return (PowerShellFetch(arguments, argumentQuoted, proxies), null);
        }

        if (Is(program, "http", "https"))
        {
            return (Httpie(arguments, argumentQuoted, proxies), null);
        }

        return ([], null);
    }

    /// <summary>How one fetcher's options are read: its short options' letters and long options' names that take a value, those whose value is an address, and those that read a file this does not.</summary>
    private sealed record FetcherOptions(string ShortValued, string ShortAddress, string ShortUnread, string[] LongValued, string[] LongAddress, string[] LongUnread);

    /// <summary>curl's options, as its manual names them; an option this does not name is read as taking no value.</summary>
    private static readonly FetcherOptions Curl = new(
        ShortValued: "AbcCdDeEFHKmoPQrtTuUwxXyYz",
        ShortAddress: "x",
        ShortUnread: "K",
        LongValued:
        [
            "--output", "--data", "--data-raw", "--data-binary", "--data-urlencode", "--data-ascii", "--json", "--header", "--proxy-header",
            "--user-agent", "--user", "--max-time", "--referer", "--cookie", "--cookie-jar", "--form", "--form-string", "--upload-file",
            "--cert", "--cert-type", "--key", "--key-type", "--cacert", "--capath", "--ciphers", "--connect-timeout", "--content-type",
            "--dns-servers", "--dns-interface", "--dns-ipv4-addr", "--dns-ipv6-addr", "--interface", "--ip-address", "--limit-rate",
            "--mail-from", "--mail-rcpt", "--mail-auth", "--netrc-file", "--oauth2-bearer", "--pass", "--proxy-user", "--pubkey",
            "--quote", "--random-file", "--range", "--telnet-option", "--write-out", "--request", "--request-target", "--dump-header",
            "--trace", "--trace-ascii", "--output-dir", "--stderr", "--unix-socket", "--abstract-unix-socket", "--variable", "--url-query",
            "--aws-sigv4", "--sasl-authzid", "--service-name", "--retry", "--retry-delay", "--retry-max-time", "--max-filesize",
            "--max-redirs", "--speed-limit", "--speed-time", "--keepalive-time", "--expect100-timeout", "--happy-eyeballs-timeout-ms",
            "--hostpubmd5", "--hostpubsha256", "--krb", "--local-port", "--login-options", "--tlsuser", "--tlspassword", "--tlsauthtype",
            "--tls-max", "--tls13-ciphers", "--curves", "--engine", "--egd-file", "--ftp-port", "--ftp-account", "--ftp-alternative-to-user",
            "--ftp-method", "--ftp-ssl-ccc-mode", "--create-file-mode", "--ech", "--noproxy", "--pinnedpubkey", "--proto", "--proto-default",
            "--proto-redir", "--rate", "--tftp-blksize", "--time-cond", "--continue-at", "--delegation", "--proxy-cacert", "--proxy-capath",
            "--proxy-cert", "--proxy-cert-type", "--proxy-ciphers", "--proxy-key", "--proxy-key-type", "--proxy-pass", "--proxy-pinnedpubkey",
            "--proxy-service-name", "--proxy-tls13-ciphers", "--proxy-tlsauthtype", "--proxy-tlspassword", "--proxy-tlsuser", "--socks5-gssapi-service",
            "--haproxy-clientip", "--etag-compare", "--etag-save", "--hsts", "--alt-svc", "--parallel-max", "--crlfile", "--sslv2", "--expand-url",
        ],
        LongAddress: ["--url", "--proxy", "--preproxy", "--socks4", "--socks4a", "--socks5", "--socks5-hostname", "--doh-url", "--ipfs-gateway", "--connect-to", "--resolve"],
        LongUnread: ["--config"]);

    /// <summary>wget's options, as its manual names them; an option this does not name is read as taking no value.</summary>
    private static readonly FetcherOptions Wget = new(
        ShortValued: "oaeitOTwQPlARDIXUB",
        ShortAddress: "B",
        ShortUnread: "ie",
        LongValued:
        [
            "--output-document", "--output-file", "--append-output", "--directory-prefix", "--user-agent", "--header", "--post-data",
            "--post-file", "--body-data", "--body-file", "--method", "--user", "--password", "--http-user", "--http-password", "--ftp-user",
            "--ftp-password", "--proxy-user", "--proxy-password", "--tries", "--timeout", "--dns-timeout", "--connect-timeout", "--read-timeout",
            "--wait", "--waitretry", "--level", "--accept", "--reject", "--accept-regex", "--reject-regex", "--domains", "--exclude-domains",
            "--referer", "--load-cookies", "--save-cookies", "--ca-certificate", "--ca-directory", "--certificate", "--certificate-type",
            "--private-key", "--private-key-type", "--quota", "--limit-rate", "--bind-address", "--exclude-directories", "--include-directories",
            "--local-encoding", "--remote-encoding", "--restrict-file-names", "--default-page", "--cut-dirs", "--secure-protocol", "--ciphers",
            "--crl-file", "--pinnedpubkey", "--random-file", "--egd-file", "--hsts-file", "--warc-file", "--warc-header", "--warc-max-size",
            "--warc-cdx", "--warc-dedup", "--warc-tempdir", "--report-speed", "--prefer-family", "--retry-on-http-error", "--progress",
            "--rejected-log", "--use-askpass", "--compression", "--max-redirect",
        ],
        LongAddress: ["--base"],
        LongUnread: ["--input-file", "--execute", "--config"]);

    /// <summary>
    /// A fetcher's words read by its options: each that is no option and no option's value a URL;
    /// a short option's letters one by one, the first that takes a value taking the rest of its
    /// word or the next one; a long option's value glued after an <c>=</c> or the next word.
    /// </summary>
    private static (IReadOnlyList<Reach> Addresses, string? Unread) Options(IReadOnlyList<string> arguments, IReadOnlyList<string> quoted, IReadOnlyList<Reach> proxies, FetcherOptions options)
    {
        var addresses = new List<Reach>(proxies);
        string? unread = null;

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (word == "--")
            {
                addresses.AddRange(arguments.Skip(at + 1).Select((rest, after) => new Reach(rest, At(quoted, at + 1 + after))));
                break;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var name = word.Split('=', 2)[0];
                var valued = options.LongValued.Contains(name, StringComparer.Ordinal) || options.LongAddress.Contains(name, StringComparer.Ordinal) || options.LongUnread.Contains(name, StringComparer.Ordinal);

                if (!valued)
                {
                    continue;
                }

                var value = word.Length > name.Length ? new Reach(word[(name.Length + 1)..], Cut(quoted, at, name.Length + 1, word.Length - name.Length - 1))
                    : at + 1 < arguments.Count ? new Reach(arguments[++at], At(quoted, at))
                    : (Reach?)null;

                if (options.LongUnread.Contains(name, StringComparer.Ordinal))
                {
                    unread ??= word;
                }
                else if (value is { } given && options.LongAddress.Contains(name, StringComparer.Ordinal))
                {
                    addresses.AddRange(Reached(name, given));
                }

                continue;
            }

            if (word.Length > 1 && word[0] == '-')
            {
                for (var letter = 1; letter < word.Length; letter++)
                {
                    if (!options.ShortValued.Contains(word[letter], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = letter + 1 < word.Length ? new Reach(word[(letter + 1)..], Cut(quoted, at, letter + 1, word.Length - letter - 1))
                        : at + 1 < arguments.Count ? new Reach(arguments[++at], At(quoted, at))
                        : (Reach?)null;

                    if (options.ShortUnread.Contains(word[letter], StringComparison.Ordinal))
                    {
                        unread ??= word;
                    }
                    else if (value is { } given && options.ShortAddress.Contains(word[letter], StringComparison.Ordinal))
                    {
                        addresses.Add(given);
                    }

                    break;
                }

                continue;
            }

            addresses.Add(new Reach(word, At(quoted, at)));
        }

        return (addresses, unread);
    }

    /// <summary>
    /// The hosts an address-valued long option reaches: <c>--connect-to</c>'s
    /// <c>HOST1:PORT1:HOST2:PORT2</c> reaches HOST2, <c>--resolve</c>'s
    /// <c>[+]HOST:PORT:ADDRESS[,ADDRESS]...</c> each address - each made from the value, so read as wholly open - any
    /// other its value as it was spelled.
    /// </summary>
    private static IEnumerable<Reach> Reached(string option, Reach value)
        => option switch
        {
            "--connect-to" => value.Address.Split(':') is { Length: >= 3 } parts && parts[2].Length > 0 ? [new Reach(parts[2].Trim('[', ']'), null)] : [],
            "--resolve" => value.Address.Split(':', 3) is { Length: 3 } parts
                ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(address => new Reach(address.Trim('[', ']'), null))
                : [],
            _ => [value],
        };

    /// <summary>
    /// The parameters of PowerShell's fetchers that take a value, by their whole names - a prefix of
    /// one read as it, where it names no other - and those whose value is an address. A parameter
    /// this does not know is read as a switch, so the word after it is read as the address it may be.
    /// </summary>
    private static readonly string[] PowerShellFetchValued =
    [
        "uri", "proxy", "outfile", "method", "custommethod", "headers", "body", "contenttype", "useragent", "timeoutsec",
        "connectiontimeoutseconds", "operationtimeoutseconds", "credential", "proxycredential", "sessionvariable", "websession",
        "infile", "certificate", "certificatethumbprint", "maximumredirection", "maximumretrycount", "retryintervalsec",
        "transferencoding", "form", "sslprotocol", "token", "authentication", "httpversion", "responseheadersvariable",
        "statuscodevariable", "unixsocket", "pipelinevariable", "outvariable", "errorvariable", "warningvariable",
        "informationvariable", "erroraction", "warningaction", "informationaction", "outbuffer", "progressaction",
    ];

    /// <summary>The switches of PowerShell's fetchers, which a prefix may name as it names a parameter.</summary>
    private static readonly string[] PowerShellFetchSwitches =
    [
        "usebasicparsing", "usedefaultcredentials", "disablekeepalive", "passthru", "resume", "skipcertificatecheck",
        "skipheadervalidation", "skiphttperrorcheck", "noproxy", "preserveauthorizationonredirect", "allowunencryptedauthentication",
        "allowinsecureredirect", "preservehttpmethodonredirect", "followrellink", "verbose", "debug",
    ];

    /// <summary>The addresses a PowerShell fetcher reaches: <c>-Uri</c>'s, <c>-Proxy</c>'s, and every word it takes by position.</summary>
    private static IReadOnlyList<Reach> PowerShellFetch(IReadOnlyList<string> arguments, IReadOnlyList<string> quoted, IReadOnlyList<Reach> proxies)
    {
        var addresses = new List<Reach>(proxies);

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (!word.StartsWith('-') || word.Length < 2)
            {
                addresses.Add(new Reach(word, At(quoted, at)));
                continue;
            }

            var spelled = word[1..].Split(':', 2);
            var prefix = spelled[0].ToLowerInvariant();
            var named = PowerShellFetchValued.Concat(PowerShellFetchSwitches).Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var parameter = named.Contains(prefix) ? prefix : named.Count == 1 ? named[0] : null;

            if (parameter is null || !PowerShellFetchValued.Contains(parameter))
            {
                continue;
            }

            var value = spelled.Length == 2 ? new Reach(spelled[1], Cut(quoted, at, spelled[0].Length + 2, spelled[1].Length))
                : at + 1 < arguments.Count ? new Reach(arguments[++at], At(quoted, at))
                : (Reach?)null;

            if (value is { Address.Length: > 0 } given && parameter is "uri" or "proxy")
            {
                addresses.Add(given);
            }
        }

        return addresses;
    }

    /// <summary>The options of <c>http</c> and <c>https</c> that take a value: their long names and short letters, and the proxy's, whose value is <c>scheme:URL</c>.</summary>
    private static readonly string[] HttpieValued =
    [
        "--output", "-o", "--auth", "-a", "--auth-type", "-A", "--session", "--session-read-only", "--verify", "--cert", "--cert-key",
        "--cert-key-pass", "--proxy", "--pretty", "--style", "-s", "--print", "-p", "--history-print", "-P", "--format-options",
        "--timeout", "--max-redirects", "--max-headers", "--boundary", "--raw", "--ssl", "--ciphers", "--default-scheme",
        "--response-charset", "--response-mime",
    ];

    /// <summary>
    /// The addresses an <c>http</c> or <c>https</c> reaches: its proxies, and its URL - the first
    /// word that is no option, or the second where the first is the method - <c>:3000/x</c> being
    /// the machine's own.
    /// </summary>
    private static IReadOnlyList<Reach> Httpie(IReadOnlyList<string> arguments, IReadOnlyList<string> quoted, IReadOnlyList<Reach> proxies)
    {
        var addresses = new List<Reach>(proxies);
        var positional = new List<Reach>();

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];
            var name = word.Split('=', 2)[0];

            if (!word.StartsWith('-'))
            {
                positional.Add(new Reach(word, At(quoted, at)));
                continue;
            }

            if (!HttpieValued.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var value = word.Length > name.Length ? word[(name.Length + 1)..] : at + 1 < arguments.Count ? arguments[++at] : null;

            // The proxy's URL is made from its value, after its scheme's name: read as wholly open.
            if (name == "--proxy" && value?.Split(':', 2) is [_, { Length: > 0 } proxy])
            {
                addresses.Add(new Reach(proxy, null));
            }
        }

        var url = positional.Count > 1 && HttpMethods.Contains(positional[0].Address, StringComparer.OrdinalIgnoreCase) ? positional[1] : positional.FirstOrDefault();

        if (url.Address is not null)
        {
            addresses.Add(url.Address.StartsWith(':') ? new Reach("localhost" + url.Address, null) : url);
        }

        return addresses;
    }

    /// <summary>The methods an <c>http</c> or <c>https</c> takes before its URL.</summary>
    private static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    /// <summary>The long options of <c>gh api</c> that take a value: glued after an <c>=</c>, or the next word.</summary>
    private static readonly string[] GhApiValueTaking =
        ["method", "header", "raw-field", "field", "preview", "jq", "template", "input", "cache", "hostname"];

    /// <summary>
    /// The short options of <c>gh api</c> that take a value, each by its long name: the rest of its word, after an
    /// <c>=</c> where one stands first, or the next word. Any other letter - gh's <c>-i</c> among them - takes none.
    /// </summary>
    private static readonly Dictionary<char, string> GhApiShorts = new()
    {
        ['X'] = "method", ['H'] = "header", ['f'] = "raw-field", ['F'] = "field", ['p'] = "preview", ['q'] = "jq", ['t'] = "template",
    };

    /// <summary>
    /// <c>gh api</c>'s words as pflag reads them: each option by its long name with its value - a long one's glued after
    /// an <c>=</c> or the next word, a short one's by <see cref="GhApiShorts"/> - and a cluster of short ones
    /// (<c>-iXDELETE</c>, <c>-iX DELETE</c>, <c>-if a=b</c>) read letter by letter, the first that takes a value taking
    /// the rest of the word or the next; and each word that is no option, nor an option's value, with no name. A
    /// short letter that takes no value - gh's -i, or one gh does not have - is read by itself, so the letters after it are
    /// read all the same. Each value carries how it stands to the shell, cut from the word it stands in by where it
    /// stands there (<paramref name="quoted"/>): never from another word that spells it alike, an option's value that
    /// would lend its quotes to the endpoint or the field it copies.
    /// </summary>
    /// <param name="arguments">The words after <c>gh</c>, <c>api</c> first.</param>
    /// <param name="quoted">How each argument's characters stand to the shell, kept in step with <paramref name="arguments"/>; empty where none was kept.</param>
    private static IEnumerable<GhApiWord> GhApiWords(IReadOnlyList<string> arguments, IReadOnlyList<string> quoted)
    {
        for (var at = 1; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (word == "--")
            {
                for (var rest = at + 1; rest < arguments.Count; rest++)
                {
                    yield return new(null, arguments[rest], At(quoted, rest));
                }

                yield break;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var name = word[2..].Split('=', 2);

                yield return name.Length == 2 ? new(name[0], name[1], Cut(quoted, at, name[0].Length + 3, name[1].Length))
                    : GhApiValueTaking.Contains(name[0], StringComparer.Ordinal) && at + 1 < arguments.Count ? new(name[0], arguments[++at], At(quoted, at))
                    : new(name[0], string.Empty, null);
                continue;
            }

            if (word.Length < 2 || word[0] != '-')
            {
                yield return new(null, word, At(quoted, at));
                continue;
            }

            for (var letter = 1; letter < word.Length; letter++)
            {
                if (!GhApiShorts.TryGetValue(word[letter], out var named))
                {
                    yield return new(word[letter].ToString(), string.Empty, null);
                    continue;
                }

                // Its value glued after it, past an '=' where one stands first, or the next word.
                var from = letter + 1 < word.Length && word[letter + 1] == '=' ? letter + 2 : letter + 1;

                yield return letter + 1 < word.Length ? new(named, word[from..], Cut(quoted, at, from, word.Length - from))
                    : at + 1 < arguments.Count ? new(named, arguments[++at], At(quoted, at))
                    : new(named, string.Empty, null);
                break;
            }
        }
    }

    /// <summary>
    /// One word of <c>gh api</c>'s, as <see cref="GhApiWords"/> reads it: the option's long name, or null for a word that
    /// is no option and no option's value; its value; and how the value stands to the shell, null where its spelling was
    /// not kept, which reads as wholly open.
    /// </summary>
    /// <param name="Name">The option's long name, or null.</param>
    /// <param name="Value">Its value, or the word itself.</param>
    /// <param name="Quoted">How each of the value's characters stands to the shell, or null.</param>
    private readonly record struct GhApiWord(string? Name, string Value, string? Quoted);

    /// <summary>
    /// The address <c>gh api</c> reaches: its endpoint where that is a whole URL, else
    /// <paramref name="host"/> - unless a <c>--hostname</c> names another; null where the command
    /// is no <c>gh api</c>. An option's value is never its endpoint. An endpoint the shell expands
    /// may be a whole URL of any host - read so only where the shell would expand it.
    /// </summary>
    private static Reach? GhApi(IReadOnlyList<string> arguments, Reach host, IReadOnlyList<string> quoted)
    {
        if (arguments.Count == 0 || !arguments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var words = GhApiWords(arguments, quoted).ToList();

        foreach (var word in words.Where(word => word.Name == "hostname" && word.Value.Length > 0))
        {
            host = new Reach(word.Value, word.Quoted);
        }

        // An endpoint named by an expansion may be a whole URL of any host.
        return words.FirstOrDefault(word => word.Name is null) is { Value: { } path } endpoint
            && (path.Contains("://", StringComparison.Ordinal) || Expansion(path, endpoint.Quoted) is not null)
            ? new Reach(path, endpoint.Quoted)
            : host;
    }

    /// <summary>
    /// The endpoint of <c>gh api</c> - its first word that is no option and no option's value - given the words after
    /// <c>gh</c>, with how it stands to the shell where the command's own spelling was kept (null where it was not,
    /// which reads as wholly open); null where it names none.
    /// </summary>
    /// <param name="arguments">The words after <c>gh</c>, <c>api</c> first.</param>
    /// <param name="quoted">How each argument's characters stand to the shell, kept in step with <paramref name="arguments"/>; empty where none was kept.</param>
    internal static (string Endpoint, string? Quoted)? GhApiEndpoint(IReadOnlyList<string> arguments, IReadOnlyList<string> quoted)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(quoted);

        return GhApiWords(arguments, quoted).FirstOrDefault(word => word.Name is null) is { Value: { } endpoint } found ? (endpoint, found.Quoted) : null;
    }

    /// <summary>
    /// Whether <c>gh api</c> writes: its method, by <c>-X</c> or <c>--method</c> in any form pflag reads
    /// (<see cref="GhApiWords"/>), any but GET or HEAD as written - so one the shell expands, which may be any, writes -
    /// and, where it names none, gh's own POST for a request given fields or an input.
    /// </summary>
    /// <param name="arguments">The words after <c>gh</c>, <c>api</c> first.</param>
    public static bool GhApiWrites(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var words = GhApiWords(arguments, []).ToList();

        return words.LastOrDefault(word => word.Name == "method") is { Name: not null } method
            ? !(method.Value.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Value.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            : words.Any(word => word.Name is "raw-field" or "field" or "input");
    }

    /// <summary>
    /// The values <c>gh api</c>'s fields give, by their names: <c>-f</c>, <c>-F</c>, <c>--raw-field</c> and
    /// <c>--field</c>, each <c>name=value</c>, in any form pflag reads (<see cref="GhApiWords"/>) - each with how it
    /// stands to the shell, taken from the word it stands in, null where that word's spelling was not kept.
    /// </summary>
    /// <param name="arguments">The words after <c>gh</c>, <c>api</c> first.</param>
    /// <param name="quoted">How each argument's characters stand to the shell, kept in step with <paramref name="arguments"/>; empty where none was kept.</param>
    internal static IEnumerable<(string Field, string? Quoted)> GhApiFields(IReadOnlyList<string> arguments, IReadOnlyList<string> quoted)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(quoted);

        return GhApiWords(arguments, quoted).Where(word => word.Name is "raw-field" or "field").Select(word => (word.Value, word.Quoted));
    }

    /// <summary>
    /// Where the host and port of an address stand in it, as written - its scheme, its user and its path left out, and
    /// an IPv6 literal's brackets with what they hold - where an expansion would name the host the address reaches: its
    /// start and its length, so how they stand to the shell is cut from the address's own place.
    /// </summary>
    private static (int Start, int Length) AuthorityOf(string address)
    {
        var start = address.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0 ? scheme + 3 : 0;
        var end = address.IndexOfAny(['/', '?', '#'], start);
        var authority = end < 0 ? address[start..] : address[start..end];
        var user = authority.LastIndexOf('@') + 1;
        var host = authority[user..];

        start += user;

        if (host.StartsWith('[') && host.IndexOf(']', StringComparison.Ordinal) is var closing and > 0)
        {
            return (start + closing + 1, host.Length - closing - 1);
        }

        return (start, host.Length);
    }
}
