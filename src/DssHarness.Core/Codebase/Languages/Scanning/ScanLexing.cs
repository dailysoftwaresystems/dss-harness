namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// One run of a scanned language's lexer over a text, what it reads alike for every language: the tokens and the
/// comments it collects, each placed on the text's lines, and the characters a name, a line break and a punctuator read
/// alone are made of.
/// </summary>
/// <param name="lines">The text's lines.</param>
/// <param name="closes">
/// What the scans of the text before this one read of its literals, which this one passes over at once, and adds what it
/// reads to (<see cref="ScanCloses"/>); a fresh one where none scanned the text before.
/// </param>
internal abstract class ScanLexing(CodeLineIndex lines, ScanCloses? closes = null)
{
    public List<ScanToken> Tokens { get; } = [];

    /// <summary>The text's lines, which a run over a part of the text inside this one shares.</summary>
    protected CodeLineIndex Lines { get; } = lines;

    /// <summary>What every scan of the text read of its literals, by where each begins: one for the text, shared by each of its scans.</summary>
    public ScanCloses Closes { get; } = closes ?? new ScanCloses();

    public List<ScanComment> Comments { get; } = [];

    /// <summary>For each literal that holds code, by where it begins: where each run of code it holds directly begins and ends.</summary>
    public Dictionary<long, List<(long Start, long End)>> Interpolations { get; } = [];

    /// <summary>
    /// For each string literal, by where its token begins: where each constant part of its value is written and how it is
    /// decoded (<see cref="ScanLiteral"/>). A token the lexer keeps whole that is no string - a character literal, a
    /// regular expression - has none.
    /// </summary>
    public Dictionary<long, ScanLiteral> Literals { get; } = [];

    /// <summary>Whether <paramref name="character"/> may begin a name: a letter - one past ASCII among them - <c>_</c> or <c>$</c>.</summary>
    public static bool IsNameStart(char character) => char.IsLetter(character) || character is '_' or '$';

    /// <summary>Whether <paramref name="character"/> may stand in a name past its first: a letter, a digit, <c>_</c> or <c>$</c>.</summary>
    public static bool IsNamePart(char character) => char.IsLetterOrDigit(character) || character is '_' or '$';

    /// <summary>How wide the line break at <paramref name="at"/> is: 2 for CR LF, 1 for CR or LF, 0 where none is.</summary>
    public static int LineBreakAt(CodeText text, long at, long end)
        => at >= end ? 0
            : text[at] == '\r' ? (at + 1 < end && text[at + 1] == '\n' ? 2 : 1)
            : text[at] == '\n' ? 1
            : 0;

    /// <summary>
    /// How long the punctuator <paramref name="character"/> begins is where the language keeps none longer whole there:
    /// 1 for a printable ASCII character no name holds, else 0 - a control character, a surrogate apart from its pair,
    /// which no token begins with and the lexer passes over.
    /// </summary>
    protected static int Alone(char character) => character is > ' ' and < '\u007f' && !IsNamePart(character) ? 1 : 0;

    /// <summary>A token of <paramref name="kind"/> from <paramref name="start"/> to <paramref name="end"/>.</summary>
    protected void Add(ScanTokenKind kind, long start, long end, ScanBranch branch = ScanBranch.None)
        => Tokens.Add(new ScanToken(kind, start, end - start, Lines.LineAt(start), Lines.LineAt(Math.Max(start, end - 1)), branch));

    /// <summary>
    /// Where the comment that begins at <paramref name="at"/> ends, within <paramref name="end"/>, by the language's rules -
    /// a line comment at its line's end, a block comment past its closer; <paramref name="at"/> where none begins there.
    /// The one reading of the language's comments, by which its code and the code its literals hold
    /// (<see cref="LiteralStack{TMark}"/>) are both read: a quote in a comment there - <c>// don't</c> - opens no string,
    /// and a brace in one closes nothing.
    /// </summary>
    protected abstract long CommentEnd(long at, long end);

    /// <summary>A comment from <paramref name="start"/> to <paramref name="end"/>, kept apart; answers where the text goes on.</summary>
    protected long Comment(long start, long end)
    {
        Comments.Add(new ScanComment(start, end - start, Lines.LineAt(start), Lines.LineAt(Math.Max(start, end - 1))));
        return end;
    }

