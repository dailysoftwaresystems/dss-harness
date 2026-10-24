namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// A text a language's lexer has cut into tokens, with its comments apart and every bracket matched: what the structural
/// scanner's languages read their declarations from. The core of the scanner - the matching of brackets over the
/// tokens, the reading of a call and the frame declarations are walked by - knows no language; each one's lexer says
/// what a token is, and its reader what a declaration is.
/// </summary>
/// <remarks>
/// Both branches of a conditional are tokens alike, since the scanner reads every branch. A bracket opened in one
/// branch and closed after the conditional ends - <c>void f(int a) {</c> under <c>#if</c>, <c>void f(int a, int b) {</c>
/// under <c>#else</c>, one body after <c>#endif</c> - is matched as though each branch had been taken alone: each branch
/// begins where the conditional began, and after it the brackets the branches left open at one depth are one, the
/// first branch's, closed by the same bracket. A brace the first branch leaves open stays open though another leaves
/// none - <c>extern "C" {</c> under a conditional of its own, closed under another - but a parenthesis or a square
/// bracket stays open only where every branch leaves one at that depth, a conditional with no alternative having an
/// empty one; every other bracket a branch leaves open is stranded (<see cref="IsStranded"/>): it matches nothing and
/// holds nothing, so text a branch leaves unbalanced never runs on over the rest of the file.
/// </remarks>
internal sealed class ScannedText
{
    private readonly int[] _match;
    private readonly bool[] _stranded;

    /// <summary>Scans <paramref name="tokens"/> of <paramref name="source"/>, matching their brackets.</summary>
    /// <param name="source">The text.</param>
    /// <param name="tokens">Its tokens, in order.</param>
    /// <param name="comments">Its comments, in order.</param>
    public ScannedText(string source, IReadOnlyList<ScanToken> tokens, IReadOnlyList<ScanComment> comments)
    {
        Source = source;
        Tokens = tokens;
        Comments = comments;
        (_match, _stranded) = Matched(source, tokens);
        Stranded = [.. Enumerable.Range(0, tokens.Count).Where(at => _stranded[at])];
    }

    /// <summary>The indices of the brackets a conditional's branch left open that stand for none after it, in order.</summary>
    public IReadOnlyList<int> Stranded { get; }

    /// <summary>The text.</summary>
    public string Source { get; }

    /// <summary>Its tokens, in order.</summary>
    public IReadOnlyList<ScanToken> Tokens { get; }

    /// <summary>Its comments, in order.</summary>
    public IReadOnlyList<ScanComment> Comments { get; }

    /// <summary>How many tokens it has.</summary>
    public int Count => Tokens.Count;

    /// <summary>The token at <paramref name="at"/>.</summary>
    public ScanToken this[int at] => Tokens[at];

    /// <summary>The text of the token at <paramref name="at"/>.</summary>
    public string Text(int at) => Source.Substring(Tokens[at].Start, Tokens[at].Length);

    /// <summary>The text from the token at <paramref name="first"/> to the one before <paramref name="end"/>, as written.</summary>
    public string Text(int first, int end)
        => first < end ? Source[Tokens[first].Start..Tokens[end - 1].End] : string.Empty;

    /// <summary>
    /// The tokens from <paramref name="first"/> to the one before <paramref name="end"/>, directives left out, each written as
    /// it is and one space between two the text has anything between - white space, a line break, a comment: a header on
    /// one line, as a node's signature is.
    /// </summary>
    public string Spelled(int first, int end)
    {
        var spelled = new System.Text.StringBuilder();
        var after = -1;

        for (var at = first; at < end && at < Tokens.Count; at++)
        {
            if (Tokens[at].Kind == ScanTokenKind.Directive)
            {
                continue;
            }

            if (after >= 0 && Tokens[at].Start > after)
            {
                spelled.Append(' ');
            }

            spelled.Append(Source, Tokens[at].Start, Tokens[at].Length);
            after = Tokens[at].End;
        }

        return spelled.ToString();
    }

    /// <summary>Whether the token at <paramref name="at"/> is a name or a punctuator written <paramref name="text"/>.</summary>
    public bool Is(int at, string text)
        => at >= 0 && at < Tokens.Count
            && Tokens[at] is { Kind: ScanTokenKind.Name or ScanTokenKind.Punctuation } token
            && token.Length == text.Length
            && string.CompareOrdinal(Source, token.Start, text, 0, text.Length) == 0;

    /// <summary>Whether the token at <paramref name="at"/> is a name.</summary>
    public bool IsName(int at) => at >= 0 && at < Tokens.Count && Tokens[at].Kind == ScanTokenKind.Name;

    /// <summary>Whether the token at <paramref name="at"/> is one of the brackets that open a group: <c>(</c>, <c>[</c>, <c>{</c>.</summary>
    public bool Opens(int at) => Is(at, "(") || Is(at, "[") || Is(at, "{");

