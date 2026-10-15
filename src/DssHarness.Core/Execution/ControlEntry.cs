using DssHarness.Core.Testing;

namespace DssHarness.Core.Execution;

/// <summary>
/// One control of a leg's sweep that did not pass, as the leg's line carries it: the unmutated run of a test binary that
/// reddened, hung or could not run - the newest evidence on what each of its arms was judged on, which stops every one
/// of them.
/// </summary>
/// <param name="Runner">The test target, as arms name it.</param>
/// <param name="Verdict">What the control came to, as the ledger names a verdict.</param>
/// <param name="Detail">Its line: the cases that reddened or why it never ran, and what ctest registers for them that a sweep does not apply.</param>
/// <param name="Reds">The cases its run reddened; none where it never ran, or reddened none.</param>
/// <param name="Records">Where the run's records are kept, as a line names a path.</param>
/// <param name="Ctest">What ctest registers for the red cases, or why it could not say; <see langword="null"/> where ctest was not asked.</param>
public sealed record ControlEntry(
    string Runner,
    string Verdict,
    string Detail,
    IReadOnlyList<string> Reds,
    string Records,
    CtestControl? Ctest);

/// <summary>
/// What ctest registered for a control's red cases - which of the tests it registers start the binary, and what of their
/// entries a sweep does not apply - or why it could not say.
/// </summary>
/// <param name="Matched">How the cases were matched - <c>one-test</c>, <c>by-name</c>, <c>uncertain</c> or <c>none</c> - or <see langword="null"/> where ctest could not say.</param>
/// <param name="Unread">Why ctest could not say; <see langword="null"/> where it answered.</param>
/// <param name="Tests">Each test of the answer, with the cases matched to it and what of its entry a sweep does not apply.</param>
public sealed record CtestControl(string? Matched, string? Unread, IReadOnlyList<CtestTestLine> Tests);
