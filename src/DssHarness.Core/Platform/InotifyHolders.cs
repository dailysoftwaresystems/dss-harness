using System.Globalization;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Platform;

/// <summary>One of this user's processes that holds inotify instances, as <c>/proc</c> shows it.</summary>
/// <param name="ProcessId">Its id.</param>
/// <param name="Name">Its program's name, or <see langword="null"/> where it could not be read.</param>
/// <param name="Instances">How many instances it holds.</param>
/// <param name="Watches">How many watches those instances hold, together.</param>
internal sealed record InotifyHolder(int ProcessId, string? Name, int Instances, int Watches);

/// <summary>What this user may hold of inotify, and who holds what, as <c>/proc</c> shows it when it is asked.</summary>
/// <param name="MaxInstances"><c>fs.inotify.max_user_instances</c>, or <see langword="null"/> where it could not be read.</param>
/// <param name="MaxWatches"><c>fs.inotify.max_user_watches</c>, or <see langword="null"/> where it could not be read.</param>
/// <param name="Holders">Each process holding any, or <see langword="null"/> where <c>/proc</c> could not be read.</param>
internal sealed record InotifyUse(long? MaxInstances, long? MaxWatches, IReadOnlyList<InotifyHolder>? Holders)
{
    /// <summary>
    /// <c>N held: 51 by pid 4242 (dssharness), ...</c> - how many of what <paramref name="held"/> counts the holders
    /// hold together, and the holders that hold any, the most first, named as a one-line message names a list; or
    /// <see langword="null"/> where <c>/proc</c> could not be read, or shows none held.
    /// </summary>
    /// <param name="held">What of a holder is counted: its instances, or its watches.</param>
    public string? Held(Func<InotifyHolder, int> held)
    {
        ArgumentNullException.ThrowIfNull(held);

        var holding = (Holders ?? [])
            .Where(holder => held(holder) > 0)
            .OrderByDescending(held)
            .ThenBy(holder => holder.ProcessId)
            .ToList();

        if (holding.Count == 0)
        {
            return null;
        }

        var total = holding.Sum(holder => (long)held(holder));
        var named = holding
            .Select(holder => $"{held(holder).ToString(CultureInfo.InvariantCulture)} by pid {holder.ProcessId.ToString(CultureInfo.InvariantCulture)}"
                + (holder.Name is { } name ? $" ({name})" : string.Empty))
            .ToList();

        return $"{total.ToString(CultureInfo.InvariantCulture)} held: {ReportText.Listed(named)}";
    }
}

/// <summary>
/// Reads, from <c>/proc</c>, what this user may hold of inotify and who holds it: the limits from
/// <c>/proc/sys/fs/inotify</c>; each of this user's processes' instances from the links in its <c>fd</c> directory
/// that point at <c>anon_inode:inotify</c>, the watches of each from the <c>inotify wd:</c> lines of its entry in
/// <c>fdinfo</c>, and its name from <c>comm</c>.
/// </summary>
/// <remarks>
/// Read only when a limit is met, to say who is holding it. Whatever cannot be read - a process gone between the
/// listing and the read, another user's entries - is passed over: what is said is what could be seen.
/// </remarks>
/// <param name="proc">Where the kernel publishes it.</param>
/// <param name="linkTarget">What a link points at, or <see langword="null"/> where it could not be read.</param>
internal sealed class InotifyHolders(string proc, Func<string, string?> linkTarget)
{
    /// <summary>What an instance's descriptor is a link to.</summary>
    public const string InstanceLink = "anon_inode:inotify";

    /// <summary>What begins each line of an instance's <c>fdinfo</c> entry that is one watch.</summary>
    public const string WatchLine = "inotify wd:";

    private readonly string _proc = proc;
    private readonly Func<string, string?> _linkTarget = linkTarget;

    /// <summary>This machine's own <c>/proc</c>.</summary>
    public InotifyHolders()
        : this("/proc", LinkTarget)
    {
    }

    /// <summary>What <c>/proc</c> shows now.</summary>
    public InotifyUse Read()
    {
        var limits = Path.Combine(_proc, "sys", "fs", "inotify");

        return new InotifyUse(
            Number(Text(Path.Combine(limits, "max_user_instances"))),
            Number(Text(Path.Combine(limits, "max_user_watches"))),
            Holders());
    }

    /// <summary>Each of this user's processes that holds an instance; <see langword="null"/> where <c>/proc</c> could not be listed.</summary>
    private List<InotifyHolder>? Holders()
    {
        IEnumerable<string> processes;

        try
        {
            processes = Directory.EnumerateDirectories(_proc).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // A user's limits are counted by the user a process runs as; where this process's own cannot be read, every
        // process that can be seen is counted, which on a machine of one user is the same.
        var user = User(Path.Combine(_proc, "self"));
        var holders = new List<InotifyHolder>();

        foreach (var process in processes)
        {
            if (!int.TryParse(Path.GetFileName(process), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || (user is not null && User(process) != user))
            {
                continue;
            }

            if (Holding(process, id) is { } holder)
            {
                holders.Add(holder);
            }
        }

        return holders;
    }

    /// <summary>What <paramref name="process"/> holds, or <see langword="null"/> where it holds no instance or could not be read.</summary>
    private InotifyHolder? Holding(string process, int id)
    {
        var instances = 0;
        var watches = 0;

        try
        {
            foreach (var descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(process, "fd")))
            {
                if (_linkTarget(descriptor) != InstanceLink)
                {
                    continue;
                }

                instances++;
                watches += Text(Path.Combine(process, "fdinfo", Path.GetFileName(descriptor)))?
                    .Split('\n')
                    .Count(line => line.StartsWith(WatchLine, StringComparison.Ordinal)) ?? 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return instances == 0 ? null : new InotifyHolder(id, Text(Path.Combine(process, "comm"))?.Trim(), instances, watches);
    }

    /// <summary>The user <paramref name="process"/> runs as - the effective one, which its limits are counted by - or <see langword="null"/>.</summary>
    private static string? User(string process)
        => Text(Path.Combine(process, "status"))?
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal))?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ElementAtOrDefault(2);

    private static string? Text(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? Number(string? text)
        => long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
