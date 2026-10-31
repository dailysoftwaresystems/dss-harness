using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Agents;

/// <summary>
/// What the process tree says of this process: the nearest ancestor of it - its parent, that one's parent, and on up -
/// that is a process of this tool's, and so the engine an agent's child was started under; or why the tree could not be
/// read. The second way a command tells it runs under an agent, beside the mark every agent's child is given
/// (<see cref="AgentChildEnvironment.AgentMarker"/>), which a child can take away in forms no policy reads: the tree
/// holds whatever the child's environment says. A process detached from the agent's tree - started by <c>at</c>, a
/// <c>tmux</c> server, a double fork - has no such ancestor, and is told by the mark alone.
/// </summary>
/// <param name="processes">The machine's process table.</param>
/// <param name="identity">This process, whose ancestors are walked.</param>
/// <param name="program">The file this process runs as.</param>
/// <param name="platform">This machine, whose paths compare its own way.</param>
public sealed class AgentAncestry(IProcessTable processes, IProcessIdentity identity, IOwnProgram program, IHostPlatform platform)
{
    /// <summary>The program a runtime hosting the tool's assembly runs as, as a build from source runs it.</summary>
    private const string RuntimeHost = "dotnet";

    /// <summary>The file of the tool's own assembly, which a runtime hosting it names on its command line.</summary>
    private static readonly string Assembly = $"{ToolPackage.Command}.dll";

    /// <summary>Reads the machine's process table and walks this process's ancestors in it (<see cref="Of"/>).</summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<AgentAncestryAnswer> ReadAsync(CancellationToken cancellationToken)
    {
        var table = await processes.ReadAsync(cancellationToken).ConfigureAwait(false);

        return Of(table, identity.CurrentId, program.Path, platform.PathComparison);
    }

    /// <summary>
    /// The nearest ancestor of <paramref name="currentId"/> in <paramref name="table"/> that is a process of this tool's
    /// (<see cref="IsTheTool"/>), walked by each process's parent: a process whose id is its child's parent's but that
    /// started after that child is another process that inherited a freed id, not the parent, and ends the walk - ids are
    /// recycled, and the start time is what tells one holder of an id from the next. Where the table was read by names
    /// alone (<see cref="ProcessTableReading.Degraded"/>), or does not hold this process, which processes started it
    /// cannot be told, and the answer says why rather than reading a tree nobody looked at as one with no agent in it.
    /// </summary>
    /// <param name="table">The machine's processes, as it was read.</param>
    /// <param name="currentId">The process whose ancestors are walked: this one.</param>
    /// <param name="ownProgram">The file this process runs as; null where the platform would not say.</param>
    /// <param name="pathComparison">How this machine compares paths.</param>
    public static AgentAncestryAnswer Of(ProcessTableReading table, int currentId, string? ownProgram, StringComparison pathComparison)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (Hosts.ProgramUpdate.Unread(table, "which processes started this one") is { } unread)
        {
            return new AgentAncestryAnswer(null, unread);
        }

        var byId = table.Processes.GroupBy(one => one.Id).ToDictionary(group => group.Key, group => group.First());

        if (!byId.TryGetValue(currentId, out var child))
        {
            return new AgentAncestryAnswer(null, "which processes started this one could not be read - the process table does not hold this process -");
        }

        var seen = new HashSet<int> { currentId };

        while (child.ParentId is { } parentId && seen.Add(parentId) && byId.TryGetValue(parentId, out var parent))
        {
            if (parent.StartedUtc is { } parentStarted && child.StartedUtc is { } childStarted && parentStarted > childStarted)
            {
                break;
            }

            if (IsTheTool(parent, ownProgram, pathComparison))
            {
                return new AgentAncestryAnswer(parent, null);
            }

            child = parent;
        }

