using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;

namespace DssHarness.Core.SetUps;

/// <summary>
/// A set-up's <c>up</c> or <c>down</c> line: what it starts, read once for <c>up &lt;name&gt;</c> and
/// <c>down &lt;name&gt;</c>, which run it at the root of the tree they were given, and for a leg that sets up, which
/// runs it in its own tree.
/// </summary>
public interface ISetUpService
{
    /// <summary>
    /// Runs the line <paramref name="direction"/> names for the set-up <paramref name="name"/> at the root of the tree
    /// <paramref name="directory"/> is in, on this machine, its inputs given <paramref name="inputs"/> over their
    /// defaults, its output streamed as it arrives.
    /// </summary>
    /// <param name="directory">A directory inside the tree.</param>
    /// <param name="direction">Up or down.</param>
    /// <param name="name">The set-up's name as typed; matched ignoring case.</param>
    /// <param name="inputs">What <c>--input name=value</c> gave, by input.</param>
    /// <param name="cancellationToken">Stops the line, and every process it started.</param>
    /// <exception cref="HarnessException">
    /// The name is not declared, or the values given cannot be had (<see cref="HarnessExit.UsageError"/>); the set-up
    /// names no line for this machine's platform, or a program the configuration does not allow; or, for down, a leg of
    /// a run still uses it (<see cref="HarnessExit.Refused"/>): nothing was run.
    /// </exception>
    /// <exception cref="ProgramStartException">The line's program could not be started.</exception>
    Task<SetUpReport> RunAsync(
        string directory,
        SetUpDirection direction,
        string name,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What the line <paramref name="direction"/> names for the declared set-up <paramref name="name"/> starts on a
    /// <paramref name="platformKey"/> machine at <paramref name="treeRoot"/>, its arguments filled in with
    /// <paramref name="values"/>.
    /// </summary>
    /// <param name="config">The configuration that declares the set-up.</param>
    /// <param name="name">The set-up's name as declared.</param>
    /// <param name="values">Every input's value, as <see cref="SetUpValues.Resolve"/> gives them.</param>
    /// <param name="direction">Up or down.</param>
    /// <param name="platformKey">The platform of the machine the line runs on.</param>
    /// <param name="treeRoot">The root of the tree the line runs in, and the repository its program must be inside.</param>
    /// <exception cref="HarnessException">
    /// The configuration declares no such set-up, or it names no line for the platform, or a program the configuration
    /// does not allow (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    SetUpCommandLine Resolve(
        HarnessConfig config,
        string name,
        IReadOnlyDictionary<string, string> values,
        SetUpDirection direction,
        string platformKey,
        string treeRoot);
}

