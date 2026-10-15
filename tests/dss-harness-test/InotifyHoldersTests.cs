using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// Who holds this user's inotify instances and watches, read from a <c>/proc</c> made for the test - a directory whose
/// descriptors are files holding what each would be a link to, so it reads the same on every system - and the
/// sentence a watch that cannot start says with it.
/// </summary>
public sealed class InotifyHoldersTests
{
    private const string Me = "1000";

    /// <summary>
    /// The instances' sentence names the limit, how many /proc shows held, and this user's processes holding them, the
    /// most first, three named and the rest counted - each by its pid and its name.
    /// </summary>
    [Fact]
    public void InstancesAllTaken_SaysTheLimit_AndThisUsersHolders_TheMostFirst_ThreeNamed()
    {
        using var proc = Proc();
        Process(proc, 4242, "dssharness", instances: [2, 0, 1]);
        Process(proc, 5151, "node", instances: [0, 0, 0, 0]);
        Process(proc, 77, "dotnet", instances: [5]);
        Process(proc, 12, "code", instances: [1]);
        Process(proc, 13, "watchman", instances: [0]);

        var refused = InputsUnwatchedException.InstancesTaken(Holders(proc).Read());

        Assert.Equal(
            "the inputs could not be watched, so none of this work was run: this user's inotify instances are all taken - "
            + "fs.inotify.max_user_instances is 128, and /proc shows 10 held: 4 by pid 5151 (node), 3 by pid 4242 "
            + "(dssharness), 1 by pid 12 (code) and 2 more. This process needs one, however many legs it runs.",
            refused.Message);
    }

    /// <summary>
    /// The watches' sentence names the limit, how many directories the work needs, and who holds the watches, counted
    /// from each instance's lines - a process holding instances with no watch is not among them.
    /// </summary>
    [Fact]
    public void WatchesRanOut_SaysTheLimit_WhatTheWorkNeeds_AndWhoHoldsTheWatches()
    {
        using var proc = Proc();
        Process(proc, 99, "node", instances: [5000, 800]);
        Process(proc, 4242, "dssharness", instances: [211]);
        Process(proc, 7, "idle", instances: [0]);

        var refused = InputsUnwatchedException.WatchesRanOut(Holders(proc).Read(), 3120);

        Assert.Equal(
            "the inputs could not be watched, so none of this work was run: this user's inotify watches ran out - "
            + "fs.inotify.max_user_watches is 8192; this work watches 3120 directory(ies) - those its inputs lie in and those above them - one watch each, and "
            + "/proc shows 6011 held: 5800 by pid 99 (node), 211 by pid 4242 (dssharness).",
            refused.Message);
    }

    /// <summary>
    /// What cannot be read is passed over: a process whose descriptors cannot be listed, a descriptor whose link
    /// cannot be read, a process with no name, another user's process, and every entry that is no process.
    /// </summary>
    [Fact]
    public void WhatCannotBeRead_IsPassedOver_AndAnotherUsersProcessIsNotCounted()
    {
        using var proc = Proc();
        Process(proc, 1, "fine", instances: [1]);
        Process(proc, 2, "theirs", instances: [1, 1, 1], user: "0");
        Process(proc, 3, null, instances: [1]);
        Process(proc, 4, "unlisted", instances: [1]);
        Directory.Delete(proc.Combine("4", "fd"), recursive: true);
        Process(proc, 5, "unreadable", instances: [1]);
        File.Delete(proc.Combine("5", "fd", "3"));
        Directory.CreateDirectory(proc.Combine("5", "fd", "3"));
        proc.WriteFile("net/unix", "not a process");

        var use = Holders(proc).Read();

        Assert.Equal([new InotifyHolder(1, "fine", 1, 1), new InotifyHolder(3, null, 1, 1)], use.Holders!.OrderBy(holder => holder.ProcessId));
        Assert.Equal("2 held: 1 by pid 1 (fine), 1 by pid 3", use.Held(holder => holder.Instances));
    }