        return new AgentAncestryAnswer(null, null);
    }

    /// <summary>
    /// Whether <paramref name="process"/> is a process of this tool's, however it was started: a program of the tool's
    /// own command (<see cref="ToolPackage.IsCommand"/>) - the installed tool, or one kept elsewhere - the very file
    /// this process runs as, or a runtime running the tool's assembly, as a build from source runs. The runtime itself
    /// is the program of many things, so it is told by the application its command line runs (<see cref="HostedApplication"/>),
    /// never by its file, nor by a word an argument of that application's holds.
    /// </summary>
    /// <param name="process">One process of the table.</param>
    /// <param name="ownProgram">The file this process runs as; null where the platform would not say.</param>
    /// <param name="pathComparison">How this machine compares paths.</param>
    public static bool IsTheTool(SampledProcess process, string? ownProgram, StringComparison pathComparison)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (ToolPackage.IsCommand(process.Name))
        {
            return true;
        }

        var hosted = string.Equals(process.Name, RuntimeHost, StringComparison.OrdinalIgnoreCase)
            || (process.Executable is { } file && string.Equals(Path.GetFileNameWithoutExtension(file), RuntimeHost, StringComparison.OrdinalIgnoreCase));

        if (hosted)
        {
            return process.CommandLine is { } line
                && HostedApplication(line) is { } application
                && string.Equals(Path.GetFileName(application.Replace('\\', '/')), Assembly, StringComparison.OrdinalIgnoreCase);
        }

        return ownProgram is { Length: > 0 } own && process.Executable is { } executable && string.Equals(executable, own, pathComparison);
    }

    /// <summary>
    /// The application a runtime host's <paramref name="commandLine"/> runs: the first word after the host's program, its
    /// <c>exec</c> and its own options (<see cref="HostOptionsWithAValue"/> each with its value), whatever words follow
    /// being that application's arguments; null where the line runs none. A path begun with a directory runs on to the
    /// first word ending in <c>.dll</c> where it is not quoted - Linux and <c>ps</c> join a process's arguments with
    /// spaces, a directory's own spaces among them - and a word naming no directory and no assembly, a command of the
    /// SDK's (<c>dotnet build</c>, a local tool's <c>dotnet dssharness</c>), is the application as it is.
    /// </summary>
    /// <param name="commandLine">The runtime host's command line, as the process table read it.</param>
    public static string? HostedApplication(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var at = 0;
        var words = 0;
        var valueOwed = false;

        while (Word(commandLine, ref at) is var (word, start, quoted))
        {
            words++;

            if (words == 1 || valueOwed || (words == 2 && word == "exec"))
            {
                valueOwed = false;
                continue;
            }

            if (!quoted && word.StartsWith('-'))
            {
                valueOwed = HostOptionsWithAValue.Contains(word);
                continue;
            }

            if (quoted || word.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || word.IndexOfAny(['/', '\\']) < 0)
            {
                return word;
            }

            for (var end = commandLine.IndexOf(".dll", start, StringComparison.OrdinalIgnoreCase); end >= 0; end = commandLine.IndexOf(".dll", end + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (end + 4 == commandLine.Length || char.IsWhiteSpace(commandLine[end + 4]))
                {
                    return commandLine[start..(end + 4)];
                }
            }

            return word;
        }

        return null;
    }

    /// <summary>The runtime host's own options that take a value, which is no application.</summary>
    private static readonly HashSet<string> HostOptionsWithAValue = new(StringComparer.Ordinal)
    {
        "--additionalprobingpath", "--additional-deps", "--depsfile", "--fx-version", "--roll-forward", "--roll-forward-on-no-candidate-fx", "--runtimeconfig",
    };

    /// <summary>The next word of <paramref name="line"/> from <paramref name="at"/>, a quoted one whole and without its quotes, where it starts and whether it was quoted; null past the last.</summary>
    private static (string Word, int Start, bool Quoted)? Word(string line, ref int at)
    {
        while (at < line.Length && char.IsWhiteSpace(line[at]))
        {
            at++;
        }

        if (at >= line.Length)
        {
            return null;
        }

        var start = at;

        if (line[at] == '"')
        {
            var close = line.IndexOf('"', at + 1);
            at = close < 0 ? line.Length : close + 1;

            return (line[(start + 1)..(close < 0 ? line.Length : close)], start, true);
        }

        while (at < line.Length && !char.IsWhiteSpace(line[at]))
        {
            at++;
        }

        return (line[start..at], start, false);
    }
}

/// <summary>What the process tree says of this process.</summary>
/// <param name="Tool">The nearest ancestor that is a process of this tool's; null where none is, or the tree could not be read.</param>
/// <param name="Unread">Why which processes started this one could not be read, as the opening of a refusal's sentence, before its "so"; null where it was read.</param>
public sealed record AgentAncestryAnswer(SampledProcess? Tool, string? Unread);