    /// <summary>
    /// Where the text's code begins from <paramref name="start"/>: past a first line's shebang (<c>#!</c>), which a
    /// script's interpreter reads and the lexer keeps as a comment - JavaScript's and Dart's; <paramref name="start"/>
    /// where it begins none, or the run is not the text's own from its start.
    /// </summary>
    protected long Shebang(CodeText text, long start, long end)
        => start == 0 && text.StartsWith("#!") ? Comment(0, LineEnd(text, 0, end)) : start;

    /// <summary>Where the line <paramref name="at"/> is on ends, within <paramref name="end"/>: at its line break, which a comment that runs to it does not hold.</summary>
    protected static long LineEnd(CodeText text, long at, long end)
    {
        while (at < end && LineBreakAt(text, at, end) == 0)
        {
            at++;
        }

        return at;
    }

    /// <summary>Records that the literal beginning at <paramref name="literal"/> holds code from <paramref name="start"/> to <paramref name="end"/>.</summary>
    protected void Interpolated(long literal, long start, long end)
    {
        if (end > start)
        {
            (Interpolations.TryGetValue(literal, out var spans) ? spans : Interpolations[literal] = []).Add((start, end));
        }
    }

    /// <summary>Records the string literal whose token begins at <paramref name="token"/>: where its value is written, and how it is decoded.</summary>
    protected void KeepLiteral(long token, ScanLiteral literal) => Literals[token] = literal;

    /// <summary>A constant part of a literal's value from <paramref name="start"/> to <paramref name="end"/>, placed on its line.</summary>
    protected ScanLiteralPart Part(long start, long end) => new(start, Math.Max(start, end), Lines.LineAt(start));

    /// <summary>
    /// The hole a bare <c>$name</c> whose <c>$</c> is at <paramref name="sigil"/> opens in a string, within
    /// <paramref name="stop"/>: where the code it is begins and ends, and where the string goes on past it; none where no
    /// name follows the <c>$</c>. The one reading of it, Dart's and PHP's alike but where their grammars part. Dart's
    /// (<paramref name="variable"/> false) is a name of no <c>$</c> - <c>'$a$b'</c> two holes - the code the name alone.
    /// PHP's is a variable, the code from its <c>$</c>, with one <c>-&gt;name</c> or <c>?-&gt;name</c> after it, or one
    /// <c>[...]</c> of a number or a variable; a bare word in the brackets - <c>"$row[key]"</c> - is a key the string
    /// names, the hole's and no code's.
    /// </summary>
    protected static (long Start, long End, long Past)? BareHole(CodeText text, long sigil, long stop, bool variable)
    {
        bool Starts(char character) => char.IsLetter(character) || character == '_' || (variable && character > '\u007f');
        bool Continues(char character) => char.IsLetterOrDigit(character) || character == '_' || (variable && character > '\u007f');

        long NameEnd(long at)
        {
            while (at < stop && Continues(text[at]))
            {
                at++;
            }

            return at;
        }

        if (sigil + 1 >= stop || !Starts(text[sigil + 1]))
        {
            return null;
        }

        var name = NameEnd(sigil + 2);

        if (!variable)
        {
            return (sigil + 1, name, name);
        }

        // $x->name, $x?->name: one member of the variable.
        foreach (var arrow in (string[])["->", "?->"])
        {
            var member = name + arrow.Length;

            if (text.At(name, arrow) && member < stop && Starts(text[member]))
            {
                var past = NameEnd(member + 1);
                return (sigil, past, past);
            }
        }

        // $x[0], $x[-1], $x[$i] - code; $x[key] - a key the string names.
        if (name < stop && text[name] == '[')
        {
            var at = name + 1;
            var code = true;

            if (at < stop && text[at] == '$' && at + 1 < stop && Starts(text[at + 1]))
            {
                at = NameEnd(at + 2);
            }
            else if (at < stop && (char.IsAsciiDigit(text[at]) || (text[at] == '-' && at + 1 < stop && char.IsAsciiDigit(text[at + 1]))))
            {
                at = NameEnd(at + 1);
            }
            else if (at < stop && Starts(text[at]))
            {
                at = NameEnd(at + 1);
                code = false;
            }

            if (at > name + 1 && at < stop && text[at] == ']')
            {
                return (sigil, code ? at + 1 : name, at + 1);
            }
        }

        return (sigil, name, name);
    }

