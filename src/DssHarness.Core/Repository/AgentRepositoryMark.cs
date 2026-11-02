using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Repository;

/// <summary>
/// The mark an agent's repository carries in its own configuration file, and the one rule that reads it: a repository is
/// an agent's repository of a main checkout only where its own <c>.git/config</c> names that checkout and the agent's
/// address, that checkout is a non-bare repository's own main checkout and carries no mark itself, and the repository
/// borrows that checkout's objects.
/// </summary>
/// <param name="MainCheckout">The main checkout the mark names, as the tool wrote it: as git spells that checkout's root.</param>
/// <param name="Agent">The agent's address the mark names, <c>orchestrator/agent</c>; empty where the mark names none.</param>
/// <remarks>
/// Read from the repository's own configuration file alone, never through the main checkout's configuration it includes,
/// so no key of the main checkout's can mark a repository, nor the main checkout itself.
/// </remarks>
public sealed record AgentRepositoryMark(string MainCheckout, string Agent)
{
    /// <summary>The section of the configuration the mark is written in.</summary>
    public const string SectionName = "harness";

    /// <summary>The key naming the main checkout, as git lists it.</summary>
    public const string MainCheckoutKey = SectionName + ".maincheckout";

    /// <summary>The key naming the agent's address, as git lists it.</summary>
    public const string AgentKey = SectionName + ".agent";

    /// <summary>The section that writes this mark into a repository's configuration file.</summary>
    internal string Section => GitConfigText.Section(SectionName, [("mainCheckout", MainCheckout), ("agent", Agent)]);

    /// <summary>
    /// The mark the repository at <paramref name="tree"/> carries in its own configuration file, or <see langword="null"/>
    /// where it carries none - its <c>.git</c> is no directory, or names no key of the mark's section.
    /// </summary>
    /// <param name="gitClient">Reads the configuration file.</param>
    /// <param name="fileSystem">Looks for the repository's <c>.git</c> directory.</param>
    /// <param name="tree">The repository's root.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">The configuration file could not be read (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<AgentRepositoryMark?> ReadAsync(IGitClient gitClient, IFileSystem fileSystem, string tree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var gitDirectory = Path.Combine(tree, ".git");

        if (!fileSystem.DirectoryExists(gitDirectory) || !fileSystem.FileExists(Path.Combine(gitDirectory, "config")))
        {
            return null;
        }

