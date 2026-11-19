using System.Collections.Immutable;
using System.Text;
using DssHarness.Core.Walks;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// A word as the shell may spell it, kept as it is written rather than spelled out: a sequence of parts, each a piece of
/// text or a choice among alternatives - a brace group's, each a pattern of its own since groups nest, or the values a
/// variable may hold. A choice is one wherever it stands: a variable read in two words of a command holds the same value in
/// both, as the shell substitutes one value of it, so the spellings of a command are the ways its choices may be made -
/// what is true - never every word's alternatives multiplied apart. Spellings are given one at a time, never held all at
/// once, and a question of every spelling a word may have is answered of the pattern as an automaton
/// (<see cref="MayNameProgram"/>): <c>{a,b}</c> written forty times is forty choices, never 2^40 words.
/// </summary>
/// <param name="Parts">The parts, in order.</param>
public sealed record WordPattern(IReadOnlyList<WordPart> Parts)
{
    /// <summary>The pattern of one piece of text, each character standing to the shell as <paramref name="quoted"/> says.</summary>
    /// <param name="text">The text.</param>
    /// <param name="quoted">How each of its characters stands to the shell (<see cref="WordQuoting"/>), in step.</param>
    public static WordPattern Of(string text, string quoted) => new([new WordPart.Text(text, quoted)]);

    /// <summary>Whether the pattern makes a choice anywhere: one that makes none has one spelling.</summary>
    public bool Chooses => Parts.Any(part => part is WordPart.Choice);

    /// <summary>The pattern of <paramref name="first"/>'s parts, then <paramref name="second"/>'s: a choice of either the same choice in both.</summary>
    public static WordPattern Joined(WordPattern first, WordPattern second) => new([.. first.Parts, .. second.Parts]);

    /// <summary>
    /// The pattern of <paramref name="word"/>'s brace groups, as <see cref="CommandAnalysis.BraceExpansions"/> spells
    /// them: each group of alternatives a choice of its own, a group nested in an alternative a choice within it. Null where
    /// the word holds no group the shell expands, or where its braces do not pair - a word whose spellings are then told
    /// one at a time, by the expansion itself.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <param name="quoted">
    /// How each of the word's characters stands to the shell (<see cref="WordQuoting"/>), or null where its spelling was
    /// not kept: each character a spelling holds keeps the class it has in the word.
    /// </param>
    public static WordPattern? Braces(string word, string? quoted = null)
    {
        ArgumentNullException.ThrowIfNull(word);

        return Paired(word) && CommandAnalysis.BraceGroup(word) is not null ? BracesOf(word, CommandAnalysis.ClassesOf(word, quoted)) : null;
    }

    /// <summary>
    /// <paramref name="text"/>'s pattern: the text before its first group, the group's alternatives as one choice, then
    /// what follows - each alternative and what follows read alike, a level deeper through <see cref="DeepCalls"/>, so no
    /// nesting of groups is refused or overflows the stack. Each character keeps its class of <paramref name="mask"/>.
    /// </summary>
    private static WordPattern BracesOf(string text, string mask) => DeepCalls.Run(() =>
    {
        if (CommandAnalysis.BraceGroup(text) is not { } group)
        {
            return Of(text, mask);
        }

        // Each alternative is the text between the group's commas, so its mask is the same slice of the word's.
        var alternatives = new List<WordPattern>();
        var start = group.Open + 1;

        foreach (var alternative in group.Alternatives)
        {
            alternatives.Add(BracesOf(alternative, mask.Substring(start, alternative.Length)));
            start += alternative.Length + 1;
        }

        var choice = new WordChoice(alternatives);
        var before = text[..group.Open];
        IReadOnlyList<WordPart> parts = before.Length > 0 ? [new WordPart.Text(before, mask[..group.Open]), new WordPart.Choice(choice)] : [new WordPart.Choice(choice)];

        return Joined(new WordPattern(parts), BracesOf(text[(group.Close + 1)..], mask[(group.Close + 1)..]));
    });

