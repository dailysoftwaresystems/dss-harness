using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A style sheet - <c>.css</c>, <c>.scss</c>, <c>.sass</c> and <c>.less</c> - read as the rules it holds: each simple
/// part of each selector - <c>.btn</c>, <c>#main</c>, <c>my-widget</c> - a <c>member</c> SubKind <c>rule</c> named as
/// the selector writes it, over the rule's lines; each custom property <c>--x</c> and each SCSS or Less variable a
/// <c>constant</c>; each <c>@keyframes</c> name a rule; each <c>@mixin</c> a member of its own. A <c>var(--x)</c> is a
/// use of the constant, an <c>@include m</c> a use of the mixin, an <c>@extend</c> an inheritance of the selector it
/// names; an <c>@import</c>, <c>@use</c> and <c>@forward</c> naming a file by a relative path is an import of it.
/// Nesting is read as SCSS and Less write it, a nested rule's parts its own.
/// </summary>
public sealed partial class CssExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "css";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".css", ".scss", ".sass", ".less");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Style sheets (.css, .scss, .sass, .less): each simple part of each selector as a rule node named as the selector writes it, each custom property and each SCSS or Less variable as a constant, each @keyframes name as a rule, each @mixin as a member, a var(--x) and an @include as uses, an @extend as an inheritance, and an @import, @use and @forward naming a file by a relative path as an import of it",
        [
            "a pseudo-class, a pseudo-element and an attribute selector: parts of no node of their own, the rule's other parts its own",
            "a rule's declarations, the custom properties among them excepted: no nodes of their own",
            "an @import or @use that names a package of a host - a url( ) or a bare name no file of the tree answers - no import of it",
            "an SCSS interpolation inside a value, and a selector an interpolation writes: read as text, never as a name",
            "an inline comment (//) in a .css file, which the language does not write: read as a comment all the same",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sheet = new Sheet(input.Text);
        var nodes = new List<ExtractNode>();
        var references = new List<ExtractReference>();
        var imports = new List<ExtractImport>();
        var ids = new ExtractIds();

        Sheet.Walk(input.Path, sheet, nodes, references, imports, ids, depth: 0, input.Lines.Count);

        // What a reference is made from: the first node of the sheet holds what no rule does - a use or an inheritance
        // the text writes above any rule. A sheet that holds no node at all has no reference made from nothing.
        var first = nodes.MinBy(node => node.Start)?.Id;

        return new FileExtract([.. nodes])
        {
            References = [.. first is null ? references.Select(reference => reference with { From = "" }).Where(reference => reference.From != "") : references.Select(reference => reference with { From = reference.From is null ? first : reference.From })],
            Imports = imports,
        };
    }

    /// <summary>
    /// A style sheet's own reading of itself, over the one text: comments and strings skipped whole, at-rules, rules
    /// with their selectors, declarations with their values, and nesting.
    /// </summary>
    private sealed partial class Sheet(string text)
    {
        /// <summary>Walks the sheet from where it stands: rules and at-rules at this depth, to the close of the block around it.</summary>
        public static void Walk(string file, Sheet sheet, List<ExtractNode> nodes, List<ExtractReference> references, List<ExtractImport> imports, ExtractIds ids, int depth, int lineCount)
        {
            while (sheet.More())
            {
                var start = sheet.Line;

                if (sheet.At())
                {
                    At(file, sheet, nodes, references, imports, ids, FileExtract.LineWithin(start, lineCount));
                    continue;
                }

                if (sheet.Close())
                {
                    return;
                }

                // A declaration the sheet's own top holds - an SCSS or Less variable - read before any selector: a
                // pseudo-class a selector writes is a selector still, told by the brace its rule opens.
                if (sheet.Declaration(out var declared) is { } variable)
                {
                    Rule(file, ids, nodes, variable, FileExtract.LineWithin(start, lineCount), "constant");

                    foreach (var used in Variables(declared!))
                    {
                        references.Add(new ExtractReference(null!, CodeEdgeKinds.Uses, used, FileExtract.LineWithin(sheet.Line, lineCount)));
                    }

                    continue;
                }

                if (sheet.Selector() is not { } selector)
                {
                    // What stands here opens no block - the end of the sheet among them - and no rule is read of it.
                    continue;
                }

                var made = nodes.Count;

                foreach (var part in Parts(selector))
                {
                    Rule(file, ids, nodes, part, FileExtract.LineWithin(start, lineCount), "rule");
                }

                // The block the selector opens: its declarations, its nested rules, and its close ending the rules it began.
                Block(file, sheet, nodes, references, ids, depth + 1, lineCount);

                for (var at = made; at < nodes.Count; at++)
                {
                    if (nodes[at].SubKind == "rule")
                    {
                        nodes[at] = nodes[at] with { End = FileExtract.LineWithin(Math.Max(nodes[at].Start, sheet.Line), lineCount) };
                    }
                }
            }
        }

        /// <summary>The block of a rule, to its close: declarations with their values read for uses, and nested rules.</summary>
        private static void Block(string file, Sheet sheet, List<ExtractNode> nodes, List<ExtractReference> references, ExtractIds ids, int depth, int lineCount)
        {
            while (sheet.More())
            {
                if (sheet.At())
                {
                    At(file, sheet, nodes, references, imports: null, ids, FileExtract.LineWithin(sheet.Line, lineCount));
                    continue;
                }

                if (sheet.Close())
                {
                    return;
                }

                if (sheet.Declaration(out var value) is not { } name)
                {
                    // A nested rule, read where the text holds none of a declaration's colon before its brace.
                    Walk(file, sheet, nodes, references, [], ids, depth, lineCount);
                    continue;
                }

                if (name.StartsWith("--", StringComparison.Ordinal) || name[0] is '$' or '@')
                {
                    Rule(file, ids, nodes, name, FileExtract.LineWithin(sheet.Line, lineCount), "constant");
                }

                foreach (var used in Variables(value!))
                {
                    references.Add(new ExtractReference(null!, CodeEdgeKinds.Uses, used, FileExtract.LineWithin(sheet.Line, lineCount)));
                }
            }
        }

        /// <summary>One at-rule, by its word.</summary>
        private static void At(string file, Sheet sheet, List<ExtractNode> nodes, List<ExtractReference> references, List<ExtractImport>? imports, ExtractIds ids, int line)
        {
            switch (sheet.Word)
            {
                case "import":
                    if (imports is not null && sheet.Argument() is { } imported && imported.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && !imported.Contains(':', StringComparison.Ordinal))
                    {
                        imports.Add(new ExtractImport(CodeImportForms.Relative, imported, line));
                    }

                    sheet.SkipStatement();
                    break;

                case "use" or "forward":
                    if (imports is not null && sheet.Argument() is { } used)
                    {
                        imports.Add(new ExtractImport(CodeImportForms.Relative, $"{used}.scss", line));
                        imports.Add(new ExtractImport(CodeImportForms.Relative, $"_{used}.scss", line) { Fallback = true });
                    }

                    sheet.SkipStatement();
                    break;

                case "keyframes":
                    if (sheet.Argument() is { } animation)
                    {
                        Rule(file, ids, nodes, animation, line, "rule");
                    }

                    sheet.SkipBlock();
                    break;

                case "mixin":
                    if (sheet.Argument() is { } mixin)
                    {
                        Rule(file, ids, nodes, mixin, line, "mixin");
                        Block(file, sheet, nodes, references, ids, depth: 1, lineCount: int.MaxValue);
                    }
                    else
                    {
                        sheet.SkipBlock();
                    }

                    break;

                case "include" or "extend":
                    if (sheet.Argument() is { } named)
                    {
                        references.Add(new ExtractReference(null!, sheet.Word == "extend" ? CodeEdgeKinds.Inherits : CodeEdgeKinds.Uses, named, line));
                    }

                    sheet.SkipStatement();
                    break;

                default:
                    sheet.SkipBlock();
                    break;
            }
        }

        /// <summary>The node of <paramref name="name"/>: one of the sheet's own, kept where it already stands.</summary>
        private static void Rule(string file, ExtractIds ids, List<ExtractNode> nodes, string name, int line, string subKind)
        {
            if (nodes.Any(node => node.Id == $"css:{file}#{name}"))
            {
                return;
            }

            nodes.Add(new ExtractNode(ids.Next($"css:{file}#{name}"), CodeNodeKinds.Member, name, name, line, line, name) { SubKind = subKind });
        }
        /// <summary>The simple parts of <paramref name="selector"/>: each class, id, placeholder and element name, and no pseudo-class, pseudo-element, attribute or combinator.</summary>
        internal static IEnumerable<string> Parts(string selector)
        {
            foreach (var one in selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var word = new StringBuilder();
                var boundary = ' ';

                foreach (var character in one)
                {
                    if (char.IsLetterOrDigit(character) || character is '_' or '-' or '$')
                    {
                        _ = word.Append(character);
                        continue;
                    }

                    if (word.Length > 0)
                    {
                        if (boundary is '.' or '#' or '%')
                        {
                            yield return boundary + word.ToString();
                        }
                        else if (boundary == ' ')
                        {
                            yield return word.ToString();
                        }

                        word.Clear();
                    }

                    boundary = character;
                }

                if (word.Length > 0)
                {
                    if (boundary is '.' or '#' or '%')
                    {
                        yield return boundary + word.ToString();
                    }
                    else if (boundary == ' ')
                    {
                        yield return word.ToString();
                    }
                }
            }
        }

        /// <summary>The custom properties and variables a declaration's value uses: a <c>var(--x)</c> and a bare <c>$x</c>.</summary>
        internal static IEnumerable<string> Variables(string value)
            => VariablesOf().Matches(value).Select(match => match.Groups["name"].Value);

        [GeneratedRegex(@"var\(\s*(?<name>--[\w-]+)|(?<name>\$[\w-]+)", RegexOptions.CultureInvariant)]
        private static partial Regex VariablesOf();

        private readonly string _text = text;
        private int _at;

        /// <summary>The line the reading stands at, from 1.</summary>
        public int Line { get; private set; } = 1;

        /// <summary>The word an at-rule is told by.</summary>
        public string Word { get; private set; } = string.Empty;

        /// <summary>Whether more is left to read.</summary>
        public bool More()
        {
            Space();
            return _at < _text.Length;
        }

        /// <summary>Whitespace, semicolons and comments.</summary>
        private void Space()
        {
            while (_at < _text.Length)
            {
                if (_text[_at] == '/' && _at + 1 < _text.Length && (_text[_at + 1] == '*' || _text[_at + 1] == '/'))
                {
                    Step();
                    continue;
                }

                if (!char.IsWhiteSpace(_text[_at]) && _text[_at] != ';')
                {
                    return;
                }

                Step();
            }
        }

        /// <summary>Reads an at-rule's word; whether one stands here, and one the sheet's own reading knows - a Less variable's <c>@name:</c> is a declaration, no at-rule.</summary>
        public bool At()
        {
            Space();

            if (_at >= _text.Length || _text[_at] != '@')
            {
                return false;
            }

            var start = ++_at;

            while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '-'))
            {
                _at++;
            }

            Word = _text[start.._at];

            if (AtWords.Contains(Word, StringComparer.Ordinal))
            {
                return true;
            }

            _at = start - 1;
            return false;
        }

        /// <summary>The at-rules the sheet's own reading takes; anything else an <c>@</c> begins - a Less variable - is no at-rule of its own.</summary>
        private static readonly string[] AtWords = ["import", "use", "forward", "keyframes", "mixin", "include", "extend", "media", "supports", "charset", "namespace", "font-face", "layer", "property", "container", "if", "else", "return", "function", "each", "debug", "warn", "error"];

        /// <summary>Closes the block the reading stands at; whether a close stands here.</summary>
        public bool Close()
        {
            Space();

            if (_at >= _text.Length || _text[_at] != '}')
            {
                return false;
            }

            _at++;
            return true;
        }

        /// <summary>The argument an at-rule takes: the quoted string or the name before its brackets, block or end.</summary>
        public string? Argument()
        {
            Space();
            var start = _at;

            while (_at < _text.Length && _text[_at] is not ('{' or ';' or '\n'))
            {
                if (_text[_at] == '\n')
                {
                    break;
                }

                if (_text[_at] is '"' or '\'')
                {
                    return Quoted();
                }

                _at++;
            }

            var argument = _text[start.._at].Trim();
            return argument.Length == 0 ? null : argument.Split(new[] { '(', ' ', '\t' }, 2)[0];
        }

        /// <summary>A selector, to the block it opens, and the brace taken; null where no block opens - the end of the sheet among them.</summary>
        public string? Selector()
        {
            var text = new StringBuilder();
            var opens = false;

            while (_at < _text.Length)
            {
                if (_text[_at] == '{')
                {
                    opens = true;
                    break;
                }

                _ = text.Append(_text[_at] == '\n' ? ' ' : _text[_at]);

                if (_text[_at] == '\n')
                {
                    Line++;
                }

                Step();
            }

            if (!opens)
            {
                return null;
            }

            _at++;
            return text.ToString().Trim();
        }

        /// <summary>
        /// A declaration, to the semicolon that ends it or the brace that closes its block: its name, with its value
        /// given back; null where what stands here is no declaration - a nested rule - and nothing read past it.
        /// </summary>
        public string? Declaration(out string? value)
        {
            value = null;
            var start = _at;

            while (_at < _text.Length && _text[_at] is not (':' or '{' or ';' or '}'))
            {
                Step();
            }

            var name = _text[start..Math.Min(_at, _text.Length)].Trim();

            // What stops the name tells what it is: a brace before a colon is a nested rule, and a name an element's or
            // the parent's - & - is a selector's part, never a declaration.
            if (name.Length == 0 || _at >= _text.Length || _text[_at] != ':' || name.AsSpan().ToArray().Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '$' or '@')))
            {
                _at = start;
                return null;
            }

            _at++;
            var from = _at;

            while (_at < _text.Length && _text[_at] is not (';' or '}' or '{'))
            {
                Step();
            }

            // A brace before the semicolon is no declaration: what the colon stood in was a selector's pseudo, and the
            // reading goes back to where it began.
            if (_at < _text.Length && _text[_at] == '{')
            {
                _at = start;
                value = null;
                return null;
            }

            value = _text[from.._at].Trim();

            if (_at < _text.Length && _text[_at] == ';')
            {
                _at++;
            }

            return name;
        }

        /// <summary>Skips a statement to its semicolon or its end of line.</summary>
        public void SkipStatement()
        {
            while (_at < _text.Length && _text[_at] is not (';' or '\n'))
            {
                Step();
            }

            if (_at < _text.Length && _text[_at] == ';')
            {
                _at++;
            }
        }

        /// <summary>Skips a block whole, to after its closing brace, or a statement where none opens.</summary>
        public void SkipBlock()
        {
            Space();

            if (_at < _text.Length && _text[_at] == '{')
            {
                var depth = 0;

                do
                {
                    if (_text[_at] == '{')
                    {
                        depth++;
                    }
                    else if (_text[_at] == '}')
                    {
                        depth--;
                    }

                    Step();
                }
                while (_at < _text.Length && depth > 0);
            }
            else
            {
                SkipStatement();
            }
        }

        /// <summary>A quoted string, to its close.</summary>
        private string Quoted()
        {
            var quote = _text[_at++];
            var start = _at;

            while (_at < _text.Length && _text[_at] != quote)
            {
                Step();
            }

            var text = _text[start.._at];
            _at++;
            return text;
        }

        /// <summary>One character ahead, comments and strings whole.</summary>
        private void Step()
        {
            if (_text[_at] is '/' && _at + 1 < _text.Length && _text[_at + 1] == '*')
            {
                _at += 2;

                while (_at + 1 < _text.Length && (_text[_at] != '*' || _text[_at + 1] != '/'))
                {
                    if (_text[_at] == '\n')
                    {
                        Line++;
                    }

                    _at++;
                }

                _at = Math.Min(_at + 2, _text.Length);
                return;
            }

            if (_text[_at] is '/' && _at + 1 < _text.Length && _text[_at + 1] == '/')
            {
                while (_at < _text.Length && _text[_at] != '\n')
                {
                    _at++;
                }

                return;
            }

            if (_text[_at] is '"' or '\'')
            {
                _ = Quoted();
                return;
            }

            if (_text[_at] == '\n')
            {
                Line++;
            }

            _at++;
        }
    }
}
