using System.Globalization;
using System.Reflection;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>What <c>update-tool</c> was asked to do.</summary>
/// <param name="Wait">Whether to wait for every DssHarness running on this machine to end, rather than be refused by one.</param>
/// <param name="ToolPath">The directory the tool is kept in, or <see langword="null"/> for this user's global tools.</param>
public sealed record ToolUpdateRequest(bool Wait = false, string? ToolPath = null);

/// <summary>The program this process is.</summary>
/// <remarks>
/// A seam, like the process table: what <c>update-tool</c> does turns on whether the process asking is the installed tool
/// itself, and a test says which without having to be it.
/// </remarks>
public interface IOwnProgram
{
    /// <summary>The file this process runs as, or <see langword="null"/> where the platform would not say.</summary>
    string? Path { get; }

    /// <summary>
    /// Loads everything this program would otherwise load from its own directory later, so nothing it has left to do asks
    /// for a file that is no longer there.
    /// </summary>
    void LoadWhole();
}

/// <inheritdoc cref="IOwnProgram"/>
public sealed class OwnProgram : IOwnProgram
{
    public string? Path => Environment.ProcessPath;

    public void LoadWhole()
    {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            try
            {
                Assembly.LoadFrom(file);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                // Not an assembly - a native library the runtime loads its own way - or one already loaded from elsewhere.
            }
        }
    }
}