    /// <summary>The index of the bracket that matches the one at <paramref name="at"/>; -1 where none does.</summary>
    public int Match(int at) => at >= 0 && at < _match.Length ? _match[at] : -1;

    /// <summary>Whether the bracket at <paramref name="at"/> is one a conditional's branch left open that stands for none after it: it matches nothing and holds nothing.</summary>
    public bool IsStranded(int at) => at >= 0 && at < _stranded.Length && _stranded[at];

    /// <summary>
    /// The index past the group the bracket at <paramref name="open"/> opens: past its match; past itself where it is
    /// stranded; the end of the text where nothing closes it.
    /// </summary>
    public int Past(int open) => _match[open] is var close and > 0 && close > open ? close + 1 : _stranded[open] ? open + 1 : Tokens.Count;

    /// <summary>The index of the token past <paramref name="at"/> that is no directive; <see cref="Count"/> where none is.</summary>
    public int Next(int at)
    {
        for (var next = at + 1; next < Tokens.Count; next++)
        {
            if (Tokens[next].Kind != ScanTokenKind.Directive)
            {
                return next;
            }
        }

        return Tokens.Count;
    }

    /// <summary>The index of the token before <paramref name="at"/> that is no directive; -1 where none is.</summary>
    public int Previous(int at)
    {
        for (var previous = at - 1; previous >= 0; previous--)
        {
            if (Tokens[previous].Kind != ScanTokenKind.Directive)
            {
                return previous;
            }
        }

        return -1;
    }

    /// <summary>The indices of the tokens from <paramref name="start"/> to the one before <paramref name="end"/> that are no directive.</summary>
    public IEnumerable<int> Code(int start, int end)
    {
        for (var at = Math.Max(0, start); at < end && at < Tokens.Count; at++)
        {
            if (Tokens[at].Kind != ScanTokenKind.Directive)
            {
                yield return at;
            }
        }
    }

    /// <summary>
    /// The group the bracket at <paramref name="open"/> opens, within <paramref name="end"/>: the index of its closing
    /// bracket and the index past it - the index past the bracket, twice, where it is stranded and holds nothing;
    /// <paramref name="end"/>, twice, where nothing closes it before.
    /// </summary>
    public (int Inner, int Past) Bounds(int open, int end)
        => Match(open) is var close and >= 0 && close < end ? (close, close + 1) : IsStranded(open) ? (open + 1, open + 1) : (end, end);

    /// <summary>
    /// The index past the statement that begins at <paramref name="at"/>, within <paramref name="end"/>: past its
    /// <c>;</c>, its groups passed over; at the <c>}</c> that ends the block it is in; past a block it ends with, and a
    /// <c>;</c> after that; at the end of the branch a stranded bracket in it lies in, the rest of that branch with it.
    /// </summary>
    public int StatementEnd(int at, int end)
    {
        for (var index = at; index < end; index++)
        {
            if (Is(index, ";"))
            {
                return index + 1;
            }

            if (IsStranded(index))
            {
                return Math.Min(BranchEnd(index), end);
            }

            if (Is(index, "}"))
            {
                return index;
            }

            if (Is(index, "{"))
            {
                var past = Bounds(index, end).Past;
                return Is(past, ";") ? past + 1 : past;
            }

            if (Is(index, "(") || Is(index, "["))
            {
                index = Bounds(index, end).Past - 1;
            }
        }

        return end;
    }

    /// <summary>
    /// The index of the directive that ends the branch of a conditional the token at <paramref name="at"/> lies in - the
    /// conditional's next alternative, or its end; <see cref="Count"/> where it lies in none.
    /// </summary>
    public int BranchEnd(int at)
    {
        var depth = 0;

        for (var index = at + 1; index < Tokens.Count; index++)
        {
            switch (Tokens[index].Branch)
            {
                case ScanBranch.Opens:
                    depth++;
                    break;
                case ScanBranch.Alternative when depth == 0:
                    return index;
                case ScanBranch.Closes when depth-- == 0:
                    return index;
            }
        }

        return Tokens.Count;
    }

    /// <summary>
    /// The comments directly above the token at <paramref name="at"/>: the run that ends on its line or the line before,
    /// each next to the one below it, none on a line a token before it ends on - so a comment trailing the code of the line
    /// above is that code's, and a blank line ends the run - what a declaration says of itself.
    /// </summary>
    public IReadOnlyList<ScanComment> CommentsAbove(int at)
    {
        var token = Tokens[at];
        var floor = at > 0 ? Tokens[at - 1].EndLine : 0;
        var last = LastCommentBefore(token.Start);
        var run = new List<ScanComment>();

        for (var index = last; index >= 0; index--)
        {
            var comment = Comments[index];
            var below = run.Count == 0 ? token.Line : run[^1].Line;

            if (comment.Line <= floor || comment.EndLine < below - 1)
            {
                break;
            }

            run.Add(comment);
        }

        run.Reverse();
        return run;
    }

