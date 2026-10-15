using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The names of a leg's compilers, <c>{compiler_C}</c> and <c>{compiler_CXX}</c>: filled in with the program the leg's
/// build identified, refused wherever nothing fills them in - saying why - and standing as written only for a check
/// made ahead of the build that identifies them.
/// </summary>
public sealed class LegCompilerNamesTests
{
    private const string Gcc = "/opt/mingw/bin/gcc";
    private const string Gxx = "/opt/mingw/bin/g++";

    /// <summary>Each name is filled in with its own language's program, wherever a line names it, as often as it does.</summary>
    [Theory]
    [InlineData(PlaceholderPolicy.Refuse)]
    [InlineData(PlaceholderPolicy.LeaveAsWritten)]
    public void ACompilersName_IsFilledInWithTheProgramItsLanguagesBuildIdentified(PlaceholderPolicy policy)
    {
        var paths = Paths(LegCompilers.Of(Named(("C", LegCompiler.Of(Gcc)), ("CXX", LegCompiler.Of(Gxx)))));

        Assert.Equal(
            $"{Gcc} --cxx={Gxx} {Gcc} {{compiler_C}} ${{compiler_C}}",
            LegPathNames.Expand("{compiler_C} --cxx={compiler_CXX} {compiler_C} {{compiler_C}} ${compiler_C}", paths, "'census' run line", policy));
    }

    /// <summary>
    /// A name nothing fills in is refused, saying which case it is: a run that reaches no leg, a caller that reads no
    /// build, a leg no build identifies a compiler for, a language its build identified none for, and a compiler its
    /// build runs with words after it. Never passed through, under either policy: it would reach a program as its own
    /// text.
    /// </summary>
    [Theory]
    [InlineData("no leg", HarnessExit.UsageError, "'census' run line names '{compiler_CXX}', and this run reaches no leg, so no build identified a compiler to put there. Run it for a leg, or take the name out.")]
    [InlineData("no reader", HarnessExit.ConfigInvalid, "'census' run line names '{compiler_CXX}', and nothing here reads which compiler the leg's build identified: only a run line and a test invocation are filled in with one.")]
    [InlineData("none", HarnessExit.ConfigInvalid, "'census' run line names '{compiler_CXX}', and project 'app' is built by dotnet, which identifies no compiler.")]
    [InlineData("no language", HarnessExit.ConfigInvalid, "'census' run line names '{compiler_CXX}', and the leg's build identified no CXX compiler.")]
    [InlineData("words", HarnessExit.ConfigInvalid, "'census' run line names '{compiler_CXX}', and its build runs '/usr/bin/ccache' followed by 'g++'.")]
    public void ANameNothingFillsIn_IsRefused_SayingWhichCaseItIs(string @case, int exitCode, string said)
    {
        var paths = @case switch
        {
            "no leg" => new LegPaths("/tree", null) { Compilers = LegCompilers.Of(Named(("CXX", LegCompiler.Of(Gxx)))) },
            "no reader" => Paths(null),
            "none" => Paths(LegCompilers.None("project 'app' is built by dotnet, which identifies no compiler")),
            "no language" => Paths(LegCompilers.Of(Named(("C", LegCompiler.Of(Gcc))))),
            _ => Paths(LegCompilers.Of(Named(("CXX", LegCompiler.None("its build runs '/usr/bin/ccache' followed by 'g++'"))))),
        };

        foreach (var policy in new[] { PlaceholderPolicy.Refuse, PlaceholderPolicy.LeaveAsWritten })
        {
            var refusal = Assert.Throws<HarnessException>(() => LegPathNames.Expand("--cxx {compiler_CXX}", paths, "'census' run line", policy));

            Assert.Equal(exitCode, refusal.ExitCode);
            Assert.Equal(said, refusal.Message);
        }
    }

    /// <summary>
    /// Ahead of the leg's build, which is what identifies its compilers, a name stands as written - whichever the
    /// policy - so a check made then passes a line the build will fill in, and every other name is still filled in.
    /// </summary>
    [Theory]
    [InlineData(PlaceholderPolicy.Refuse)]
    [InlineData(PlaceholderPolicy.LeaveAsWritten)]
    public void AheadOfTheBuild_ACompilersName_StandsAsWritten(PlaceholderPolicy policy)
        => Assert.Equal(
            $"{{compiler_C}} -o {Path.Combine("/tree", "build", "v")} {{compiler_CXX}}",
            LegPathNames.Expand("{compiler_C} -o {buildDir} {compiler_CXX}", Paths(LegCompilers.AheadOfTheBuild), "test.args", policy));

