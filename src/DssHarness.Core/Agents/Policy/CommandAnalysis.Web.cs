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
    /// passed over.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one, as <see cref="AnalyzedCommand.Words"/> holds them.</param>
    /// <exception cref="NotSupportedException">An address's host is named by an expansion, which this does not read.</exception>
    public static IReadOnlyList<string> WebAddresses(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        // A file URL reads the machine's own files, no network: it is a path, as the policy reads it.
        var addresses = Fetched(words).Addresses.Where(address => !address.StartsWith("file:", StringComparison.OrdinalIgnoreCase)).ToList();

        if (addresses.FirstOrDefault(address => Expansion(Authority(address)) is not null) is { } expanded)
        {
            throw new NotSupportedException($"'{expanded}' names the host it reaches by {Expansion(Authority(expanded))}, which this does not read");
        }

        return addresses;
    }

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

        return Fetched(words).Unread;
    }

    /// <summary>The environment names a fetcher reaches the network through: a proxy any of them names is a host the request reaches.</summary>
    private static readonly string[] ProxyNames = ["http_proxy", "https_proxy", "all_proxy", "ftp_proxy"];

    /// <summary>
    /// What one fetcher command reaches: its addresses, and the option that reads them from a file
    /// this does not read, where one does.
    /// </summary>
    private static (IReadOnlyList<string> Addresses, string? Unread) Fetched(IReadOnlyList<string> words)
    {
        var environment = EnvironmentOf(words);

        if (environment == words.Count)
        {
            return ([], null);
        }

        var program = Name(words[environment]);
        var arguments = words.Skip(environment + 1).ToList();
        var assigned = words.Take(environment).Select(assignment => new[] { AssignedName(assignment), assignment.Split('=', 2)[1] }).ToList();
        var proxies = assigned
            .Where(pair => ProxyNames.Contains(pair[0], StringComparer.OrdinalIgnoreCase) && pair[1].Length > 0)
            .Select(pair => pair[1])
            .ToList();

        if (program.Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            var host = assigned.LastOrDefault(pair => pair[0] == "GH_HOST" && pair[1].Length > 0)?[1];
            return (GhApi(arguments, host ?? "api.github.com") is { } endpoint ? [.. proxies, endpoint] : [], null);
        }

        if (program.Equals("curl", StringComparison.OrdinalIgnoreCase))
        {
            return Options(arguments, proxies, Curl);
        }

        if (program.Equals("wget", StringComparison.OrdinalIgnoreCase))
        {
            return Options(arguments, proxies, Wget);
        }

        if (Is(program, "Invoke-WebRequest", "Invoke-RestMethod", "iwr", "irm"))
        {
            return (PowerShellFetch(arguments, proxies), null);
        }

        if (Is(program, "http", "https"))
        {
            return (Httpie(arguments, proxies), null);
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
    private static (IReadOnlyList<string> Addresses, string? Unread) Options(IReadOnlyList<string> arguments, IReadOnlyList<string> proxies, FetcherOptions options)
    {
        var addresses = new List<string>(proxies);
        string? unread = null;

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (word == "--")
            {
                addresses.AddRange(arguments.Skip(at + 1));
                break;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var name = word.Split('=', 2)[0];
                var glued = word.Length > name.Length ? word[(name.Length + 1)..] : null;
                var valued = options.LongValued.Contains(name, StringComparer.Ordinal) || options.LongAddress.Contains(name, StringComparer.Ordinal) || options.LongUnread.Contains(name, StringComparer.Ordinal);

                if (!valued)
                {
                    continue;
                }

                var value = glued ?? (at + 1 < arguments.Count ? arguments[++at] : null);

                if (options.LongUnread.Contains(name, StringComparer.Ordinal))
                {
                    unread ??= word;
                }
                else if (value is not null && options.LongAddress.Contains(name, StringComparer.Ordinal))
                {
                    addresses.AddRange(Reached(name, value));
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

                    var value = letter + 1 < word.Length ? word[(letter + 1)..] : at + 1 < arguments.Count ? arguments[++at] : null;

                    if (options.ShortUnread.Contains(word[letter], StringComparison.Ordinal))
                    {
                        unread ??= word;
                    }
                    else if (value is not null && options.ShortAddress.Contains(word[letter], StringComparison.Ordinal))
                    {
                        addresses.Add(value);
                    }

                    break;
                }

                continue;
            }

            addresses.Add(word);
        }

        return (addresses, unread);
    }

    /// <summary>
    /// The hosts an address-valued long option reaches: <c>--connect-to</c>'s
    /// <c>HOST1:PORT1:HOST2:PORT2</c> reaches HOST2, <c>--resolve</c>'s
    /// <c>[+]HOST:PORT:ADDRESS[,ADDRESS]...</c> each address, any other its value.
    /// </summary>
    private static IEnumerable<string> Reached(string option, string value)
        => option switch
        {
            "--connect-to" => value.Split(':') is { Length: >= 3 } parts && parts[2].Length > 0 ? [parts[2].Trim('[', ']')] : [],
            "--resolve" => value.Split(':', 3) is { Length: 3 } parts ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(address => address.Trim('[', ']')) : [],
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
    private static IReadOnlyList<string> PowerShellFetch(IReadOnlyList<string> arguments, IReadOnlyList<string> proxies)
    {
        var addresses = new List<string>(proxies);

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (!word.StartsWith('-') || word.Length < 2)
            {
                addresses.Add(word);
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

            var value = spelled.Length == 2 ? spelled[1] : at + 1 < arguments.Count ? arguments[++at] : null;

            if (value is { Length: > 0 } && parameter is "uri" or "proxy")
            {
                addresses.Add(value);
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
    private static IReadOnlyList<string> Httpie(IReadOnlyList<string> arguments, IReadOnlyList<string> proxies)
    {
        var addresses = new List<string>(proxies);
        var positional = new List<string>();

        for (var at = 0; at < arguments.Count; at++)
        {
            var word = arguments[at];
            var name = word.Split('=', 2)[0];

            if (!word.StartsWith('-'))
            {
                positional.Add(word);
                continue;
            }

            if (!HttpieValued.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var value = word.Length > name.Length ? word[(name.Length + 1)..] : at + 1 < arguments.Count ? arguments[++at] : null;

            if (name == "--proxy" && value?.Split(':', 2) is [_, { Length: > 0 } proxy])
            {
                addresses.Add(proxy);
            }
        }

        var url = positional.Count > 1 && HttpMethods.Contains(positional[0], StringComparer.OrdinalIgnoreCase) ? positional[1] : positional.FirstOrDefault();

        if (url is not null)
        {
            addresses.Add(url.StartsWith(':') ? "localhost" + url : url);
        }

        return addresses;
    }

    /// <summary>The methods an <c>http</c> or <c>https</c> takes before its URL.</summary>
    private static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    /// <summary>The options of <c>gh api</c> that take a value in the next word.</summary>
    private static readonly string[] GhApiValueTaking =
        ["-X", "--method", "-H", "--header", "-f", "--raw-field", "-F", "--field", "-p", "--preview", "-q", "--jq", "-t", "--template", "--input", "--cache"];

    /// <summary>
    /// The address <c>gh api</c> reaches: its endpoint where that is a whole URL, else
    /// <paramref name="host"/> - unless a <c>--hostname</c> names another; null where the command
    /// is no <c>gh api</c>. An option's value is never its endpoint.
    /// </summary>
    private static string? GhApi(IReadOnlyList<string> arguments, string host)
    {
        if (arguments.Count == 0 || !arguments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

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
        }

        // An endpoint named by an expansion may be a whole URL of any host.
        return GhApiEndpoint(arguments) is { } path && (path.Contains("://", StringComparison.Ordinal) || Expansion(path) is not null) ? path : host;
    }

    /// <summary>
    /// The endpoint of <c>gh api</c> - its first word that is no option and no option's value - given
    /// the words after <c>gh</c>; null where it names none.
    /// </summary>
    /// <param name="arguments">The words after <c>gh</c>, <c>api</c> first.</param>
    internal static string? GhApiEndpoint(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (var at = 1; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if ((GhApiValueTaking.Contains(word, StringComparer.Ordinal) || word.Equals("--hostname", StringComparison.OrdinalIgnoreCase)) && at + 1 < arguments.Count)
            {
                at++;
            }
            else if (!word.StartsWith('-'))
            {
                return word;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <c>gh api</c> writes: its method, by <c>-X</c> or <c>--method</c>, any but GET or HEAD - one the shell
    /// expands may be any - and, where it names none, gh's own POST for a request given fields or an input.
    /// </summary>
    /// <param name="arguments">The words after <c>gh</c>, <c>api</c> first.</param>
    public static bool GhApiWrites(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? method = null;

        for (var at = 1; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (word is "-X" or "--method" && at + 1 < arguments.Count)
            {
                method = arguments[++at];
            }
            else if (word.StartsWith("--method=", StringComparison.Ordinal))
            {
                method = word["--method=".Length..];
            }
            else if (word.StartsWith("-X", StringComparison.Ordinal) && word.Length > 2)
            {
                method = word[2] == '=' ? word[3..] : word[2..];
            }
        }

        return method is not null
            ? Expansion(method) is not null || !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            : GhApiFields(arguments).Any() || arguments.Any(word => word == "--input" || word.StartsWith("--input=", StringComparison.Ordinal));
    }

    /// <summary>
    /// The values <c>gh api</c>'s fields give, by their names: <c>-f</c>, <c>-F</c>,
    /// <c>--raw-field</c> and <c>--field</c>, each <c>name=value</c>, the next word or glued after
    /// an <c>=</c>.
    /// </summary>
    /// <param name="arguments">The words after <c>gh</c>, <c>api</c> first.</param>
    internal static IEnumerable<string> GhApiFields(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (var at = 1; at < arguments.Count; at++)
        {
            var word = arguments[at];

            if (word is "-f" or "-F" or "--raw-field" or "--field" && at + 1 < arguments.Count)
            {
                yield return arguments[++at];
            }
            else if (word.StartsWith("--raw-field=", StringComparison.Ordinal) || word.StartsWith("--field=", StringComparison.Ordinal))
            {
                yield return word.Split('=', 2)[1];
            }
            else if (word.Length > 2 && word[0] == '-' && word[1] is 'f' or 'F' && word[2] != '-')
            {
                yield return word[2..];
            }
        }
    }

    /// <summary>
    /// The host and port of an address, as written - its scheme, its user and its path left out,
    /// and an IPv6 literal's brackets with what they hold - where an expansion would name the host
    /// the address reaches.
    /// </summary>
    private static string Authority(string address)
    {
        var rest = address.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0 ? address[(scheme + 3)..] : address;
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        var host = authority[(authority.LastIndexOf('@') + 1)..];

        return host.StartsWith('[') && host.IndexOf(']', StringComparison.Ordinal) is var closing and > 0 ? host[(closing + 1)..] : host;
    }
}
