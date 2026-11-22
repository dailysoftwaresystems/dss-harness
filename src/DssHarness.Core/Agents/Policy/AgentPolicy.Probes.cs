using DssHarness.Core.Hosts;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The readings of a command that hold several values a variable may take, however many: a command need not be spelled
/// once for every way its variables' choices may be made - 2^k readings for k choices - for a decision of a word asks of
/// it only what its spellings name, and what they name is decided by the pattern
/// (<see cref="AgentPolicy.Decisive"/>). A choice whose alternatives are plain paths that name no program any analysis
/// reads is held as a core - one text standing for every alternative, mapped back to the choice wherever the word is
/// decided - and every other choice is spelled one alternative at a time. The command as it is spelled comes first, so
/// what reads the words as written - the run rules, the mark, the web addresses - reads them whole.
/// </summary>
public sealed partial class AgentPolicy
{
    /// <summary>
    /// The programs and marks whose words an analysis of a command matches by their names: a word whose pattern may name
    /// one is spelled, one alternative at a time, never held as a core - for its words are read, not only named.
    /// </summary>
    internal static readonly IReadOnlyList<string> ReadsWordsOf =    [
        .. RootActions,
        .. GitStateVerbs,
        .. GitStateForms.SelectMany(form => form.Split(' ')),
        .. GhCommands,
        .. GitCommands,
        .. GhStateCommands,
        .. GhOutOfStepCommands,
        .. CredentialPrintingCommands,
        .. ToolVerbs,
        .. SlotOrStateVerbs,
        .. OpenVerbs,
        .. LiveRunHeld,
        .. LiveRunOpen,
        .. CommandAnalysis.FileWriters,
        .. CommandAnalysis.CopyWriters,
        .. CommandAnalysis.InPlaceEditors,
        .. CommandAnalysis.OutputWriters,
        .. CommandAnalysis.Shells,
        .. new[] { AgentChildEnvironment.AgentMarker, ToolPackage.Command },
        .. new[]
        {
            "cat", "echo", "printf", "tee", "dd", "source", ".", "cd", "pushd", "chdir", "export", "declare", "typeset", "local", "readonly",
            "read", "eval", "exec", "env", "nohup", "setsid", "wsl", "schtasks", "sudo", "doas", "su", "find", "xargs", "ssh", "scp", "sftp", "fish",
        },
    ];

    /// <summary>ReadsWordsOf as one tree of names, each with the ends a program's name is folded of, built once: one walk of a word's pattern answers whether it may name one of them.</summary>
    private static WordPattern.NameTree? _readWords;

    /// <summary><see cref="ReadsWordsOf"/> as one tree of names (<see cref="WordPattern.NameTree.Of"/>), each name holding every end it is folded of: asked of a word's pattern whole, never one name at a time.</summary>
    private static WordPattern.NameTree ReadWords => _readWords ??= WordPattern.NameTree.Of(
        ReadsWordsOf.SelectMany(program => (IEnumerable<string>)[program, .. CommandAnalysis.ProgramEnds.Select(end => program + end)]));

    /// <summary>
    /// The cores of one decision of a string: each choice the readings hold as one text standing for its alternatives,
    /// and each core's choice and word where it holds one. Lives for one decision of a string: a core is decided
    /// by the readings it was made for.
    /// </summary>
    private sealed class Probes
    {
        /// <summary>The core each choice is held as, by the choice's identity.</summary>
        public Dictionary<WordChoice, string> CoreOf { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The choice each core stands for, by its text.</summary>
        public Dictionary<string, WordChoice> ChoiceOf { get; } = new(StringComparer.Ordinal);

        private int _given;

        /// <summary>
        /// The core for <paramref name="choice"/>: one made where it was made before - the same choice is the same core
        /// wherever it stands - its text naming no file (a character no path holds ends it). The core's text is its
        /// sentinel alone, never a root before it: it is read back by the sentinel it holds (<see cref="Templated"/>),
        /// and the alternatives it stands for carry their own root.
        /// </summary>
        public string Core(WordChoice choice)
        {
            if (CoreOf.TryGetValue(choice, out var core))
            {
                return core;
            }

            core = $"\u0001zzh{++_given}\u0001";
            CoreOf[choice] = core;
            ChoiceOf[core] = choice;

            return core;
        }
    }

    /// <summary>
    /// The definition the readings are given by, for the test that both decide alike: true spells every way the
    /// variables' choices may be made, one reading each, the definition the probes (<see cref="ProbesCore"/>) answer for
    /// without spelling; false, as the policy runs, holds as one core a choice whose alternatives name nothing the
    /// analysis reads.
    /// </summary>
    internal static bool EveryReading { get; set; }

    /// <summary>
    /// The definition a word pattern is decided by, for the test that both decide alike: true spells every spelling of
    /// it, one at a time, where the policy (<see cref="Decisive"/>) reads only those that decide it.
    /// </summary>
    internal static bool EverySpelling { get; set; }

    /// <summary>
    /// The cores of the decision of the string being read; null outside a decision of a string. Held per decision by
    /// the decision's own asynchronous flow, as the policy's other per-decision keeps are: the calls of a turn are
    /// decided at once, each its own decision.
    /// </summary>
    private readonly AsyncLocal<Probes?> _probes = new();

