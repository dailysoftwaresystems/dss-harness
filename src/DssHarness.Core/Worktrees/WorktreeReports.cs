using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Worktrees;

/// <summary>What <c>list-worktree</c> reports: the worktrees, and the copies hosts keep of them and of those that are gone.</summary>
public static class WorktreeReports
{
    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>
    /// The command that removes the copies hosts keep of trees gone from this machine, told by their tree: never by the
    /// name they are kept under, which a worktree here - a live agent's among them - may bear now.
    /// </summary>
    private static readonly string CleanGone = $"{ToolPackage.Command} {Runs.CleanService.CommandName} {Runs.CleanService.GoneCopiesOption}";

    /// <summary>
    /// The name a person types for the worktree whose copies are kept under <paramref name="name"/>: an orchestrator's
    /// agent's as <c>orchestrator/agent</c>, never as the two-hyphen name its copies' directories bear.
    /// </summary>
    private static string Shown(string name) => WorktreeAddress.OfCopyName(name);

    /// <summary>What <c>list-worktree</c> reports.</summary>
    /// <param name="worktrees">The worktrees there are.</param>
    /// <param name="copies">The copies hosts keep, as recorded here and, when asked, as each host has them.</param>
    /// <param name="json">Whether to answer with one JSON document rather than lines.</param>
    public static CommandOutcome List(IReadOnlyList<WorktreeListing> worktrees, HostCopyListing copies, bool json)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(copies);

        var message = Summary(worktrees, copies);

        if (json)
        {
            var document = Document(worktrees, copies).ToJsonString(JsonOptions);

            return copies.ExitCode == HarnessExit.Success
                ? CommandOutcome.Ok(message) with { Data = [document], Quiet = true }
                : CommandOutcome.Failed(copies.ExitCode, message) with { Data = [document] };
        }

        IReadOnlyList<string> lines = [.. Lines(worktrees, copies)];