/// <inheritdoc cref="ISetUpService" />
/// <param name="contextLoader">Reads the tree and its configuration.</param>
/// <param name="processRunner">Starts the line's program.</param>
/// <param name="platform">Says which platform's line runs.</param>
/// <param name="toolPolicy">Says whether the line's program may run.</param>
/// <param name="programs">Finds the line's program as a leg on this machine finds what it starts.</param>
/// <param name="holds">Says which legs still use a set-up a typed down would bring down.</param>
/// <param name="output">Where the command says what it runs, and the line's output goes.</param>
public sealed class SetUpService(
    IHarnessContextLoader contextLoader,
    IProcessRunner processRunner,
    IHostPlatform platform,
    ActionToolPolicy toolPolicy,
    LocalProgramResolver programs,
    SetUpHolds holds,
    IHarnessOutput output) : ISetUpService
{
    /// <summary>The command that runs a set-up's <c>up</c> line, named as its key is.</summary>
    public const string UpCommand = SetUpConfig.UpKey;

    /// <summary>The command that runs a set-up's <c>down</c> line, named as its key is.</summary>
    public const string DownCommand = SetUpConfig.DownKey;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHostPlatform _platform = platform;
    private readonly ActionToolPolicy _toolPolicy = toolPolicy;
    private readonly LocalProgramResolver _programs = programs;
    private readonly SetUpHolds _holds = holds;
    private readonly IHarnessOutput _output = output;

    /// <summary>The command, and the key in <c>config.json</c>, of <paramref name="direction"/>.</summary>
    /// <param name="direction">Up or down.</param>
    public static string CommandName(SetUpDirection direction) => SetUpConfig.KeyOf(direction);

    /// <inheritdoc />
    public async Task<SetUpReport> RunAsync(
        string directory,
        SetUpDirection direction,
        string name,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(inputs);

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        var config = context.Config;
        var root = context.Layout.RepositoryRoot;
        var declared = DeclaredName.In(config.SetUp.Keys, name)
            ?? throw new HarnessException(HarnessExit.UsageError, DeclaredName.NotDeclared(name, "setUp", config.SetUp.Keys));
        var values = SetUpValues.Resolve(declared, config.SetUp[declared], inputs, CommandLineInputs.Option, out var problems);

        if (problems.Count > 0)
        {
            throw new HarnessException(HarnessExit.UsageError, $"{string.Join("; ", problems)}. Nothing was run.");
        }

        var line = Resolve(config, declared, values, direction, _platform.PlatformKey, root);

        // Brought down under legs still using it, their work would go on against services that are gone, and fail for a
        // reason no line of theirs says. The last of them brings it down as it ends.
        if (direction == SetUpDirection.Down)
        {
            var sharing = new SetUpSharing(await _contextLoader.RepositoryOfAsync(context, cancellationToken).ConfigureAwait(false), config.SetUp[declared].MachineWide);

            if (_holds.HeldOn(declared, root, sharing) is { Count: > 0 } held)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"set-up '{declared}' {(sharing.MachineWide ? "on this machine" : $"in '{_output.Shown(root)}'")} is still used by "
                    + $"{string.Join(", ", held.Select(hold => _holds.Said(hold, root)))}: "
                    + $"the last of them brings it down as it ends (record: '{_output.Shown(_holds.Location)}'). Nothing was run.");
            }
        }

        _output.Info(CommandName(direction), $"'{declared}'{SetUpValues.Said(values)} in '{_output.Shown(root)}': {line.Line}");

        // Looked for as a leg on this machine looks for what it starts - on the PATH, then where toolSearchDirectories
        // names - so a program a leg's line found, this line finds too.
        var search = _programs.Resolve([line.FileName], ToolSearchDirectories.For(config.ToolSearchDirectories, _platform.PlatformKey));

        // Read against the tree the line runs in, never this process's own directory: '-C' runs a line somewhere this
        // process is not.
        var result = await _processRunner
            .RunAsync(
                new ProcessRequest
                {
                    FileName = line.FileName,
                    Arguments = line.Arguments,
                    WorkingDirectory = root,
                    AppendToPath = search.Directories,
                    OnOutputLine = _output.Raw,
                    OnErrorLine = _output.RawError,
                    OutputKept = StreamKept.Tail,
                    ErrorKept = StreamKept.Tail,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new SetUpReport(direction, declared, values, _platform.PlatformKey, _output.Shown(root), new SetUpRan(line.Line, result.ExitCode, result.Duration));
    }

    /// <inheritdoc />
    public SetUpCommandLine Resolve(
        HarnessConfig config,
        string name,
        IReadOnlyDictionary<string, string> values,
        SetUpDirection direction,
        string platformKey,
        string treeRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        var owner = $"setUp '{name}' {CommandName(direction)}";

        // A tree a leg works in reads its own configuration, which may be older than the one the run was asked from.
        if (!config.SetUp.TryGetValue(name, out var setUp))
        {
            throw new HarnessException(HarnessExit.Refused, $"{DeclaredName.NotDeclared(name, "setUp", config.SetUp.Keys)} Nothing was run.");
        }

        var lines = setUp.Line(direction);
        var line = lines.For(platformKey)
            ?? throw new HarnessException(
                HarnessExit.Refused,
                $"{owner} has no command line for {platformKey}: it names {string.Join(", ", lines.Values.Keys)}. Nothing was run.");

        // The file was refused for these already; a configuration built in memory is held to the same line.
        if (CommandLineText.Split(line, out var written) is { } problem)
        {
            throw new HarnessException(HarnessExit.Refused, $"{owner} '{line}' {problem} Nothing was run.");
        }

        // Each argument filled in apart, so a value holding a space is still the one argument it was written as.
        var filled = written.Select(argument => LegPathNames.Fill(argument, values, $"{owner} '{line}'")).ToList();

        if (_toolPolicy.Classify(filled[0], config.Tools, treeRoot) == ProgramAllowance.Undeclared)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{owner} '{filled[0]}' {ActionToolPolicy.Undeclared(config.Tools)}. Nothing was run.");
        }

        return new SetUpCommandLine(line, ProcessRunner.Anchored(filled[0], treeRoot), [.. filled.Skip(1)]);
    }
}

/// <summary>What a set-up's line starts.</summary>
/// <param name="Line">The line, as the configuration wrote it for this platform, before its inputs are filled in.</param>
/// <param name="FileName">Its program: a name looked for on PATH, or a full path read against the tree it runs in.</param>
/// <param name="Arguments">Its arguments, one each, its inputs filled in.</param>
public sealed record SetUpCommandLine(string Line, string FileName, IReadOnlyList<string> Arguments);

/// <summary>A set-up's line that ran: what it was, how it exited and how long it took.</summary>
/// <param name="Line">The line, as the configuration wrote it for this platform.</param>
/// <param name="ExitCode">The code its program exited with.</param>
/// <param name="Duration">How long it ran.</param>
public sealed record SetUpRan(string Line, int ExitCode, TimeSpan Duration)
{
    /// <summary>Whether the program exited zero.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>What <c>up &lt;name&gt;</c> or <c>down &lt;name&gt;</c> ran.</summary>
/// <param name="Direction">Up or down.</param>
/// <param name="Name">The set-up, as declared.</param>
/// <param name="Inputs">The value each of its inputs was given.</param>
/// <param name="Platform">The platform whose line ran.</param>
/// <param name="Directory">The tree root it ran in, as the command shows a path.</param>
/// <param name="Ran">The line, its exit code and how long it took.</param>
public sealed record SetUpReport(
    SetUpDirection Direction,
    string Name,
    IReadOnlyDictionary<string, string> Inputs,
    string Platform,
    string Directory,
    SetUpRan Ran);