    /// <summary>
    /// What a literal's reading is in, innermost last: the literal, an interpolation's code with how many braces it holds
    /// open, a literal that code holds, and on - one stack, never a call into itself, so no nesting reaches past the
    /// reader's stack - each run of code a literal holds directly recorded as its own where it ends, and each constant
    /// part of its own value between its opener, its holes - each hole's sigil and closer the hole's - and its closer. A
    /// literal the code holds is its own token when that code is read in its turn, and nothing of it is this one's: what
    /// this reading found of it - its parts, its holes, its code, where it ends - is kept in the text's close-index
    /// (<see cref="ScanCloses"/>), so the scan of that code passes over it at once, and no character is read again by the
    /// scan of each interpolation outside it.
    /// </summary>
    /// <typeparam name="TMark">What the lexer keeps of each literal it opens: its quote, whether it is raw, whether it is triple.</typeparam>
    /// <param name="lexing">The run the literal is read in.</param>
    /// <param name="literal">Where the literal begins.</param>
    protected sealed class LiteralStack<TMark>(ScanLexing lexing, long literal)
    {
        private readonly List<LiteralFrame> _frames = [];

        /// <summary>The literal read, the first opened; none before it is.</summary>
        private LiteralFrame? _outermost;

        /// <summary>Whether the literal is still open.</summary>
        public bool Open => _frames.Count > 0;

        /// <summary>Whether the reading is in an interpolation's code.</summary>
        public bool InCode => _frames[^1].Code;

        /// <summary>What the lexer keeps of the literal the reading is in.</summary>
        public TMark Mark => _frames[^1].Mark;

        /// <summary>How many braces the interpolation's code the reading is in holds open: a closing brace stands at none of them ends it.</summary>
        public int Depth => _frames[^1].Depth;

        /// <summary>
        /// Whether the reading is in the tail of a field of the literal it is in - what stands between where <see cref="EndCode"/>
        /// ended the field's code and the field's closer, an f-string's conversion and format - none of it the value's constant text.
        /// </summary>
        public bool InTail => _frames[^1] is { Code: false, Tails: > 0 };

        /// <summary>The constant parts of the literal's own value read so far.</summary>
        public IReadOnlyList<ScanLiteralPart> Parts => _outermost?.Parts ?? [];

        /// <summary>Whether the literal holds holes of its own.</summary>
        public bool Holes => _outermost?.Holes ?? false;

        /// <summary>Where each run of code the literal holds directly begins and ends, as recorded so far: none empty.</summary>
        public IReadOnlyList<(long Start, long End)> Spans => _outermost?.Spans ?? [];

        /// <summary>
        /// Opens a literal whose token begins at <paramref name="start"/>: the one read, its value's text beginning at
        /// <paramref name="content"/> - past its opener - or one an interpolation's code holds.
        /// </summary>
        public void OpenLiteral(TMark mark, long start, long content)
        {
            var frame = new LiteralFrame(mark, code: false, start, content);

            _outermost ??= frame;
            _frames.Add(frame);
        }

        /// <summary>
        /// Opens a literal whose value's text begins at <paramref name="content"/>, for a lexer that keeps no close-index of
        /// what its literals' code holds - a style sheet's, which keeps its own: the one read begins where the stack was made
        /// for, and nothing is kept of one its code holds.
        /// </summary>
        public void OpenLiteral(TMark mark, long content) => OpenLiteral(mark, _outermost is null ? literal : -1, content);

        /// <summary>Closes the literal the reading is in, whose closer stands at <paramref name="at"/> and ends before <paramref name="past"/>.</summary>
        public void CloseLiteral(long at, long past)
        {
            var frame = _frames[^1];

            _frames.RemoveAt(_frames.Count - 1);
            EndPart(frame, at);
            Kept(frame, past, leftOpen: false);
        }

        /// <summary>Closes the literal the reading is in, whose closer stands at <paramref name="at"/>, keeping nothing of it (<see cref="OpenLiteral(TMark, long)"/>).</summary>
        public void CloseLiteral(long at) => CloseLiteral(at, past: -1);

        /// <summary>Opens an interpolation's code, which begins at <paramref name="at"/>, its sigil at <paramref name="sigil"/>.</summary>
        public void OpenCode(long sigil, long at)
        {
            var holder = _frames[^1];

            EndPart(holder, sigil);
            holder.Holes = true;
            _frames.Add(new LiteralFrame(default!, code: true, at, at));
        }

