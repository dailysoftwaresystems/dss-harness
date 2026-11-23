using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DssHarness.Core.Text;

namespace DssHarness.Core.Mutations;

/// <summary>One case a test binary's report names.</summary>
/// <param name="Id">Its name as an arm declares it: <c>classname.name</c>, or the name alone where it has no class.</param>
/// <param name="Ran">Whether it ran: anything but a case the report says was not run, a skipped one included.</param>
/// <param name="Red">Whether it failed: it holds a failure or an error.</param>
/// <param name="Time">How long it took, in seconds as the report writes it; null where the report says nothing.</param>
/// <param name="Message">
/// What its failure or error said: the <c>message</c> attribute the runner gave it, else the first line of its body;
/// null where it passed or said nothing.
/// </param>
/// <param name="File">The file it names - the case's own <c>file</c> attribute (GoogleTest's), else one its body places itself in (doctest's <c>path(line):</c>); null where neither does.</param>
/// <param name="Line">The line its <paramref name="File"/> names; null where none was named.</param>
/// <param name="Outcome">How it failed: <c>errored</c> where it holds an <c>error</c>, <c>failed</c> where a <c>failure</c>; null where it did not.</param>
public sealed record JUnitCase(
    string Id,
    bool Ran,
    bool Red,
    double? Time = null,
    string? Message = null,
    string? File = null,
    int? Line = null,
    string? Outcome = null);

/// <summary>
/// A test binary's JUnit XML report, read the one way GoogleTest, Catch2, doctest and Boost.Test all write it: every
/// <c>testcase</c> under the <c>testsuites</c> or <c>testsuite</c> root, at whatever depth its suites nest.
/// </summary>
/// <remarks>
/// <para>
/// A case is named <c>classname.name</c>, which for GoogleTest is <c>Suite.Case</c>, and <c>Prefix/Suite.Case/0</c> for a
/// parameterized one; a case with no class is named by its name. It ran unless its <c>status</c> is <c>notrun</c>, which
/// is how GoogleTest reports a disabled case: a skipped case ran, and was skipped, which a count of cases run includes.
/// It is red when it holds a <c>failure</c> or an <c>error</c>.
/// </para>
/// <para>
/// Read from the report, never from what the binary printed: a case name parsed out of a test's output is whatever the
/// output happened to say, and the report is the runner's own record of every case it reached.
/// </para>
/// </remarks>
public sealed class JUnitReport
{
    private JUnitReport(IReadOnlyList<JUnitCase> cases, TimeSpan? caseTime)
    {
        Cases = cases;
        CaseTime = caseTime;
    }

    /// <summary>Every case, in the order the report names them.</summary>
    public IReadOnlyList<JUnitCase> Cases { get; }

    /// <summary>
    /// How long the cases took by the runner's own clock: the sum of every case's <c>time</c>, in seconds as the report
    /// writes it, or <see langword="null"/> where no case says - what the run took beyond it is the program's start and
    /// exit.
    /// </summary>
    public TimeSpan? CaseTime { get; }

    /// <summary>How many cases ran, skipped ones included.</summary>
    public int Ran => Cases.Count(@case => @case.Ran);

