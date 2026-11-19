using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Json;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Text;

namespace DssHarness.Core.Hosts;

/// <summary>An SDK as <c>dotnet --list-sdks</c> lists it.</summary>
/// <param name="Version">The SDK's version.</param>
/// <param name="Location">Where it is installed.</param>
public sealed record SdkListing(string Version, string Location)
{
    /// <summary>The SDK's major version, or 0 when the version cannot be read.</summary>
    public int Major => int.TryParse(Version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
        ? major
        : 0;

    /// <summary>
    /// Whether it is installed at a Windows path, which is how a Windows host is told from any other
    /// before DssHarness runs there.
    /// </summary>
    public bool OnWindows => Platform.PlatformPaths.NamesADrive(Location) || Location.StartsWith(@"\\", StringComparison.Ordinal);
}

/// <summary>
/// Reads what programs on a host print. Each reader accepts only the shape it knows and reports
/// anything else as unreadable, because a guess at a host's state is exactly how a leg ends up
/// running somewhere it cannot.
/// </summary>
public static class HostProbes
{
    /// <summary>ssh's own exit code for a failure of ssh itself, such as a connection or authentication failure.</summary>
    public const int SshFailed = 255;

    /// <summary>
    /// The most characters an IP address is written in, its scope aside: an IPv6 address with an IPv4 one in its last
    /// 32 bits, <c>ffff:ffff:ffff:ffff:ffff:ffff:255.255.255.255</c> - the text form's own longest (RFC 4291), and so no
    /// bound of the code's: a run of characters longer than this is no address, whatever it holds.
    /// </summary>
    private const int LongestAddress = 45;

    /// <summary>One run of the characters a host name or an address is spelt from, which nothing else adjoins.</summary>
    private static readonly Regex AddressLike = LinearRegex.Compile(@"[0-9A-Za-z.:%_-]+");

    /// <summary>ssh's debug line naming the address it authenticates to, with the port straight after it.</summary>
    private static readonly Regex SshAuthenticatingTo = LinearRegex.Compile(@"Authenticating to (?<address>[0-9A-Za-z.:%_-]+):(?<port>[0-9]+) as '");

    /// <summary>A line in which ssh says it never connected: see <see cref="NeverConnected"/>.</summary>
    private static readonly Regex SshNeverConnected = LinearRegex.Compile(
        @"^(?:ssh(?:\.exe)?: (?:Could not resolve hostname |connect to host \S+ port \S+: )|banner exchange: Connection to UNKNOWN port -1: |Connection timed out during banner exchange$)");

    /// <summary>One SDK as <c>dotnet --list-sdks</c> lists it: its version, then where it is in brackets.</summary>
    private static readonly Regex SdkLine = LinearRegex.Compile(@"^(?<version>\d+\.\d+\.\d+\S*)\s+\[(?<location>.+)\]$");

    /// <summary>
    /// Whether the host ran a command at all. A program that ran and failed exits non-zero; any
    /// failure of ssh's own - a connection that never opened, or one that closed - is its
    /// <see cref="SshFailed"/>; wsl.exe that failed itself
    /// exits with a code no program in a distribution can, a negative one, as Windows reports
    /// 0xFFFFFFFF, where a program's own status is 0 to 255; and one that hung has no exit code to
    /// read. Only an answer says anything about the host.
    /// </summary>
    /// <param name="result">What running the command produced.</param>
    public static bool Answered(Processes.ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.TimedOut && result.ExitCode != SshFailed && result.ExitCode >= 0;
    }

    /// <summary>Reads <c>uname -sm</c> into an operating system and a processor, in configuration's words.</summary>
    public static (string? Os, string? Processor) ReadUname(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var words = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return words.Length < 2
            ? (null, null)
            : (PlatformNames.ForKernel(words[0]), PlatformNames.ForMachine(words[^1]));
    }

    /// <summary>Reads <c>dotnet --list-sdks</c>, skipping any line that is not an SDK, such as a first-run banner.</summary>
    public static IReadOnlyList<SdkListing> ReadSdks(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return [.. output
            .Split('\n')
            .Select(line => SdkLine.Match(line.Trim()))
            .Where(match => match.Success)
            .Select(match => new SdkListing(match.Groups["version"].Value, match.Groups["location"].Value))];
    }