    /// <summary>
    /// <paramref name="word"/> with every whole core in it standing for the choice it was made for: the pattern of the
    /// paths it may name. Null where the word holds no core, or only part of one - a derivation of it cut through a
    /// core, which is spelled instead.
    /// </summary>
    private WordPattern? Templated(string word)
    {
        if (!word.Contains('\u0001', StringComparison.Ordinal) || _probes.Value is not { } probes)
        {
            return null;
        }

        var parts = new List<WordPart>();
        var at = 0;

        while (at < word.Length)
        {
            var end = word.IndexOf('\u0001', at);

            if (end < 0)
            {
                parts.Add(new WordPart.Text(word[at..], new string(WordQuoting.Open, word.Length - at)));
                break;
            }

            var after = word.IndexOf('\u0001', end + 1);

            if (after < 0)
            {
                // Half a core: a derivation cut through it, and the word is spelled.
                return null;
            }

            if (end > at)
            {
                parts.Add(new WordPart.Text(word[at..end], new string(WordQuoting.Open, end - at)));
            }

            if (!probes.ChoiceOf.TryGetValue(word[end..(after + 1)], out var choice))
            {
                return null;
            }

            parts.Add(new WordPart.Choice(choice));
            at = after + 1;
        }

        return new WordPattern(parts);
    }

    /// <summary>
    /// The readings a command is decided on where its words' or files' patterns hold choices
    /// (<paramref name="words"/>, <paramref name="targets"/>): a reading for each way the choices that are not held as
    /// cores may be made - one alternative at a time, the same alternative of one choice in every word and file that
    /// reads it - the choices that are held as cores standing as one text for every alternative
    /// (<see cref="Probes.Core"/>). One reading alone where every choice is a core; the command as it is spelled is the
    /// caller's first reading.
    /// </summary>
    /// <param name="analyzed">The command, as it is spelled.</param>
    /// <param name="words">Its words' patterns.</param>
    /// <param name="targets">Its redirects' files' patterns.</param>
    /// <param name="probes">The cores of this decision of the string.</param>
    private static IEnumerable<AnalyzedCommand> ProbesCore(AnalyzedCommand analyzed, IReadOnlyList<WordPattern> words, IReadOnlyList<WordPattern> targets, Probes probes)
    {
        // The places a program's name is read: the command's environment, and every word a program may run from. A
        // choice in the last segment of a word at one of them is spelled, one alternative at a time.
        var named = new HashSet<int> { CommandAnalysis.EnvironmentOf(analyzed.Words) };
        named.UnionWith(CommandAnalysis.ProgramsAt(analyzed.Words));

        for (var at = 0; at < analyzed.Words.Count && CommandAnalysis.RunsAt(analyzed.Words, at); at++)
        {
            named.Add(at);
        }

        // Every choice the command's patterns hold, and whether it is held as a core: one whose alternatives are plain
        // paths that begin alike, and whose word - where a program's name is read - no spelling of it names a program or
        // mark whose words are read.
        var choices = new List<WordChoice>();
        var coreable = new Dictionary<WordChoice, bool>(ReferenceEqualityComparer.Instance);

        for (var at = 0; at < words.Count; at++)
        {
            foreach (var choice in ChoicesOf(words[at]))
            {
                if (!coreable.ContainsKey(choice))
                {
                    choices.Add(choice);
                    coreable[choice] = Tokenizable(choice, named.Contains(at) && InLastSegment(words[at], choice));
                }
            }
        }

        foreach (var target in targets)
        {
            foreach (var choice in ChoicesOf(target))
            {
                if (!coreable.ContainsKey(choice))
                {
                    choices.Add(choice);
                    coreable[choice] = Tokenizable(choice, atLastSegment: false);
                }
            }
        }

        for (var at = 0; at < words.Count; at++)
        {
            // A word whose spelling names a program or mark whose words are read spells its choices: its words are
            // read, not only named.
            if (words[at].MayNameAProgram(ReadWords))
            {
                foreach (var choice in ChoicesOf(words[at]).Where(choice => coreable.ContainsKey(choice)))
                {
                    coreable[choice] = false;
                }
            }
        }

        // The choices to spell, one alternative at a time: every way they may be made.
        IReadOnlyList<WordChoice> spelled = [.. choices.Where(choice => !coreable[choice])];

        foreach (var binding in Bindings(spelled))
        {
            yield return new AnalyzedCommand(
                [.. words.Select((pattern, at) => SpelledWith(pattern, probes, binding))],
                [.. analyzed.Redirects.Zip(targets, (redirect, target) => redirect with { Target = redirect.Operator == "<<<" ? redirect.Target : SpelledWith(target, probes, binding) })],
                [.. words.Select(pattern => SpelledMaskWith(pattern, probes, binding))])
            {
                Wrappers = analyzed.Wrappers,
            };
        }
    }

