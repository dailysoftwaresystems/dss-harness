using System.Collections;
using System.Runtime.Versioning;
using Microsoft.Mxc.Sdk.V1;

namespace DssHarness.Core.Processes;

/// <summary>
/// The step that starts a program inside MXC's process container (<see cref="MxcProcessContainer"/>): the contained step
/// starts this tool as <c>isolate-exec</c> in the child's place, inside the step's job, and this starts the program inside
/// a container of its own - its places as the step was handed them, its working directory this process's own, its
/// environment this process's own with the profile's variables set over it - hands it this process's input, hands its output and errors back as they come, and ends with its exit code.
/// </summary>
/// <remarks>
/// Not a command anyone uses: nobody types it, help does not list it, and no host is ever asked to run it. Where the
/// container cannot be made the step says why on its error stream and ends with <see cref="ContainExec.Failed"/>, never
/// with a code the program could end with.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class IsolateExec
{
    /// <summary>
    /// Starts <paramref name="program"/> inside a container over <paramref name="places"/>, and waits for it.
    /// </summary>
    /// <param name="places">The profile's places.</param>
    /// <param name="program">The program, by its whole path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="variables">The profile's variables, set over this process's environment in the container's.</param>
    /// <param name="error">Where a failure of the step's own is said.</param>
    /// <param name="cancellationToken">Stops the wait; the job the step is in ends the program.</param>
    /// <returns>The program's own exit code, or <see cref="ContainExec.Failed"/> where no container was made.</returns>
    public static async Task<int> RunAsync(ContainedPlaces places, string program, IReadOnlyList<string> arguments, IReadOnlyList<KeyValuePair<string, string>> variables, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(error);

        if (!Path.IsPathFullyQualified(program))
        {
            error.WriteLine($"{MxcProcessContainer.CommandName} was given '{program}', which is no whole path; the process runner names every program by its whole path.");
            return ContainExec.Failed;
        }

        MxcProcess process;

        try
        {
            var request = MxcProcessContainer.Request(places, program, arguments, Environment.CurrentDirectory, Variables(variables), Drives(), Entries);
            process = await MxcContainer.SpawnAsync(request, new SpawnOptions { Telemetry = new TelemetryConfig { Enabled = false } }, cancellationToken).ConfigureAwait(false);
        }
        catch (MxcException ex)
        {
            error.WriteLine($"{MxcProcessContainer.CommandName} could not start '{program}' inside MXC's process container: {ex.Message.TrimEnd('.')} ({ex.Code}).");
            return ContainExec.Failed;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            error.WriteLine($"{MxcProcessContainer.CommandName} could not load MXC's library to start '{program}': {ex.Message}");
            return ContainExec.Failed;
        }

        using (process)
        {
            // Input is handed on as it comes and closed at its end; the program's end does not wait for it.
            if (process.StandardInput is { } input)
            {
                _ = HandOnAsync(Console.OpenStandardInput(), input, close: true);
            }

            var output = process.StandardOutput is { } read ? HandOnAsync(read, Console.OpenStandardOutput(), close: false) : Task.CompletedTask;
            var errors = process.StandardError is { } failed ? HandOnAsync(failed, Console.OpenStandardError(), close: false) : Task.CompletedTask;
            var ended = await process.WaitAsync(cancellationToken).ConfigureAwait(false);

            await Task.WhenAll(output, errors).ConfigureAwait(false);

            return ended.ExitCode;
        }
    }

    /// <summary>Copies <paramref name="from"/> to <paramref name="to"/> to its end, flushing as it goes; an end of either side ends the copy.</summary>
    private static async Task HandOnAsync(Stream from, Stream to, bool close)
    {
        var buffer = new byte[4096];

        try
        {
            int read;

            while ((read = await from.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                await to.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The other side has gone: the program ended, or nothing reads what it writes.
        }
        catch (ObjectDisposedException)
        {
            // As above.
        }
        finally
        {
            if (close)
            {
                await to.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>This process's environment, whole - the child's, as the step was given it - with <paramref name="set"/> set over it.</summary>
    private static Dictionary<string, string> Variables(IReadOnlyList<KeyValuePair<string, string>> set)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                variables[name] = value;
            }
        }

        foreach (var (name, value) in set)
        {
            variables[name] = value;
        }

        return variables;
    }

    /// <summary>The root of every fixed drive of this machine, the system's among them.</summary>
    private static IReadOnlyList<string> Drives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);

        return [.. DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed)
            .Select(drive => drive.RootDirectory.FullName)
            .Prepend(system ?? string.Empty)
            .Where(root => root.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every entry <paramref name="directory"/> holds, by its whole path; none where it cannot be listed.</summary>
    private static IEnumerable<string> Entries(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? [.. Directory.EnumerateFileSystemEntries(directory)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