        /// <summary>
        /// A bare hole - a <c>$name</c> with no braces (<see cref="BareHole"/>) - of the literal the reading is in, its sigil
        /// at <paramref name="sigil"/>, its code from <paramref name="start"/> to <paramref name="end"/>, the literal going on
        /// at <paramref name="past"/>: recorded as its own, its code read in its turn.
        /// </summary>
        public void Hole(long sigil, long start, long end, long past)
        {
            var holder = _frames[^1];

            EndPart(holder, sigil);
            holder.Holes = true;
            Span(holder, start, end);
            holder.Part = past;
        }

        /// <summary>
        /// A hole of the literal the reading is in that holds no code the graph reads - PHP's <c>${name}</c>, a variable
        /// read by its name - from its sigil at <paramref name="sigil"/>, the literal going on at <paramref name="past"/>.
        /// </summary>
        public void Hole(long sigil, long past)
        {
            var holder = _frames[^1];

            EndPart(holder, sigil);
            holder.Holes = true;
            holder.Part = past;
        }

        /// <summary>
        /// Ends the interpolation's code the reading is in at <paramref name="at"/>, where the language's own syntax ends it
        /// short of its closer - an f-string's <c>=</c> of a debugging field, <c>!</c> before a conversion and <c>:</c> before
        /// its format - recorded as the code of the literal that holds it; what stands from there to the closer is the field's
        /// tail (<see cref="InTail"/>), none of it the value's constant text, which <see cref="CloseCode"/> says goes on. A
        /// field the tail holds - a format's own - is opened as any interpolation is (<see cref="OpenCode"/>), and closing it
        /// leaves the reading in the tail.
        /// </summary>
        public void EndCode(long at)
        {
            var top = _frames[^1];

            _frames.RemoveAt(_frames.Count - 1);

            var holder = _frames[^1];
            Span(holder, top.Start, at);
            holder.Part = -1;
            holder.Tails++;
        }

        /// <summary>
        /// The closer at <paramref name="at"/> of an interpolation whose code <see cref="EndCode"/> ended short of it: the
        /// reading leaves its tail, and the constant text of the literal's value goes on past it - where it is in the tail of
        /// no field around it, a field a format holds being closed in the tail of the field it is the format of.
        /// </summary>
        public void CloseCode(long at)
        {
            var holder = _frames[^1];

            holder.Tails = Math.Max(0, holder.Tails - 1);

            if (holder.Tails == 0)
            {
                holder.Part = at + 1;
            }
        }

        /// <summary>Ends the constant part of <paramref name="holder"/>'s value being read at <paramref name="at"/>.</summary>
        private void EndPart(LiteralFrame holder, long at)
        {
            if (holder.Part >= 0)
            {
                holder.Parts.Add(lexing.Part(holder.Part, at));
                holder.Part = -1;
            }
        }

        /// <summary>
        /// Records that <paramref name="holder"/> holds code from <paramref name="start"/> to <paramref name="end"/> directly
        /// - none where it is empty - as the lexing's own where it is the literal read.
        /// </summary>
        private void Span(LiteralFrame holder, long start, long end)
        {
            if (end > start)
            {
                holder.Spans.Add((start, end));
            }

            if (ReferenceEquals(holder, _outermost))
            {
                lexing.Interpolated(literal, start, end);
            }
        }

        /// <summary>
        /// Keeps what was read of <paramref name="frame"/>, a literal the code of the one read holds, in the text's
        /// close-index: the literal read is its lexer's to keep, which knows its form whole.
        /// </summary>
        private void Kept(LiteralFrame frame, long end, bool leftOpen)
        {
            if (!ReferenceEquals(frame, _outermost) && frame.Start >= 0 && end >= 0)
            {
                lexing.Closes.Keep(frame.Start, new ScanLiteralRead(end, frame.Parts, frame.Spans) { Holes = frame.Holes, LeftOpen = leftOpen, Content = frame.Content });
            }
        }

        /// <summary>
        /// Reads what an interpolation's code holds at <paramref name="at"/> - its first character <paramref name="character"/> -
        /// alike in every language: a comment, by the language's own rules (<see cref="CommentEnd"/>), passed over as none
        /// of the code's, so a quote in <c>// don't</c> opens no string and a brace in it closes nothing; a brace, held open
        /// or closing (<see cref="Brace"/>). Answers where the reading goes on; <paramref name="at"/> where neither stands
        /// there, and the lexer reads what does by its language's rules - a string, a template, a regular expression.
        /// </summary>
        public long Past(char character, long at, long end)
        {
            if (lexing.CommentEnd(at, end) is var comment && comment > at)
            {
                return comment;
            }

            if (character is '{' or '}')
            {
                Brace(character == '{', at);
                return at + 1;
            }

            return at;
        }

