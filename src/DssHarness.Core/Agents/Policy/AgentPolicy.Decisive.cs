using DssHarness.Core.Text;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The spellings of a word pattern that decide it: a decision of a path compares the names its segments spell - with the
/// places the policy guards, with what the disk holds, with the ends a program's name is folded of - and a shell pattern
/// it may spell is asked which guarded place it may reach. Two spellings that stand alike to every one of those are
/// decided alike, so one of each way is spelled: the names that matter, the names on the disk, a name none of them is,
/// and - where the word may spell a pattern - one spelling for each place the pattern may reach, found as a witness.
/// Nothing is spelled out that a name, a form or a witness decides.
/// </summary>
/// <remarks>
/// <para>
/// What a segment's choices (<see cref="WordPattern"/>) are spelled as (<see cref="SpellingsThatDecide"/>): each name
/// beneath the way that matters - the next segment of a guarded place below it, an entry of the directory there - and
/// one spelling that is none of them (<see cref="WordPattern.WordMatcher.SpellingBesides"/>); a choice that spells a
/// separator carries whole steps of the path, and is branched one alternative of its forms at a time, at one place of
/// each way they stand (<see cref="Representatives"/>). A word that may spell a shell pattern - an unquoted star,
/// question mark or bracket - is asked for a witness per guarded place it may reach
/// (<see cref="GlobWitness"/>), the product of its spellings and the place read together.
/// </para>
/// <para>
/// What it costs: each segment's names against the ways the segments before it stand, so a word of n segments of k names
/// each is read in time polynomial in n and k, never k^n; a witness is one walk over the pairs of a spelling's state and
/// a place's segments.
/// </para>
/// </remarks>
public sealed partial class AgentPolicy
{
    /// <summary>
    /// Every spelling of <paramref name="pattern"/> that a decision of it need read, in the order the pattern spells
    /// them: its own readings first, then - where a segment chooses - the names that decide it, and where the word may
    /// spell a shell pattern, one witness per guarded place of <paramref name="matters"/> it may reach. Every other
    /// spelling is decided as one of these is.
    /// </summary>
    /// <param name="pattern">The word's pattern, its parts with their quoting classes.</param>
    /// <param name="place">The place the word is read from, spelled whole.</param>
    /// <param name="matters">The places a path the word names is compared with - the places no command reaches, the tool's state, the places no command removes.</param>
    /// <param name="dotNames">Whether a pattern of the word's matches a name beginning with a dot.</param>
    private IEnumerable<(string Word, string Quoted)> Decisive(WordPattern pattern, string place, IReadOnlyList<string> matters, bool dotNames)
    {
        // The definition the word is decided by, for the test that both decide alike: every spelling, one at a time.
        if (EverySpelling)
        {
            return pattern.Spellings();
        }

        if (!pattern.Chooses)
        {
            return [SpelledOnce(pattern)];
        }

        // A choice that spells a separator carries whole steps of the path: branched one alternative at a time - at one
        // place of each way its forms stand, where it is a directory the shell may stand in - each branch the word with
        // that alternative in the choice's place, decided as the word itself is.
        if (SeparatorChoice(pattern) is { } carried)
        {
            var branches = carried.Forms is { } forms
                ? Representatives([.. forms]).Select(text => WordPattern.Of(text, new string(WordQuoting.Open, text.Length))).ToList()
                : [.. carried.Alternatives];
            var spelled = new List<(string, string)>();

            foreach (var branch in branches)
            {
                spelled.AddRange(Decisive(Substituted(pattern, carried, branch), place, matters, dotNames));
            }

            return spelled.Distinct();
        }
        // A word that may spell a shell pattern is decided by its witnesses: one spelling per guarded place it may
        // reach, beside the names that decide its choosing segments.
        if (MaySpellAGlob(pattern))
        {
            var witnesses = new List<(string, string)>();

            foreach (var matter in matters.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var against in Ancestry(matter))
                {
                    if (GlobWitness(pattern, against, dotNames) is { } reached && !witnesses.Contains(reached))
                    {
                        witnesses.Add(reached);
                    }
                }

                if (GlobWitness(pattern, matter, dotNames, exactly: false) is { } into && !witnesses.Contains(into))
                {
                    witnesses.Add(into);
                }
            }

            // A pattern a program's own name is folded of - .git among them - is asked of the word whole.
            if (SegmentSpells(pattern, ".git") is { } gits && !witnesses.Contains((gits, new string(WordQuoting.Open, gits.Length))))
            {
                witnesses.Add((gits, new string(WordQuoting.Open, gits.Length)));
            }

            return witnesses.DefaultIfEmpty(SpelledOnce(pattern));
        }