        return copies.ExitCode == HarnessExit.Success
            ? CommandOutcome.Ok(message, lines)
            : CommandOutcome.Failed(copies.ExitCode, message, lines);
    }

    /// <summary>
    /// The listing list-worktree answers with where it stops before it lists anything - a --directory that does not
    /// exist, a repository not initialised: its own document, listing no worktree and no copy, after the code it exits
    /// with and the line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary) => ReportJson.Stopped(exitCode, summary, Document([], new HostCopyListing()));

    private static string Summary(IReadOnlyList<WorktreeListing> worktrees, HostCopyListing copies)
    {
        var said = worktrees.Count == 0 ? "no worktrees" : ReportText.Counted(worktrees.Count, "worktree");

        if (copies.Unreadable is { } unreadable)
        {
            return $"{said}; the copies hosts keep of them cannot be listed: {unreadable}";
        }

        if (copies.Gone.Count > 0)
        {
            said += $"; {ReportText.Counted(copies.Gone.Count, "worktree")} that are gone left copies on hosts, which '{CleanGone}' removes";
        }

        if (copies.Untold.Count > 0)
        {
            said += $"; {ReportText.Counted(copies.Untold.Count, "worktree")} with copies on hosts cannot be looked at here, so nothing here removes their copies";
        }

        if (copies.Hosts is { } hosts)
        {
            var unasked = hosts.Count(host => host.Unasked is not null);

            said += hosts.Count == 0 ? "; no host is declared, so none was asked"
                : unasked == 0 ? $"; asked {ReportText.Counted(hosts.Count, "host")}"
                : $"; {unasked} of {ReportText.Counted(hosts.Count, "host")} could not be asked";
        }

        return said;
    }

    private static IEnumerable<string> Lines(IReadOnlyList<WorktreeListing> worktrees, HostCopyListing copies)
    {
        foreach (var worktree in worktrees)
        {
            yield return worktree.ToString();

            foreach (var copy in copies.OfListed.GetValueOrDefault(worktree.Name) ?? [])
            {
                yield return $"  {copy.Host}: {copy.Path}";
            }
        }

        foreach (var tree in copies.Elsewhere)
        {
            yield return $"{Shown(tree.Name)}, the worktree at '{tree.Tree}', outside the worktrees root";

            foreach (var copy in tree.Copies)
            {
                yield return $"  {copy.Host}: {copy.Path}";
            }
        }

        foreach (var tree in copies.Gone)
        {
            yield return $"{Shown(tree.Name)}, gone from '{tree.Tree}': '{CleanGone}' removes the copies it left{NameNow(tree)}";

            foreach (var copy in tree.Copies)
            {
                yield return $"  {copy.Host}: {copy.Path}";
            }
        }

        foreach (var tree in copies.Untold)
        {
            yield return $"{Shown(tree.Name)}, the worktree at '{tree.Tree}', which cannot be looked at here: {tree.Why.TrimEnd('.')}; {Untouched}";

            foreach (var copy in tree.Copies)
            {
                yield return $"  {copy.Host}: {copy.Path}";
            }
        }

        foreach (var host in copies.Hosts ?? [])
        {
            if (host.Unasked is { } why)
            {
                // Where it keeps them is named only where it is known: no other path stands in for one its item would set.
                yield return host.RepositoryPath is { } beside
                    ? $"{host.Host}: could not be asked about the copies beside '{beside}': {why.TrimEnd('.')}"
                    : $"{host.Host}: could not be asked about its copies: {why.TrimEnd('.')}";
                continue;
            }

            // A host asked about its copies was asked beside where it keeps them (HostCopyLister): a place is known.
            var repositoryPath = host.RepositoryPath
                ?? throw new InvalidOperationException($"Host {host.Host} was asked about its copies with no place it keeps them named.");

            foreach (var line in Kept(host.Host, repositoryPath, host.Copies, earlier: false))
            {
                yield return line;
            }

            foreach (var place in host.Earlier)
            {
                if (place.Unasked is { } unasked)
                {
                    yield return $"{host.Host}: could not be asked about the copies beside '{place.RepositoryPath}', {EarlierPlace}: {unasked.TrimEnd('.')}";
                    continue;
                }

                foreach (var line in Kept(host.Host, place.RepositoryPath, place.Copies, earlier: true))
                {
                    yield return line;
                }
            }

            foreach (var missing in host.Missing)
            {
                yield return $"  {missing.Entry.Path}  recorded here, and not there: {Missing(missing, repositoryPath)}";
            }
        }

        foreach (var copy in copies.Undeclared)
        {
            yield return $"{copy.Host}: recorded as keeping '{copy.Path}', and no configuration here declares it, so it was not asked";
        }
    }

    /// <summary>What is said of where a host was asked about copies beside an earlier place, after the place.</summary>
    private const string EarlierPlace = "where this machine's record says it kept copies before";

    /// <summary>What is said of the copies of a tree that cannot be looked at: nothing reaches them while that is so.</summary>
    private const string Untouched = "nothing here removes or forgets its copies while that is so, since it may still be there, using them";

    /// <summary>
    /// The lines of what a host keeps beside one copy of the main checkout: where it keeps this machine's copies now, or an
    /// earlier place the record names, said so.
    /// </summary>
    private static IEnumerable<string> Kept(HostId host, string repositoryPath, IReadOnlyList<HostCopySeen> copies, bool earlier)
    {
        var place = earlier ? $"'{repositoryPath}', {EarlierPlace}" : $"'{repositoryPath}'";

        yield return copies.Count == 0
            ? $"{host} keeps no worktree copy beside {place}"
            : $"{host} keeps {copies.Count} worktree {(copies.Count == 1 ? "copy" : "copies")} beside {place}, "
                + $"{DiskSpace.Size(copies.Sum(copy => copy.Found.Bytes))} in all";

        foreach (var copy in copies)
        {
            yield return $"  {copy.Found.Path}  {DiskSpace.Size(copy.Found.Bytes)}  {Standing(copy)}";
        }
    }

    /// <summary>What a found copy is to this machine, and what removing it here would do with it.</summary>
    private static string Standing(HostCopySeen copy) => copy.Standing switch
    {
        CopyStanding.Listed => Noted($"worktree '{Shown(copy.Found.Name)}'", copy.Found),
        CopyStanding.Elsewhere => Noted($"the worktree at '{copy.Tree}'", copy.Found),
        CopyStanding.Gone => $"gone from '{copy.Tree}': '{CleanGone}' {WhatRemovingDoes(copy.Found)}",
        CopyStanding.Untold => Noted($"the worktree at '{copy.Tree}', which cannot be looked at here: {copy.Why.TrimEnd('.')}; {Untouched}", copy.Found),
        _ => $"not recorded here, so nothing here reaches it; {Origin(copy.Found)}{MadeFrom(copy)}",
    };

    /// <summary>
    /// Where a copy the record does not hold was made from, as its marker says: a tree here, whose copy this machine
    /// forgot, one that cannot be looked at here, with why, or one that is not here - gone from here, or another machine's;
    /// nothing where the marker names none.
    /// </summary>
    private static string MadeFrom(HostCopySeen copy) => copy.Tree switch
    {
        null => string.Empty,
        var tree when copy.TreeHere => $"; made from '{tree}', a tree on this machine",
        var tree when copy.Why.Length > 0 => $"; made from '{tree}', which cannot be looked at here: {copy.Why.TrimEnd('.')}",
        var tree => $"; made from '{tree}', which is not here: gone from this machine, or another machine's",
    };

    /// <summary>
    /// What forgets a copy recorded here that its host does not keep, by where its tree stands: a gone tree's is forgotten
    /// by the clean that removes the copies gone trees left; a tree here makes its copy again at its next sync, there where
    /// it was kept beside <paramref name="repositoryPath"/>, where the host keeps this machine's copies now, and beside that
    /// place where it was kept beside an earlier one, which then stays recorded until the tree's copies are dealt with; and a
    /// copy of a tree that cannot be looked at is forgotten by nothing while that is so.
    /// </summary>
    private static string Missing(HostCopyMissing missing, string repositoryPath)
    {
        var entry = missing.Entry;
        var now = string.Equals(entry.KeptBeside(), repositoryPath.TrimEnd('/', '\\'), StringComparison.Ordinal);

        return missing.Standing switch
        {
            CopyStanding.Gone => $"gone from '{entry.Tree}': '{CleanGone}' forgets it",
            CopyStanding.Untold => $"the worktree at '{entry.Tree}' cannot be looked at here: {missing.Why.TrimEnd('.')}; nothing here forgets it while that is so",
            CopyStanding.Listed when now => $"the next sync of worktree '{Shown(entry.Worktree)}' there makes it again",
            CopyStanding.Listed => $"the next sync of worktree '{Shown(entry.Worktree)}' makes its copy beside '{repositoryPath}', and deleting the worktree forgets this one",
            _ when now => $"the next sync of the worktree at '{entry.Tree}' there makes it again",
            _ => $"the next sync of the worktree at '{entry.Tree}' makes its copy beside '{repositoryPath}', and once that worktree is gone '{CleanGone}' forgets this one",
        };
    }

    /// <summary>
    /// What is said of a gone tree whose name a worktree here bears now: deleting that worktree would take the gone tree's
    /// copies with it, which the clean removes without it.
    /// </summary>
    private static string NameNow(RecordedTree tree)
        => tree.NameNowAt is { } at
            ? $"; '{Shown(tree.Name)}' is now the worktree at '{at}', whose deletion would take them with it - the clean never reaches it"
            : string.Empty;

    /// <summary><paramref name="said"/>, with what the copy's marker says where that is out of the ordinary.</summary>
    private static string Noted(string said, HostCopyFound copy)
        => copy.Origin == CopyOrigin.Made ? said : $"{said}; {Origin(copy)}";

    /// <summary>What removing a gone worktree's copy does with it, by what its marker says.</summary>
    private static string WhatRemovingDoes(HostCopyFound copy) => copy.Origin switch
    {
        CopyOrigin.Made => "removes it",
        CopyOrigin.Unreadable => $"leaves it recorded, as {Origin(copy)}",
        _ => $"forgets it and leaves it in place, as {Origin(copy)}",
    };

    /// <summary>
    /// What a copy's marker says of how it came to be: a host's copy of a worktree, as listed, or a mutation worker's,
    /// as a clean or a sweep that leaves it says.
    /// </summary>
    /// <param name="copy">The copy, as a listing found it.</param>
    internal static string Origin(HostCopyFound copy) => copy.Origin switch
    {
        CopyOrigin.Made => copy.CreatedUtc is { } at ? $"made by the harness at {at}" : "made by the harness",
        CopyOrigin.TakenOver => "the harness took over a directory that was there, which is yours to remove",
        CopyOrigin.Unmarked => "nothing there says the harness made it, so it is yours to remove",
        _ => $"its marker cannot be read: {copy.Problem?.TrimEnd('.')}",
    };

    /// <summary>
    /// What is said of a directory under the name of a copy of a family - a mutation worker, a leg copy - that nothing
    /// says the harness made, once the tree it was kept in is gone: kept in that tree, it was the tree's, and went with it
    /// as everything else the tree held.
    /// </summary>
    /// <param name="path">Where it was.</param>
    /// <param name="member">What a line calls one copy of its family, in the singular: <c>leg copy</c>, <c>mutation worker</c>.</param>
    internal static string WentWithItsTree(string path, string member) => $"'{path}', named as a {member} though nothing says the harness made it, went with it";

    private static JsonObject Document(IReadOnlyList<WorktreeListing> worktrees, HostCopyListing copies)
    {
        var document = new JsonObject
        {
            ["worktrees"] = new JsonArray([.. worktrees.Select(worktree => (JsonNode)new JsonObject
            {
                ["name"] = worktree.Name,
                ["kind"] = worktree.Kind,
                ["baseCommit"] = worktree.BaseCommit,
                ["path"] = worktree.Path,
                ["copies"] = Copies(copies.OfListed.GetValueOrDefault(worktree.Name) ?? []),
            })]),
            ["elsewhere"] = Trees(copies.Elsewhere, remedy: false),
            ["gone"] = Trees(copies.Gone, remedy: true),
            ["untold"] = Trees(copies.Untold, remedy: false),
        };

        if (copies.Hosts is { } hosts)
        {
            document["hosts"] = new JsonArray([.. hosts.Select(host => (JsonNode)Host(host))]);
            document["undeclared"] = new JsonArray([.. copies.Undeclared.Select(copy => (JsonNode)new JsonObject
            {
                ["host"] = copy.Host,
                ["name"] = Shown(copy.Worktree),
                ["path"] = copy.Path,
                ["tree"] = copy.Tree,
            })]);
        }

        if (copies.Unreadable is { } unreadable)
        {
            document["recordUnreadable"] = unreadable;
        }

        return document;
    }

    private static JsonArray Copies(IReadOnlyList<HostCopyEntry> copies)
        => new([.. copies.Select(copy => (JsonNode)new JsonObject
        {
            ["host"] = copy.Host,
            ["path"] = copy.Path,
        })]);

    private static JsonArray Trees(IReadOnlyList<RecordedTree> trees, bool remedy)
        => new([.. trees.Select(tree =>
        {
            var node = new JsonObject
            {
                ["name"] = Shown(tree.Name),
                ["tree"] = tree.Tree,
                ["copies"] = Copies(tree.Copies),
            };

            if (tree.Why.Length > 0)
            {
                node["why"] = tree.Why;
            }

            if (remedy)
            {
                node["deletedBy"] = CleanGone;

                if (tree.NameNowAt is { } at)
                {
                    node["nameNowAt"] = at;
                }
            }

            return (JsonNode)node;
        })]);

    private static JsonObject Host(HostCopiesAnswer host)
    {
        var node = new JsonObject
        {
            ["host"] = host.Host.ToString(),
            ["repositoryPath"] = host.RepositoryPath,
        };

        if (host.Unasked is { } why)
        {
            node["unasked"] = why;
            return node;
        }

        node["copies"] = Seen(host.Copies);
        node["earlier"] = new JsonArray([.. host.Earlier.Select(place => (JsonNode)(place.Unasked is { } unasked
            ? new JsonObject { ["repositoryPath"] = place.RepositoryPath, ["unasked"] = unasked }
            : new JsonObject { ["repositoryPath"] = place.RepositoryPath, ["copies"] = Seen(place.Copies) }))]);
        node["missing"] = new JsonArray([.. host.Missing.Select(missing => (JsonNode)new JsonObject
        {
            ["name"] = Shown(missing.Entry.Worktree),
            ["path"] = missing.Entry.Path,
            ["tree"] = missing.Entry.Tree,
            ["standing"] = JsonNamingPolicy.CamelCase.ConvertName(missing.Standing.ToString()),
            ["why"] = missing.Why.Length > 0 ? missing.Why : null,
        })]);

        return node;
    }

    /// <summary>The copies a host keeps beside one place, as the document lists them.</summary>
    private static JsonArray Seen(IReadOnlyList<HostCopySeen> copies)
        => new([.. copies.Select(copy => (JsonNode)new JsonObject
        {
            ["name"] = Shown(copy.Found.Name),
            ["path"] = copy.Found.Path,
            ["bytes"] = copy.Found.Bytes,
            ["origin"] = JsonNamingPolicy.CamelCase.ConvertName(copy.Found.Origin.ToString()),
            ["createdBy"] = copy.Found.CreatedBy,
            ["createdUtc"] = copy.Found.CreatedUtc,
            ["problem"] = copy.Found.Problem,
            ["standing"] = JsonNamingPolicy.CamelCase.ConvertName(copy.Standing.ToString()),
            ["tree"] = copy.Tree,
            ["treeHere"] = copy.Standing == CopyStanding.Unrecorded && copy.Tree is not null ? (bool?)copy.TreeHere : null,
            ["why"] = copy.Why.Length > 0 ? copy.Why : null,
        })]);
}