    /// <summary>
    /// The pattern cut where its own text holds a run of <paramref name="separator"/>'s characters: each piece the run
    /// before it - empty for the first - and the parts between, a choice kept whole in the piece it stands in. A path's
    /// segments, for a pattern whose choices spell no separator.
    /// </summary>
    /// <param name="separator">Whether a character separates.</param>
    internal IReadOnlyList<(string Before, WordPattern Piece)> CutAt(Func<char, bool> separator)
    {
        ArgumentNullException.ThrowIfNull(separator);

        var pieces = new List<(string, WordPattern)>();
        var before = string.Empty;
        var parts = new List<WordPart>();

        foreach (var part in Parts)
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

                while (run < text.Spelled.Length && !separator(text.Spelled[run]))
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

                while (end < text.Spelled.Length && separator(text.Spelled[end]))
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

    /// <summary>Whether every character a choice of the pattern may spell - at any depth - is one <paramref name="allowed"/> takes.</summary>
    /// <param name="allowed">Whether a character is allowed.</param>
    internal bool ChoicesSpellOnly(Func<char, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        var waiting = new Stack<WordPattern>(Parts.OfType<WordPart.Choice>().SelectMany(choice => choice.Of.Alternatives));
        var seen = new HashSet<WordChoice>(ReferenceEqualityComparer.Instance);

        while (waiting.TryPop(out var pattern))
        {
            foreach (var part in pattern.Parts)
            {
                switch (part)
                {
                    case WordPart.Text text when !text.Spelled.All(allowed):
                        return false;
                    case WordPart.Choice choice when seen.Add(choice.Of):
                        foreach (var alternative in choice.Of.Alternatives)
                        {
                            waiting.Push(alternative);
                        }

                        break;
                }
            }
        }

        return true;
    }

    /// <summary>The pattern as an automaton (<see cref="WordMatcher"/>), built once for the questions asked of it.</summary>
    internal WordMatcher Matcher() => new(Automaton.Of(this));

    /// <summary>
    /// Whether every brace of <paramref name="word"/> pairs, each <c>}</c> closing a <c>{</c> before it and none left open:
    /// in such a word an alternative, and what stands around its group, pairs its own braces, so the groups the shell
    /// expands after one is spelled are the ones its pattern holds.
    /// </summary>
    private static bool Paired(string word)
    {
        var open = 0;

        foreach (var character in word)
        {
            open += character switch { '{' => 1, '}' => -1, _ => 0 };

            if (open < 0)
            {
                return false;
            }
        }

        return open == 0;
    }

    /// <summary>
    /// Every spelling of <paramref name="words"/> together, one for each way their choices may be made - a choice met in
    /// two of them made once, the same in both - each a word for each pattern, with how its characters stand to the
    /// shell. Given lazily, depth first, on a stack of its own: a caller that stops at the first spelling it refuses
    /// spells no more, and nothing is held but the way to the spelling at hand.
    /// </summary>
    /// <param name="words">The patterns, in order.</param>
    public static IEnumerable<IReadOnlyList<(string Word, string Quoted)>> Spellings(IReadOnlyList<WordPattern> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        // What is still to be spelled, as a stack of items: a part, with the class every character of it takes where a
        // variable's value stands in a word (the class of the dollar it stands for), or the end of a word.
        var pending = ImmutableStack<Pending>.Empty;

        for (var at = words.Count - 1; at >= 0; at--)
        {
            pending = pending.Push(Pending.EndOfWord);
            pending = Pushed(pending, words[at].Parts, null);
        }

        var ways = new Stack<Way>();
        ways.Push(new Way(pending, ImmutableList<(string, string)>.Empty, string.Empty, string.Empty, ImmutableDictionary<WordChoice, int>.Empty));

        while (ways.TryPop(out var way))
        {
            var (left, spelled, text, quoted, chosen) = way;
            var branched = false;

            while (!left.IsEmpty)
            {
                left = left.Pop(out var item);

                if (item.Part is null)
                {
                    spelled = spelled.Add((text, quoted));
                    (text, quoted) = (string.Empty, string.Empty);
                    continue;
                }

                if (item.Part is WordPart.Text piece)
                {
                    text += piece.Spelled;
                    quoted += item.Class is { } its ? new string(its, piece.Spelled.Length) : piece.Quoted;
                    continue;
                }

                var part = (WordPart.Choice)item.Part;
                var choice = part.Of;
                var alternatives = choice.Alternatives;
                var taken = part.Class ?? item.Class;

                if (chosen.TryGetValue(choice, out var made))
                {
                    left = Pushed(left, alternatives[made].Parts, taken);
                    continue;
                }

                // Each alternative a way of its own, the first given first.
                for (var at = alternatives.Count - 1; at >= 0; at--)
                {
                    ways.Push(new Way(Pushed(left, alternatives[at].Parts, taken), spelled, text, quoted, chosen.Add(choice, at)));
                }

                branched = true;
                break;
            }

            if (!branched)
            {
                yield return spelled;
            }
        }
    }

    /// <summary><paramref name="parts"/> on top of <paramref name="pending"/>, the first on top, each taking <paramref name="taken"/>'s class where one is given.</summary>
    private static ImmutableStack<Pending> Pushed(ImmutableStack<Pending> pending, IReadOnlyList<WordPart> parts, char? taken)
    {
        for (var at = parts.Count - 1; at >= 0; at--)
        {
            pending = pending.Push(new Pending(parts[at], taken));
        }

        return pending;
    }

    /// <summary>The spellings of this one pattern (<see cref="Spellings(IReadOnlyList{WordPattern})"/>), each with how its characters stand to the shell.</summary>
    public IEnumerable<(string Word, string Quoted)> Spellings() => Spellings([this]).Select(spelled => spelled[0]);

    /// <summary>
    /// Whether some spelling of the pattern names <paramref name="program"/>, in any case, as
    /// <see cref="CommandAnalysis.Name"/> reads a word: its last segment - past the <c>@</c>s cmd reads before a command and
    /// before any separators after it - with one of a Windows program's ends folded away (<c>sudo.exe</c>). Answered of the
    /// pattern as an automaton: the states it may stand in where a segment begins are found once, the name and each end
    /// read from them, and whether nothing but separators may follow - in time linear in the pattern, however many
    /// spellings it has.
    /// </summary>
    /// <param name="program">The program's name.</param>
    /// <param name="ends">The ends a program's name is folded of, as <see cref="CommandAnalysis.Name"/> folds one.</param>
    internal bool MayNameProgram(string program, IReadOnlyList<string> ends)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        ArgumentNullException.ThrowIfNull(ends);

        var automaton = Automaton.Of(this);

        // Where a last segment may begin: the start, past any '@'s, and wherever a separator was just read.
        var begins = automaton.Closure([automaton.Start]);
        begins = automaton.Closure([.. begins, .. automaton.Repeated(begins, character => character == '@')]);
        var reached = automaton.Reachable();
        begins.UnionWith(automaton.Closure(automaton.Step(reached, character => character is '/' or '\\')));

        foreach (var end in (IEnumerable<string>)["", .. ends])
        {
            var states = automaton.Read(begins, program + end);

            if (states.Count > 0 && automaton.Repeated(automaton.Closure(states), character => character is '/' or '\\').Concat(states).Any(state => state == automaton.Accept))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The questions asked of a pattern's spellings as a set, each answered of its automaton in time linear in the pattern
    /// (and in the names given), however many spellings it has: whether it spells a name, what it may begin or end with,
    /// and a spelling none of a set of names is.
    /// </summary>
    internal sealed class WordMatcher
    {
        private readonly Automaton _automaton;

        internal WordMatcher(Automaton automaton) => _automaton = automaton;

        /// <summary>Whether some spelling is <paramref name="text"/>, in any case unless <paramref name="exactly"/>.</summary>
        public bool Spells(string text, bool exactly = false) => _automaton.Read([_automaton.Start], text, exactly).Contains(_automaton.Accept);

        /// <summary>Whether some spelling begins with a character <paramref name="read"/> takes.</summary>
        public bool MayBeginWith(Func<char, bool> read) => _automaton.Step(_automaton.Closure([_automaton.Start]), read).Any();

        /// <summary>Whether some spelling ends with a character <paramref name="read"/> takes.</summary>
        public bool MayEndWith(Func<char, bool> read) => _automaton.Closure(_automaton.Step(_automaton.Reachable(), read)).Contains(_automaton.Accept);

        /// <summary>
        /// A spelling that is none of <paramref name="names"/> in any case, or null where every spelling is one of them:
        /// the automaton read beside a tree of the names, the first spelling found that leaves the tree or stops short of
        /// a name's end - one walk over the pairs of a state and a place in the tree, whatever the number of spellings.
        /// </summary>
        public string? SpellingBesides(IEnumerable<string> names)
        {
            ArgumentNullException.ThrowIfNull(names);

            // The names as a tree of their characters in upper case: a node's children, and whether a name ends there.
            var children = new List<Dictionary<char, int>> { new() };
            var ends = new List<bool> { false };

            foreach (var name in names)
            {
                var node = 0;

                foreach (var character in name)
                {
                    if (!children[node].TryGetValue(char.ToUpperInvariant(character), out var child))
                    {
                        child = children.Count;
                        children.Add([]);
                        ends.Add(false);
                        children[node][char.ToUpperInvariant(character)] = child;
                    }

                    node = child;
                }

                ends[node] = true;
            }

            // Out of the tree: a spelling no name begins with.
            const int Out = -1;
            var start = (_automaton.Start, 0);
            var reachedFrom = new Dictionary<(int State, int Node), ((int State, int Node) From, char? Read)> { [start] = (start, null) };
            var waiting = new Queue<(int State, int Node)>([start]);

            while (waiting.TryDequeue(out var at))
            {
                if (at.State == _automaton.Accept && (at.Node == Out || !ends[at.Node]))
                {
                    var spelled = new StringBuilder();

                    for (var back = at; back != start; back = reachedFrom[back].From)
                    {
                        if (reachedFrom[back].Read is { } read)
                        {
                            spelled.Insert(0, read);
                        }
                    }

                    return spelled.ToString();
                }

                foreach (var to in _automaton.FreeOf(at.State))
                {
                    if (reachedFrom.TryAdd((to, at.Node), (at, null)))
                    {
                        waiting.Enqueue((to, at.Node));
                    }
                }

                foreach (var (character, to) in _automaton.ReadsOf(at.State))
                {
                    var node = at.Node == Out ? Out : children[at.Node].TryGetValue(char.ToUpperInvariant(character), out var child) ? child : Out;

                    if (reachedFrom.TryAdd((to, node), (at, character)))
                    {
                        waiting.Enqueue((to, node));
                    }
                }
            }

            return null;
        }
    }

    /// <summary>What is still to be spelled: a part and the class its characters take, or the end of a word (no part).</summary>
    private readonly record struct Pending(WordPart? Part, char? Class)
    {
        public static Pending EndOfWord => new(null, null);
    }

    /// <summary>One way of spelling being followed: what is left, the words spelled, the word at hand, and the choices made.</summary>
    private readonly record struct Way(ImmutableStack<Pending> Left, ImmutableList<(string, string)> Spelled, string Text, string Quoted, ImmutableDictionary<WordChoice, int> Chosen);

    /// <summary>
    /// The pattern as a nondeterministic automaton over its characters: a state between each two, a choice a branch to
    /// each alternative's own and back. A choice met twice is two branches here - the automaton answers what any spelling
    /// may be, every choice made apart - which is what it is asked of a brace word, whose every group is a choice of its
    /// own.
    /// </summary>
    internal sealed class Automaton
    {
        private readonly List<List<(char Character, int To)>> _reads = [];
        private readonly List<List<int>> _free = [];

        private Automaton()
        {
        }

        public int Start { get; private set; }

        public int Accept { get; private set; }

        public static Automaton Of(WordPattern pattern)
        {
            var automaton = new Automaton();
            automaton.Start = automaton.State();

            // Each pattern built from where it begins to where it ends, on a stack of its own: a pattern waiting while a
            // choice's alternatives are built, each from the choice's start to its end.
            var built = new Stack<(WordPattern Pattern, int From, int To)>();
            automaton.Accept = automaton.State();
            built.Push((pattern, automaton.Start, automaton.Accept));

            while (built.TryPop(out var each))
            {
                var at = each.From;

                for (var index = 0; index < each.Pattern.Parts.Count; index++)
                {
                    var next = index == each.Pattern.Parts.Count - 1 ? each.To : automaton.State();

                    switch (each.Pattern.Parts[index])
                    {
                        case WordPart.Text text:
                            var from = at;

                            for (var character = 0; character < text.Spelled.Length; character++)
                            {
                                var to = character == text.Spelled.Length - 1 ? next : automaton.State();
                                automaton._reads[from].Add((text.Spelled[character], to));
                                from = to;
                            }

                            if (text.Spelled.Length == 0)
                            {
                                automaton._free[at].Add(next);
                            }

                            break;
                        case WordPart.Choice choice:
                            foreach (var alternative in choice.Of.Alternatives)
                            {
                                if (alternative.Parts.Count == 0)
                                {
                                    automaton._free[at].Add(next);
                                }
                                else
                                {
                                    built.Push((alternative, at, next));
                                }
                            }

                            break;
                    }

                    at = next;
                }

                if (each.Pattern.Parts.Count == 0)
                {
                    automaton._free[each.From].Add(each.To);
                }
            }

            return automaton;
        }

        /// <summary>The states <paramref name="state"/> leads to reading nothing.</summary>
        public IReadOnlyList<int> FreeOf(int state) => _free[state];

        /// <summary>The characters <paramref name="state"/> reads, and where each leads.</summary>
        public IReadOnlyList<(char Character, int To)> ReadsOf(int state) => _reads[state];

        private int State()
        {
            _reads.Add([]);
            _free.Add([]);
            return _reads.Count - 1;
        }

        /// <summary><paramref name="states"/> and every state reached from them reading nothing.</summary>
        public HashSet<int> Closure(IEnumerable<int> states)
        {
            var closed = new HashSet<int>();
            var waiting = new Stack<int>(states);

            while (waiting.TryPop(out var state))
            {
                if (closed.Add(state))
                {
                    foreach (var to in _free[state])
                    {
                        waiting.Push(to);
                    }
                }
            }

            return closed;
        }

        /// <summary>The states one character <paramref name="read"/> takes leads to from <paramref name="states"/>, before any closure.</summary>
        public IEnumerable<int> Step(IEnumerable<int> states, Func<char, bool> read)
            => states.SelectMany(state => _reads[state].Where(each => read(each.Character)).Select(each => each.To));

        /// <summary>Every state reached from <paramref name="states"/> reading any number of characters <paramref name="read"/> takes, each closed.</summary>
        public HashSet<int> Repeated(IEnumerable<int> states, Func<char, bool> read)
        {
            var reached = Closure(states);
            var waiting = new Stack<int>(reached);

            while (waiting.TryPop(out var state))
            {
                foreach (var to in Closure(Step([state], read)))
                {
                    if (reached.Add(to))
                    {
                        waiting.Push(to);
                    }
                }
            }

            return reached;
        }

        /// <summary>Every state reached from the start reading anything.</summary>
        public HashSet<int> Reachable() => Repeated([Start], _ => true);

        /// <summary>The states reached from <paramref name="states"/> reading <paramref name="text"/>, in any case unless <paramref name="exactly"/>; empty where none is.</summary>
        public HashSet<int> Read(IEnumerable<int> states, string text, bool exactly = false)
        {
            var current = Closure(states);

            foreach (var character in text)
            {
                current = Closure(Step(current, read => exactly ? read == character : char.ToUpperInvariant(read) == char.ToUpperInvariant(character)));

                if (current.Count == 0)
                {
                    break;
                }
            }

            return current;
        }
    }
}

/// <summary>One part of a <see cref="WordPattern"/>.</summary>
public abstract record WordPart
{
    /// <summary>A piece of text, with how each of its characters stands to the shell (<see cref="WordQuoting"/>), in step.</summary>
    /// <param name="Spelled">The text.</param>
    /// <param name="Quoted">How each character stands.</param>
    public sealed record Text(string Spelled, string Quoted) : WordPart;

    /// <summary>
    /// A choice among alternatives, one wherever it stands (<see cref="WordChoice"/>): where it stands for a variable's
    /// value, every character of the alternative taken stands to the shell as the dollar it stands for did
    /// (<paramref name="Class"/>); as each alternative spells it where no class is given.
    /// </summary>
    /// <param name="Of">The choice.</param>
    /// <param name="Class">The class every character it spells takes, or null where each keeps its own.</param>
    public sealed record Choice(WordChoice Of, char? Class = null) : WordPart;
}

/// <summary>
/// A choice among alternatives, each a <see cref="WordPattern"/>, known by its identity alone: wherever the same choice
/// stands, in one word or in several, one alternative is taken for all of them - a variable's one value, read twice.
/// </summary>
/// <param name="alternatives">The alternatives, in order.</param>
public sealed class WordChoice(IReadOnlyList<WordPattern> alternatives)
{
    private readonly IReadOnlyList<AgentPolicy.PlaceForm>? _forms;

    /// <summary>The alternatives, in order.</summary>
    public IReadOnlyList<WordPattern> Alternatives { get; } = alternatives ?? throw new ArgumentNullException(nameof(alternatives));

    /// <summary>
    /// A choice that stands for a directory the shell may stand in - <c>PWD</c>'s own - each alternative the pattern of
    /// the places one form holds: a decision that walks a path reads the forms themselves, at one place of each way they
    /// stand, never every alternative.
    /// </summary>
    /// <param name="alternatives">The alternatives, in order.</param>
    /// <param name="forms">The forms, one per alternative.</param>
    internal WordChoice(IReadOnlyList<WordPattern> alternatives, IReadOnlyList<AgentPolicy.PlaceForm> forms) : this(alternatives) => _forms = forms;

    /// <summary>
    /// The places the alternatives stand for, one form per alternative, where the choice is a directory the shell may
    /// stand in; null where the alternatives are the choice entire.
    /// </summary>
    internal IReadOnlyList<AgentPolicy.PlaceForm>? Forms => _forms;
}