    /// <summary>The cases that ran, by name.</summary>
    public IReadOnlySet<string> RanCases => Cases.Where(@case => @case.Ran).Select(@case => @case.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The cases that failed, by name, each once, in the order the report names them.</summary>
    public IReadOnlyList<string> Reds => [.. Cases.Where(@case => @case.Red).Select(@case => @case.Id).Distinct(StringComparer.Ordinal)];

    /// <summary>Reads <paramref name="xml"/>; <see langword="null"/> where it is no report.</summary>
    /// <param name="xml">The report's text.</param>
    public static JUnitReport? Read(string xml) => Read(xml, out _);

    /// <summary>
    /// Reads <paramref name="xml"/>; <see langword="null"/> where it is no XML, no JUnit report, or declares a document
    /// type, which a report never needs and an entity in one could make reading it reach for other files -
    /// <paramref name="problem"/> saying which, as a verdict's line can say it: a runner writing another format, and a
    /// report a crash cut short, are told apart by what the reader made of each.
    /// </summary>
    /// <param name="xml">The report's text.</param>
    /// <param name="problem">Why it is no report, or <see langword="null"/> where it is one.</param>
    public static JUnitReport? Read(string xml, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var cases = new List<JUnitCase>();

        problem = ReadCases(() => new StringReader(xml), cases.Add);

        if (problem is not null)
        {
            return null;
        }

        var timed = cases.Select(@case => @case.Time).OfType<double>().ToList();

        return new JUnitReport(cases, timed.Count == 0 ? null : TimeSpan.FromSeconds(Math.Min(timed.Sum(), TimeSpan.MaxValue.TotalSeconds)));
    }

    /// <summary>
    /// Reads the report <paramref name="open"/> opens a case at a time, giving each to <paramref name="each"/> as it is
    /// read - one case held at a time, whatever the report's size - and answers why it is no report, or
    /// <see langword="null"/> where it is one. A report that breaks off has given every case before the break.
    /// </summary>
    /// <param name="open">Opens the report's text; opened again only to say why it is no XML.</param>
    /// <param name="each">Takes each case, in the order the report names them.</param>
    internal static string? ReadCases(Func<TextReader> open, Action<JUnitCase> each)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(each);

        try
        {
            using var text = open();
            using var reader = ReaderOf(text);

            if (RootOf(reader) is not ("testsuites" or "testsuite") and var root)
            {
                return $"its root is '{root}', and a JUnit report's is 'testsuites' or 'testsuite'";
            }

            CasesOf(reader, each);
            return null;
        }
        catch (XmlException ex)
        {
            return Unread(ex, open);
        }
    }

    /// <summary>
    /// The one reader every XML report and results file is read through: no document type is taken - a report never
    /// needs one, and an entity in one could reach for other files or expand without end - and no other file is reached
    /// for.
    /// </summary>
    /// <param name="text">The document's text.</param>
    internal static XmlReader ReaderOf(TextReader text)
        => XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

    /// <summary>The local name of the document's root, <paramref name="reader"/> left on it; empty where it holds none.</summary>
    /// <param name="reader">The reader, at the document's start.</param>
    /// <exception cref="XmlException">The text is no XML.</exception>
    internal static string RootOf(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return reader.MoveToContent() == XmlNodeType.Element ? reader.LocalName : string.Empty;
    }

