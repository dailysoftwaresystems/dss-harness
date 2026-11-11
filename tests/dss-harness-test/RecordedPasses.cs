using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Repository;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// A recorded passed of one arm on one leg that nothing this machine reads has moved since, written where the record
/// keeps it: what it was judged on read from the tree as the one reading judges it - the rows, the texts and the sites
/// as the tree holds them now, the tables its builds were read from, the settings its leg is swept with on the host it
/// names - and its key of those parts. The one way a test records a pass a listing then reads as unmoved here.
/// </summary>
internal static class RecordedPasses
{
    /// <summary>The run a recorded pass names.</summary>
    public const string Run = "20261012-145059-ba433418";

    /// <summary>Records a passed of <paramref name="arm"/> on <paramref name="leg"/>, judged on what the tree at <paramref name="root"/> holds now.</summary>
    /// <param name="fileSystem">Writes the record.</param>
    /// <param name="layout">Where the repository keeps its records.</param>
    /// <param name="config">The configuration whose leg and host settings the pass was swept with.</param>
    /// <param name="root">The tree whose files the pass was judged on.</param>
    /// <param name="leg">The leg it passed on.</param>
    /// <param name="arm">The arm, as its registry declares it.</param>
    /// <param name="host">The host it was judged on.</param>
    /// <param name="tables">The tables its builds were read from; none where it was judged on no build.</param>
    public static void Record(
        IFileSystem fileSystem,
        HarnessLayout layout,
        HarnessConfig config,
        string root,
        string leg,
        MutationArm arm,
        HostId? host = null,
        IReadOnlyList<BuiltFromTable>? tables = null)
    {
        var on = host ?? HostId.Local;
        var built = tables ?? [];

        string Held(string path) => FileContentHash.Of(File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))));

        var judged = new JudgedOn
        {
            Judge = ArmJudge.Revision,
            Rows = JudgedOn.RowsOf(arm),
            Texts = JudgedOn.TextsOf(arm).ToDictionary(path => path, path => (string?)Held(path), StringComparer.Ordinal),
            Sites = arm.Sites.ToDictionary(site => site.Site, site => (string?)Held(site.Site), StringComparer.Ordinal),
            Built = built.ToDictionary(table => table.Target, table => table.Digest(), StringComparer.Ordinal),
            Compilers = [],
            Settings = SweepSettings.Of(config, config.Legs[leg], config.Hosts.SettingsFor(on)),
            RunReads = new Dictionary<string, string>(StringComparer.Ordinal),
            Host = on.ToString(),
        };

        Assert.Null(new ArmVerdictRecord(fileSystem, layout).Merge(
            leg,
            arm.Id,
            new RecordedVerdict
            {
                Verdict = "passed",
                Run = Run,
                At = DateTimeOffset.UnixEpoch,
                SweptIn = RepositoryTree.MainCheckout,
                Key = judged.Key(),
                JudgedOn = judged,
            },
            built));
    }
}
