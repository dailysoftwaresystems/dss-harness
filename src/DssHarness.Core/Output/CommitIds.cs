namespace DssHarness.Core.Output;

/// <summary>
/// How long a commit's id is as a message shows it, in the operation that shows it: git's own abbreviation for the
/// repository the operation works in - <c>git rev-parse --short</c>, which grows with the repository so an id it shows
/// stays unique there - asked of git once, the first time a commit is shown, and kept for the operation's life.
/// </summary>
/// <remarks>
/// A presentation, never a cut of what is kept: every record and every <c>--json</c> document carries a commit's whole
/// id, and only a line a person reads shows it abbreviated. Where no repository is known - an operation that works in
/// none, or code that runs outside any operation - a commit is shown whole, since no length that git did not choose is
/// unique anywhere. Held per operation, as each operation's output is, and found by the operation's own flow: every
/// asynchronous call an operation makes sees the ids its operation keeps.
/// </remarks>
public sealed class CommitIds
{
    /// <summary>The ids of the operation whose flow is running, where it keeps any.</summary>
    private static readonly AsyncLocal<CommitIds?> Ambient = new();

    private readonly Lazy<int?> _length;

    /// <summary>Ids shown at the length <paramref name="length"/> answers, asked the first time one is shown.</summary>
    /// <param name="length">
    /// How many characters git's own abbreviation takes in the operation's repository; <see langword="null"/> where git
    /// cannot say - no repository, or none with a commit - and ids are then shown whole.
    /// </param>
    public CommitIds(Func<int?> length)
    {
        ArgumentNullException.ThrowIfNull(length);

        _length = new Lazy<int?>(length, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The ids of the operation whose flow is running; <see langword="null"/> outside every operation.</summary>
    public static CommitIds? Current => Ambient.Value;

    /// <summary>Keeps <paramref name="ids"/> as the running operation's until the answer is disposed, which puts back what was kept before.</summary>
    /// <param name="ids">The operation's ids.</param>
    public static IDisposable Use(CommitIds ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var before = Ambient.Value;
        Ambient.Value = ids;

        return new Restored(before);
    }

    /// <summary>
    /// <paramref name="commit"/> as a message shows it: its first characters, as many as git's own abbreviation takes in
    /// the operation's repository, and the whole id where git could not say or the id is no longer than that.
    /// </summary>
    /// <param name="commit">The commit's id, whole.</param>
    public string Shown(string commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        return _length.Value is { } length && length > 0 && commit.Length > length ? commit[..length] : commit;
    }

    /// <summary>Puts back the ids kept before, once.</summary>
    private sealed class Restored(CommitIds? before) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Ambient.Value = before;
            }
        }
    }
}
