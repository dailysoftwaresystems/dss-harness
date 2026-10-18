using System.ComponentModel;
using System.Runtime.InteropServices;
using DssHarness.Core.Results;

namespace DssHarness.Core.Processes;

/// <summary>
/// The step in starting a process that puts it where it is contained first: the tool runs itself as
/// <c>contain-exec</c>, which joins the container asked for and then becomes, or starts, the program -
/// so no child is ever outside its container for an instant, which a child put in after its start
/// would be.
/// </summary>
/// <remarks>
/// <para>
/// Not a command anyone uses: nobody types it, help does not list it, and no host is ever asked to
/// run it. The process runner starts it, as the one way it has of placing a contained child, and
/// reads the child through it as through any other: on Linux and macOS the process becomes the
/// program under the same id; on Windows it hands the streams it was given on to the program, waits,
/// and ends with the program's own code.
/// </para>
/// <para>
/// On Linux and macOS full containment is a session and group of one's own (<c>setsid</c>); on
/// Windows it is the job object the engine made and named. The group alone - apart from the
/// terminal, within the session - is the lesser level, asked for by name.
/// </para>
/// </remarks>
public static class ContainExec
{
    /// <summary>The step's name, as the process runner starts it by.</summary>
    public const string CommandName = "contain-exec";

    /// <summary>The option naming the Windows job object to join.</summary>
    public const string JobOption = "--job";

    /// <summary>The option asking for a process group of one's own, apart from the terminal.</summary>
    public const string GroupOption = "--group";

    /// <summary>What separates the step's own options from the program and its arguments.</summary>
    public const string Separator = "--";

    /// <summary>The code the step's own failures end with; a line on the error stream names which failure it was.</summary>
    public const int Failed = HarnessExit.InternalError;

    /// <summary>
    /// Runs the step in this process: puts it where it is asked to be contained, then becomes, or
    /// starts and waits for, <paramref name="program"/>.
    /// </summary>
    /// <param name="job">The name of the Windows job object to join, for full containment there.</param>
    /// <param name="group">Whether a process group of one's own is the containment asked for, apart from the terminal alone.</param>
    /// <param name="program">The program, by its resolved path.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <param name="error">Where a failure of the step itself is said.</param>
    /// <returns>The program's own exit code, or <see cref="Failed"/> where the step failed.</returns>
    public static int Run(string? job, bool group, string program, IReadOnlyList<string> arguments, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(error);

        if (job is not null && group)
        {
            error.WriteLine($"contain-exec was asked for both '{JobOption}' and '{GroupOption}'; it takes one.");
            return Failed;
        }

        if (job is { Length: 0 })
        {
            error.WriteLine($"contain-exec was asked for a job with no name; '{JobOption}' takes the name the process runner gave it.");
            return Failed;
        }

        if (job is null && !group && OperatingSystem.IsWindows())
        {
            // Full containment on Windows is the job object, which the engine that starts this
            // step makes and names; a group alone is asked for by name.
            error.WriteLine($"contain-exec was asked for full containment with no '{JobOption}': on Windows that is a job object, named by the process runner that made it.");
            return Failed;
        }

        if (OperatingSystem.IsWindows())
        {
            return RunOnWindows(job, group, program, arguments, error);
        }

        if (group ? !Containment.JoinOwnGroup() : !Containment.StartSession())
        {
            var reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            error.WriteLine($"contain-exec could not {What(group)}: {reason}");
            return Failed;
        }

        // Of a success this process is the program, and there is no return; the number that comes
        // back is why it could not become it.
        _ = Containment.Become(program, arguments);
        error.WriteLine($"contain-exec could not become '{program}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        return Failed;
    }

    /// <summary>The Windows half: join the job where one is named, start the program in a group of its own, wait.</summary>
    private static int RunOnWindows(string? job, bool group, string program, IReadOnlyList<string> arguments, TextWriter error)
    {
        if (job is { } named)
        {
            if (!Containment.JoinJob(named, out var why))
            {
                error.WriteLine($"contain-exec could not join the job '{named}': {new Win32Exception(why).Message}");
                return Failed;
            }
        }

        try
        {
            using var process = Containment.StartInOwnGroup(program, arguments);

            return Containment.Wait(process);
        }
        catch (Win32Exception ex)
        {
            error.WriteLine($"contain-exec could not start '{program}': {ex.Message}");
            return Failed;
        }
    }

    /// <summary>What the step was asked to make of itself, as a failure names it.</summary>
    private static string What(bool group)
        => group ? "join a group of its own" : "start a session of its own";
}