    /// <summary>Where this process's own user cannot be read, every process that can be seen is counted.</summary>
    [Fact]
    public void WhereThisProcesssOwnUserCannotBeRead_EveryProcessSeenIsCounted()
    {
        using var proc = Proc();
        File.Delete(proc.Combine("self", "status"));
        Process(proc, 1, "mine", instances: [1]);
        Process(proc, 2, "theirs", instances: [1], user: "0");

        Assert.Equal(2, Holders(proc).Read().Holders!.Count);
    }

    /// <summary>
    /// A /proc that shows nobody holding any ends the sentence at the limit; one that is not there at all ends it before
    /// - nothing is said that could not be read.
    /// </summary>
    [Fact]
    public void WithNobodySeenHolding_TheSentenceEndsAtTheLimit_AndWithNoProcBeforeIt()
    {
        using var proc = Proc();

        Assert.Equal(
            "this user's inotify instances are all taken - fs.inotify.max_user_instances is 128. This process needs one, however many legs it runs.",
            InputsUnwatchedException.InstancesTaken(Holders(proc).Read()).Why);

        var none = new InotifyHolders(proc.Combine("gone"), File.ReadAllText).Read();

        Assert.Equal(new InotifyUse(null, null, null), none);
        Assert.Equal("this user's inotify instances are all taken. This process needs one, however many legs it runs.", InputsUnwatchedException.InstancesTaken(none).Why);
        Assert.Equal(
            "this user's inotify watches ran out; this work watches 1 directory(ies) - those its inputs lie in and those above them - one watch each.",
            InputsUnwatchedException.WatchesRanOut(none, 1).Why);
    }

    /// <summary>A /proc whose links are files holding what each would point at.</summary>
    private static InotifyHolders Holders(TempDirectory proc) => new(proc.Path, ReadLink);

    private static string? ReadLink(string path)
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

    /// <summary>A /proc with the usual limits, and this process running as <see cref="Me"/>.</summary>
    private static TempDirectory Proc()
    {
        var proc = new TempDirectory();
        proc.WriteFile("sys/fs/inotify/max_user_instances", "128\n");
        proc.WriteFile("sys/fs/inotify/max_user_watches", "8192\n");
        proc.WriteFile("sys/fs/inotify/max_queued_events", "16384\n");
        proc.WriteFile("self/status", Status(Me));

        return proc;
    }

    /// <summary>
    /// A process with an instance for each of <paramref name="instances"/>, holding that many watches, beside a socket
    /// and a file that are no instances.
    /// </summary>
    private static void Process(TempDirectory proc, int id, string? name, int[] instances, string user = Me)
    {
        var at = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        proc.WriteFile($"{at}/status", Status(user));

        if (name is not null)
        {
            proc.WriteFile($"{at}/comm", name + "\n");
        }

        proc.WriteFile($"{at}/fd/0", "/dev/null");
        proc.WriteFile($"{at}/fd/1", "socket:[4026]");
        proc.WriteFile($"{at}/fdinfo/1", "pos:\t0\nflags:\t02\n");

        for (var index = 0; index < instances.Length; index++)
        {
            var descriptor = (index + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var watches = Enumerable.Range(1, instances[index])
                .Select(watch => $"inotify wd:{watch} ino:{watch + 100:x} sdev:800021 mask:fc6 ignored_mask:0 fhandle-bytes:8\n");

            proc.WriteFile($"{at}/fd/{descriptor}", InotifyHolders.InstanceLink);
            proc.WriteFile($"{at}/fdinfo/{descriptor}", "pos:\t0\nflags:\t02000000\nmnt_id:\t15\n" + string.Concat(watches));
        }
    }

    private static string Status(string user) => $"Name:\tx\nUmask:\t0022\nState:\tS (sleeping)\nUid:\t{user}\t{user}\t{user}\t{user}\nGid:\t1000\t1000\t1000\t1000\n";
}
