namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// Where a string's commands may stand, held as forms of places rather than every place one by one: a move after a
/// <c>;</c> holds where it goes and fails where it stood, so k relative moves may leave the shell in 2^k places - each a
/// place this decides - which a form holds as one start and k steps, each of which may be taken or not.
/// </summary>
/// <remarks>
/// A path is decided at one place of each way the places stand to the decisions (<see cref="Placed"/>), however many a
/// form holds; what a string wrote and then runs, and the text <c>PWD</c> holds, are read of the forms themselves - a file
/// written and a file run the same where some place of each form is (<see cref="Meet"/>), <c>PWD</c> each place a form
/// holds, as the pattern of its steps (<see cref="PlaceForm.Pattern"/>). Every operation is linear in a form's steps, so a
/// string of k moves is followed in time polynomial in k.
/// </remarks>
public sealed partial class AgentPolicy
{
    /// <summary>
    /// A set of places: <see cref="Base"/>, then each step one of its ways - a run of names, each a segment beneath what
    /// precedes it - or, where the step may be passed, none. A place no move made relative is a form of no steps.
    /// </summary>
    internal sealed class PlaceForm
    {
        private PlaceForm(string @base, IReadOnlyList<PlaceStep> steps)
        {
            Base = @base;
            Steps = steps;
            Key = string.Concat(new[] { @base }.Concat(steps.Select(step => (step.Optional ? "\u0001?" : "\u0001!") + string.Join('\u0002', step.Ways.Select(way => string.Join('\u0003', way))))));
        }

        /// <summary>The place every place of the form begins at, spelled whole.</summary>
        public string Base { get; }

        /// <summary>The steps from it, in order.</summary>
        public IReadOnlyList<PlaceStep> Steps { get; }

        /// <summary>The form's one spelling, by which two alike are one.</summary>
        public string Key { get; }

        /// <summary>The form of one place.</summary>
        public static PlaceForm Of(string place) => new(place, []);

        /// <summary>
        /// Each place of the form with <paramref name="name"/> beneath it: a step of one way that may not be passed takes it
        /// on its way, any other step is followed by one of it alone - so the ways of a step are never multiplied.
        /// </summary>
        public PlaceForm Appended(string name)
            => Steps.Count > 0 && !Steps[^1].Optional && Steps[^1].Ways.Count == 1
                ? new PlaceForm(Base, [.. Steps.Take(Steps.Count - 1), new PlaceStep([[.. Steps[^1].Ways[0], name]], false)])
                : new PlaceForm(Base, [.. Steps, new PlaceStep([[name]], false)]);

        /// <summary>
        /// The directory above each place of the form, as the system walks <c>..</c>: the last step's ways one name shorter,
        /// a way of one name leaving the step out; past a step that may be passed, the directory above each place before it
        /// too; above the base, its parent, a root its own.
        /// </summary>
        public IReadOnlyList<PlaceForm> Popped()
        {
            if (Steps.Count == 0)
            {
                return [Of(Path.GetDirectoryName(Base) ?? Base)];
            }

            var before = new PlaceForm(Base, [.. Steps.Take(Steps.Count - 1)]);
            var last = Steps[^1];
            var shorter = last.Ways.Where(way => way.Count > 1).Select(way => (IReadOnlyList<string>)[.. way.Take(way.Count - 1)]).ToList();
            var popped = new List<PlaceForm>();

            if (last.Optional)
            {
                popped.AddRange(before.Popped());
            }

            if (last.Ways.Any(way => way.Count == 1))
            {
                popped.Add(before);
            }

            if (shorter.Count > 0)
            {
                popped.Add(new PlaceForm(Base, [.. before.Steps, new PlaceStep(shorter, false)]));
            }

            return Union(popped);
        }

        /// <summary>
        /// <paramref name="forms"/> as few forms as hold the same places: each once by its spelling, a form and the form one
        /// step longer one form whose last step may be passed, and two forms alike but for their last steps one whose last
        /// step goes either's ways - repeated until no two are one.
        /// </summary>
        public static IReadOnlyList<PlaceForm> Union(IEnumerable<PlaceForm> forms)
        {
            var held = new List<PlaceForm>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var form in forms)
            {
                if (keys.Add(form.Key))
                {
                    held.Add(form);
                }
            }

