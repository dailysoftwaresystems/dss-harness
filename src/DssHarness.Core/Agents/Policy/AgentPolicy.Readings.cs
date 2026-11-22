using DssHarness.Core.FileSystem;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The readings of a path the policy decides, read one segment at a time and kept one of each way they may stand: a path
/// whose segments may each be read as several names - a Windows short name as every guarded name it may shorten
/// (<see cref="WindowsNames.Lengthenings"/>) - has readings past counting, and every decision of one asks the same few
/// things of it, so two readings that stand alike to every one of them are decided alike, and one of them is decided.
/// </summary>
/// <remarks>
/// <para>
/// What every decision of a path asks of it (<see cref="Placed"/>): where it stands to each place the policy guards, roots
/// and reaches (<see cref="Tracked"/>) - the place itself, one that holds it, one it holds, one beside it - which for a path
/// that is neither one of them, nor holds one, nor is on the disk is where the nearest of its directories that is stands to
/// them, the path lying beneath that one by names nothing guards; whether a name beneath that one is <c>.git</c>, holds a
/// shell pattern, may match <c>.git</c> past a pattern, or is a Windows device, and where; where it resolves, read alike; and
/// for a file tool, where each path rule, worktree rule, protected path and step write stands to it, read a segment at a
/// time as the rule's pattern reads a path (<see cref="GlobPattern.Next"/>). Two readings alike in all of it are alike to
/// every decision: the first, in the order the readings are spelled, is decided, and a refusal of any of them is a refusal
/// of it, said in its words.
/// </para>
/// <para>
/// What it costs: each segment's names against the ways the readings before it stand - at most one way for each place a
/// path may be and each depth and index of the names it holds - so a path of n segments of k names each is read in time
/// polynomial in n and k, never k^n.
/// </para>
/// </remarks>
public sealed partial class AgentPolicy
{
    /// <summary>Every place a decision of a path compares it with, as its roots and refusals spell them (<see cref="Placed"/>).</summary>
    private IReadOnlyList<string>? _tracked;

    /// <summary>
    /// Every place a decision of a path compares it with: the places no action reaches, the tool's state, every tree and its
    /// <c>.git</c>, every root, the working directory and the reporter's root - one of them, a directory that holds one, or
    /// one on the disk is a place a reading is read at as it is spelled.
    /// </summary>
    private IReadOnlyList<string> Tracked => _tracked ??=
    [
        .. new[]
        {
            _unreachable.SelectMany(kind => kind.Places), _state, _gitTrees, _gitTrees.Select(tree => Path.Combine(tree, ".git")),
            _allRoots, _readRoots, _refusalReadRoots, _writableRoots, [_workingRoot, _ground.WorkingDirectory],
            new[] { _reporterRoot, _resolvedReporterRoot }.OfType<string>(),
        }.SelectMany(places => places).Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>Every pattern a <c>Read</c> decision of a path matches a path by, with the root it is matched beneath and how names compare.</summary>
    private IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? _readMatchers;

    /// <summary>Every pattern a write of a path matches a path by, with the root it is matched beneath and how names compare.</summary>
    private IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? _writeMatchers;

    /// <summary>
    /// The patterns a read of a path (<see cref="Read"/>, <see cref="Search"/>) is decided by: each <c>Read</c> rule's path
    /// pattern beneath the roots its verb reads. The readings such a decision keeps one of each way of
    /// (<see cref="Lengthenings"/>) are keyed by where these stand and no other pattern, for no other pattern is asked of
    /// the readings a read decides - so the ways a write's patterns alone would tell apart, a read never spells apart.
    /// </summary>
    private IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)> ReadMatchers => _readMatchers ??=
    [
        .. _rules.Where(pair => pair.Rule.PathPattern is not null && pair.Rule.Verb is AgentRuleVerb.Read)
            .SelectMany(pair => _refusalReadRoots.Select(root => (pair.Rule.PathPattern!, root, Refusals))),
    ];