        // A plainly spelled word - its choices spelling names, its text names and separators, any character quoted
        // spelled as itself - is decided by the spellings that decide it. A word of options that is not one names its
        // paths in the pieces past its markers: each is decided where the whole word is not plain, never spelled out.
        if (PlainSegments(pattern) is { } segments)
        {
            return SpellingsThatDecide(segments, place, matters);
        }

        var derived = DerivedPieces(pattern);

        if (derived.Count > 0)
        {
            var spelled = new List<(string, string)>();

            foreach (var piece in derived)
            {
                spelled.AddRange(Decisive(piece, place, matters, dotNames));
            }

            return spelled.Distinct();
        }

        return pattern.Spellings();
    }

    /// <summary>
    /// <paramref name="pattern"/>'s segments - each with the separators before it - where every spelling of the word is a
    /// plain path that names itself alone: its choices spelling nothing but a name's letters, digits, <c>_</c>, <c>-</c>,
    /// <c>+</c> and <c>.</c> and its separators - any character quoted spelled as itself, so a quoted star names itself -
    /// none of them able to begin the word with <c>-</c>, and every segment that holds one spelling neither nothing,
    /// <c>.</c>, <c>..</c> nor a name ending in a dot; and no <c>..</c> after the first such segment. Such a word is read
    /// by <see cref="CommandAnalysis.PathsNamed"/> as itself alone, and what decides each spelling is which names its
    /// segments spell (<see cref="SpellingsThatDecide"/>). Null for any other word.
    /// </summary>
    private static IReadOnlyList<(string Before, WordPattern Piece)>? PlainSegments(WordPattern pattern)
    {
        static bool Named(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '+' or '.';

        if (!PartsOf(pattern).All(each => Named(each.Character) || each.Character is '/' or '\\' || each.Class is not WordQuoting.Open)
            || pattern.Matcher().MayBeginWith(character => character == '-'))
        {
            return null;
        }

        var segments = SegmentsOf(pattern);
        var chosen = false;

        foreach (var (_, piece) in segments)
        {
            if (!piece.Chooses)
            {
                if (chosen && piece.Parts is [WordPart.Text { Spelled: ".." }])
                {
                    return null;
                }

                continue;
            }

            var matcher = piece.Matcher();

            if (matcher.Spells(string.Empty) || matcher.Spells(".") || matcher.Spells("..") || matcher.MayEndWith(character => character == '.'))
            {
                return null;
            }

            chosen = true;
        }

        return segments;
    }

    /// <summary>The one spelling of a pattern that chooses nothing, with how its characters stand to the shell.</summary>
    private static (string Word, string Quoted) SpelledOnce(WordPattern pattern)
    {
        var word = string.Empty;
        var quoted = string.Empty;

        foreach (var part in pattern.Parts)
        {
            if (part is WordPart.Text text)
            {
                word += text.Spelled;
                quoted += text.Quoted.Length >= text.Spelled.Length ? text.Quoted[..text.Spelled.Length] : new string(WordQuoting.Open, text.Spelled.Length);
            }
        }

        return (word, quoted);
    }

    /// <summary>
    /// The choice of <paramref name="pattern"/>'s that spells a separator - a whole path, a directory the shell may stand
    /// in - where it holds one; null where none does, or where several do (such a word's spellings are decided one at a
    /// time, exactly).
    /// </summary>
    private static WordChoice? SeparatorChoice(WordPattern pattern)
    {
        WordChoice? carried = null;

        foreach (var choice in pattern.Parts.OfType<WordPart.Choice>())
        {
            if (new WordPattern([new WordPart.Choice(choice.Of)]).Matcher().MayBeginWith(character => character is '/' or '\\'))
            {
                if (carried is not null)
                {
                    return null;
                }

                carried = choice.Of;
            }
        }

        return carried;
    }

    /// <summary>
    /// <paramref name="pattern"/> with every <paramref name="choice"/> in it replaced by <paramref name="alternative"/>:
    /// the one way the word spells that the choice's alternatives leave where they are - the alternative's parts
    /// <paramref name="alternative"/> holds in the choice's place, a part of the pattern itself, never a choice around
    /// them, so a choice that spells a separator is not replaced by one again.
    /// </summary>
    private static WordPattern Substituted(WordPattern pattern, WordChoice choice, WordPattern alternative)
    {
        List<WordPart> parts = [];

        foreach (var part in pattern.Parts)
        {
            if (part is not WordPart.Choice each || !ReferenceEquals(each.Of, choice))
            {
                parts.Add(part);
                continue;
            }

            // The alternative's parts where the choice stands, each character of them standing to the shell as the
            // choice's did - a nested choice's own class first, as one spelling of it takes.
            parts.AddRange(alternative.Parts.Select(part => part switch
            {
                WordPart.Text text => new WordPart.Text(text.Spelled, each.Class is { } taken ? new string(taken, text.Spelled.Length) : text.Quoted),
                WordPart.Choice nested => nested with { Class = nested.Class ?? each.Class },
                _ => part,
            }));
        }

        return new WordPattern([.. parts]);
    }

    /// <summary>Whether some spelling of <paramref name="pattern"/> holds an unquoted star, question mark or bracket: a pattern the shell expands.</summary>
    private static bool MaySpellAGlob(WordPattern pattern)
        => PartsOf(pattern).Any(each => each.Character is '*' or '?' or '[' && each.Class is not WordQuoting.Literal and not WordQuoting.DoubleQuoted);

    /// <summary>Every character of a pattern's spellings, each with the class it takes, on one stack of its parts.</summary>
    private static IEnumerable<(char Character, char Class)> PartsOf(WordPattern pattern)
    {
        var waiting = new Stack<(WordPattern Pattern, char? Class)>();
        waiting.Push((pattern, null));
        var seen = new HashSet<WordPattern>(ReferenceEqualityComparer.Instance);

        while (waiting.TryPop(out var each))
        {
            if (!seen.Add(each.Pattern))
            {
                continue;
            }

            foreach (var part in each.Pattern.Parts)
            {
                if (part is WordPart.Choice choice)
                {
                    foreach (var alternative in choice.Of.Alternatives)
                    {
                        waiting.Push((alternative, choice.Class));
                    }
                }
                else if (part is WordPart.Text text)
                {
                    for (var at = 0; at < text.Spelled.Length; at++)
                    {
                        yield return (text.Spelled[at], each.Class is { } taken ? taken : text.Quoted.Length > at ? text.Quoted[at] : WordQuoting.Open);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A spelling of <paramref name="pattern"/> that, read as the shell reads a path pattern, may be
    /// <paramref name="place"/> or lie beneath it - with <paramref name="exactly"/>, may be the place itself - or null
    /// where none may: the pattern's segments and the place's read together, each pair of a spelling's state and a
    /// place's segments once (<see cref="GlobAgainst"/> lifted to a pattern). A <c>**</c> of many directories and a name
    /// a dot begins are read as <see cref="GlobReaches"/> reads them. The witness spells the pattern's own characters -
    /// its stars as it spells them - so what it names, the shell names by them.
    /// </summary>
    /// <param name="pattern">The word's pattern.</param>
    /// <param name="place">The place the pattern may reach.</param>
    /// <param name="dotNames">Whether a star matches a name a dot begins.</param>
    /// <param name="exactly">Whether the pattern may be the place itself, not reach into it.</param>
    private (string Word, string Quoted)? GlobWitness(WordPattern pattern, string place, bool dotNames, bool exactly = false)
    {
        var pieces = SegmentsOf(pattern);
        var placed = place.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        // Whether the pattern's first p pieces may be the place's first q segments, and how: a ** takes none of them or
        // any run of them, and each other piece some spelling of one segment, spelled as the piece spells it.
        var met = new (bool Reached, (int FromP, int FromQ, string Spelled)? From)[pieces.Count + 1, placed.Length + 1];
        met[0, 0] = (true, null);

        void Reach(int toP, int toQ, (int FromP, int FromQ, string Spelled) from)
        {
            if (!met[toP, toQ].Reached)
            {
                met[toP, toQ] = (true, from);
            }
        }

        for (var q = 0; q <= placed.Length; q++)
        {
            for (var p = 0; p <= pieces.Count; p++)
            {
                if (!met[p, q].Reached || p == pieces.Count)
                {
                    continue;
                }

                var (before, piece) = pieces[p];

                // A ** piece alone - a whole segment spelled exactly two stars - takes no segment or any run of them,
                // crossing a dot-named directory where a star matches one.
                if (before.Length == 0 && MaySpell(piece, "**"))
                {
                    Reach(p + 1, q, (p, q, "**"));

                    if (q < placed.Length && (dotNames || !placed[q].StartsWith('.')))
                    {
                        Reach(p, q + 1, (p, q, string.Empty));
                    }

                    continue;
                }

                if (q < placed.Length && (dotNames || MayBeginWithDot(piece) || !placed[q].StartsWith('.')) && SegmentSpells(piece, placed[q]) is { } spelled)
                {
                    Reach(p + 1, q + 1, (p, q, spelled));
                }
            }
        }

        // A path the pattern names reaches the place where the place is met with the pattern's rest still to go; it is
        // the place itself where they are met with nothing left of the pattern but its **'s.
        var reached = Enumerable.Range(0, pieces.Count + 1).Where(p => met[p, placed.Length].Reached).ToList();

        if (reached.Count == 0 || (exactly && !met[pieces.Count, placed.Length].Reached))
        {
            return null;
        }

        var end = exactly ? pieces.Count : reached.Max();

        // The pieces past the end are the pattern's rest still to go: spelled as they first spell themselves.
        var word = string.Empty;
        var quoted = string.Empty;

        for (var p = pieces.Count - 1; p >= end; p--)
        {
            var first = new WordPattern([.. pieces[p].Piece.Parts, new WordPart.Text(pieces[p].Before, new string(WordQuoting.Open, pieces[p].Before.Length))]).Spellings().FirstOrDefault();

            if (first.Word is not { } spelled)
            {
                return null;
            }

            word = spelled + word;
            quoted = new string(WordQuoting.Open, spelled.Length) + quoted;
        }

        for (var (p, q) = (end, placed.Length); p > 0 || q > 0;)
        {
            if (met[p, q].From is not { } from)
            {
                return null;
            }

            var (before, _) = pieces[from.FromP];
            word = before + from.Spelled + word;
            quoted = new string(WordQuoting.Open, before.Length + from.Spelled.Length) + quoted;
            (p, q) = (from.FromP, from.FromQ);
        }

        return (word, quoted);
    }

    /// <summary>Whether some spelling of <paramref name="piece"/> is <paramref name="text"/>, its characters taken one for one.</summary>
    private static bool MaySpell(WordPattern piece, string text)
    {
        var parts = PartsOf(piece).ToList();

        return parts.Count == text.Length && parts.Zip(text).All(pair => char.ToUpperInvariant(pair.First.Character) == char.ToUpperInvariant(pair.Second));
    }

    /// <summary>Whether some spelling of <paramref name="piece"/> begins with a dot.</summary>
    private static bool MayBeginWithDot(WordPattern piece) => piece.Matcher().MayBeginWith(character => character == '.');

    /// <summary>
    /// The pieces of <paramref name="pattern"/> a word of options names as its paths, where the whole word is not a plain
    /// path: the value after its first <c>=</c> (<c>--file=x</c>), a dash word's after its <c>:</c> (<c>-Path:x</c>), a
    /// short option's glued value (<c>-ix</c>) - each cut where its marker stands in the word's fixed text before the
    /// pattern's first choice, every choice the pattern holds lying past it (where one stands before the marker, or may
    /// spell one, the word is read whole). Empty where no marker stands there.
    /// </summary>
    private static IReadOnlyList<WordPattern> DerivedPieces(WordPattern pattern)
    {
        var text = string.Empty;

        foreach (var part in pattern.Parts)
        {
            if (part is WordPart.Choice)
            {
                break;
            }

            text += ((WordPart.Text)part).Spelled;
        }

        if (text.Length == 0 || !pattern.Chooses)
        {
            return [];
        }

        List<(int At, WordPattern Piece)> pieces = [];
        var cuts = new List<int>();

        if (text.IndexOf('=', StringComparison.Ordinal) is var equals && equals >= 0)
        {
            cuts.Add(equals);
        }

        if (text.StartsWith('-'))
        {
            if (text.IndexOf(':', StringComparison.Ordinal) is var colon && colon > 1)
            {
                cuts.Add(colon);
            }

            if (text.Length > 2 && text[1] != '-')
            {
                cuts.Add(1);
            }
        }

        foreach (var at in cuts.OrderBy(each => each))
        {
            if (Sliced(pattern, at + 1) is { } piece)
            {
                pieces.Add((at, piece));
            }
        }

        return [.. pieces.OrderBy(each => each.At).Select(each => each.Piece)];
    }

    /// <summary>
    /// <paramref name="pattern"/> from character <paramref name="from"/> on - its fixed text cut, every choice the
    /// pattern holds kept; null where a choice stands before <paramref name="from"/>, whose word is read whole.
    /// </summary>
    private static WordPattern? Sliced(WordPattern pattern, int from)
    {
        var parts = new List<WordPart>();
        var at = 0;

        foreach (var part in pattern.Parts)
        {
            if (part is WordPart.Text text)
            {
                if (at >= from)
                {
                    parts.Add(part);
                }
                else if (at + text.Spelled.Length > from)
                {
                    var start = from - at;
                    parts.Add(new WordPart.Text(text.Spelled[start..], text.Quoted.Length >= text.Spelled.Length ? text.Quoted[start..] : string.Empty));
                }

                at += text.Spelled.Length;
                continue;
            }

            if (at < from)
            {
                return null;
            }

            parts.Add(part);
        }

        return new WordPattern(parts);
    }

    /// <summary>
    /// A spelling of <paramref name="piece"/> that names <paramref name="name"/> - the piece's spellings and the name
    /// read together as the shell reads a path's segment (<see cref="SegmentPattern"/> lifted to a pattern: an unquoted
    /// star any run of characters, a question mark one, a bracket its members, every other character itself) - or null
    /// where none does. The spelling returned is the piece's own characters, its stars as it spells them; a bracket is
    /// read by <see cref="Bracket"/> as the segment expression reads it.
    /// </summary>
    private static string? SegmentSpells(WordPattern piece, string name)
    {
        var spelled = new System.Text.StringBuilder();

        // The states of the walk - a parts list met again at the same part, character and name place answers as it did
        // before, the class its characters take with it: keyed by the list's identity, for the same indices in two lists
        // are two states.
        var seen = new HashSet<(IReadOnlyList<WordPart> Parts, int Part, int At, int Name, char? Taken)>();

        bool Walk(IReadOnlyList<WordPart> parts, int part, int at, char? taken, int named)
        {
            while (true)
            {
                if (named == name.Length)
                {
                    // The name is met: the rest of the piece must spell nothing.
                    return RestSpellsNothing(parts, part, at);
                }

                if (part >= parts.Count || !seen.Add((parts, part, at, named, taken)))
                {
                    return false;
                }

                if (parts[part] is WordPart.Text text)
                {
                    if (at >= text.Spelled.Length)
                    {
                        part++;
                        at = 0;
                        continue;
                    }

                    var character = text.Spelled[at];
                    var @class = taken ?? (text.Quoted.Length > at ? text.Quoted[at] : WordQuoting.Open);

                    // An unquoted star: any run of the name's characters, the star itself the spelling of it.
                    if (character == '*' && @class == WordQuoting.Open)
                    {
                        for (var run = name.Length; run >= named; run--)
                        {
                            spelled.Append('*');

                            if (Walk(parts, part, at + 1, taken, run))
                            {
                                return true;
                            }

                            spelled.Length--;
                        }

                        return false;
                    }

                    // An unquoted question mark: one character, whichever it is.
                    if (character == '?' && @class == WordQuoting.Open)
                    {
                        spelled.Append('?');

                        if (Walk(parts, part, at + 1, taken, named + 1))
                        {
                            return true;
                        }

                        spelled.Length--;

                        return false;
                    }

                    // An unquoted bracket: its members, as the segment expression reads them.
                    if (character == '[' && @class == WordQuoting.Open && Bracket(text.Spelled, at) is var (closing, members) && closing > 0)
                    {
                        var expression = LinearRegex.Compile("^" + members + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (expression.IsMatch(name[named].ToString()))
                        {
                            spelled.Append(text.Spelled[at..(closing + 1)]);

                            if (Walk(parts, part, closing + 1, taken, named + 1))
                            {
                                return true;
                            }

                            spelled.Length -= closing + 1 - at;
                        }

                        return false;
                    }

                    // Any other character is itself, in any case.
                    if (char.ToUpperInvariant(character) != char.ToUpperInvariant(name[named]))
                    {
                        return false;
                    }

                    spelled.Append(character);
                    at++;
                    named++;
                    continue;
                }

                // A choice: each of its alternatives may spell what follows, its class where it gives one.
                var choice = (WordPart.Choice)parts[part];
                var before = spelled.Length;

                foreach (var alternative in choice.Of.Alternatives)
                {
                    if (Walk(alternative.Parts, 0, 0, choice.Class ?? taken, named))
                    {
                        return true;
                    }

                    spelled.Length = before;
                }

                return false;
            }
        }

        return Walk(piece.Parts, 0, 0, null, 0) ? spelled.ToString() : null;
    }

    /// <summary>Whether nothing more of <paramref name="parts"/> from (<paramref name="part"/>, <paramref name="at"/>) spells a character.</summary>
    private static bool RestSpellsNothing(IReadOnlyList<WordPart> parts, int part, int at)
    {
        for (var each = part; each < parts.Count; each++)
        {
            if (parts[each] is not WordPart.Text text)
            {
                return false;
            }

            if (each == part ? text.Spelled.Length > at : text.Spelled.Length > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The places <paramref name="pattern"/> reaches from the places <paramref name="forms"/> hold, as forms: a pattern
    /// that chooses nothing a path read whole (<see cref="Reached"/>); one whose choices spell names and separators a
    /// form whose steps are those choices - each alternative a way of the step it spans, an empty one a step that may be
    /// passed - and one that stands for a directory the shell may stand in (<see cref="WordChoice.Forms"/>) the forms of
    /// that directory, what follows appended to them. One form per way the pattern goes, however many places it spells:
    /// never a place spelled out.
    /// </summary>
    /// <param name="pattern">The word's pattern, as the command's reading spells it.</param>
    /// <param name="forms">The places the command may stand in.</param>
    /// <param name="tildeExpands">Whether the shell expands a tilde at the word's start.</param>
    private IReadOnlyList<PlaceForm> FormsOf(WordPattern pattern, IReadOnlyList<PlaceForm> forms, bool tildeExpands = true)
    {
        var (word, quoted) = SpelledOnce(pattern);

        if (!pattern.Chooses)
        {
            return Reached(forms, word, pattern: false, tildeExpands);
        }

        // The pieces between the pattern's choices, and the choices themselves, in order: a run of fixed text, then a
        // choice, each alternative of which is walked as the places it reaches.
        IReadOnlyList<PlaceForm> reached = forms;
        var text = string.Empty;
        var home = tildeExpands && CommandAnalysis.TildeExpands(word, quoted);

        foreach (var part in pattern.Parts)
        {
            if (part is WordPart.Text fixedText)
            {
                // The text before the first choice names where the walk begins: home-expanded where it leads so.
                if (text.Length == 0 && ReferenceEquals(reached, forms))
                {
                    text = fixedText.Spelled;
                    continue;
                }

                var sofar = text + fixedText.Spelled;
                reached = PlaceForm.Union(reached.SelectMany(form => StepsOf(form, sofar, home)));
                text = string.Empty;
                home = false;
                continue;
            }

            var choice = (WordPart.Choice)part;

            // A choice that stands for a directory the shell may stand in: its forms are the places, what follows
            // appended to them.
            if (choice.Of.Forms is { } stands)
            {
                reached = PlaceForm.Union(stands);
                text = string.Empty;
                home = false;
                continue;
            }

            // A choice of the paths it may spell: each alternative a way of the step from the places so far.
            var ways = new List<IReadOnlyList<string>>();

            foreach (var alternative in choice.Of.Alternatives)
            {
                var (spelled, _) = SpelledOnce(alternative);

                // An alternative that itself chooses is walked as the places it reaches: one alternative, one form-set.
                if (alternative.Chooses)
                {
                    reached = PlaceForm.Union(reached.SelectMany(form => Reached([form], spelled, pattern: false, tildeExpands: false)));
                    continue;
                }

                if (spelled.Length == 0)
                {
                    continue;
                }

                ways.Add([.. RelativeSegments(spelled)]);
            }

            if (ways.Count > 0)
            {
                reached = PlaceForm.Union(reached.Select(form => form.Stepped(ways, optional: ways.Count != choice.Of.Alternatives.Count)));
            }

            text = string.Empty;
            home = false;
        }

        if (text.Length > 0)
        {
            reached = PlaceForm.Union(reached.SelectMany(form => StepsOf(form, text, home)));
        }

        return reached;
    }

    /// <summary>The form with <paramref name="text"/> walked from it: rooted, its whole read from the working directory; relative, its segments one step of the form.</summary>
    private IReadOnlyList<PlaceForm> StepsOf(PlaceForm form, string text, bool tildeExpands)
        => Reached([form], text, pattern: false, tildeExpands);

    /// <summary>The non-empty segments of a relative alternative, in order.</summary>
    private static IReadOnlyList<string> RelativeSegments(string spelled)
    {
        var segments = new List<string>();

        foreach (var segment in spelled.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != "."))
        {
            segments.Add(segment);
        }

        return segments;
    }

    /// <summary>
    /// The segments of <paramref name="pattern"/>, each with the separators before it - a choice kept whole in the piece
    /// it stands in, as <see cref="WordPattern.CutAt"/> cuts, the separators' own spellings with them.
    /// </summary>
    private static IReadOnlyList<(string Before, WordPattern Piece)> SegmentsOf(WordPattern pattern)
    {
        var pieces = new List<(string, WordPattern)>();
        var before = string.Empty;
        var parts = new List<WordPart>();

        foreach (var part in pattern.Parts)
        {
            if (part is not WordPart.Text text)
            {
                parts.Add(part);
                continue;
            }

            var at = 0;

            while (at < text.Spelled.Length)
            {
                var run = at;

                while (run < text.Spelled.Length && text.Spelled[run] is not ('/' or '\\'))
                {
                    run++;
                }

                if (run > at)
                {
                    parts.Add(new WordPart.Text(text.Spelled[at..run], text.Quoted.Length >= run ? text.Quoted[at..run] : string.Empty));
                }

                if (run == text.Spelled.Length)
                {
                    break;
                }

                var end = run;

                while (end < text.Spelled.Length && text.Spelled[end] is '/' or '\\')
                {
                    end++;
                }

                pieces.Add((before, new WordPattern([.. parts])));
                parts.Clear();
                before = text.Spelled[run..end];
                at = end;
            }
        }

        pieces.Add((before, new WordPattern([.. parts])));
        return pieces;
    }
}