/// <summary>
/// Updates the DssHarness installed on this machine to the newest release, through the one door that is safe on a machine
/// several sessions share: only while no DssHarness runs there.
/// </summary>
/// <remarks>
/// <para>
/// Measured on a consumer's Windows host: a <c>dotnet tool update</c> typed by one session while another's commands were
/// running took the tool's shim away for ten seconds, failed, and put it back - twice - and every command typed
/// meanwhile, by anybody, found no tool. The update moves the installed version's directory aside before it writes the
/// new one, and Windows moves no directory a running program has loaded files from; elsewhere the move succeeds, and
/// takes the files from under whatever is running. So an update beside a running DssHarness is refused, each one named,
/// or waited for where asked - as a host's is refused by the machine that dispatches to it
/// (<see cref="HostInspector"/>).
/// </para>
/// <para>
/// The command asking is a DssHarness too. Typed by its name on Windows - as the installed tool is - it is itself what
/// stops the update, so there it changes nothing and says the command that does: the newest release run beside the
/// installed one by <c>dotnet tool exec</c>, as the program <c>dotnet</c>, from the package cache, holding none of the
/// installed files. Elsewhere the installed tool replaces its own files while it runs, having first loaded the whole
/// of itself. A DssHarness is told by the name of its program, as a host's is, so one kept elsewhere - a build from
/// source, a tool in a directory of its own - is counted too: wrongly, and safely.
/// </para>
/// <para>
/// Versions only move up, to the newest release nuget.org lists, from nuget.org alone
/// (<see cref="ToolPackage.Source"/>), and an update is said done only once the tool is listed as that release.
/// </para>
/// </remarks>
public sealed class ToolUpdateService(
    IProcessTable processes,
    IProcessRunner processRunner,
    IPublishedToolVersions published,
    IToolIdentityProvider identity,
    IProcessIdentity process,
    IOwnProgram program,
    IHostPlatform platform,
    IHarnessOutput output,
    Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    /// <summary>The command, as it is typed and as it reports.</summary>
    public const string CommandName = "update-tool";

    /// <summary>The option that names a directory the tool is kept in.</summary>
    public const string ToolPathOption = "--tool-path";

    /// <summary>The option that waits for every DssHarness running on the machine to end.</summary>
    public const string WaitOption = "--wait";

    /// <summary>How often a wait looks at what the machine runs: reading the table starts a program on Windows.</summary>
    public static readonly TimeSpan LooksEvery = TimeSpan.FromSeconds(5);

    /// <summary>How often a wait says again who it waits for, as a leg waiting for its machine does.</summary>
    public static readonly TimeSpan SaidAgainEvery = LegAdmission.SaidAgainEvery;

    /// <summary>Longest listing the installed tools may take.</summary>
    private static readonly TimeSpan ListBudget = TimeSpan.FromMinutes(2);

    private readonly IProcessTable _processes = processes;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IPublishedToolVersions _published = published;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly IProcessIdentity _process = process;
    private readonly IOwnProgram _program = program;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? Task.Delay;

    /// <summary>
    /// The command that updates the installed tool from a copy of the newest release run beside it, as a refusal names
    /// it and as help does: one line, to type as it stands.
    /// </summary>
    /// <param name="request">The options it carries.</param>
    public static string FromBeside(ToolUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Join(
            ' ',
            [
                HostInspector.DotnetProgram, "tool", "exec", ToolPackage.Id, "--yes", "--source", ToolPackage.Source, "--", CommandName,
                .. request.ToolPath is { } path ? new[] { ToolPathOption, OrchestrationReports.Argument(path) } : [],
                .. request.Wait ? new[] { WaitOption } : [],
            ]);
    }

    /// <summary>
    /// What a running DssHarness was asked, read from its <paramref name="commandLine"/> as the word after its program,
    /// or <see langword="null"/> where that is no command's name. Nothing else of the line is ever repeated: an input's
    /// value may be on it.
    /// </summary>
    /// <param name="commandLine">The command line, where the platform exposes one.</param>
    public static string? AskedOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var line = commandLine.TrimStart();
        var rest = line[0] == '"'
            ? line.IndexOf('"', 1) is var close and > 0 ? line[(close + 1)..] : string.Empty
            : line.IndexOfAny([' ', '\t']) is var space and > 0 ? line[space..] : string.Empty;
        var word = rest.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        return word is { Length: > 0 } && char.IsAsciiLetterLower(word[0]) && word.All(letter => char.IsAsciiLetterLower(letter) || letter == '-')
            ? word
            : null;
    }

    /// <summary>Updates the installed tool, or says why it was not.</summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="cancellationToken">Stops what comes before the update - the readings, a wait - and never the update once it has begun.</param>
    public async Task<CommandOutcome> RunAsync(ToolUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kept = request.ToolPath is { } path ? $"'{path}'" : "this user's global tools";
        var listing = await ListAsync(request, cancellationToken).ConfigureAwait(false);

        if (!listing.Succeeded || !HostProbes.TryReadToolVersion(listing.StandardOutput, ToolPackage.Id, out var installed))
        {
            return new CommandOutcome(HarnessExit.CommandFailed, HostProbes.Failure($"the .NET tools in {kept} could not be listed, so nothing was changed", listing));
        }

        var (aim, untold) = await TargetAsync(installed, kept, cancellationToken).ConfigureAwait(false);

        if (aim is not { } target)
        {
            return new CommandOutcome(HarnessExit.CommandFailed, untold!);
        }

        if (installed is not null && SemanticVersion.TryParse(installed, out var held) && SemanticVersion.Compare(held, target) >= 0)
        {
            return CommandOutcome.Ok($"{ToolPackage.Id} {installed} in {kept} is the newest release: nothing to update");
        }

        var move = installed is null ? $"{ToolPackage.Id} {target} was not installed in {kept}" : $"{ToolPackage.Id} in {kept} was not updated from {installed} to {target}";
        var table = await _processes.ReadAsync(cancellationToken).ConfigureAwait(false);
        var running = Running(table);

        if (_platform.Current == PlatformId.Windows && IsTheTool(_program.Path))
        {
            return new CommandOutcome(
                HarnessExit.Refused,
                $"{move}: Windows replaces no program while it runs, and this command is itself a {ToolPackage.Id} running on this machine. Nothing was changed.",
                [
                    "Run the update from a copy of the newest release beside the installed one, which this does without installing it:",
                    "  " + FromBeside(request),
                    .. Named($"{Count(running)} on this machine, which that command waits for with {WaitOption}, and is refused by without it:", running),
                    .. running.Count == 0 ? [] : Unread(table),
                ]);
        }

        if (running.Count > 0 && !request.Wait)
        {
            return new CommandOutcome(
                HarnessExit.Refused,
                $"{move}: {Count(running)} on this machine, and an update beside one fails where a running program cannot be replaced, "
                + "takes its files from under it where one can, and leaves no tool to find for whatever is typed meanwhile. Nothing was changed.",
                [.. running.Select(one => "  " + Describe(one)), .. Unread(table), RunAgain]);
        }

        if (running.Count > 0)
        {
            // A table read by names alone names no process's parent: a wait asked from inside one of these could not be
            // told from one that ends, and would be waited on for good without a word.
            if (table.Degraded is { } unread)
            {
                return new CommandOutcome(
                    HarnessExit.Refused,
                    $"{move}: {Count(running)} on this machine, and which process started which could not be read - {unread.TrimEnd('.')} - so "
                    + "a wait asked from inside one of them could not be told from one that ends. Nothing was changed.",
                    [.. running.Select(one => "  " + Describe(one)), "Run again once each has ended."]);
            }

            if (StartedThis(table, running) is { } parent)
            {
                return new CommandOutcome(
                    HarnessExit.Refused,
                    $"{move}: this command was started by a {ToolPackage.Id} that is still running, {Describe(parent)}, which an update would "
                    + "take the files of, and which a wait from inside it would never see end. Nothing was changed: run it once that command has ended.");
            }

            var moving = installed is null ? $"install {ToolPackage.Id} {target} in {kept}" : $"update {ToolPackage.Id} in {kept} from {installed} to {target}";

            await WaitAsync(moving, running, cancellationToken).ConfigureAwait(false);
        }

        return await ChangeAsync(request, kept, installed, target.ToString()).ConfigureAwait(false);
    }

    /// <summary>What a refusal over running processes ends with.</summary>
    private const string RunAgain = $"Run again once each has ended, or with {WaitOption} to wait for them.";

    /// <summary>
    /// The release the tool is moved to, or why there is none to move it to. It is the newest nuget.org lists; or, where
    /// the feed does not say, the copy running where that is a release newer than what is installed - run from its
    /// package, as <c>dotnet tool exec</c> runs one - which is then said, with why the feed did not: the tool is moved to
    /// a release nobody named.
    /// </summary>
    private async Task<(SemanticVersion? Target, string? Untold)> TargetAsync(string? installed, string kept, CancellationToken cancellationToken)
    {
        var copy = SemanticVersion.TryParse(_identity.Current.Version, out var running) ? running : null;
        var held = SemanticVersion.TryParse(installed, out var listed) ? listed : null;
        var holds = installed is null ? $"{ToolPackage.Id} is not installed in {kept}" : $"{ToolPackage.Id} {installed} is installed in {kept}";

        if ((held ?? copy) is not { } from)
        {
            // No fault of the feed's, which was never asked: said as what it is.
            var own = $"this copy's own version, '{_identity.Current.Version}',";

            return (
                null,
                $"which release of {ToolPackage.Id} is the newest was not asked of nuget.org, so nothing was changed: {holds}, and "
                + $"{(installed is null ? $"{own} is not" : $"neither '{installed}' nor {own} is")} one a release can be told newer than");
        }

        var answer = await _published.NewestAsync(from, cancellationToken).ConfigureAwait(false);

        if (answer.Newest is { } newest)
        {
            return (newest, null);
        }

        var unanswered = $"nuget.org did not say which release of {ToolPackage.Id} is the newest - {answer.Untold} -";

        if (copy is { IsPrerelease: false } && (held is null || SemanticVersion.Compare(copy, held) > 0))
        {
            _output.Info(
                CommandName,
                $"{unanswered} so the release this copy is, {copy}, is the one {(held is null ? "installed" : $"the {held} installed is moved to")}");

            return (copy, null);
        }

        return (null, $"{unanswered} so nothing was changed: {holds}");
    }

    /// <summary>Installs the tool, or updates it, and says so once it is listed as the release it was moved to.</summary>
    /// <remarks>
    /// Never stopped by this command once it has begun, nor is the reading of what it left: an update cut off part way
    /// leaves a machine with no tool, or half of one, and a command that then says nothing of which. An interruption
    /// that reaches dotnet itself, as one typed at a terminal can, is read back as any failure is. dotnet has its own bound
    /// (<see cref="ToolPackage.ChangeBudget"/>), and the command line waits for this as for any command past its point
    /// of no return (<see cref="PointOfNoReturn"/>).
    /// </remarks>
    private async Task<CommandOutcome> ChangeAsync(ToolUpdateRequest request, string kept, string? installed, string target)
    {
        var (verb, doing, did) = installed is null
            ? ("install", $"installing {ToolPackage.Id} {target} in {kept}", $"installed {ToolPackage.Id} {target} in {kept}")
            : ("update", $"updating {ToolPackage.Id} in {kept} from {installed} to {target}", $"updated {ToolPackage.Id} in {kept} from {installed} to {target}");

        // Where this process is the installed tool, its files are about to be replaced under it.
        if (IsTheTool(_program.Path))
        {
            _program.LoadWhole();
        }

        _output.Info(CommandName, doing);

        var change = await _processRunner
            .RunAsync(
                new ProcessRequest
                {
                    FileName = HostInspector.DotnetProgram,
                    Arguments = ToolPackage.ChangeArguments(verb, target, request.ToolPath),
                    Timeout = ToolPackage.ChangeBudget,
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        // What the machine now runs, in dotnet's own listing - or why that is not known, in dotnet's own words.
        var listing = await ListAsync(request, CancellationToken.None).ConfigureAwait(false);
        string? now = null;
        var unknown = !listing.Succeeded
            ? HostProbes.Failure("its tools could not be listed", listing)
            : HostProbes.TryReadToolVersion(listing.StandardOutput, ToolPackage.Id, out now) ? null : "what dotnet listed is not something this build reads";
        var listed = now is null ? "not listed there" : $"listed there as {now}";
        var since = unknown is null ? $"it is {listed} since" : $"what is listed there since is not known: {unknown}";

        if (!change.Succeeded)
        {
            var started = Running(await _processes.ReadAsync(CancellationToken.None).ConfigureAwait(false));

            return new CommandOutcome(
                HarnessExit.CommandFailed,
                HostProbes.Failure($"{doing} failed", change),
                [
                    .. Named($"{Count(started, "started")} on this machine while it ran, which is what stops an update:", started),
                    unknown is null ? $"{ToolPackage.Id} is {listed} since." : $"What is listed there since is not known: {unknown.TrimEnd('.')}.",
                    .. started.Count == 0 ? [] : new[] { RunAgain },
                ]);
        }

        return now == target
            ? CommandOutcome.Ok($"{did}, and {since}")
            : new CommandOutcome(HarnessExit.CommandFailed, $"dotnet said it {did}, and {since}");
    }

    /// <summary>Waits until no DssHarness runs on this machine, saying who it waits for as it starts and every so often.</summary>
    private async Task WaitAsync(string moving, IReadOnlyList<SampledProcess> running, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;

        _output.Info(CommandName, $"waiting to {moving} until {Awaited(running)}");

        while (running.Count > 0)
        {
            await _wait(LooksEvery, cancellationToken).ConfigureAwait(false);

            waited += LooksEvery;
            running = Running(await _processes.ReadAsync(cancellationToken).ConfigureAwait(false));

            if (running.Count > 0 && waited >= SaidAgainEvery)
            {
                waited = TimeSpan.Zero;
                _output.Info(CommandName, $"still waiting to {moving} until {Awaited(running)}");
            }
        }

        _output.Info(CommandName, $"no {ToolPackage.Id} is running on this machine now");
    }

    private Task<ProcessResult> ListAsync(ToolUpdateRequest request, CancellationToken cancellationToken)
        => _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = HostInspector.DotnetProgram,
                Arguments = ToolPackage.ListArguments(request.ToolPath),
                Timeout = ListBudget,
            },
            cancellationToken);

    /// <summary>Every DssHarness <paramref name="table"/> lists but this process, by the name of its program, in the order of their ids.</summary>
    private IReadOnlyList<SampledProcess> Running(ProcessTableReading table)
        => [.. table.Processes.Where(one => one.Id != _process.CurrentId && IsTheTool(one.Name)).OrderBy(one => one.Id)];

    /// <summary>The nearest of <paramref name="running"/> that started this process, through whatever it started in between, or <see langword="null"/>.</summary>
    private SampledProcess? StartedThis(ProcessTableReading table, IReadOnlyList<SampledProcess> running)
    {
        var parents = table.Processes.GroupBy(one => one.Id).ToDictionary(group => group.Key, group => group.First().ParentId);
        var seen = new HashSet<int>();

        for (var at = parents.GetValueOrDefault(_process.CurrentId); at is { } id && seen.Add(id); at = parents.GetValueOrDefault(id))
        {
            if (running.FirstOrDefault(one => one.Id == id) is { } parent)
            {
                return parent;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="program"/>, a path or a bare name, is the tool's own command, as a host's process listing is read.</summary>
    private static bool IsTheTool(string? program)
        => program is not null && HostProbes.ListsProcess(program, ToolPackage.Command);

    /// <summary>
    /// That <paramref name="table"/> was read by names alone, and why, where it was: what each process was asked is then
    /// missing from its line, and said to be rather than left to look like a process that was asked nothing.
    /// </summary>
    private static IEnumerable<string> Unread(ProcessTableReading table)
        => table.Degraded is { } why ? [$"What each was asked could not be read: {why.TrimEnd('.')}."] : [];

    /// <summary>Each of <paramref name="running"/> on a line of its own, under <paramref name="heading"/>; nothing where there is none.</summary>
    private static IEnumerable<string> Named(string heading, IReadOnlyList<SampledProcess> running)
        => running.Count == 0 ? [] : [heading, .. running.Select(one => "  " + Describe(one))];

    private static string Describe(SampledProcess one)
        => string.Create(CultureInfo.InvariantCulture, $"pid {one.Id}") + (AskedOf(one.CommandLine) is { } asked ? $" ({ToolPackage.Command} {asked})" : string.Empty);

    private static string Count(IReadOnlyList<SampledProcess> running, string doing = "running")
        => running.Count == 1
            ? $"1 {ToolPackage.Id} process {(doing == "running" ? "is running" : doing)}"
            : string.Create(CultureInfo.InvariantCulture, $"{running.Count} {ToolPackage.Id} processes {(doing == "running" ? "are running" : doing)}");

    private static string Awaited(IReadOnlyList<SampledProcess> running)
        => (running.Count == 1
                ? $"1 {ToolPackage.Id} process on this machine has ended: "
                : string.Create(CultureInfo.InvariantCulture, $"{running.Count} {ToolPackage.Id} processes on this machine have ended: "))
            + string.Join(", ", running.Select(Describe));
}