    /// <summary>
    /// The names are this tool's: listed with the rest, so an input or a runner value of either name is refused where it
    /// is declared; made by the build, so a step naming one builds its leg first; and refused spelled in another case.
    /// </summary>
    [Fact]
    public void TheNames_AreThisToolsOwn_AndTheBuildMakesWhatTheyName()
    {
        Assert.Equal("compiler_C", LegPathNames.CompilerC);
        Assert.Equal("compiler_CXX", LegPathNames.CompilerCxx);
        Assert.Equal([("compiler_C", "C"), ("compiler_CXX", "CXX")], LegPathNames.Compilers);
        Assert.Equal(["product", "compiler_C", "compiler_CXX", "actionBuild"], LegPathNames.All.SkipWhile(name => name != "product").Take(4));
        Assert.Equal(["buildDir", "product", "compiler_C", "compiler_CXX"], LegPathNames.Built);

        Assert.Equal(["compiler_CXX", "product", "compiler_C"], LegPathNames.BuiltNamesIn(["{compiler_CXX} --version", "{treeDir}", "{product} {compiler_C} {compiler_CXX}"]));
        Assert.Equal("{compiler_C} and {compiler_CXX}", LegPathNames.Spelled(["compiler_C", "compiler_CXX"]));

        LegPathNames.RefuseUnknown("{compiler_C} {compiler_CXX}", "'census' run line");

        foreach (var policy in new[] { PlaceholderPolicy.Refuse, PlaceholderPolicy.LeaveAsWritten })
        {
            var miscased = Assert.Throws<HarnessException>(() => LegPathNames.RefuseUnknown("{compiler_cxx}", "'census' run line", policy));

            Assert.Contains("which is '{compiler_CXX}' spelled differently", miscased.Message, StringComparison.Ordinal);
        }

        Assert.Equal("C", LegPathNames.CompilerLanguage("compiler_C"));
        Assert.Equal("CXX", LegPathNames.CompilerLanguage("compiler_CXX"));
        Assert.Null(LegPathNames.CompilerLanguage("compiler_c"));
        Assert.Null(LegPathNames.CompilerLanguage("product"));
    }

    /// <summary>
    /// A run line starts the leg's own compiler only where its program is written as one compiler's name in its braces
    /// and nothing beside it: anything more is another program, a doubled brace is the text itself, and a name in
    /// another case is no name.
    /// </summary>
    [Theory]
    [InlineData("{compiler_C}", true)]
    [InlineData("{compiler_CXX}", true)]
    [InlineData("{compiler_C}.exe", false)]
    [InlineData("{compiler_C}/../other", false)]
    [InlineData(" {compiler_C}", false)]
    [InlineData("{compiler_C}{compiler_CXX}", false)]
    [InlineData("{{compiler_C}}", false)]
    [InlineData("${compiler_C}", false)]
    [InlineData("{compiler_c}", false)]
    [InlineData("{product}", false)]
    [InlineData("compiler_C", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ALineStartsTheLegsCompiler_OnlyWrittenAsItsNameAlone(string? written, bool alone)
        => Assert.Equal(alone, LegPathNames.IsACompilerAlone(written));

    /// <summary>
    /// What the build identified is read where a line first names a compiler, and once: a line naming none reads
    /// nothing, and the check made before a leg's first step and the step itself are given one answer.
    /// </summary>
    [Fact]
    public void WhatTheBuildIdentified_IsReadWhereALineFirstNamesOne_AndOnce()
    {
        var reads = 0;

        var paths = Paths(LegCompilers.ReadBy(() =>
        {
            reads++;

            return Named(("C", LegCompiler.Of(Gcc)));
        }));

        Assert.Equal(Path.Combine("/tree", "build", "v"), LegPathNames.Expand("{buildDir}", paths, "'census' run line"));
        Assert.Equal(0, reads);

        Assert.Equal(Gcc, LegPathNames.Expand("{compiler_C}", paths, "'census' run line"));
        Assert.Equal($"{Gcc} {Gcc}", LegPathNames.Expand("{compiler_C} {compiler_C}", paths with { StepBuild = "/tree/step" }, "'census' run line"));
        Assert.Throws<HarnessException>(() => LegPathNames.Expand("{compiler_CXX}", paths, "'census' run line"));
        Assert.Equal(1, reads);
    }

    /// <summary>A compiler is a program or a reason, never neither; and a set of them ahead of the build names none yet.</summary>
    [Fact]
    public void ACompiler_IsAProgramOrAReason()
    {
        Assert.Equal((Gcc, null), (LegCompiler.Of(Gcc).Program, LegCompiler.Of(Gcc).Problem));
        Assert.Equal((null, "why"), (LegCompiler.None("why").Program, LegCompiler.None("why").Problem));
        Assert.ThrowsAny<ArgumentException>(() => LegCompiler.Of(" "));
        Assert.ThrowsAny<ArgumentException>(() => LegCompiler.None(""));
        Assert.ThrowsAny<ArgumentException>(() => LegCompilers.None(" "));

        Assert.Null(LegCompilers.AheadOfTheBuild.For("C"));
        Assert.Equal("why", LegCompilers.None("why").For("CXX")!.Problem);
        Assert.Equal(Gcc, LegCompilers.Of(Named(("C", LegCompiler.Of(Gcc)))).For("C")!.Program);
        Assert.Equal("the leg's build identified no CXX compiler", LegCompilers.Of(Named(("C", LegCompiler.Of(Gcc)))).For("CXX")!.Problem);
    }

    private static LegPaths Paths(LegCompilers? compilers)
        => new("/tree", Path.Combine("/tree", "build", "v")) { Compilers = compilers };

    private static Dictionary<string, LegCompiler> Named(params (string Language, LegCompiler Compiler)[] compilers)
        => compilers.ToDictionary(compiler => compiler.Language, compiler => compiler.Compiler, StringComparer.Ordinal);
}
