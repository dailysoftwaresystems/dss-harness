using System.Text.Json;
using System.Text.Json.Nodes;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Worktrees;

/// <summary>What <c>list-worktree</c> reports: the worktrees, and the copies hosts keep of them and of those that are gone.</summary>
public static class WorktreeReports
{
    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>The command that deletes a worktree, and deals with the copies left under its name.</summary>
    private static string Delete(string name) => $"{ToolPackage.Command} {WorktreeService.DeleteCommand} {Shown(name)}";

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

    private static string Summary(IReadOnlyList<WorktreeListing> worktrees, HostCopyListing copies)
    {
        var said = worktrees.Count == 0 ? "no worktrees" : $"{worktrees.Count} worktree(s)";

        if (copies.Unreadable is { } unreadable)
        {
            return $"{said}; the copies hosts keep of them cannot be listed: {unreadable}";
        }

        if (copies.Gone.Count > 0)
        {
            said += $"; {copies.Gone.Count} worktree(s) that are gone left copies on hosts, which '{Delete("<name>")}' deals with";
        }

        if (copies.Hosts is { } hosts)
        {
            var unasked = hosts.Count(host => host.Unasked is not null);

            said += hosts.Count == 0 ? "; no host is declared, so none was asked"
                : unasked == 0 ? $"; asked {hosts.Count} host(s)"
                : $"; {unasked} of {hosts.Count} host(s) could not be asked";
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
            yield return $"{Shown(tree.Name)}, gone from '{tree.Tree}': '{Delete(tree.Name)}' deals with the copies it left";

            foreach (var copy in tree.Copies)
            {
                yield return $"  {copy.Host}: {copy.Path}";
            }
        }

        foreach (var host in copies.Hosts ?? [])
        {
            if (host.Unasked is { } why)
            {
                yield return $"{host.Host}: could not be asked about the copies beside '{host.RepositoryPath}': {why.TrimEnd('.')}";
                continue;
            }

            yield return host.Copies.Count == 0
                ? $"{host.Host} keeps no worktree copy beside '{host.RepositoryPath}'"
                : $"{host.Host} keeps {host.Copies.Count} worktree {(host.Copies.Count == 1 ? "copy" : "copies")} beside "
                    + $"'{host.RepositoryPath}', {DiskSpace.Size(host.Copies.Sum(copy => copy.Found.Bytes))} in all";

            foreach (var copy in host.Copies)
            {
                yield return $"  {copy.Found.Path}  {DiskSpace.Size(copy.Found.Bytes)}  {Standing(copy)}";
            }

            foreach (var missing in host.Missing)
            {
                yield return $"  {missing.Path}  recorded here, and not there: '{Delete(missing.Worktree)}' forgets it";
            }
        }

        foreach (var copy in copies.Undeclared)
        {
            yield return $"{copy.Host}: recorded as keeping '{copy.Path}', and no configuration here declares it, so it was not asked";
        }
    }

    /// <summary>What a found copy is to this machine, and what deleting its name would do with it.</summary>
    private static string Standing(HostCopySeen copy) => copy.Standing switch
    {
        CopyStanding.Listed => Noted($"worktree '{Shown(copy.Found.Name)}'", copy.Found),
        CopyStanding.Elsewhere => Noted($"the worktree at '{copy.Tree}'", copy.Found),
        CopyStanding.Gone => $"gone from '{copy.Tree}': '{Delete(copy.Found.Name)}' {WhatDeletingDoes(copy.Found)}",
        _ => $"not recorded here, so deleting a worktree here never reaches it; {Origin(copy.Found)}",
    };

    /// <summary><paramref name="said"/>, with what the copy's marker says where that is out of the ordinary.</summary>
    private static string Noted(string said, HostCopyFound copy)
        => copy.Origin == CopyOrigin.Made ? said : $"{said}; {Origin(copy)}";

    /// <summary>What deleting the name a gone worktree's copy is kept under does with it, by what its marker says.</summary>
    private static string WhatDeletingDoes(HostCopyFound copy) => copy.Origin switch
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

    private static JsonObject Document(IReadOnlyList<WorktreeListing> worktrees, HostCopyListing copies)
    {
        var document = new JsonObject
        {
            ["worktrees"] = new JsonArray([.. worktrees.Select(worktree => (JsonNode)new JsonObject
            {
                ["name"] = worktree.Name,
                ["baseCommit"] = worktree.BaseCommit,
                ["path"] = worktree.Path,
                ["copies"] = Copies(copies.OfListed.GetValueOrDefault(worktree.Name) ?? []),
            })]),
            ["elsewhere"] = Trees(copies.Elsewhere, remedy: false),
            ["gone"] = Trees(copies.Gone, remedy: true),
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

            if (remedy)
            {
                node["deletedBy"] = Delete(tree.Name);
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

        node["copies"] = new JsonArray([.. host.Copies.Select(copy => (JsonNode)new JsonObject
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
        })]);
        node["missing"] = new JsonArray([.. host.Missing.Select(missing => (JsonNode)new JsonObject
        {
            ["name"] = Shown(missing.Worktree),
            ["path"] = missing.Path,
            ["tree"] = missing.Tree,
        })]);

        return node;
    }
}