        var read = await gitClient
            .RunAsync(tree, ["config", "--file", Path.Combine(gitDirectory, "config"), "--null", "--get-regexp", $@"^{SectionName}\."], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // git answers 1 where no key matches: a repository carrying no mark.
        if (read.ExitCode == 1 && !read.TimedOut)
        {
            return null;
        }

        if (!read.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The configuration of the repository at '{ReportText.Printable(tree)}' could not be read: {read.FailureMessage.TrimEnd('.')}.");
        }

        var values = read.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('\n', 2))
            .Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        return new AgentRepositoryMark(
            values.GetValueOrDefault(MainCheckoutKey, string.Empty),
            values.GetValueOrDefault(AgentKey, string.Empty));
    }

    /// <summary>
    /// Why the main checkout this mark names does not lend the repository at <paramref name="tree"/> its objects, to follow
    /// "which"; <see langword="null"/> where it does: it is there, a non-bare repository's own main checkout carrying no mark
    /// itself, and the repository borrows its objects.
    /// </summary>
    /// <param name="gitClient">Asks both repositories.</param>
    /// <param name="fileSystem">Looks for the main checkout.</param>
    /// <param name="platform">How paths compare.</param>
    /// <param name="tree">The marked repository's root.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git could not be asked (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public async Task<string?> NotLentAsync(IGitClient gitClient, IFileSystem fileSystem, IHostPlatform platform, string tree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(platform);

        if (MainCheckout.Length == 0)
        {
            return "is named by nothing: its mark names no main checkout";
        }

        if (!fileSystem.DirectoryExists(MainCheckout))
        {
            return "is not there";
        }

        var main = await gitClient.GetMainWorktreeAsync(MainCheckout, cancellationToken).ConfigureAwait(false);

        if (main is null)
        {
            return "is no repository's main checkout";
        }

        if (main.IsBare)
        {
            return "is a bare repository, which has no checkout";
        }

        if (!PathContainment.AreSame(main.Path, MainCheckout, platform.PathComparison))
        {
            return $"is no main checkout: git answers for it with '{ReportText.Printable(main.Path)}'";
        }

        if (await ReadAsync(gitClient, fileSystem, MainCheckout, cancellationToken).ConfigureAwait(false) is not null)
        {
            return "is itself an agent's repository";
        }

        var objects = await ObjectsOfAsync(gitClient, MainCheckout, cancellationToken).ConfigureAwait(false);

        return await BorrowsAsync(gitClient, platform, tree, objects, cancellationToken).ConfigureAwait(false)
            ? null
            : $"does not lend it its objects: '{ReportText.Printable(objects)}' is not among the object directories git reads there";
    }

    /// <summary>The object directory of the repository whose checkout is <paramref name="mainCheckout"/>, as git spells it: absolute.</summary>
    /// <param name="gitClient">Asks git.</param>
    /// <param name="mainCheckout">The checkout.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git could not say (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<string> ObjectsOfAsync(IGitClient gitClient, string mainCheckout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        var asked = await gitClient
            .RunAsync(mainCheckout, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return asked.Succeeded && asked.OutputLines is [var common]
            ? common + "/objects"
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Where the repository of '{ReportText.Printable(mainCheckout)}' keeps its objects could not be read: {asked.FailureMessage.TrimEnd('.')}.");
    }

    /// <summary>
    /// Whether git, asked in the repository at <paramref name="tree"/>, reads <paramref name="objects"/> as one of the object
    /// directories it borrows from (<c>git count-objects -v</c>'s <c>alternate:</c> lines).
    /// </summary>
    /// <param name="gitClient">Asks git.</param>
    /// <param name="platform">How paths compare.</param>
    /// <param name="tree">The repository's root.</param>
    /// <param name="objects">The object directory.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git could not say (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<bool> BorrowsAsync(IGitClient gitClient, IHostPlatform platform, string tree, string objects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentNullException.ThrowIfNull(platform);

        var counted = await gitClient
            .RunAsync(tree, ["-c", "core.quotePath=false", "count-objects", "-v"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!counted.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Which objects the repository at '{ReportText.Printable(tree)}' borrows could not be read: {counted.FailureMessage.TrimEnd('.')}.");
        }

        const string Alternate = "alternate: ";

        return counted.OutputLines
            .Where(line => line.StartsWith(Alternate, StringComparison.Ordinal))
            .Select(line => Unquoted(line[Alternate.Length..]))
            .Any(borrowed => PathContainment.AreSame(Path.GetFullPath(borrowed), Path.GetFullPath(objects), platform.PathComparison));
    }

    /// <summary>A path git printed, read back: one in double quotes has its C-style escapes undone, its octal ones as UTF-8 bytes.</summary>
    private static string Unquoted(string printed)
    {
        if (printed.Length < 2 || printed[0] != '"' || printed[^1] != '"')
        {
            return printed;
        }

        var bytes = new List<byte>();
        var inner = printed[1..^1];

        for (var index = 0; index < inner.Length; index++)
        {
            if (inner[index] != '\\' || index + 1 == inner.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(inner[index].ToString()));
                continue;
            }

            var escaped = inner[++index];

            if (escaped is >= '0' and <= '7' && index + 2 < inner.Length)
            {
                bytes.Add(Convert.ToByte(inner.Substring(index, 3), 8));
                index += 2;
                continue;
            }

            bytes.Add(escaped switch
            {
                'n' => (byte)'\n',
                't' => (byte)'\t',
                'b' => (byte)'\b',
                'r' => (byte)'\r',
                'a' => (byte)'\a',
                'f' => (byte)'\f',
                'v' => (byte)'\v',
                _ => (byte)escaped,
            });
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}
