using System.CommandLine;

namespace RepoHarness.Cli;

/// <summary>
/// A command line's flags, read where the parser may have refused the line: whether <c>--json</c> is set
/// (<see cref="JsonAnswer.Asked"/>), and each flag a command's stopped document says (<see cref="StoppedDocument"/>). The
/// one reader of them there, and it never throws: a flag whose own value the parser refused - given twice, a value each
/// time - reads as not given, since the parser could not tell what it was given, where reading that value would throw and
/// end the process with no answer at all.
/// </summary>
/// <remarks>
/// The parser weighs every option of the command it reached before it hands the line back, so a value it refused is
/// among that option's own errors by then.
/// </remarks>
internal sealed class CommandFlags
{
    private readonly ParseResult _parseResult;

    /// <summary>Reads the flags of <paramref name="parseResult"/>.</summary>
    /// <param name="parseResult">The command line, parsed or refused.</param>
    internal CommandFlags(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        _parseResult = parseResult;
    }

    /// <summary>
    /// Whether <paramref name="flag"/> is set: given, as the command's own, with no value or a value the parser read as
    /// true - never where the parser refused its value.
    /// </summary>
    /// <param name="flag">One of the command's flags.</param>
    internal bool IsSet(Option<bool> flag)
    {
        ArgumentNullException.ThrowIfNull(flag);

        return _parseResult.GetResult(flag) is { } given && !given.Errors.Any() && given.GetValueOrDefault<bool>();
    }
}
