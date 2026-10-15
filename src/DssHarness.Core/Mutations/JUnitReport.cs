using System.Xml;
using System.Xml.Linq;

namespace RepoHarness.Core.Mutations;

/// <summary>One case a test binary's report names.</summary>
/// <param name="Id">Its name as an arm declares it: <c>classname.name</c>, or the name alone where it has no class.</param>
/// <param name="Ran">Whether it ran: anything but a case the report says was not run, a skipped one included.</param>
/// <param name="Red">Whether it failed: it holds a failure or an error.</param>
public sealed record JUnitCase(string Id, bool Ran, bool Red);

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
    private JUnitReport(IReadOnlyList<JUnitCase> cases) => Cases = cases;

    /// <summary>Every case, in the order the report names them.</summary>
    public IReadOnlyList<JUnitCase> Cases { get; }

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

        problem = null;

        XDocument document;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            // Said in this tool's words where the reader refused a document type: its own message says how to make a
            // reader take one, which is nothing a report's author can do.
            problem = DeclaresDocumentType(xml)
                ? "it declares a document type, which no report needs and this never reads"
                : $"it is no XML: {ex.Message}";

            return null;
        }

        if (document.Root is not { Name.LocalName: "testsuites" or "testsuite" } root)
        {
            problem = $"its root is '{document.Root?.Name.LocalName}', and a JUnit report's is 'testsuites' or 'testsuite'";
            return null;
        }

        return new JUnitReport(
        [
            .. root.DescendantsAndSelf("testcase").Select(@case => new JUnitCase(
                Id(@case),
                !string.Equals((string?)@case.Attribute("status"), "notrun", StringComparison.Ordinal),
                @case.Elements().Any(child => child.Name.LocalName is "failure" or "error"))),
        ]);
    }

    /// <summary>
    /// Whether <paramref name="xml"/> declares a document type: one follows its prolog - white space, processing
    /// instructions and comments - which is where a reader that takes none stops. One named inside a comment, or
    /// below the root, declares nothing.
    /// </summary>
    private static bool DeclaresDocumentType(ReadOnlySpan<char> xml)
    {
        while (true)
        {
            xml = xml.TrimStart();

            if (xml.StartsWith("<!DOCTYPE", StringComparison.Ordinal))
            {
                return true;
            }

            var closes = xml.StartsWith("<?", StringComparison.Ordinal) ? "?>"
                : xml.StartsWith("<!--", StringComparison.Ordinal) ? "-->"
                : null;

            if (closes is null)
            {
                return false;
            }

            var end = xml.IndexOf(closes, StringComparison.Ordinal);

            if (end < 0)
            {
                return false;
            }

            xml = xml[(end + closes.Length)..];
        }
    }

    /// <summary><c>classname.name</c>, or the name alone where the case has no class.</summary>
    private static string Id(XElement @case)
    {
        var name = (string?)@case.Attribute("name") ?? string.Empty;

        return (string?)@case.Attribute("classname") is { Length: > 0 } classname ? classname + "." + name : name;
    }
}
