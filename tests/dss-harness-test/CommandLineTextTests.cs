using DssHarness.Core.Configuration;
using DssHarness.Core.Processes;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

/// <summary>
/// How a command line written as text becomes a program and its arguments - the one splitter an action file's run
/// lines and a set-up's lines are read by - and the shapes a per-platform value shares.
/// </summary>
public sealed class CommandLineTextTests
{
    /// <summary>
    /// Words split at spaces, a double-quoted run of them kept as one word without its quotes, the space around the line
    /// no part of it, and nothing a shell would read - a pipe, a variable, a wildcard - anything but an argument.
    /// </summary>
    [Theory]
    [InlineData("docker compose up -d", new[] { "docker", "compose", "up", "-d" })]
    [InlineData("  tool  \"two words\" x  ", new[] { "tool", "two words", "x" })]
    [InlineData("tool a|b $HOME *.txt && c", new[] { "tool", "a|b", "$HOME", "*.txt", "&&", "c" })]
    public void ALine_SplitsIntoItsProgramAndArguments_WithNoShell(string line, string[] expected)
    {
        Assert.Null(CommandLineText.Split(line, out var arguments));
        Assert.Equal(expected, arguments);
    }

    /// <summary>
    /// A line with an unbalanced quote is refused rather than run with what follows the quote dropped, and a line naming
    /// no program is refused too; neither hands over any argument.
    /// </summary>
    [Theory]
    [InlineData("tool --x \"a b\" \"c", "has an unbalanced double quote. The command splitter drops everything after an unterminated quote without a word, so this line would run with its remaining arguments missing.")]
    [InlineData("   ", "names no program.")]
    [InlineData("\"\"", "names no program.")]
    public void ALineThatCannotRunAsWritten_IsRefused(string line, string expected)
    {
        Assert.Equal(expected, CommandLineText.Split(line, out var arguments));
        Assert.Empty(arguments);
    }

    /// <summary>
    /// What a refusal says of a program nothing allows names what is declared, or that nothing is: one sentence for
    /// every line held to the tool policy.
    /// </summary>
    [Fact]
    public void AnUndeclaredProgram_IsSaidOneWay_NamingWhatIsDeclared()
    {
        Assert.Equal(
            "is not declared under 'tools' and is not a path inside the repository (nothing is declared under 'tools')",
            ActionToolPolicy.Undeclared([]));
        Assert.Equal(
            "is not declared under 'tools' and is not a path inside the repository (declared: 'cmake', 'git')",
            ActionToolPolicy.Undeclared([new ToolConfig { Name = "cmake" }, new ToolConfig { Name = "git" }]));
    }

    /// <summary>
    /// Two values of one per-platform shape are equal where they say the same, whichever way they were written; a value of
    /// another shape saying the same is not, since a build output and a set-up's line are never one another.
    /// </summary>
    [Fact]
    public void PerPlatformValues_AreEqualWithinTheirShape_NeverAcrossShapes()
    {
        Assert.Equal(SetUpLine.Everywhere("x"), (SetUpLine)"x");
        Assert.True(SetUpLine.Keyed([new("windows", "a"), new("all", "b")]) == SetUpLine.Keyed([new("windows", "a"), new("all", "b")]));
        Assert.NotEqual<PerPlatformText>(SetUpLine.Everywhere("x"), BuildOutput.Everywhere("x"));
        Assert.False(SetUpLine.Everywhere("x").Equals(BuildOutput.Everywhere("x")));
        Assert.Equal(SetUpLine.Everywhere("x"), SetUpLine.Keyed([new("all", "x")]));
        Assert.NotEqual(SetUpLine.Everywhere("x"), SetUpLine.Everywhere("X"));
    }
}