    /// <summary>The index of the last comment that begins before <paramref name="position"/>; -1 where none does.</summary>
    private int LastCommentBefore(int position)
    {
        var low = 0;
        var high = Comments.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var middle = (low + high) / 2;

            if (Comments[middle].Start < position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    /// <summary>One bracket left open: its kind and each token that opened it, several where branches each opened one.</summary>
    private sealed record Open(char Bracket, List<int> Tokens);

    /// <summary>One conditional being read: the index of its first directive, the brackets open where it began, and those each branch read so far left.</summary>
    private sealed record Conditional(int At, List<Open> Start, List<List<Open>> Ends);

    /// <summary>The match of every bracket of <paramref name="tokens"/>, -1 where none matches it, and whether each is stranded.</summary>
    private static (int[] Match, bool[] Stranded) Matched(string source, IReadOnlyList<ScanToken> tokens)
    {
        var match = new int[tokens.Count];
        var stranded = new bool[tokens.Count];
        Array.Fill(match, -1);

        var open = new List<Open>();
        var conditionals = new Stack<Conditional>();

        for (var at = 0; at < tokens.Count; at++)
        {
            var token = tokens[at];

            if (token.Kind == ScanTokenKind.Directive)
            {
                switch (token.Branch)
                {
                    case ScanBranch.Opens:
                        conditionals.Push(new Conditional(at, Copy(open), []));
                        break;

                    // Each branch is read from where the conditional began.
                    case ScanBranch.Alternative when conditionals.TryPeek(out var conditional):
                        conditional.Ends.Add(Copy(open));
                        open = Copy(conditional.Start);
                        break;

                    case ScanBranch.Closes when conditionals.TryPop(out var conditional):
                        conditional.Ends.Add(open);
                        open = Ended(conditional, stranded);
                        break;
                }

                continue;
            }

            if (token is not { Kind: ScanTokenKind.Punctuation, Length: 1 })
            {
                continue;
            }

            switch (source[token.Start])
            {
                case var bracket and ('(' or '[' or '{'):
                    open.Add(new Open(bracket, [at]));
                    break;

                case var closing and (')' or ']' or '}'):
                    Close(open, match, at, closing switch { ')' => '(', ']' => '[', _ => '{' });
                    break;
            }
        }

        return (match, stranded);
    }

    /// <summary>
    /// The bracket at <paramref name="at"/> closes the nearest open one of its kind: a brace whatever is open above it,
    /// which is left unmatched; a parenthesis or a square bracket only where no brace stands between, else it matches
    /// nothing - so a stray one never ends a body.
    /// </summary>
    private static void Close(List<Open> open, int[] match, int at, char bracket)
    {
        for (var depth = open.Count - 1; depth >= 0; depth--)
        {
            if (open[depth].Bracket == bracket)
            {
                foreach (var opener in open[depth].Tokens)
                {
                    match[opener] = at;
                }

                match[at] = open[depth].Tokens[0];
                open.RemoveRange(depth, open.Count - depth);
                return;
            }

            if (open[depth].Bracket == '{')
            {
                return;
            }
        }
    }

    /// <summary>
    /// What is open once <paramref name="conditional"/> ends: what its first branch left, each bracket another branch left
    /// open at the same depth, of the same kind, joined to it - closed by one bracket. A parenthesis or a square bracket
    /// of the first branch's own that a branch - the empty one of a conditional with no alternative among them - did not
    /// leave open too, and each bracket of another branch's own not joined, is stranded.
    /// </summary>
    private static List<Open> Ended(Conditional conditional, bool[] stranded)
    {
        var ends = conditional.Ends;
        var kept = ends[0];
        var joined = new HashSet<Open>(ReferenceEqualityComparer.Instance);

        bool Own(Open bracket) => bracket.Tokens[0] > conditional.At;

        for (var depth = kept.Count - 1; depth >= 0; depth--)
        {
            var bracket = kept[depth];

            if (!Own(bracket))
            {
                continue;
            }

            var alike = ends.Skip(1).Select(end => depth < end.Count && end[depth].Bracket == bracket.Bracket && Own(end[depth]) ? end[depth] : null).ToList();

            if (bracket.Bracket != '{' && (ends.Count == 1 || alike.Contains(null)))
            {
                Strand(bracket, stranded);
                kept.RemoveAt(depth);
                continue;
            }

            foreach (var other in alike.OfType<Open>())
            {
                bracket.Tokens.AddRange(other.Tokens.Except(bracket.Tokens));
                joined.Add(other);
            }
        }

        foreach (var bracket in ends.Skip(1).SelectMany(end => end).Where(bracket => Own(bracket) && !joined.Contains(bracket)))
        {
            Strand(bracket, stranded);
        }

        return kept;
    }

    private static void Strand(Open bracket, bool[] stranded)
    {
        foreach (var opener in bracket.Tokens)
        {
            stranded[opener] = true;
        }
    }

    private static List<Open> Copy(List<Open> open) => [.. open.Select(bracket => bracket with { Tokens = [.. bracket.Tokens] })];
}
