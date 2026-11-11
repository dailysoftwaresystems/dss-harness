using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Mutations;

/// <summary>
/// The one reading of the arms registry a listing is made of, as <c>check-mutations --list</c> lists it and any other
/// reader of the registry reads it: the registry and its texts verified as a sweep verifies them, every selected leg
/// read as a listing reads a leg - its record, the tree's one reading, what its configure would be given - with the
/// arms selected and each arm's scopes beside them. One owner, so that "unmoved here" means one thing wherever it is
/// said. Reads no host, takes no lock and writes nothing.
/// </summary>
/// <param name="syncService">Reads a tree once, whole, as every reader of a tree reads it.</param>
/// <param name="fileSystem">Reads the registry and the record.</param>
/// <param name="platform">This machine, whose file system compares the names of its trees.</param>
public sealed class ArmListingReader(
    ISyncService syncService,
    IFileSystem fileSystem,
    IHostPlatform platform)
{
    private readonly ISyncService _syncService = syncService;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// The reading a listing is made of: every leg selected by <paramref name="legNames"/> listed, with the arms
    /// <paramref name="armNames"/> selects and the registry itself with each arm's scopes.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="armNames">What <c>--arms</c> was given, or <see langword="null"/> for every arm.</param>
    /// <param name="legNames">What <c>--legs</c> was given, or <see langword="null"/> for every declared leg.</param>
    /// <param name="command">The command reading it, as a refusal names it.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="HarnessException">
    /// As <c>check-mutations --list</c> refuses: the registry or a text it cites cannot be read whole, a scope names no
    /// leg, or <c>--arms</c> names an arm no A row declares.
    /// </exception>
    internal async Task<ArmListingRead> ReadListingAsync(
        HarnessContext context,
        IReadOnlyList<string>? armNames,
        IReadOnlyList<string>? legNames,
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var config = context.Config;
        var arms = await ReadAsync(context, armNames, command, cancellationToken, sweeps: false).ConfigureAwait(false);
        var legs = LegSelection.Resolve(config, legNames).Legs;
        var unsweepable = MutationService.Unsweepable(config, legs);
        var record = new ArmVerdictRecord(_fileSystem, context.Layout);
        var trees = new Dictionary<string, TreeFiles>(StringComparer.FromComparison(_platform.PathComparison));
        var listed = new List<ListedLeg>();

        foreach (var leg in legs)
        {
            var tree = LegTrees.Here(context, leg.Leg, null);

            if (!trees.TryGetValue(tree, out var files))
            {
                using var reading = await _syncService.ReadSourceAsync(tree, cancellationToken).ConfigureAwait(false);

                trees[tree] = files = new TreeFiles(reading.Files, PathCase.In(_fileSystem, tree));
            }

            var named = LegPlacement.Named(config, leg.Leg);
            var project = VariantKey.ProjectFor(config, leg.Leg);

            // One read of a table to a digest, for the whole listing, however many arms and entries name it.
            var tables = new Dictionary<string, RecordedTable>(StringComparer.Ordinal);

            RecordedTable TableOf(string digest)
                => tables.TryGetValue(digest, out var read) ? read : tables[digest] = record.Table(leg.Name, digest);

            listed.Add(MutationListing.Of(new ListingInput(
                leg.Name,
                ArmSelection.For(leg.Name, arms.Registry, arms.Selected, arms.Scopes).Driven,
                unsweepable.GetValueOrDefault(leg.Name),
                record.Read(leg.Name),
                TableOf,
                files,
                named?.ToString(),

                // Asked of the host the leg names, or of the one an entry was judged on where it names none: which host a
                // sweep would place it on is not read here, so it is read as it was, and the environment its build is
                // given with it - what that host declares, as the sweep that judged it was given what it declared.
                host => SweepSettings.Of(config, leg.Leg, config.Hosts.SettingsFor(host)),
                config.Mutations,
                project is null ? null : JudgedCauses.GenerationOf(config, VariantKey.For(config, leg.Leg, leg.Leg.Os), project))));
        }

        return new ArmListingRead(new ArmListing(listed, config.Mutations.Registry!, record.Directory), arms);
    }

    /// <summary>
    /// Reads and verifies the registry <paramref name="context"/>'s configuration names, with every text it cites, and
    /// selects the arms <paramref name="armNames"/> names.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="armNames">What <c>--arms</c> was given, or <see langword="null"/> for every arm.</param>
    /// <param name="command">The command reading it, as the refusal of a registry left unset names it.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <param name="sweeps">Whether what is read is to be swept, rather than listed or checked: a sweep needs more of it.</param>
    /// <exception cref="HarnessException">
    /// No registry is configured, it or a text it cites cannot be read whole, the registry is not UTF-8 or a line of it
    /// opens with a byte order mark (<see cref="RegistryText.TryRead"/>), it or a text it cites is one a sync
    /// withholds from every copy of the tree - by the configuration's rules or by git's - a scope names no leg, or a
    /// selected arm runs a binary with no report arguments configured where <paramref name="sweeps"/> - a listing runs
    /// none, and needs none (<see cref="HarnessExit.ConfigInvalid"/>); or
    /// <c>--arms</c> names an arm it does not declare (<see cref="HarnessExit.UsageError"/>); or git could not be asked
    /// what it ignores in the tree (<see cref="HarnessExit.CommandFailed"/>), which no sync of it could be made without.
    /// </exception>
    internal async Task<SweepArms> ReadAsync(HarnessContext context, IReadOnlyList<string>? armNames, string command, CancellationToken cancellationToken, bool sweeps = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var settings = context.Config.Mutations;
        var root = context.Layout.RepositoryRoot;

        if (!MutationService.Declares(context, _fileSystem))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                settings.Registry is { } configured
                    ? $"mutations.registry names '{configured}', which is not a file in '{root}'."
                    : $"{command} has no arms registry to {(sweeps ? "sweep" : "read")}: set mutations.registry to the registry's path, relative to the repository root.");
        }

        var registry = settings.Registry!;
        var file = TreeRegistryFiles.In(root, registry);

        byte[] bytes;

        try
        {
            bytes = _fileSystem.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // There, and not read: the registry's refusal, as one that is not there is - never a defect of this tool.
            throw new HarnessException(HarnessExit.ConfigInvalid, $"mutations.registry names '{registry}', which could not be read: {ex.Message}");
        }

        // Read as the one reader of a registry's bytes reads it - UTF-8 and nothing else, a byte order mark at its start
        // no part of its first row - as its merge and the reading of its history read every version of it: read leniently
        // here, a registry one of those refuses would be swept, listed and checked as another text.
        if (!RegistryText.TryRead(bytes, out var text, out var unread, out var line))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                line is { } number
                    ? $"mutations.registry names '{registry}', whose line {number} {unread}; take the mark off that line."
                    : $"mutations.registry names '{registry}', which {unread}; save it as UTF-8 - the arms registry is read as UTF-8 and "
                        + "nothing else, as its merge and its history are.");
        }

        var rows = text.Text;

        // What a sync withholds is in no copy of the tree, and a worker is such a copy, as a host's is: by the rules the
        // configuration names, which the registry and the text directory are held to as it is read, and by git's - what
        // git ignores a sync leaves behind as well, and only the tree says which that is. So it is asked here, as the
        // tree's view is made.
        var files = await TreeRegistryFiles.OfAsync(_syncService, _fileSystem, context, cancellationToken).ConfigureAwait(false);

        if (files.IsWithheld(PathPatterns.Normalize(registry)))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"mutations.registry names '{registry}', which a sync withholds from every copy of the tree - {MutationRegistryValidation.Withholders} - so no "
                + "host sweeping a leg would hold it: keep it where a sync carries it.");
        }

        // Every problem of the registry in one refusal, and before --arms is weighed against it: a scope naming no leg is
        // the registry's whatever else is wrong with it, and whichever arms the command names - one it does not declare
        // among them, which is a mistake of the command and is said once the registry itself can be swept.
        var validation = await MutationRegistryValidation.ValidateAsync(rows, files, settings, context.Config, cancellationToken).ConfigureAwait(false);

        if (!validation.Valid)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, string.Join(Environment.NewLine, validation.Refusal(registry)));
        }

        var selected = ArmSelection.Resolve(validation.Registry, armNames);

        if (sweeps && selected.Any(arm => arm.Kind == RedKind.TestRed) && settings.ReportArgs is not { Count: > 0 })
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{MutationReport.Setting} is not set, and the sweep runs {MutationRegistryParser.TestRed} arms, whose binaries it "
                + "judges by the JUnit report each run writes: set it to the arguments that make the binary write one to "
                + $"{{{MutationReport.Placeholder}}}, such as [\"--gtest_output=xml:{{{MutationReport.Placeholder}}}\"].");
        }

        // A runner runReads names that no TEST-RED row names covers nothing: an entry for a runner that was renamed, or
        // spelled otherwise, would silently keep every key of the runner it meant to name.
        var named = validation.Registry.Arms
            .Where(arm => arm.Kind == RedKind.TestRed)
            .Select(arm => arm.Runner)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (settings.RunReads?.Keys.FirstOrDefault(runner => !named.Contains(runner)) is { } unnamed)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{MutationSettings.RunReadsSetting} names runner '{unnamed}', which no {MutationRegistryParser.TestRed} row of '{registry}' names: "
                + "an entry no arm runs covers nothing. Name the runner as its A row does, or leave it out.");
        }

        return new SweepArms(validation.Registry, selected, validation.Scopes);
    }
}