        /// <summary>
        /// A brace in an interpolation's code at <paramref name="at"/>: an opening one held open; a closing one closing the
        /// last held open, else the code itself - recorded as the code of the literal that holds it, whose value's constant
        /// text goes on past it but in a field's tail (<see cref="InTail"/>), where the code was a format's own field.
        /// </summary>
        private void Brace(bool opens, long at)
        {
            var top = _frames[^1];

            if (opens || top.Depth > 0)
            {
                top.Depth += opens ? 1 : -1;
                return;
            }

            _frames.RemoveAt(_frames.Count - 1);

            var holder = _frames[^1];
            Span(holder, top.Start, at);
            holder.Part = holder.Tails > 0 ? -1 : at + 1;
        }

        /// <summary>
        /// Ends the reading at <paramref name="at"/>: each interpolation the text ends in is read to there, each part of a
        /// literal's own value left open ends there, and each literal left open ends there, open - the literal read still
        /// open, as <see cref="Open"/> says.
        /// </summary>
        public void End(long at)
        {
            for (var index = _frames.Count - 1; index >= 0; index--)
            {
                var frame = _frames[index];

                if (frame.Code)
                {
                    Span(_frames[index - 1], frame.Start, at);
                    continue;
                }

                EndPart(frame, at);
                Kept(frame, at, leftOpen: true);
            }
        }

        /// <summary>
        /// One frame of the reading: a literal - where its token begins, where its value's text does, its parts and the
        /// code it holds directly as read so far - or the code of an interpolation, where it begins and how many braces it
        /// holds open.
        /// </summary>
        /// <param name="mark">What the lexer keeps of a literal; none of code.</param>
        /// <param name="code">Whether it is an interpolation's code.</param>
        /// <param name="start">Where a literal's token begins; where code begins.</param>
        /// <param name="content">Where a literal's value's text begins.</param>
        private sealed class LiteralFrame(TMark mark, bool code, long start, long content)
        {
            public TMark Mark { get; } = mark;

            public bool Code { get; } = code;

            public long Start { get; } = start;

            public long Content { get; } = content;

            /// <summary>How many braces code holds open.</summary>
            public int Depth { get; set; }

            /// <summary>Where the constant part of a literal's value being read began; -1 outside one.</summary>
            public long Part { get; set; } = code ? -1 : content;

            /// <summary>A literal's constant parts read so far.</summary>
            public List<ScanLiteralPart> Parts { get; } = [];

            /// <summary>Where each run of code a literal holds directly begins and ends.</summary>
            public List<(long Start, long End)> Spans { get; } = [];

            /// <summary>Whether a literal holds holes of its own.</summary>
            public bool Holes { get; set; }

            /// <summary>
            /// How many fields a literal's reading is in the tail of (<see cref="InTail"/>): the field whose code ended short of
            /// its closer, and each a format of it holds whose own code did, the innermost last.
            /// </summary>
            public int Tails { get; set; }
        }
    }

    /// <summary>
    /// A number at <paramref name="start"/>, as Dart, JavaScript and PHP write one: digits, the letters of a hexadecimal, a
    /// binary or an octal one and of a suffix, separators, a fraction's point before a digit, and an exponent's sign but in
    /// a hexadecimal one. Answers where the text goes on.
    /// </summary>
    protected long Number(CodeText text, long start, long end)
    {
        var at = start;
        var hexadecimal = start + 1 < end && text[start] == '0' && text[start + 1] is 'x' or 'X';

        while (at < end)
        {
            if (!hexadecimal && at + 1 < end && text[at] is 'e' or 'E' && text[at + 1] is '+' or '-')
            {
                at += 2;
            }
            else if (text[at] == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1]))
            {
                at++;
            }
            else if (IsNamePart(text[at]) && text[at] != '$')
            {
                at++;
            }
            else
            {
                break;
            }
        }

        Add(ScanTokenKind.Number, start, at);
        return at;
    }
}