    /// <summary>
    /// The patterns a write of a path (<see cref="Write"/>) is decided by: each <c>Write</c> rule's path pattern beneath
    /// every root, the worktree rules and protected paths beneath every root, and the step's writes beneath every root and
    /// its worktree - never a <c>Read</c> rule's, which no write's decision asks of the readings it decides.
    /// </summary>
    private IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)> WriteMatchers => _writeMatchers ??=
    [
        .. _rules.Where(pair => pair.Rule.PathPattern is not null && pair.Rule.Verb is AgentRuleVerb.Write)
            .SelectMany(pair => _allRoots.Select(root => (pair.Rule.PathPattern!, root, Refusals))),
        .. _worktreePolicyWrites.Concat(_protectedPaths).SelectMany(pair => _allRoots.Select(root => (pair.Pattern, root, Refusals))),
        .. _writes.SelectMany(pair => _allRoots.Append(_workingRoot).Select(root => (pair.Pattern, root, _platform.PathComparison))),
    ];

    /// <summary>
    /// Where a path stands to every decision of it, read from its root a segment at a time: a place one of them names, holds
    /// or the disk holds, by its spelling; past the nearest such directory, the <paramref name="Anchor"/>, by what its names
    /// beneath it are - how many, and at which of them the first <c>.git</c>, the first shell pattern and the first name past
    /// that pattern that may match <c>.git</c> (a pattern's star matching a dot-name or not) stand, whether that pattern may
    /// itself, and which are Windows devices. Nothing beneath a directory that is not on the disk is, and none of those names is a place compared, so two
    /// paths alike in all of it stand alike to each place compared, and to each place a step back from them reaches.
    /// </summary>
    /// <param name="Path">The path, as spelled.</param>
    /// <param name="Anchor">The nearest directory of it that is a place compared, holds one, or is on the disk; null where the path itself is.</param>
    /// <param name="Depth">How many names lie beneath the anchor.</param>
    /// <param name="FirstGit">Where the first <c>.git</c> beneath the anchor stands, counted from 1; 0 where none.</param>
    /// <param name="FirstPattern">Where the first name holding a shell pattern stands; 0 where none.</param>
    /// <param name="GitPast">Where the first name past that pattern that may match <c>.git</c> stands, a star matching no dot-name; 0 where none.</param>
    /// <param name="GitPastDots">The same, a star matching a dot-name.</param>
    /// <param name="PatternGit">Whether the first name holding a pattern may match <c>.git</c>: 1 where a star matching no dot-name lets it, 2 where one matching one does, added.</param>
    /// <param name="Devices">Where each Windows device's name stands, joined by commas.</param>
    private readonly record struct Placed(string Path, string? Anchor, int Depth, int FirstGit, int FirstPattern, int GitPast, int GitPastDots, int PatternGit, string Devices)
    {
        /// <summary>
        /// The text two paths standing alike share: the path itself where it is a place compared, holds one or is on the
        /// disk, and where its first name beneath the anchor holds a pattern, which may match a place compared; else what
        /// its names beneath the anchor are.
        /// </summary>
        public string Key => Anchor is null ? "C" + Path
            : FirstPattern == 1 ? "X" + Path
            : $"O{Depth}|{FirstGit}|{FirstPattern}|{GitPast}|{GitPastDots}|{PatternGit}|{Devices}|{Anchor}";
    }

    /// <summary>One reading being read: its text so far, where it stands and where it resolves, and - for a file tool - where each of its patterns stands to both.</summary>
    private sealed record Reading(string Text, Placed Asked, Placed Resolved, IReadOnlyList<GlobPattern.Progress?>? AskedRules, IReadOnlyList<GlobPattern.Progress?>? ResolvedRules)
    {
        /// <summary>What two readings standing alike to every decision share (<see cref="Placed"/>), spelled once.</summary>
        public string Key => _key ??= Asked.Anchor is null
            ? Asked.Key
            : string.Join('\n', [Asked.Key, Resolved.Key, Rules(AskedRules), Rules(ResolvedRules)]);

        /// <summary><see cref="Key"/>, built once: a reading asked of the decisions again answers with what it spelled first.</summary>
        private string? _key;

        /// <summary>Whether the reading stands and resolves alike (<see cref="AgentPolicy.Stepped"/>): its two sides step as one, asked once.</summary>
        public bool Alike => _alike ??= Resolved.Equals(Asked) && Rules(ResolvedRules) == Rules(AskedRules);

        /// <summary><see cref="Alike"/>, read once.</summary>
        private bool? _alike;

        private static string Rules(IReadOnlyList<GlobPattern.Progress?>? rules)
            => rules is null ? string.Empty : string.Join(';', rules.Select(rule => rule is { } stands ? $"{stands.Places}/{(stands.MatchedAbove ? 1 : 0)}{(stands.MatchedHere ? 1 : 0)}" : "-"));
    }

    /// <summary>
    /// <paramref name="whole"/>, and every reading of it a short name gives (<see cref="WindowsNames.Lengthened"/>) that
    /// stands to some decision as none before it does: the readings read a segment at a time from its root, each segment's
    /// names against each way the readings before it stand, one reading kept of each way - the first spelled, so the path
    /// itself comes first and a refusal of any reading is said of the first one refused.
    /// </summary>
    /// <param name="whole">A whole path, made plain (<see cref="Plain"/>).</param>
    /// <param name="matchers">The patterns the decision asking matches a path by, so where each stands to a reading tells two apart; null where none does.</param>
    private IReadOnlyList<string> Lengthenings(string whole, IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers)
    {
        var parts = WindowsNames.Lengthenings(whole, _guardedNames);
        var first = 0;

        while (first < parts.Count && parts[first].Count == 1)
        {
            first++;
        }

        if (first == parts.Count)
        {
            return [whole];
        }

        var start = string.Concat(parts.Take(first).Select(part => part[0]));
        List<Reading> readings = [Began(start, matchers)];

        foreach (var part in parts.Skip(first))
        {
            // A run of separators joins what follows: no decision reads it.
            if (part[0].Length > 0 && part[0].All(character => character is '/' or '\\'))
            {
                readings = [.. readings.Select(reading => reading with { Text = reading.Text + part[0] })];
                continue;
            }

            var ways = new HashSet<string>(StringComparer.Ordinal);
            var next = new List<Reading>();

            foreach (var reading in readings)
            {
                foreach (var name in part)
                {
                    if (Stepped(reading, name, matchers) is var stepped && ways.Add(stepped.Key))
                    {
                        next.Add(stepped);
                    }
                }
            }

            readings = next;
        }

        return [.. readings.Select(reading => reading.Text)];
    }

    /// <summary>A reading of <paramref name="start"/>, the path's text before its first name that may be read as another, where it stands read from its root.</summary>
    private Reading Began(string start, IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers)
    {
        var path = Path.TrimEndingDirectorySeparator(start);

        return Stood(start, Classified(path), matchers);
    }

    /// <summary>A reading of a path that stands where <paramref name="asked"/> says: where it resolves read anew, and its patterns' standing read whole.</summary>
    private Reading Stood(string text, Placed asked, IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers)
    {
        var resolved = ResolvedPlace(asked.Path);

        return new Reading(text, asked, resolved, RulesAt(matchers, asked.Path), RulesAt(matchers, resolved.Path));
    }

    /// <summary>
    /// <paramref name="reading"/> with <paramref name="name"/> its next segment: where it stands read anew while it is a place
    /// compared, and past one stepped a name further - where it resolves, and each pattern's standing, alike. A reading that
    /// stands and resolves alike steps its two sides as one: both are asked of the disk's answer once.
    /// </summary>
    private Reading Stepped(Reading reading, string name, IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers)
    {
        var text = reading.Text + name;
        var asked = Advanced(reading.Asked, name, Path.TrimEndingDirectorySeparator(text));

        // A place compared is read whole; past one, where it resolves and each pattern's standing step a name on.
        return asked.Anchor is null
            ? Stood(text, asked, matchers)
            : new Reading(
                text,
                asked,
                reading.Alike ? asked : Advanced(reading.Resolved, name, Path.Join(reading.Resolved.Path, name)),
                Next(matchers, reading.AskedRules!, name),
                reading.Alike ? Next(matchers, reading.AskedRules!, name) : Next(matchers, reading.ResolvedRules!, name));
    }

    /// <summary>Each pattern's standing a segment further on (<see cref="GlobPattern.Next"/>): one the path is not beneath the root of stays so.</summary>
    private IReadOnlyList<GlobPattern.Progress?> Next(IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers, IReadOnlyList<GlobPattern.Progress?> standing, string name)
        => matchers is null ? [] : [.. matchers.Select((matcher, at) => standing[at] is { } stands ? matcher.Pattern.Next(stands, name, matcher.Comparison) : (GlobPattern.Progress?)null)];

    /// <summary>
    /// Where each pattern stands to <paramref name="path"/>, read whole: begun at its root, stepped through each segment of
    /// the path beneath it; null where the path is neither its root nor beneath it.
    /// </summary>
    private IReadOnlyList<GlobPattern.Progress?> RulesAt(IReadOnlyList<(GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers, string path)
        => matchers is null ? [] : [.. matchers.Select(matcher =>
        {
            if (BeneathOf(matcher.Root, path, Refusals) is { } relative)
            {
                var stands = matcher.Pattern.Begin();

                foreach (var segment in relative.Split('/'))
                {
                    stands = matcher.Pattern.Next(stands, segment, matcher.Comparison);
                }

                return stands;
            }

            return InsideOrSameOf(matcher.Root, path, Refusals) ? matcher.Pattern.Begin() : (GlobPattern.Progress?)null;
        })];

    /// <summary>
    /// Where <paramref name="path"/> resolves, standing as <see cref="Classified"/> reads it; where it does not resolve as a
    /// place - a device, a path this cannot read - its own text, standing apart from every other.
    /// </summary>
    private Placed ResolvedPlace(string path)
    {
        try
        {
            return Classified(Resolved(path));
        }
        catch (Exception exception) when (Undecidable(exception))
        {
            return new Placed(path, Anchor: path, 0, 0, 1, 0, 0, 0, string.Empty);
        }
    }

    /// <summary>Where <paramref name="path"/> stands (<see cref="Placed"/>), read from its root a segment at a time.</summary>
    private Placed Classified(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var placed = new Placed(root, null, 0, 0, 0, 0, 0, 0, string.Empty);
        var at = root.Length;

        while (at < path.Length)
        {
            var end = path.IndexOfAny(['/', '\\'], at);
            var segment = end < 0 ? path[at..] : path[at..end];

            if (segment.Length > 0)
            {
                placed = Advanced(placed, segment, end < 0 ? path : path[..end]);
            }

            if (end < 0)
            {
                break;
            }

            at = end + 1;
        }

        return placed;
    }

    /// <summary>
    /// Where a path stands once <paramref name="name"/> is read after <paramref name="placed"/>, <paramref name="path"/> the
    /// path so far: a place compared, or one that holds one or is on the disk, by its spelling; past the last such, by what
    /// its names beneath it are.
    /// </summary>
    private Placed Advanced(Placed placed, string name, string path)
    {
        if (placed.Anchor is null)
        {
            return Compared(path)
                ? placed with { Path = path }
                : new Placed(path, placed.Path, 1, IsGit(name) ? 1 : 0, HoldsPattern(name) ? 1 : 0, 0, 0, 0, IsDeviceName(name) ? "1" : string.Empty);
        }

        // A name past the first pattern counts where it may match .git, and so does that pattern itself where the anchor
        // holds one - MayMatchGitsOwn's two cases - each read once as the shell reads it.
        var depth = placed.Depth + 1;
        var first = placed.FirstPattern == 0 && HoldsPattern(name);
        var past = placed.FirstPattern > 0;

        return new Placed(
            path,
            placed.Anchor,
            depth,
            placed.FirstGit > 0 ? placed.FirstGit : IsGit(name) ? depth : 0,
            first ? depth : placed.FirstPattern,
            placed.GitPast > 0 ? placed.GitPast : past && MayMatchGit(name, dotNames: false) ? depth : 0,
            placed.GitPastDots > 0 ? placed.GitPastDots : past && MayMatchGit(name, dotNames: true) ? depth : 0,
            first ? (MayMatchGit(name, dotNames: false) ? 1 : 0) + (MayMatchGit(name, dotNames: true) ? 2 : 0) : placed.PatternGit,
            IsDeviceName(name) ? (placed.Devices.Length > 0 ? placed.Devices + "," : string.Empty) + depth : placed.Devices);
    }

    /// <summary>Whether <paramref name="path"/> is a place a decision compares a path with, holds one, or is on the disk - read at as it is spelled.</summary>
    private bool Compared(string path)
    {
        if (Tracked.Any(place => PathContainment.IsInsideOrSame(path, place, Refusals)))
        {
            return true;
        }

        try
        {
            return KindAt(path) is not PathKind.None;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A name the system cannot hold is on no disk.
            return false;
        }
    }

    /// <summary>Whether <paramref name="name"/> is git's own directory's, as the refusals compare names.</summary>
    private static bool IsGit(string name) => name.Equals(".git", Refusals);

    /// <summary>Whether <paramref name="name"/> holds a shell pattern's character.</summary>
    private static bool HoldsPattern(string name) => name.IndexOfAny(['*', '?', '[']) >= 0;

    /// <summary>
    /// Whether <paramref name="name"/> may match <c>.git</c> as a segment of a pattern (<see cref="MayMatchGitsOwn"/>): a name
    /// holding none matches it where it is it in any case, as the segment's expression reads it.
    /// </summary>
    private bool MayMatchGit(string name, bool dotNames)
        => HoldsPattern(name) ? DotNamed(name, ".git", dotNames) && SegmentPattern(name).IsMatch(".git") : name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> is a Windows device's, on Windows.</summary>
    private bool IsDeviceName(string name) => OnWindows && IsDevice(name);
}
