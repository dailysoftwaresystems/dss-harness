namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// One run of code a scanned reader's calls are read from, as a frame of the reader's own stack rather than a call into
/// itself: its text - an interpolation's scanned only once the run is reached, the first scan's index of its literals
/// passed on (<see cref="InterpolatedText.Closes"/>) - where the reading of it stands and where it ends, the node it is the
/// calls of, and what it binds. A run its reading meets in its text - an interpolation's code, a test group's body - is
/// pushed and read before this one goes on, so code nested any depth is read in the order it is written.
/// </summary>
/// <typeparam name="TKey">The key of the node the run is the calls of, as the reader holds one.</typeparam>
/// <typeparam name="TLocals">What the run's code binds, as the reader keeps it.</typeparam>
internal class ScanRun<TKey, TLocals>
{
    private readonly Func<InterpolatedText>? _scanned;
    private InterpolatedText? _text;
    private int _to;

    /// <summary>A run of <paramref name="text"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public ScanRun(InterpolatedText text, int from, int to, TKey key, TLocals locals)
    {
        _text = text;
        _to = to;
        At = from;
        Key = key;
        Locals = locals;
    }

    /// <summary>A run of the whole of the text <paramref name="scanned"/> scans, once it is reached.</summary>
    public ScanRun(Func<InterpolatedText> scanned, TKey key, TLocals locals)
    {
        _scanned = scanned;
        Key = key;
        Locals = locals;
    }

    /// <summary>Its text: scanned now, where it is an interpolation's not scanned yet.</summary>
    public InterpolatedText Text
    {
        get
        {
            if (_text is null)
            {
                _text = _scanned!();
                _to = _text.Scan.Count;
            }

            return _text;
        }
    }

    /// <summary>Where it ends: its text's end, for an interpolation's, once it is scanned.</summary>
    public int To
    {
        get
        {
            _ = Text;
            return _to;
        }
    }

    /// <summary>Where its reading stands.</summary>
    public int At { get; set; }

    /// <summary>The node it is the calls of.</summary>
    public TKey Key { get; }

    /// <summary>What its code binds.</summary>
    public TLocals Locals { get; }
}