            for (var merged = true; merged;)
            {
                merged = false;

                for (var at = 0; at < held.Count && !merged; at++)
                {
                    for (var other = at + 1; other < held.Count && !merged; other++)
                    {
                        if (Merged(held[at], held[other]) is { } one)
                        {
                            held[at] = one;
                            held.RemoveAt(other);
                            merged = true;
                        }
                    }
                }

                // A merge may make a form another one already is: each is kept once still.
                if (merged)
                {
                    keys.Clear();
                    held = [.. held.Where(form => keys.Add(form.Key))];
                }
            }

            return held;
        }

        /// <summary>The one form holding the places of <paramref name="first"/> and <paramref name="second"/>, where there is one this reads; null where not.</summary>
        private static PlaceForm? Merged(PlaceForm first, PlaceForm second)
        {
            if (!first.Base.Equals(second.Base, StringComparison.Ordinal))
            {
                return null;
            }

            foreach (var (longer, shorter) in new[] { (first, second), (second, first) })
            {
                // A form and the same form a step longer: the step may be passed.
                if (longer.Steps.Count == shorter.Steps.Count + 1 && Alike(longer.Steps, shorter.Steps, shorter.Steps.Count))
                {
                    return new PlaceForm(longer.Base, [.. shorter.Steps, longer.Steps[^1] with { Optional = true }]);
                }
            }

            if (first.Steps.Count > 0 && first.Steps.Count == second.Steps.Count && Alike(first.Steps, second.Steps, first.Steps.Count - 1))
            {
                var ways = new List<IReadOnlyList<string>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var way in first.Steps[^1].Ways.Concat(second.Steps[^1].Ways))
                {
                    if (seen.Add(string.Join('\u0003', way)))
                    {
                        ways.Add(way);
                    }
                }

                return new PlaceForm(first.Base, [.. first.Steps.Take(first.Steps.Count - 1), new PlaceStep(ways, first.Steps[^1].Optional || second.Steps[^1].Optional)]);
            }