    /// <summary>
    /// Reads the installed version of <paramref name="packageId"/> from
    /// <c>dotnet tool list --global --format json</c>.
    /// </summary>
    /// <returns>
    /// Whether the text is that document. <paramref name="version"/> is <see langword="null"/> when it is
    /// and the tool is not installed.
    /// </returns>
    public static bool TryReadToolVersion(string output, string packageId, out string? version)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        version = null;

        // The document is found rather than assumed to start the output: a first-run banner can come first.
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output.AsMemory(start), JsonDepth.Document);

            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var tool in data.EnumerateArray())
            {
                // NuGet package IDs compare ignoring case, and the listing writes them in lower case.
                if (tool.TryGetProperty("packageId", out var id)
                    && string.Equals(id.GetString(), packageId, StringComparison.OrdinalIgnoreCase))
                {
                    version = tool.TryGetProperty("version", out var installed) ? installed.GetString() : null;
                    return version is not null;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether wsl.exe said that no distribution has the name it was given.</summary>
    /// <remarks>
    /// Matched on the error code wsl.exe prints, never on its sentence, which is translated into the
    /// machine's language.
    /// </remarks>
    public static bool IsMissingDistribution(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.Ordinal);
    }

    /// <summary>Whether WSL could not start <paramref name="program"/> in a distribution because the distribution has no such program.</summary>
    public static bool IsMissingProgramInWsl(string text, string program)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains($"execvpe({program}) failed", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a process listing shows <paramref name="program"/> running: the output of
    /// <c>ps -A -o comm=</c>, one name or path per line, or of <c>tasklist /FO CSV /NH</c>, which starts
    /// each line with the quoted image name.
    /// </summary>
    public static bool ListsProcess(string listing, string program)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var image = line[0] == '"' ? line[1..].Split('"')[0] : line;
            var name = Path.GetFileName(image.Replace('\\', '/'));

            if (string.Equals(name, program, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, program + ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How a program that did not do what was asked is reported: what failed, then how it failed, in
    /// the program's own words. One helper so that every reason reads the same and none of them
    /// silently drops what the program said, which is usually the only thing that identifies the fault.
    /// Where ssh never connected to the host to start it, that is said instead, as <see cref="Unreached"/>
    /// says it: nothing the program would have done is any part of the reason.
    /// </summary>
    /// <param name="what">What was being done, as a lower-case fragment, with any remedy after a semicolon.</param>
    /// <param name="result">What the program did.</param>
    /// <param name="through">The connection it was started through, where one carried it to a host.</param>
    public static string Failure(string what, ProcessResult result, HostConnection? through = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(result);

        if (Unreached(result, through) is { } unreached)
        {
            return unreached;
        }

        if (result.TimedOut)
        {
            return $"{what}: there was no answer within {result.Duration.TotalSeconds:0} seconds";
        }

        var said = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return $"{what} (exit {result.ExitCode}): {Quoted(AsConfigured(said, through))}";
    }

    /// <summary>
    /// That <paramref name="through"/>'s host could not be reached, in ssh's own words, where ssh never
    /// connected to it to start <paramref name="result"/>'s program - see <see cref="NeverConnected"/> - or
    /// <see langword="null"/>: for a program that ran, one this machine or WSL started, and an ssh failure
    /// after the host answered - a key refused, or a connection that ended part way: the host was reached.
    /// </summary>
    /// <param name="result">What starting a program through the connection produced.</param>
    /// <param name="through">The connection, or <see langword="null"/> for a program this machine ran itself.</param>
    /// <remarks>
    /// Said before whatever the program was asked to do, because nothing of it happened: reported as
    /// DssHarness not answering from its tool path, a name that did not resolve sent the reader after an
    /// install that was fine, with the reason ssh gave further along the same line.
    /// </remarks>
    public static string? Unreached(ProcessResult result, HostConnection? through)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.TimedOut
            && through?.Host.Kind == HostKind.Ssh
            && result.ExitCode == SshFailed
            && NeverConnected(result.StandardError) is { } said
                ? CouldNotReach(AsConfigured(said, through))
                : null;
    }

    /// <summary>
    /// The reasons given, as the parts of one sentence, in order; or <see langword="null"/> where none was given:
    /// every reason a search or a probe gives for what it could not do is said this one way.
    /// </summary>
    /// <param name="reasons">Each reason, or <see langword="null"/> or empty where there is none.</param>
    public static string? Joined(params IEnumerable<string?> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        return string.Join("; ", reasons.Where(reason => !string.IsNullOrEmpty(reason))) is { Length: > 0 } said ? said : null;
    }

    /// <summary>That the host could not be reached, with what ssh said about it.</summary>
    /// <param name="said">ssh's words, quoted as they are.</param>
    public static string CouldNotReach(string said) => $"the host could not be reached: ssh said {said}";

    /// <summary>
    /// That <paramref name="through"/>'s host could not be reached, with what ssh said on standard error over
    /// <paramref name="result"/> quoted, as <see cref="Quoted"/> quotes it - the host named as the configuration
    /// declares it, never by an address its name resolved to here (see <see cref="AsConfigured"/>).
    /// </summary>
    /// <param name="result">What ssh did, having failed itself.</param>
    /// <param name="through">The connection it failed over.</param>
    public static string CouldNotReach(ProcessResult result, HostConnection through)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(through);

        return CouldNotReach(Quoted(AsConfigured(result.StandardError, through)));
    }

    /// <summary>
    /// The line in which ssh said it never connected to the host - the name did not resolve, nothing took
    /// the connection at the address, or what took it never answered as an ssh server - or
    /// <see langword="null"/> where it said no such thing.
    /// </summary>
    /// <param name="standardError">What ssh printed on standard error.</param>
    /// <remarks>
    /// Measured with OpenSSH for Windows 9.5p2 and 10.0p2 and Git for Windows' 10.5p1. A name that does not
    /// resolve is "ssh: Could not resolve hostname", and an address where nothing answers "ssh: connect to
    /// host ... port ...: Connection timed out", from each. A refused connection is "ssh: connect to host"
    /// from Git's, and "banner exchange: Connection to UNKNOWN port -1" from the Windows builds, whose
    /// connection has no far end to name. One taken where nothing then said it was an ssh server within the
    /// connect timeout - a host asleep behind whatever took the connection for it, or one still waking - is
    /// "Connection timed out during banner exchange", then "Connection to ... port ... timed out", from
    /// 10.0p2 and 10.5p1 alike, measured against a listener that never spoke: no session began, so nothing
    /// ran there. Anything else ssh fails over came after the host answered.
    /// </remarks>
    public static string? NeverConnected(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        return standardError
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => SshNeverConnected.IsMatch(line));
    }

    /// <summary>
    /// Whether ssh failed in a way a host still waking up explains: it never connected - the name did not
    /// resolve, or nothing took the connection at the address - or no answer came in time. Narrower than
    /// <see cref="FailedBeforeAnySession"/> on purpose, and never to be merged with it: a key the host showed
    /// that the name is not known by is the host answering, and would be refused again however long it waited.
    /// </summary>
    /// <param name="result">What an ssh call produced.</param>
    public static bool MayBeWaking(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.TimedOut || (result.ExitCode == SshFailed && NeverConnected(result.StandardError) is not null);
    }

    /// <summary>
    /// Whether ssh failed, before any session began, for a reason the address it was given can be to blame
    /// for - it never connected, or it refused the key the host showed - so that nothing ran there, and a
    /// call made again runs nothing twice. A login refused is not one: the host whose key was accepted refused
    /// it, and would refuse it again.
    /// </summary>
    /// <param name="result">What an ssh call produced.</param>
    /// <remarks>
    /// Measured with the same three clients: a key known_hosts does not hold, or holds another of, ends with
    /// "Host key verification failed." from each.
    /// </remarks>
    public static bool FailedBeforeAnySession(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.TimedOut
            && result.ExitCode == SshFailed
            && (NeverConnected(result.StandardError) is not null
                || result.StandardError.Contains("Host key verification failed.", StringComparison.Ordinal));
    }

    /// <summary>
    /// That this machine stopped being heard by a host while what it had asked of it was still running, and why, where
    /// <paramref name="result"/> says so; <see langword="null"/> where it does not. A host that hears no beat, or whose
    /// input ends, stops what it was asked and says the machine that asked has gone: said from this end too, since that
    /// machine is here, reading it.
    /// </summary>
    /// <param name="result">What running a request through a connection produced.</param>
    public static string? BeatLost(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.BeatLost is { } why
            ? $"this machine could no longer write to the connection it holds open to the host ({why.TrimEnd('.')}), and a host that hears "
                + "nothing more from the machine that asked stops what it was asked"
            : null;
    }

    /// <summary>
    /// Why a program a host was asked to run never reported how it finished: that the host could not be
    /// reached, as <see cref="Unreached"/> says it, where ssh never connected; otherwise that the program
    /// may not have run, or run only in part, with the exit the connection ended with and what it said last.
    /// </summary>
    /// <param name="what">The program as the reader knows it: its name quoted, with anything that tells it apart.</param>
    /// <param name="result">What running it through the connection produced.</param>
    /// <param name="through">The connection it was run through.</param>
    public static string NeverFinished(string what, ProcessResult result, HostConnection through)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(result);

        return Unreached(result, through)
            ?? $"{what} never reported how it finished, so it may not have run, or run only in part; "
                + $"the connection ended with exit {result.ExitCode}{Detail(AsConfigured(result.StandardError, through))}";
    }

    /// <summary>
    /// What a program printed on standard error, quoted after a colon as <see cref="Quoted"/> quotes it,
    /// or nothing where it printed nothing.
    /// </summary>
    /// <param name="standardError">What it printed.</param>
    public static string Detail(string standardError)
    {
        var said = Quoted(standardError);

        return said.Length == 0 ? string.Empty : ": " + said;
    }

    /// <summary>
    /// <paramref name="said"/> with every address this machine resolved the host's name to written as the
    /// configuration declares the host, which is what the reader named and what everything else about the host
    /// is said in terms of.
    /// </summary>
    /// <param name="said">What ssh, or a program on the host, printed.</param>
    /// <param name="through">The connection it printed for, or <see langword="null"/> where there is none.</param>
    /// <remarks>
    /// <para>
    /// A pinned connection hands ssh the address this machine resolved the declared name to, and one never pinned,
    /// or whose pin was dropped, lets ssh look the name up itself. Either way ssh names the address it dialled
    /// when it fails - "Connection to 198.51.100.7 port 22 timed out", "harness@198.51.100.7: Permission denied
    /// (publickey)." - and the harness relays its words as they are. An address this machine worked out is not
    /// the reader's to publish: it reaches a leg's reason, the text and the JSON, and whatever keeps them.
    /// </para>
    /// <para>
    /// Measured: ssh does not always spell an address as this machine does. On Linux, a link-local address
    /// dialled as <c>fe80::1%2</c> is named <c>fe80::1%eth0</c>, its scope by the interface's name. So an IPv6
    /// address is matched as the address it is, whatever its scope or spelling; an IPv4 one only as it is always
    /// spelt, since '192.0.522' parses as 192.0.2.10 and is a version, not an address. Only these addresses are
    /// rewritten, and only into what the configuration itself declares, so nothing else ssh said is touched.
    /// </para>
    /// <para>
    /// The addresses are those the connection has learnt, which it learns again, afresh, before every call that lets
    /// ssh look the name up itself: see <see cref="ResolvedAddresses"/>. What the host's own programs print is
    /// rewritten too - on either stream, in its leg's reason and last lines, and in what it found about itself -
    /// since ssh's words arrive among them on standard error, and two copies of one line must not differ in what
    /// they name.
    /// </para>
    /// </remarks>
    public static string AsConfigured(string said, HostConnection? through)
    {
        ArgumentNullException.ThrowIfNull(said);

        if (through is not { Address: { Length: > 0 } declared } || Resolved(through) is not { Count: > 0 } resolved)
        {
            return said;
        }

        // ssh's debug line puts the port straight after the address it dialled, an IPv6 one unbracketed, which no
        // word of its own can tell from another address: "Authenticating to 2001:db8::7:22 as 'harness'". Its shape
        // says where the port starts.
        said = SshAuthenticatingTo.Replace(
            said,
            match => Address(match.Groups["address"].Value) is { } address && resolved.Contains(address)
                ? $"Authenticating to {declared}:{match.Groups["port"].Value} as '"
                : match.Value);

        // Only where an address stands as a word, once what is set aside around it is (see Spelt): a label or two before
        // it, and a colon, a full stop or a port after it. An unbounded replacement of '10.0.0.5' rewrites the '10.0.0.50'
        // a command itself printed, handing the reader an address that never existed, and does the same to an IPv6
        // address inside a longer one. This runs over every line a host writes, not only over ssh's, so a line that
        // merely contains the address as part of something else is left alone.
        return AddressLike.Replace(
            said,
            match => Spelt(match.Value) is ({ } address, var start, var length) && resolved.Contains(address)
                ? match.Value[..start] + declared + match.Value[(start + length)..]
                : match.Value);
    }

    /// <summary>
    /// Every address <paramref name="through"/>'s host name has resolved to here, each as the address it is: its pin's
    /// among them, since a pin is made from what the name resolved to.
    /// </summary>
    private static List<IPAddress> Resolved(HostConnection through)
        => [.. (through.Resolved?.All ?? []).Select(resolved => Address(resolved.AsSpan())).OfType<IPAddress>()];

    /// <summary>
    /// The address <paramref name="token"/> spells, as the address it is, with where in the token it starts and how
    /// much of it it takes; <see langword="null"/> where it spells none. What follows an address is set aside: the
    /// colon ssh puts after a refused login's host, a full stop, and a port, as ssh writes one after an IPv4 or a
    /// scoped address - "192.0.2.10:22", "fe80::1%12:22". So is every label stuck to its front with a colon, where the
    /// word is no address from its start - "addr:192.0.2.10", as ifconfig prints one, or as many as a word holds.
    /// </summary>
    /// <remarks>
    /// One pass over the word, by its grammar: a label ends at its colon, and the next start is after it, so each
    /// character is looked at a fixed number of times however many labels there are - what follows the address, the
    /// scope after a '%', the colon that ends it, each found once and carried to the next start - and each start weighs
    /// a few lengths of at most <see cref="LongestAddress"/> characters. A word of a million colons is read in a
    /// million steps, never a million times a million.
    /// </remarks>
    private static (IPAddress Address, int Start, int Length)? Spelt(string token)
    {
        // Set aside once for the whole word: colons and full stops at its end belong to no address, whichever start the
        // address is read from.
        var coreEnd = token.AsSpan().TrimEnd(":.").Length;

        // The first '%' at or after the start - none yet looked for, or none there - and where the scope it begins ends.
        var percent = NotLooked;
        var scopeEnd = 0;
        Span<int> lengths = stackalloc int[4];

        for (var start = 0; ;)
        {
            if (percent == NotLooked || (percent >= 0 && percent < start))
            {
                percent = token.IndexOf('%', start);
                scopeEnd = percent < 0 ? 0 : ScopeEnd(token, percent);
            }

            var colon = token.IndexOf(':', start);
            var count = Lengths(token, start, coreEnd, percent, scopeEnd, colon, lengths);

            for (var index = 0; index < count; index++)
            {
                if (Address(token.AsSpan(start, lengths[index])) is { } address)
                {
                    return (address, start, lengths[index]);
                }
            }

            // No further label where no colon follows, or where the next one starts a label that is empty.
            if (colon <= start)
            {
                return null;
            }

            start = colon + 1;
        }
    }

    /// <summary>What a look for the next '%' has not been made of yet: no index, and not the -1 that says there is none.</summary>
    private const int NotLooked = -2;

    /// <summary>
    /// Where the scope the '%' at <paramref name="percent"/> begins ends in <paramref name="token"/>: at the colon after
    /// it, or the word's end, the full stops before that set aside. A scope is an interface's name or number: it holds
    /// no colon, and it ends in no full stop.
    /// </summary>
    private static int ScopeEnd(string token, int percent)
    {
        var colon = token.IndexOf(':', percent);
        var end = colon < 0 ? token.Length : colon;

        while (end > percent && token[end - 1] == '.')
        {
            end--;
        }

        return end;
    }

    /// <summary>
    /// How long an address the word from <paramref name="start"/> on begins with may be, longest first, written into
    /// <paramref name="lengths"/>: a few lengths, whatever the word's own, so that a word of any length a host prints is
    /// read in one pass. Answers how many it wrote.
    /// </summary>
    /// <param name="token">The word.</param>
    /// <param name="start">Where in it this reading starts.</param>
    /// <param name="coreEnd">Where the word ends once the colons and full stops at its end are set aside.</param>
    /// <param name="percent">The first '%' at or after <paramref name="start"/>, or -1 where there is none.</param>
    /// <param name="scopeEnd">Where the scope that '%' begins ends (<see cref="ScopeEnd"/>).</param>
    /// <param name="colon">The first colon at or after <paramref name="start"/>, or -1 where there is none.</param>
    /// <param name="lengths">Where the lengths go: room for four.</param>
    private static int Lengths(string token, int start, int coreEnd, int percent, int scopeEnd, int colon, Span<int> lengths)
    {
        if (percent >= 0)
        {
            // A scope that is empty is none: the address then ends at its '%'.
            var scoped = scopeEnd - start;
            var at = percent - start;

            lengths[0] = scoped > at + 1 ? scoped : at;
            return 1;
        }

        var core = Math.Max(0, coreEnd - start);
        var count = 0;

        // An IPv6 address can itself end in '::', so as many as two of the colons after it may be its own.
        for (var length = Math.Min(token.Length - start, core + 2); length >= core && length > 0; length--)
        {
            lengths[count++] = length;
        }

        // An IPv4 address with its port. An IPv6 one cannot be told from a port without the brackets ssh puts round
        // it, which leave the address a word of its own; before its first colon is never one.
        if (colon > start && colon < start + core)
        {
            lengths[count++] = colon - start;
        }

        return count;
    }

    /// <summary>
    /// The address <paramref name="text"/> spells, as the address it is - an IPv6 one with its scope set aside,
    /// and an IPv4 one mapped into IPv6 as that IPv4 one - or <see langword="null"/> where it spells none as
    /// programs print one.
    /// </summary>
    private static IPAddress? Address(ReadOnlySpan<char> text)
    {
        // A scope's '%' stands within an address's own length from the start, or the text before it is no address.
        var scope = text[..Math.Min(text.Length, LongestAddress + 1)].IndexOf('%');
        var bare = scope < 0 ? text : text[..scope];

        // Longer than an address is ever written, it is none; and most of what a host prints holds neither a full stop nor
        // a colon, and is passed over without being parsed.
        if (bare.Length > LongestAddress || bare.IndexOfAny('.', ':') < 0)
        {
            return null;
        }

        if (!IPAddress.TryParse(bare, out var address))
        {
            return null;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        // An IPv4 address has no scope, and is taken only as it is always spelt: '192.0.522' parses as 192.0.2.10,
        // and is a version.
        return scope < 0 && bare.SequenceEqual(address.ToString()) ? address : null;
    }

    /// <summary>
    /// What a program printed, quoted on one line and whole: each line trimmed, the empty ones left out, and the rest
    /// joined by <c> / </c>. Nothing of it is cut - the part a quotation leaves out is the part a reader needed - so a
    /// reason that quotes a program says all it said, and so does every record and <c>--json</c> answer that carries the
    /// reason: a vcvarsall that prints three megabytes before it fails is quoted as those three megabytes.
    /// </summary>
    /// <param name="text">What the program printed.</param>
    public static string Quoted(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Join(" / ", text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
    }

    /// <summary>
    /// What <see cref="Quoted"/> gives, by the name it had while it quoted only the end of what a program printed: kept for
    /// the callers outside the hosts' code until each names <see cref="Quoted"/>.
    /// </summary>
    /// <param name="text">What the program printed.</param>
    public static string Excerpt(string text) => Quoted(text);
}