/// <summary>
/// A scanned text whose literals may hold code - Dart's, JavaScript's and PHP's interpolations, a JSX element's expressions -
/// which a reader scans in its turn as a text of its own.
/// </summary>
/// <param name="Scan">The tokens, comments apart and brackets matched.</param>
/// <param name="Interpolations">For each literal that holds code, by where it begins: where each run of code it holds directly begins and ends.</param>
/// <param name="Lines">The text's lines.</param>
/// <param name="Elements">For each JSX element, by where it begins: where the name of each tag it opens stands; none where the text holds no markup.</param>
internal sealed record InterpolatedText(
    ScannedText Scan,
    IReadOnlyDictionary<long, List<(long Start, long End)>> Interpolations,
    CodeLineIndex Lines,
    IReadOnlyDictionary<long, List<(long Start, long End)>>? Elements = null)
{
    /// <summary>
    /// What the scans of the text read of its literals (<see cref="ScanCloses"/>): what the scan of a literal's code is
    /// given, so it passes over each literal that code holds at once; none where the lexer keeps no such index.
    /// </summary>
    public ScanCloses? Closes { get; init; }
}

/// <summary>
/// One text's close-index: what each literal a scan of the text read - at any depth, in the code an interpolation holds
/// too - was found to be, by where its token begins: where it ends, its value's parts, its holes and the code it holds
/// directly. The scan of an interpolation's code, given the index the scan before it filled, passes over each literal that
/// code holds to its end at once, taking what was found, rather than reading it again with all it nests; so each
/// character of a text is read by its first scan and by the scan of its own interpolation's code alone - never again by
/// the scan of each interpolation outside it - however deep they nest. One index serves one text and one reader at a
/// time.
/// </summary>
internal sealed class ScanCloses
{
    private readonly Dictionary<long, ScanLiteralRead> _read = [];

    /// <summary>
    /// How many characters the scans of the text read: each scan's own, less those of each literal it passed over at once
    /// - what tells a scan that read again what an earlier one read.
    /// </summary>
    public long Read { get; private set; }

    /// <summary>Counts a scan of the text from <paramref name="start"/> to <paramref name="end"/>.</summary>
    internal void Scanned(long start, long end) => Read += Math.Max(end - start, 0);

    /// <summary>Keeps what the literal whose token begins at <paramref name="start"/> was read to be, where nothing is kept of it yet.</summary>
    internal void Keep(long start, ScanLiteralRead read) => _read.TryAdd(start, read);

    /// <summary>
    /// What the literal whose token begins at <paramref name="start"/> was read to be, for a scan that reads to
    /// <paramref name="end"/>: one that closed before that end, or was left open at it - the scan's own reading of it,
    /// which would end at the same place; <see langword="null"/> where none was kept, and the scan reads it.
    /// </summary>
    internal ScanLiteralRead? Of(long start, long end)
    {
        if (!_read.TryGetValue(start, out var read) || (read.LeftOpen ? read.End != end : read.End > end))
        {
            return null;
        }

        Read -= read.End - start;
        return read;
    }
}

/// <summary>
/// What a scan found one literal to be (<see cref="ScanCloses"/>): where its token ends, its value's constant parts and
/// the code it holds directly, each as its lexer says them; how its parts are decoded is its lexer's to say again, by its
/// form and where it stands.
/// </summary>
/// <param name="End">Where its token ends.</param>
/// <param name="Parts">Its value's constant parts; a JSX element's, the strings its attributes hold.</param>
/// <param name="Spans">Where each run of code it holds directly begins and ends, none empty, in order.</param>
internal sealed record ScanLiteralRead(long End, IReadOnlyList<ScanLiteralPart> Parts, IReadOnlyList<(long Start, long End)> Spans)
{
    /// <summary>Whether it holds holes of its own.</summary>
    public bool Holes { get; init; }

    /// <summary>Whether its closer never came: its reading ran to the end of the scan that read it.</summary>
    public bool LeftOpen { get; init; }

    /// <summary>Where its value's text begins.</summary>
    public long Content { get; init; }

    /// <summary>How many characters of white space each of its lines begins with that are no part of its value: a heredoc's.</summary>
    public long Indent { get; init; }

    /// <summary>Where the name of each tag it opens stands, where it is a JSX element; <see langword="null"/> where it is a string.</summary>
    public IReadOnlyList<(long Start, long End)>? Tags { get; init; }

    /// <summary>Where it is a JSX element, where the name of the attribute each of its parts is the value of stands (<see cref="ScanLiteral.Names"/>); <see langword="null"/> where it is a string.</summary>
    public IReadOnlyList<(long Start, long End)>? Names { get; init; }
}