            return null;
        }

        /// <summary>Whether the first <paramref name="count"/> steps of the two are alike.</summary>
        private static bool Alike(IReadOnlyList<PlaceStep> first, IReadOnlyList<PlaceStep> second, int count)
            => Enumerable.Range(0, count).All(at => first[at].Optional == second[at].Optional
                && first[at].Ways.Count == second[at].Ways.Count
                && first[at].Ways.Zip(second[at].Ways).All(pair => pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal)));

        /// <summary>
        /// The pattern of the text of every place the form holds, as <c>PWD</c> holds it: the base, then each step's ways as a
        /// choice - none of them where the step may be passed. A base that is a root gives its places' text after it with no
        /// separator of its own, and its own text where every step may be passed.
        /// </summary>
        public WordPattern Pattern()
        {
            if (Steps.Count == 0)
            {
                return WordPattern.Of(Base, new string(WordQuoting.Open, Base.Length));
            }

            var separator = Path.DirectorySeparatorChar.ToString();
            var rooted = Path.GetDirectoryName(Base) is null;
            var start = rooted ? Base.TrimEnd('/', '\\') : Path.TrimEndingDirectorySeparator(Base);
            var parts = new List<WordPart> { new WordPart.Text(start, new string(WordQuoting.Open, start.Length)) };

            foreach (var step in Steps)
            {
                var ways = step.Ways.Select(way => separator + string.Join(separator, way)).Concat(step.Optional ? [string.Empty] : []).ToList();

                parts.Add(new WordPart.Choice(new WordChoice([.. ways.Select(way => WordPattern.Of(way, new string(WordQuoting.Open, way.Length)))])));
            }

            var pattern = new WordPattern(parts);

            return rooted && Steps.All(step => step.Optional)
                ? new WordPattern([new WordPart.Choice(new WordChoice([WordPattern.Of(Base, new string(WordQuoting.Open, Base.Length)), pattern]))])
                : pattern;
        }
    }

    /// <summary>One step of a <see cref="PlaceForm"/>: its ways, each a run of names, and whether it may be passed.</summary>
    internal sealed record PlaceStep(IReadOnlyList<IReadOnlyList<string>> Ways, bool Optional);

    /// <summary>
    /// What a relative path does to a place it is read from, as the system walks it (<see cref="Plain"/>): each segment a
    /// name gone beneath, <c>..</c> a step back up, <c>.</c> and an empty one nothing - and, where <paramref name="pattern"/>
    /// says the shell expands it, a segment beginning with a dot that may match <c>..</c> or <c>.</c> any of them
    /// (<see cref="ReadingForms"/>). Null where the path is not relative - rooted, a drive's, a home's - read whole instead.
    /// </summary>
    /// <returns>Each segment's alternatives in order: a name, null for a step up, or empty for none.</returns>
    private IReadOnlyList<IReadOnlyList<string?>>? RelativeSteps(string path, bool pattern, bool tildeExpands = true)
    {
        var text = HomeExpanded(path.Trim(), _platform.HomeDirectory, tildeExpands);

        if (text.Length == 0 || Path.IsPathRooted(text) || (OnWindows && DriveSpelled(text) is not null) || (text.Length > 1 && text[1] == ':'))
        {
            return null;
        }

        var named = OnWindows ? WindowsNames.Trimmed(text) : text;
        var steps = new List<IReadOnlyList<string?>>();

        foreach (var segment in named.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                steps.Add([null]);
            }
            else if (pattern && segment.Length > 1 && segment[0] == '.' && segment.IndexOfAny(['*', '?', '['], 1) > 0)
            {
                var expression = SegmentPattern(segment);

                steps.Add([segment, .. expression.IsMatch("..") ? new string?[] { null } : [], .. expression.IsMatch(".") ? new[] { string.Empty } : []]);
            }
            else
            {
                steps.Add([segment]);
            }
        }

        return steps;
    }

    /// <summary>
    /// Every place <paramref name="path"/> reaches from the places <paramref name="forms"/> hold, as forms: a relative one
    /// stepped from each form (<see cref="RelativeSteps"/>), any other read whole from each form's base
    /// (<see cref="CommandSpelled"/>, its short names kept, as a move keeps them).
    /// </summary>
    private IReadOnlyList<PlaceForm> Reached(IReadOnlyList<PlaceForm> forms, string path, bool pattern, bool tildeExpands = true)
    {
        if (RelativeSteps(path, pattern, tildeExpands) is not { } steps)
        {
            return PlaceForm.Union(forms.Select(form => form.Base).Distinct(StringComparer.Ordinal)
                .SelectMany(place => Spelled(path, place, tildeExpands, lengthen: false))
                .SelectMany(whole => ReadingForms(whole, dots: pattern, shorts: false)));
        }

        return Walked(forms, steps);
    }

    /// <summary>Each form of <paramref name="forms"/> walked through <paramref name="steps"/> (<see cref="RelativeSteps"/>), the forms joined as one where they can be after each.</summary>
    private static IReadOnlyList<PlaceForm> Walked(IReadOnlyList<PlaceForm> forms, IReadOnlyList<IReadOnlyList<string?>> steps)
    {
        IReadOnlyList<PlaceForm> reached = PlaceForm.Union(forms);

        foreach (var step in steps)
        {
            reached = PlaceForm.Union(reached.SelectMany(form => step.SelectMany(alternative => alternative switch
            {
                null => form.Popped(),
                { Length: 0 } => [form],
                _ => (IReadOnlyList<PlaceForm>)[form.Appended(alternative)],
            })));
        }

        return reached;
    }

    /// <summary>
    /// The forms of the places <paramref name="reading"/> reaches, read as the shell and the system read a command's path:
    /// where <paramref name="dots"/> says so, each segment that begins with a dot and may match <c>..</c> or <c>.</c> read
    /// as that name too - for a shell that does not skip them (sh, bash before 5.2 or with <c>globskipdots</c> off) matches
    /// them so - and where <paramref name="shorts"/> does, each short name as each guarded name it may shorten
    /// (<see cref="WindowsNames.Lengthenings"/>): the reading before its first such segment the base, each segment from it
    /// a step as the system walks it, so a step back up folds the name before it away (<see cref="Walked"/>) and no
    /// combination, however many, is spelled out. The reading itself is a place of them.
    /// </summary>
    /// <param name="reading">A whole path, made plain (<see cref="Plain"/>).</param>
    /// <param name="dots">Whether the shell expands the path's patterns.</param>
    /// <param name="shorts">Whether its short names are read as the guarded names they may shorten.</param>
    private IReadOnlyList<PlaceForm> ReadingForms(string reading, bool dots, bool shorts)
    {
        IReadOnlyList<string?> Alternatives(string name)
        {
            if (name == "..")
            {
                return [null];
            }

            if (dots && MayReadAsDots(name))
            {
                var expression = SegmentPattern(name);

                return [name, .. expression.IsMatch("..") ? new string?[] { null } : [], .. expression.IsMatch(".") ? new[] { string.Empty } : []];
            }

            return shorts && name.Contains('~', StringComparison.Ordinal) ? WindowsNames.Lengthenings(name, _guardedNames)[0] : [name];
        }

        var at = Path.GetPathRoot(reading)?.Length ?? 0;

        while (at < reading.Length)
        {
            var end = reading.IndexOfAny(['/', '\\'], at);
            var segment = end < 0 ? reading[at..] : reading[at..end];

            if (segment.Length > 0 && Alternatives(segment).Count > 1)
            {
                var steps = reading[at..].Split(['/', '\\']).Where(name => name.Length > 0 && name != ".").Select(Alternatives).ToList();

                return Walked([PlaceForm.Of(Path.TrimEndingDirectorySeparator(reading[..at]))], steps);
            }

            if (end < 0)
            {
                break;
            }

            at = end + 1;
        }

        return [PlaceForm.Of(reading)];
    }

    /// <summary>Whether a shell that does not skip them may match <c>..</c> or <c>.</c> by <paramref name="segment"/>: one that begins with a dot and holds a pattern after it.</summary>
    private static bool MayReadAsDots(string segment) => segment.Length > 1 && segment[0] == '.' && segment.IndexOfAny(['*', '?', '['], 1) > 0;

    /// <summary>
    /// <paramref name="reading"/>, and one place of each way the places it reaches (<see cref="ReadingForms"/>) stand to the
    /// decisions (<see cref="Representatives"/>): every place it may reach, decided as each would be, never every
    /// combination spelled out.
    /// </summary>
    private IEnumerable<string> ReadAsCommandsRead(string reading, bool dots, bool shorts)
        => ReadingForms(reading, dots, shorts) is var forms && forms is [{ Steps.Count: 0 }] ? [reading] : [reading, .. Representatives(forms)];

    /// <summary>
    /// One place of each way the places <paramref name="forms"/> hold stand to the decisions (<see cref="Reading.Key"/>), in
    /// the order the forms and their places come - a step passed before it is taken - each spelled whole: a path is read
    /// against each, and decided at it as at every place standing alike.
    /// </summary>
    private List<string> Representatives(IReadOnlyList<PlaceForm> forms)
    {
        var places = new List<string>();

        Represent(forms, 0, new HashSet<string>(StringComparer.Ordinal), places);

        return places;
    }

    /// <summary>
    /// <see cref="Representatives"/> read on: the places of <paramref name="forms"/> from <paramref name="from"/> on added to
    /// <paramref name="places"/> where they stand in a way <paramref name="ways"/> does not yet hold - the forms before it
    /// read already, as a string's places only grow.
    /// </summary>
    private void Represent(IReadOnlyList<PlaceForm> forms, int from, HashSet<string> ways, List<string> places)
    {
        foreach (var form in forms.Skip(from))
        {
            List<Reading> readings = [Stood(form.Base, Classified(form.Base), matchers: null)];

            foreach (var step in form.Steps)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var next = new List<Reading>();

                foreach (var reading in readings)
                {
                    if (step.Optional && seen.Add(reading.Key))
                    {
                        next.Add(reading);
                    }

                    foreach (var way in step.Ways)
                    {
                        var stepped = way.Aggregate(reading, (on, name) => SteppedInto(on, name));

                        if (seen.Add(stepped.Key))
                        {
                            next.Add(stepped);
                        }
                    }
                }

                readings = next;
            }

            places.AddRange(readings.Where(reading => ways.Add(reading.Key)).Select(reading => reading.Text));
        }
    }

    /// <summary><paramref name="reading"/> with <paramref name="name"/> the directory beneath it, spelled as the system joins one (<see cref="Stepped"/>).</summary>
    private Reading SteppedInto(Reading reading, string name)
        => Stepped(
            reading.Text.EndsWith(Path.DirectorySeparatorChar) || reading.Text.EndsWith(Path.AltDirectorySeparatorChar) ? reading : reading with { Text = reading.Text + Path.DirectorySeparatorChar },
            name,
            matchers: null);

    /// <summary>
    /// Whether some place of <paramref name="first"/> is some place of <paramref name="second"/>, as places compare, each
    /// short name read as each guarded name it may shorten (<see cref="WindowsNames.Lengthenings"/>): the two read as
    /// automata over their segments, side by side, each pair of states once.
    /// </summary>
    private bool Meet(PlaceForm first, PlaceForm second)
    {
        if (first.Key.Equals(second.Key, StringComparison.Ordinal))
        {
            return true;
        }

        var (one, other) = (Automaton(first), Automaton(second));
        var seen = new HashSet<(int, int)>();
        var pending = new Stack<(int, int)>();

        void Reach(int at, int against)
        {
            if (seen.Add((at, against)))
            {
                pending.Push((at, against));
            }
        }

        Reach(0, 0);

        while (pending.TryPop(out var pair))
        {
            var (at, against) = pair;

            if (at == one.End && against == other.End)
            {
                return true;
            }

            foreach (var edge in one.Edges.Where(edge => edge.From == at))
            {
                if (edge.Names is null)
                {
                    Reach(edge.To, against);
                    continue;
                }

                foreach (var step in other.Edges.Where(step => step.From == against && step.Names is not null && step.Names.Intersect(edge.Names, StringComparer.OrdinalIgnoreCase).Any()))
                {
                    Reach(edge.To, step.To);
                }
            }

            foreach (var step in other.Edges.Where(step => step.From == against && step.Names is null))
            {
                Reach(at, step.To);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the text of some place of <paramref name="form"/>, its separators spelled '/' and each short name read as each
    /// name it may shorten, ends in <paramref name="end"/>: read from the form's end back, each segment's names against
    /// what of the end is still to be met, one count of it for each state.
    /// </summary>
    private bool MayEndIn(PlaceForm form, string end)
    {
        var automaton = Automaton(form);
        var seen = new HashSet<(int, int)>();
        var pending = new Stack<(int State, int Left)>();
        pending.Push((automaton.End, end.Length));

        while (pending.TryPop(out var at))
        {
            if (!seen.Add(at))
            {
                continue;
            }

            foreach (var edge in automaton.Edges.Where(edge => edge.To == at.State))
            {
                if (edge.Names is null)
                {
                    pending.Push((edge.From, at.Left));
                    continue;
                }

                // A segment is spelled after a separator, but the first, which a root's text spells whole.
                foreach (var name in edge.Names.Select(name => (edge.From == 0 ? name : "/" + name).Replace('\\', '/')))
                {
                    var still = end[..at.Left];

                    if (name.Length >= still.Length)
                    {
                        if (name.EndsWith(still, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    else if (still.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push((edge.From, at.Left - name.Length));
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="form"/> as an automaton over the segments of its places: the base's segments in a row - its root's
    /// first, as its text spells it - then each step's ways from where the step begins to where it ends, and a step that
    /// may be passed passed by an edge that reads nothing. Each segment's edge reads every name it may be.
    /// </summary>
    private (IReadOnlyList<(int From, int To, IReadOnlyList<string>? Names)> Edges, int End) Automaton(PlaceForm form)
    {
        var edges = new List<(int From, int To, IReadOnlyList<string>? Names)>();
        var root = Path.GetPathRoot(form.Base) ?? string.Empty;
        var state = 0;
        var states = 1;

        IReadOnlyList<string> Names(string segment) => OnWindows ? WindowsNames.Lengthenings(segment, _guardedNames)[0] : [segment];

        void Read(string segment)
        {
            edges.Add((state, states, Names(segment)));
            state = states++;
        }

        if (root.Length > 0)
        {
            edges.Add((state, states, [root.TrimEnd('/', '\\')]));
            state = states++;
        }

        foreach (var segment in form.Base[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            Read(segment);
        }

        foreach (var step in form.Steps)
        {
            var from = state;
            var to = states++;

            foreach (var way in step.Ways)
            {
                state = from;

                foreach (var name in way.Take(way.Count - 1))
                {
                    Read(name);
                }

                edges.Add((state, to, Names(way[^1])));
            }

            if (step.Optional)
            {
                edges.Add((from, to, null));
            }

            state = to;
        }

        return (edges, state);
    }
}