    /// <summary>Every choice a pattern holds, its alternatives' included, each once.</summary>
    private static IEnumerable<WordChoice> ChoicesOf(WordPattern pattern)
    {
        var seen = new HashSet<WordChoice>(ReferenceEqualityComparer.Instance);
        var waiting = new Stack<WordPattern>();
        waiting.Push(pattern);

        while (waiting.TryPop(out var each))
        {
            foreach (var part in each.Parts)
            {
                if (part is not WordPart.Choice choice || !seen.Add(choice.Of))
                {
                    continue;
                }

                yield return choice.Of;

                foreach (var alternative in choice.Of.Alternatives)
                {
                    waiting.Push(alternative);
                }
            }
        }
    }

    /// <summary>Whether <paramref name="choice"/> stands in <paramref name="pattern"/>'s last segment, which names it.</summary>
    private static bool InLastSegment(WordPattern pattern, WordChoice choice)
        => SegmentsOf(pattern) is [.., ({ }, var last)] && last.Parts.OfType<WordPart.Choice>().Any(part => ReferenceEquals(part.Of, choice));

    /// <summary>Every way the choices of <paramref name="spelled"/> may be made: one binding of each to an alternative.</summary>
    private static IEnumerable<Dictionary<WordChoice, int>> Bindings(IReadOnlyList<WordChoice> spelled)
    {
        var binding = new Dictionary<WordChoice, int>(ReferenceEqualityComparer.Instance);

        for (var made = 0; ; made++)
        {
            var at = made;

            foreach (var choice in spelled)
            {
                binding[choice] = at % choice.Alternatives.Count;
                at /= choice.Alternatives.Count;
            }

            if (at > 0 && made > 0)
            {
                yield break;
            }

            yield return new Dictionary<WordChoice, int>(binding, ReferenceEqualityComparer.Instance);
        }
    }

    /// <summary>
    /// The one spelling of <paramref name="pattern"/> under <paramref name="binding"/>: each choice held as a core
    /// standing as its core's text, each spelled choice bound to one alternative, the same in every word that reads it.
    /// </summary>
    private static string SpelledWith(WordPattern pattern, Probes probes, Dictionary<WordChoice, int> binding) => Spell(pattern, probes, binding).text;

    /// <summary>How each character of <see cref="SpelledWith"/>'s spelling stands to the shell.</summary>
    private static string SpelledMaskWith(WordPattern pattern, Probes probes, Dictionary<WordChoice, int> binding) => Spell(pattern, probes, binding).mask;

    /// <summary><see cref="SpelledWith"/> and its mask, spelled once for both.</summary>
    private static (string text, string mask) Spell(WordPattern pattern, Probes probes, Dictionary<WordChoice, int> binding)
    {
        var text = string.Empty;
        var mask = string.Empty;

        void Walk(WordPattern each, char? taken)
        {
            foreach (var part in each.Parts)
            {
                if (part is WordPart.Text fixedText)
                {
                    text += fixedText.Spelled;
                    mask += taken is { } given ? new string(given, fixedText.Spelled.Length)
                        : fixedText.Quoted.Length >= fixedText.Spelled.Length ? fixedText.Quoted
                        : new string(WordQuoting.Open, fixedText.Spelled.Length);
                    continue;
                }

                var choice = (WordPart.Choice)part;

                if (binding.TryGetValue(choice.Of, out var alternative))
                {
                    Walk(choice.Of.Alternatives[alternative], choice.Class ?? taken);
                    continue;
                }

                var core = probes.Core(choice.Of);
                text += core;
                mask += new string(WordQuoting.Open, core.Length);
            }
        }

        Walk(pattern, null);
        return (text, mask);
    }

    /// <summary>
    /// Whether <paramref name="choice"/> may be held as one core in the readings of a command: every alternative of it a
    /// plain path - names, separators, dots, a drive's or home's lead - longer than two characters and no separator at
    /// its end, and all beginning as the word does (rooted, or not), spelling nothing a path's reading turns on (an
    /// <c>=</c>, a <c>@</c>, a <c>&lt;</c>, a <c>;</c>, a blank, a quote, a pattern's character, an expansion, a file
    /// address). The names it may spell are asked of the word it stands in (<see cref="ProbesCore"/>).
    /// </summary>
    /// <param name="choice">The choice.</param>
    /// <param name="atLastSegment">Whether the choice stands in the last segment of a word whose name a program's place reads.</param>
    private static bool Tokenizable(WordChoice choice, bool atLastSegment)
    {
        static bool Plain(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '+' or '.' or '/' or '\\' or '~' or ':';

        if (choice.Alternatives.Count < 2 || atLastSegment)
        {
            return false;
        }

        var rooted = 0;

        foreach (var alternative in choice.Alternatives)
        {
            var parts = PartsOf(alternative).ToList();

            if (parts.Count <= 2 || parts[^1].Character is '/' or '\\' || parts.Any(part => !Plain(part.Character)))
            {
                return false;
            }

            if (parts[0].Character is '/' or '\\')
            {
                rooted++;
            }
        }

        // All the alternatives begin alike: rooted, or relative - a path they name is read the one way.
        return rooted == 0 || rooted == choice.Alternatives.Count;
    }
}
