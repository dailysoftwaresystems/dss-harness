namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>What a token of a scanned text is, as every scanned language's lexer tells them apart.</summary>
internal enum ScanTokenKind
{
    /// <summary>An identifier or a keyword: a run of a name's characters.</summary>
    Name,

    /// <summary>A number, its separators and suffix with it.</summary>
    Number,

    /// <summary>A string or a character literal, its quotes with it: never read as code.</summary>
    Text,

    /// <summary>One punctuator - a bracket among them, which the scan matches - or a few the language's lexer keeps whole, as C++'s <c>::</c> and <c>-&gt;</c>.</summary>
    Punctuation,

    /// <summary>A line the lexer keeps whole and the code around it does not read - a directive, as C's preprocessor lines are - its continuations with it.</summary>
    Directive,
}

/// <summary>Where a directive stands among a conditional's branches: what the scan's bracket matching reads both branches by.</summary>
internal enum ScanBranch
{
    /// <summary>No conditional's.</summary>
    None,

    /// <summary>It opens a conditional, as C's <c>#if</c>, <c>#ifdef</c> and <c>#ifndef</c> do.</summary>
    Opens,

    /// <summary>It begins another branch of the conditional it is in, as C's <c>#elif</c> and <c>#else</c> do.</summary>
    Alternative,

    /// <summary>It ends the conditional it is in, as C's <c>#endif</c> does.</summary>
    Closes,
}

/// <summary>One token of a scanned text.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Start">Where it begins in the text.</param>
/// <param name="Length">How many characters it has.</param>
/// <param name="Line">The line it begins on, from 1.</param>
/// <param name="EndLine">The line it ends on.</param>
/// <param name="Branch">Where a directive stands among a conditional's branches.</param>
internal readonly record struct ScanToken(ScanTokenKind Kind, int Start, int Length, int Line, int EndLine, ScanBranch Branch = ScanBranch.None)
{
    /// <summary>Where it ends in the text: the first character after it.</summary>
    public int End => Start + Length;
}

/// <summary>One comment of a scanned text, which no token holds: what a declaration's summary is read from.</summary>
/// <param name="Start">Where it begins in the text, its marker with it.</param>
/// <param name="Length">How many characters it has.</param>
/// <param name="Line">The line it begins on, from 1.</param>
/// <param name="EndLine">The line it ends on.</param>
internal readonly record struct ScanComment(int Start, int Length, int Line, int EndLine);