    /// <summary>
    /// Every <c>testcase</c> beneath where <paramref name="reader"/> stands, at whatever depth its suites nest, given to
    /// <paramref name="each"/> as it is read.
    /// </summary>
    /// <param name="reader">The reader, on the report's root.</param>
    /// <param name="each">Takes each case.</param>
    /// <exception cref="XmlException">The report breaks off, or is no XML from some point on.</exception>
    internal static void CasesOf(XmlReader reader, Action<JUnitCase> each)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(each);

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "testcase" && reader.NamespaceURI.Length == 0)
            {
                each(Read((XElement)XNode.ReadFrom(reader)));
            }
            else
            {
                reader.Read();
            }
        }
    }

    /// <summary>
    /// Why a document that broke off as <paramref name="ex"/> says is no report, in this tool's words where it declares a
    /// document type: the reader's own message says how to make a reader take one, which is nothing a report's author can
    /// do. Whether it declares one is read from its prolog alone, <paramref name="open"/> opening it again.
    /// </summary>
    /// <param name="ex">What the reader made of it.</param>
    /// <param name="open">Opens the document's text again.</param>
    internal static string Unread(XmlException ex, Func<TextReader> open)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(open);

        using var text = open();

        return DeclaresDocumentType(text)
            ? "it declares a document type, which no report needs and this never reads"
            : $"it is no XML: {ex.Message}";
    }

    /// <summary>One case, whole: its identity as ever, and - where it failed - how long, what it said, and where.</summary>
    private static JUnitCase Read(XElement @case)
    {
        var failed = @case.Elements().FirstOrDefault(child => child.Name.LocalName is "failure" or "error");

        if (failed is null)
        {
            return new(
                Id(@case),
                !string.Equals((string?)@case.Attribute("status"), "notrun", StringComparison.Ordinal),
                Red: false,
                Time: SecondsIn(@case));
        }
        // What the failure said: the runner's own summary of it where it gave one, else the first line of the detail
        // it wrote under it - which is a whole assertion's words, and more than a name to search a log for.
        var body = BodyOf(failed);
        var said = (string?)failed.Attribute("message") is { Length: > 0 } message ? message
            : body.FirstOrDefault(line => line.Trim().Length > 0)?.Trim();

        // Where it was: the case's own place where the runner wrote one (GoogleTest's attributes), else the first
        // place its body names (doctest writes its failures as `path(line): what failed`).
        var file = (string?)@case.Attribute("file");
        var line = int.TryParse((string?)@case.Attribute("line"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var placed) ? placed : (int?)null;

        // A place whose line is no whole number an int holds names no line, never a failure of the reading.
        if (file is null && body.FirstOrDefault(Place.IsMatch) is { } named)
        {
            var found = Place.Match(named);
            file = found.Groups["file"].Value;
            line = int.TryParse(found.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        return new(
            Id(@case),
            !string.Equals((string?)@case.Attribute("status"), "notrun", StringComparison.Ordinal),
            Red: true,
            Time: SecondsIn(@case),
            Message: said,
            File: file,
            Line: line,
            Outcome: failed.Name.LocalName == "error" ? "errored" : "failed");
    }

    /// <summary>The case's own time, in seconds as the report writes it; null where it says none.</summary>
    private static double? SecondsIn(XElement @case)
        => double.TryParse((string?)@case.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds >= 0 ? seconds : null;

    /// <summary>A failure element's body, its lines as the runner wrote them - a CDATA block's as much as a text's.</summary>
    private static IReadOnlyList<string> BodyOf(XElement failed)
        => (failed.Value ?? string.Empty).Split('\n').Select(text => text.TrimEnd('\r')).ToList();

    /// <summary>The place doctest writes a failure at: <c>path(line): what failed</c>.</summary>
    private static readonly Regex Place = LinearRegex.Compile(@"^(?<file>.+?)\((?<line>\d+)\)\s*:");

    /// <summary>
    /// Whether <paramref name="text"/> declares a document type: one follows its prolog - white space, processing
    /// instructions and comments - which is where a reader that takes none stops. One named inside a comment, or below
    /// the root, declares nothing. Read a character at a time, the prolog alone, however long the document is.
    /// </summary>
    private static bool DeclaresDocumentType(TextReader text)
    {
        while (true)
        {
            while (text.Peek() is var next && next >= 0 && char.IsWhiteSpace((char)next))
            {
                text.Read();
            }

            if (text.Read() != '<')
            {
                return false;
            }

            string? closes;

            if (text.Peek() == '?')
            {
                closes = "?>";
            }
            else if (text.Peek() != '!')
            {
                return false;
            }
            else
            {
                text.Read();

                if (text.Peek() == 'D')
                {
                    return Opened(text, "DOCTYPE");
                }

                closes = Opened(text, "--") ? "-->" : null;
            }

            if (closes is null || !Through(text, closes))
            {
                return false;
            }
        }

        // Whether the text goes on with exactly what is asked, read past it where it does.
        static bool Opened(TextReader text, string asked)
        {
            foreach (var expected in asked)
            {
                if (text.Read() != expected)
                {
                    return false;
                }
            }

            return true;
        }

        // Read to the end of what closes - the last characters read being it - false where the text ends before it.
        static bool Through(TextReader text, string closes)
        {
            var last = new char[closes.Length];
            var read = 0;

            while (text.Read() is var next && next >= 0)
            {
                Array.Copy(last, 1, last, 0, last.Length - 1);
                last[^1] = (char)next;
                read++;

                if (read >= last.Length && last.AsSpan().SequenceEqual(closes))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary><c>classname.name</c>, or the name alone where the case has no class.</summary>
    private static string Id(XElement @case)
    {
        var name = (string?)@case.Attribute("name") ?? string.Empty;

        return (string?)@case.Attribute("classname") is { Length: > 0 } classname ? classname + "." + name : name;
    }
}
