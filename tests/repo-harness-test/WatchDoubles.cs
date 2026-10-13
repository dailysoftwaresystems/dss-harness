using System.Collections.Concurrent;
using System.Diagnostics;
using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// A kind of watch that watches nothing. It keeps what it was asked to watch and whom to tell, so a test tells the watch
/// what a platform would, at the moment it chooses - and it refuses to start where the test says so, as a machine that
/// cannot watch does.
/// </summary>
/// <param name="refusal">What starting raises, or nothing for a kind that starts.</param>
/// <param name="mostDirectories">Its bound on directories: .NET's watcher's unless a test says otherwise.</param>
internal sealed class ScriptedWatchKind(InputsUnwatchedException? refusal = null, int? mostDirectories = FileSystemWatcherKind.MostDirectoriesWatched) : IWatchKind
{
    /// <summary>What a machine that will not watch says, in a system's own words.</summary>
    public const string SystemsWords = "The configured user limit (128) on the number of inotify instances has been reached.";

    /// <summary>A kind that refuses every start as a machine out of what watching takes does.</summary>
    public static ScriptedWatchKind Refusing() => new(InputsUnwatchedException.BySystem(new IOException(SystemsWords)));

    /// <summary>What a span refused by <see cref="Refusing"/> says, whole.</summary>
    public static string Refused => InputsUnwatchedException.Opening + SystemsWords;

    /// <inheritdoc/>
    public int? MostDirectories => mostDirectories;

    /// <summary>The directories the last start was asked to watch, as it was asked.</summary>
    public IReadOnlyList<(string Directory, bool Recursive)>? Targets { get; private set; }

    /// <summary>Whom the last start was to tell.</summary>
    public IWatchSink? Sink { get; private set; }

    /// <summary>How many times it was asked to start.</summary>
    public int Starts { get; private set; }

    /// <summary>How many of the watches it started were stopped.</summary>
    public int Stops { get; private set; }

    public IDisposable Start(IReadOnlyList<(string Directory, bool Recursive)> targets, IWatchSink sink)
    {
        Starts++;

        if (refusal is not null)
        {
            throw refusal;
        }

        Targets = targets;
        Sink = sink;

        return new Stop(this);
    }

    private sealed class Stop(ScriptedWatchKind kind) : IDisposable
    {
        public void Dispose() => kind.Stops++;
    }
}

/// <summary>Keeps what a kind of watch tells, in the order it was told, for a test of a real one to wait on.</summary>
internal sealed class RecordingWatchSink : IWatchSink
{
    /// <summary>How <see cref="Heard"/> says an entry was written to.</summary>
    public const string WasWritten = "written";

    /// <summary>How <see cref="Heard"/> says an entry arrived, went or was renamed.</summary>
    public const string WasNamed = "named";

    /// <summary>How <see cref="Heard"/> says the watch was lost.</summary>
    public const string WasLost = "lost";

    private readonly ConcurrentQueue<(string What, string Path)> _heard = new();

    /// <summary>Everything told so far: what happened, and to which path - or why, for a watch lost.</summary>
    public IReadOnlyList<(string What, string Path)> Heard => [.. _heard];

    public void Written(string fullPath) => _heard.Enqueue((WasWritten, fullPath));

    public void Named(string fullPath) => _heard.Enqueue((WasNamed, fullPath));

    public void Lost(string why) => _heard.Enqueue((WasLost, why));

    /// <summary>
    /// Waits until the sink has been told <paramref name="what"/> of <paramref name="path"/>. File system notifications
    /// are delivered by the operating system when it gets to them, so the wait is generous and its failure is a real one.
    /// </summary>
    public async Task WaitForAsync(string what, string path, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (_heard.Contains((what, path)))
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        Assert.Fail($"The watch was never told '{path}' was {what}. It was told: {string.Join("; ", _heard.Select(heard => $"{heard.What} {heard.Path}"))}");
    }
}
