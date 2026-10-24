using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// A process a test learned of by the id it was told - a line a child wrote, a record - with its start, read as it was
/// learned (<see cref="ProcessIdentity.StampOf"/>), so that what the test asks of it, waits for or ends is that very
/// process: an id is given to the next process as soon as its holder ends, and the suite starts its processes side by
/// side, so a leftover ended by its id alone, once it had gone, could take another test's process with it.
/// </summary>
/// <param name="Id">The id it was told.</param>
/// <param name="Stamp">Its start, or <see langword="null"/> where it had gone before it was learned.</param>
internal readonly record struct LearnedProcess(int Id, string? Stamp)
{
    private static readonly ProcessIdentity Identity = new(new HostPlatform());

    /// <summary>No process: what a test holds before it learns of one.</summary>
    public static LearnedProcess None => default;

    /// <summary>The process <paramref name="id"/> names, its start read now.</summary>
    public static LearnedProcess Of(int id) => new(id, Identity.StampOf(id));

    /// <summary>
    /// The process named by the number after <paramref name="name"/> where <paramref name="line"/> is that and a number, its
    /// start read now; else <see langword="null"/>.
    /// </summary>
    public static LearnedProcess? In(string line, string name)
        => line.StartsWith(name, StringComparison.Ordinal) && int.TryParse(line[name.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? Of(id)
            : null;

    /// <summary>Whether this very process runs now - never one given its id since.</summary>
    public bool Running
    {
        get
        {
            if (Stamp is not { } stamp)
            {
                return false;
            }

            try
            {
                using var found = Process.GetProcessById(Id);
                return !found.HasExited && Identity.IsSame(Id, stamp);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Whether it is gone, looking for as long as a container or a session takes to end it.</summary>
    public bool AwaitGone()
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(10); waited += TimeSpan.FromMilliseconds(100))
        {
            if (!Running)
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    /// <summary>Ends it, taking its tree with it, where it still runs - never a process given its id since.</summary>
    public void TryEnding()
    {
        if (Stamp is not { } stamp)
        {
            return;
        }

        try
        {
            using var found = Process.GetProcessById(Id);

            // Held open, a process's id passes to no other on Windows, so the one told apart is the one ended.
            if (OperatingSystem.IsWindows())
            {
                _ = found.SafeHandle;
            }

            if (Identity.IsSame(Id, stamp))
            {
                found.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone, or going.
        }
    }

    /// <summary>Its id, as a line names it.</summary>
    public override string ToString() => Id.ToString(CultureInfo.InvariantCulture);
}
