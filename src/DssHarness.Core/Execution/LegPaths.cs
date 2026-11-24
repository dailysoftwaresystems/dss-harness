using System.Text.RegularExpressions;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Text;

namespace DssHarness.Core.Execution;

/// <summary>
/// Which leg a piece of work is, in the words a configured command can spell.
/// </summary>
/// <remarks>
/// Derived, never supplied. An instrument that records which host it measured has to get that name
/// from somewhere, and a value typed into a run line by hand can be typed wrongly: a benchmark
/// labelled with the wrong processor is worse than one labelled with nothing, because the number
/// looks comparable and is not. The harness already knows every one of these at the moment a step
/// starts; until now it had no way to say them.
/// </remarks>
/// <param name="Leg">The leg's name, as <c>legs</c> declares it.</param>
/// <param name="Os">The operating system the work ran on.</param>
/// <param name="Processor">The processor the leg targets, which under emulation is not the host's.</param>
/// <param name="Toolchain">The toolchain, or <c>none</c> for a leg that builds nothing.</param>
/// <param name="Config">The build configuration.</param>
/// <param name="Variant">The variant directory's name: processor, toolchain, config and any sanitizer.</param>
/// <param name="Host">The host the work ran on, as this tool names it.</param>
/// <param name="RunId">The run this work belongs to.</param>
public sealed record LegIdentity(
    string Leg,
    string Os,
    string Processor,
    string Toolchain,
    string Config,
    string Variant,
    string Host,
    string RunId)
{
    /// <summary>
    /// The distribution the leg's own WSL item names, spelt as WSL lists it - what <c>wsl.exe -d</c> takes - or
    /// <see langword="null"/> for a leg of any other host, or one whose item could not be read where it was placed.
    /// </summary>
    public string? Distribution { get; init; }

    /// <summary>
    /// Each WSL item the configuration declares, by its name, with the distribution it names as read where the leg was
    /// placed - on a host, as the machine that sent the leg there told it - or <see langword="null"/> where its .env could
    /// not be read there. Never an item's credential.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Distributions { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the leg runs in a WSL distribution, whose own item's distribution <see cref="Distribution"/> is.</summary>
    public bool InWsl { get; init; }
}

/// <summary>
/// The directories one leg runs against, who that leg is, and what its build produces.
/// </summary>
/// <remarks>
/// The build directory is derived from the processor, the toolchain and the configuration, so no
/// tracked file can name it.
/// <para>
/// A configured command sometimes has to name a directory the configuration cannot spell. The build
/// directory is the case that forces it: <c>build/&lt;processor&gt;-&lt;toolchain&gt;-&lt;config&gt;</c>
/// differs per leg and is chosen by this tool, so a project that builds out of source cannot write
/// down where its own tests live. Measured: <c>ctest</c> started at the tree root of such a project
/// reports "No tests were found!!!" and exits in under a fifth of a second, having found 2204 tests
/// nowhere, while the same arguments with the build directory find all of them.
/// </para>
/// </remarks>
public sealed record LegPaths
{
    /// <summary>The directories one leg runs against.</summary>
    /// <param name="treeRoot">The leg's tree: its worktree, or the repository.</param>
    /// <param name="buildDirectory">
    /// The leg's variant-keyed build directory, or <see langword="null"/> where this run reaches no
    /// leg and therefore has none. Null is refused when something names it, never substituted.
    /// </param>
    public LegPaths(string treeRoot, string? buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        TreeRoot = treeRoot;
        BuildDirectory = string.IsNullOrWhiteSpace(buildDirectory) ? null : buildDirectory;
    }

    /// <summary>The leg's tree: its worktree, or the repository.</summary>
    public string TreeRoot { get; }

    /// <summary>
    /// The leg's variant-keyed build directory, which only the harness knows, or
    /// <see langword="null"/> where this run reaches no leg.
    /// </summary>
    public string? BuildDirectory { get; }

    /// <summary>The tree's <c>.harness-config</c> directory.</summary>
    public string HarnessDirectory => Path.Combine(TreeRoot, HarnessLayout.DirectoryName);

    /// <summary>
    /// Which leg this is, or <see langword="null"/> for a caller that has no leg in hand.
    /// </summary>
    public LegIdentity? Identity { get; init; }

    /// <summary>
    /// The one file this leg's build is declared to produce, or <see langword="null"/> when the
    /// project declares none for this platform, or declares several.
    /// </summary>
    /// <remarks>
    /// One, or nothing. <c>buildOutputs</c> is a list and every entry in it must exist for the build
    /// to be witnessed, so "the product" is only a well-formed question when the list holds exactly
    /// one answer for this platform. Where it holds several, naming it is refused rather than
    /// guessed: an instrument pointed at the wrong one of three binaries reports a measurement of
    /// something nobody asked about, and reports it as a success.
    /// </remarks>
    public string? Product { get; init; }

    /// <summary>Why <see cref="Product"/> is null, for a refusal that can say which case it is.</summary>
    public string? ProductProblem { get; init; }

    /// <summary>
    /// What the names of this leg's compilers are filled in with, or <see langword="null"/> for a caller that reads no
    /// build: a name of one is then refused, saying so, never passed through.
    /// </summary>
    public LegCompilers? Compilers { get; init; }

    /// <summary>
    /// The directory this run of a test invocation has its runner write its structured results into, or
    /// <see langword="null"/> outside one: made empty by the harness before the runner starts, inside the leg's own
    /// records of the run, so a results file read there is this run's and no earlier one's.
    /// </summary>
    public string? ResultsDirectory { get; init; }

    /// <summary>
    /// Where this run of the action writes while it runs, or <see langword="null"/> outside an
    /// action.
    /// </summary>
    public string? ActionBuild { get; init; }

    /// <summary>
    /// Where this run of the action's persisted outputs are kept, or <see langword="null"/> outside
    /// an action.
    /// </summary>
    public string? ActionArtifacts { get; init; }

    /// <summary>
    /// This step's own directory under <see cref="ActionBuild"/>, or <see langword="null"/> outside
    /// a step.
    /// </summary>
    public string? StepBuild { get; init; }

    /// <summary>
    /// This run's artifacts across every leg, holding one directory per leg, or
    /// <see langword="null"/> outside an action.
    /// </summary>
    /// <remarks>
    /// How a step reads what another leg produced. <see cref="ActionArtifacts"/> is this leg's own,
    /// which is the wrong half of the question for a round trip: the point of one is that what was
    /// built on one machine runs on another, so the consumer has to be able to name the producer.
    /// </remarks>
    public string? RunArtifacts { get; init; }
}

/// <summary>
/// What a compiler's name is filled in with for one language: the program the leg's build identified, or why no program
/// fills the name in. Exactly one of the two.
/// </summary>
public sealed record LegCompiler
{
    private LegCompiler(string? program, string? problem)
    {
        Program = program;
        Problem = problem;
    }

    /// <summary>The program, whole, as its machine spells a path; <see langword="null"/> where none fills the name in.</summary>
    public string? Program { get; }

    /// <summary>Why no program fills the name in, as a refusal ends; <see langword="null"/> where one does.</summary>
    public string? Problem { get; }

    /// <summary>The compiler <paramref name="program"/> is.</summary>
    /// <param name="program">The program the leg's build identified, whole.</param>
    public static LegCompiler Of(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        return new(program, null);
    }

    /// <summary>No compiler, for <paramref name="problem"/>.</summary>
    /// <param name="problem">Why, as a refusal ends: it follows "and", and takes the full stop.</param>
    public static LegCompiler None(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        return new(null, problem);
    }
}

/// <summary>
/// What the names of a leg's compilers - <c>{compiler_C}</c>, <c>{compiler_CXX}</c> - are filled in with.
/// </summary>
/// <remarks>
/// A leg's compiler is what its build identified, so it is known only once the leg is built: CMake identifies a compiler
/// as it first configures a directory. Read where a line first names one, and once: the check made before a leg's first
/// step and the step itself are given one answer, and a run naming none reads nothing.
/// </remarks>
public sealed class LegCompilers
{
    private readonly Lazy<IReadOnlyDictionary<string, LegCompiler>>? _read;
    private readonly string? _none;

    private LegCompilers(Lazy<IReadOnlyDictionary<string, LegCompiler>>? read, string? none)
    {
        _read = read;
        _none = none;
    }

    /// <summary>
    /// Ahead of the leg's build, which is what identifies them: a name stands as written, for a check made then of a
    /// line that is filled in - or refused - once the leg is built.
    /// </summary>
    public static LegCompilers AheadOfTheBuild { get; } = new(null, null);

    /// <summary>None, whichever the language, for <paramref name="why"/>.</summary>
    /// <param name="why">Why no build identifies one, as a refusal ends.</param>
    public static LegCompilers None(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new(null, why);
    }

    /// <summary>What <paramref name="read"/> reads, where a line first names a compiler, and never again.</summary>
    /// <param name="read">Reads the compiler of each language the leg's build identified, by the language as CMake names it.</param>
    public static LegCompilers ReadBy(Func<IReadOnlyDictionary<string, LegCompiler>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return new(new Lazy<IReadOnlyDictionary<string, LegCompiler>>(read), null);
    }

    /// <summary><paramref name="named"/>, already read.</summary>
    /// <param name="named">The compiler of each language, by the language as CMake names it.</param>
    public static LegCompilers Of(IReadOnlyDictionary<string, LegCompiler> named)
    {
        ArgumentNullException.ThrowIfNull(named);

        return ReadBy(() => named);
    }

    /// <summary>
    /// What fills in the compiler of <paramref name="language"/>, or <see langword="null"/> ahead of the leg's build,
    /// where the name stands as written. A language the build identified no compiler for has none, saying so.
    /// </summary>
    /// <param name="language">The language, as CMake names it: <c>C</c> or <c>CXX</c>.</param>
    public LegCompiler? For(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        if (_none is { } why)
        {
            return LegCompiler.None(why);
        }

        return _read is null
            ? null
            : _read.Value.GetValueOrDefault(language) ?? LegCompiler.None($"the leg's build identified no {language} compiler");
    }
}

/// <summary>
/// What a caller means by a brace group this vocabulary does not own.
/// </summary>
/// <remarks>
/// The distinction is whose text the string is. A setting in <c>config.json</c> and a step's run
/// line are both written for this tool, so a name it cannot fill in is a typo and refusing it names
/// the line to fix. What a brace group is never allowed to be is passed through silently: a run line
/// holding <c>{greeting}</c> reached the program as those nine characters and the leg reported
/// passed, which is a wrapper reporting success without doing what it was asked.
/// <para>
/// <c>${NAME}</c> is another expander's syntax and is always left alone, and a literal brace is
/// written doubled: <c>{{</c> and <c>}}</c>. That is what keeps <c>awk '{{print}}'</c> expressible
/// while <c>{print}</c> is still refused rather than guessed at.
/// </para>
/// </remarks>
public enum PlaceholderPolicy
{
    /// <summary>A name this vocabulary cannot fill in is a mistake.</summary>
    Refuse,

    /// <summary>A brace group this vocabulary does not own is left exactly as written.</summary>
    LeaveAsWritten,
}

/// <summary>
/// The names a configured command may spell, and how a name is replaced with its value.
/// </summary>
/// <remarks>
/// One vocabulary, expanded wherever a configured string may hold one, rather than a key per
/// question. <c>coresArgs</c> established the shape with <c>{cores}</c>; this is the same idea for
/// the directories a leg runs against, who that leg is, and what its build produces.
/// <para>
/// A name nothing replaces is refused, never passed through. Passed through it reaches the runner as
/// the literal text <c>{buildDir}</c>, and what a runner does with a directory that cannot exist is
/// its own business: ctest reports no tests and exits 8, which reads as a suite that ran and found
/// nothing.
/// </para>
/// </remarks>
public static class LegPathNames
{
    /// <summary>Names the leg's variant-keyed build directory.</summary>
    public const string BuildDirectory = "buildDir";

    /// <summary>Names the leg's tree root.</summary>
    public const string TreeRoot = "treeDir";

    /// <summary>Names the tree's <c>.harness-config</c> directory.</summary>
    public const string HarnessDirectory = "harnessDir";

    /// <summary>
    /// Names the directory a test invocation's run has its runner write its structured results into - made empty for
    /// each run, so what is read there is that run's - in the invocation's <c>args</c> and <c>results</c> alone.
    /// </summary>
    public const string ResultsDirectory = "resultsDir";

    /// <summary>Names the leg, as <c>legs</c> declares it.</summary>
    public const string Leg = "leg";

    /// <summary>Names the operating system the work ran on.</summary>
    public const string Os = "os";

    /// <summary>Names the processor the leg targets.</summary>
    public const string Processor = "processor";

    /// <summary>Names the toolchain.</summary>
    public const string Toolchain = "toolchain";

    /// <summary>Names the build configuration.</summary>
    public const string Config = "config";

    /// <summary>Names the variant directory: processor, toolchain, config and any sanitizer.</summary>
    public const string Variant = "variant";

    /// <summary>Names the host the work ran on.</summary>
    public const string Host = "host";

    /// <summary>Names the run this work belongs to.</summary>
    public const string RunId = "runId";

    /// <summary>
    /// Names the distribution a WSL leg's own item names, spelt as WSL lists it - what <c>wsl.exe -d</c> takes. Followed by
    /// a dot and a WSL item's name, <c>{distribution.&lt;item&gt;}</c>, it names that item's distribution, on any leg.
    /// </summary>
    /// <remarks>
    /// The distribution lives in the item's .env, beside its credential, which a program the repository runs must never
    /// read for it: config.json names the item alone, and an item named otherwise than its distribution sent a driver that
    /// passed the item's name to <c>wsl.exe -d</c> to no distribution at all.
    /// </remarks>
    public const string Distribution = "distribution";

    /// <summary>What a name of a WSL item's distribution starts with, before the item's name: <c>distribution.</c>.</summary>
    public const string ItemDistributionPrefix = Distribution + ".";

    /// <summary>Names the one file this leg's build is declared to produce.</summary>
    public const string Product = "product";

    /// <summary>Names where this run of the action writes while it runs.</summary>
    public const string ActionBuild = "actionBuild";

    /// <summary>Names where this run of the action's persisted outputs are kept.</summary>
    public const string ActionArtifacts = "actionArtifacts";

    /// <summary>Names this step's own directory under the action's build directory.</summary>
    public const string StepBuild = "stepBuild";

    /// <summary>Names this run's artifacts across every leg, one directory per leg.</summary>
    public const string RunArtifacts = "runArtifacts";

    /// <summary>Names the C compiler the leg's build identified.</summary>
    public const string CompilerC = "compiler_C";

    /// <summary>Names the C++ compiler the leg's build identified.</summary>
    public const string CompilerCxx = "compiler_CXX";

    /// <summary>
    /// The names of a leg's compilers, each with the language whose compiler it names, as CMake names the language:
    /// the two CMake keeps a record of identifying.
    /// </summary>
    public static IReadOnlyList<(string Name, string Language)> Compilers { get; } = [(CompilerC, "C"), (CompilerCxx, "CXX")];

    /// <summary>The language whose compiler <paramref name="name"/> names, or <see langword="null"/> where it names none.</summary>
    /// <param name="name">A name, without its braces.</param>
    public static string? CompilerLanguage(string name)
        => Compilers.FirstOrDefault(compiler => string.Equals(compiler.Name, name, StringComparison.Ordinal)).Language;

    /// <summary>
    /// Whether <paramref name="written"/> is one compiler's name in its braces and nothing beside it: a run line that
    /// starts so starts the leg's own compiler, whatever its path.
    /// </summary>
    /// <param name="written">A run line's program, as written.</param>
    public static bool IsACompilerAlone(string? written)
        => Compilers.Any(compiler => string.Equals(written, $"{{{compiler.Name}}}", StringComparison.Ordinal));

    /// <summary>Every name, in the order a refusal lists them.</summary>
    /// <remarks>A WSL item's distribution, <c>{distribution.&lt;item&gt;}</c>, is one of a family, by the item's name: see <see cref="IsOwned"/>.</remarks>
    public static IReadOnlyList<string> All { get; } =
    [
        BuildDirectory, TreeRoot, HarnessDirectory, ResultsDirectory,
        Leg, Os, Processor, Toolchain, Config, Variant, Host, RunId, Distribution,
        Product, CompilerC, CompilerCxx, ActionBuild, ActionArtifacts, StepBuild, RunArtifacts,
    ];

    /// <summary>
    /// Whether <paramref name="name"/> is one this tool fills in: one of <see cref="All"/>, or a WSL item's distribution,
    /// <c>distribution.&lt;item&gt;</c>, whatever item it names - an input or a value of either name would get this tool's
    /// value in a run line, and the action's in its environment.
    /// </summary>
    /// <param name="name">A name, without its braces.</param>
    public static bool IsOwned(string name) => All.Contains(name, StringComparer.Ordinal) || ItemOf(name) is not null;

    /// <summary>The WSL item <paramref name="name"/> names the distribution of, or <see langword="null"/> where it is no such name.</summary>
    /// <param name="name">A name, without its braces.</param>
    public static string? ItemOf(string name)
        => name.StartsWith(ItemDistributionPrefix, StringComparison.Ordinal) && name.Length > ItemDistributionPrefix.Length
            ? name[ItemDistributionPrefix.Length..]
            : null;

    /// <summary>
    /// The names whose value is a directory that is never under a leg's build directory: the tree, the harness's own
    /// directory, a test run's results directory, and the run-of-an-action ones. A setting whose paths are relative to the
    /// build directory - a test invocation's <c>keepOnFailure</c> - refuses an entry naming any of them, naming the key,
    /// when the configuration is read.
    /// </summary>
    public static IReadOnlyList<string> Directories { get; } =
    [
        TreeRoot, HarnessDirectory, ResultsDirectory, ActionBuild, ActionArtifacts, StepBuild, RunArtifacts,
    ];

    /// <summary>
    /// The names whose value the leg's build makes: its build directory, the one file it is declared to make, and the
    /// compilers it identifies. A step or a runner's phase naming any reads what the build left there, so a run that
    /// runs it builds the leg first: see <see cref="BuiltNamesIn"/>.
    /// </summary>
    public static IReadOnlyList<string> Built { get; } = [BuildDirectory, Product, CompilerC, CompilerCxx];

    /// <summary>
    /// The names of <see cref="Built"/> that <paramref name="values"/> ask to have filled in, each once, in the order they
    /// first name them; empty where they name neither. A doubled brace, and a group another expander owns, name nothing.
    /// </summary>
    /// <param name="values">The configured strings: a step's or a runner phase's run lines and working directory.</param>
    /// <remarks>Read from what a line says rather than declared beside it: see <see cref="Runners.ActionStep.NeedsBuild"/>.</remarks>
    public static IReadOnlyList<string> BuiltNamesIn(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return [.. values.SelectMany(NamesIn).Where(name => Built.Contains(name, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <paramref name="names"/> as a refusal writes them, each in its braces: <c>{product} and {buildDir}</c>.
    /// </summary>
    /// <param name="names">Names of this vocabulary, as <see cref="BuiltNamesIn"/> gives them.</param>
    public static string Spelled(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return string.Join(" and ", names.Select(name => $"{{{name}}}"));
    }

    /// <summary>
    /// A brace group as it is written. Three shapes, in the order they are recognised: a doubled
    /// brace, which is how a literal one is written; another expander's <c>${NAME}</c>, which this
    /// one never touches; and a name in braces, which is this vocabulary's.
    /// </summary>
    /// <remarks>
    /// Matched rather than searched for by name, so that a name nobody declared is found and refused
    /// instead of surviving into a command line.
    /// <para>
    /// The name is <see cref="NamePattern"/>; the first two shapes are <see cref="LiteralPattern"/>.
    /// </para>
    /// </remarks>
    private static readonly Regex Placeholder = LinearRegex.Compile(LiteralPattern + @"|\{(?<name>" + NamePattern + @")\}");

    /// <summary>
    /// The brace groups every template the tool fills writes as text: a doubled brace, <c>{{</c> or
    /// <c>}}</c>, which stands for one, and another expander's <c>${...}</c>, up to its first
    /// <c>}</c>, left as written. The one spelling of them, which this vocabulary's placeholders and
    /// a workflow's templates (<see cref="Orchestration.Flow.FlowTemplate"/>) are both read by.
    /// </summary>
    private const string LiteralPattern = @"(?<doubled>\{\{|\}\})|(?<other>\$\{[^}]*\})";

    /// <summary>A brace group <see cref="LiteralPattern"/> names, wherever it starts.</summary>
    private static readonly Regex Literal = LinearRegex.Compile(LiteralPattern);

    /// <summary>
    /// The brace groups <see cref="LiteralPattern"/> names in one template, told place by place to a reader walking it:
    /// the leftmost group at or past where the reader asks is sought (<see cref="LinearRegex.MatchFrom"/>) and kept while
    /// it lies ahead - a group starts at a place exactly when the leftmost one sought from that place starts at it - so a
    /// reader whose places never go back has the template searched in time linear in its length, however many of
    /// another expander's groups it leaves unclosed.
    /// </summary>
    /// <param name="template">The template, as it is written.</param>
    public sealed class Literals(string template)
    {
        /// <summary>The leftmost group found at or past <see cref="_from"/>; null before the first is sought.</summary>
        private Match? _ahead;

        /// <summary>Where <see cref="_ahead"/> was sought from.</summary>
        private int _from;

        /// <summary>The template, as it is written.</summary>
        public string Template { get; } = template ?? throw new ArgumentNullException(nameof(template));

        /// <summary>
        /// How much of the template the searches spanned: each from where it was sought to the end of the group it found,
        /// or to the template's end where it found none - what a search reads to answer.
        /// </summary>
        public long Spanned { get; private set; }

        /// <summary>
        /// The brace group that starts at <paramref name="at"/> - the text it writes and how much of the template it takes -
        /// or null where none starts there.
        /// </summary>
        /// <param name="at">Where to look.</param>
        public (string Text, int Length)? At(int at)
        {
            if (_ahead is null || _from > at || (_ahead.Success && _ahead.Index < at))
            {
                _ahead = LinearRegex.MatchFrom(Literal, Template, at);
                _from = at;
                Spanned += (_ahead.Success ? _ahead.Index + _ahead.Length : Template.Length) - at;
            }

            return _ahead.Success && _ahead.Index == at
                ? (_ahead.Groups["doubled"].Success ? _ahead.Value[..1] : _ahead.Value, _ahead.Length)
                : null;
        }
    }

    /// <summary>
    /// What a name in braces is made of: a letter or <c>_</c>, then letters, digits, <c>_</c>, <c>-</c>
    /// and <c>.</c>.
    /// </summary>
    /// <remarks>
    /// As wide as the things that get named here, because a name the pattern cannot see is a name
    /// neither half of this can refuse: it reaches the program as its own text and the step exits
    /// zero having done nothing, which is the failure this whole rule exists to end. A runner value
    /// directory holds <c>CORPUS_PATH</c> and <c>_JAVA_OPTIONS</c>; an input the pattern cannot see
    /// is refused where it is declared, by <see cref="IsName"/>.
    /// </remarks>
    private const string NamePattern = "[A-Za-z_][A-Za-z0-9_.-]*";

    /// <summary>
    /// What a name in braces is made of, as a refusal of one that is not says it.
    /// </summary>
    public const string NameRule = "starts with a letter or '_' and holds only letters, digits, '_', '-' and '.'";

    /// <summary>A name in braces, whole (<see cref="NamePattern"/>).</summary>
    private static readonly Regex WholeName = LinearRegex.Compile(@"\A" + NamePattern + @"\z");

    /// <summary>Whether a configured string can name <paramref name="name"/> in braces, as <c>{name}</c>.</summary>
    /// <param name="name">The name something declares.</param>
    public static bool IsName(string name) => WholeName.IsMatch(name);

    /// <summary>
    /// <paramref name="value"/> with every placeholder this vocabulary owns replaced by what it
    /// names, every doubled brace reduced to one, and everything else left as written.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="paths">The leg's directories, identity and product.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <param name="policy">What to do with a brace group this vocabulary does not own.</param>
    /// <param name="extra">
    /// Names this caller supplies beyond the built-in ones, such as an action's declared inputs.
    /// Matched exactly: a lookup that ignored case would let an action declaring <c>home</c> replace
    /// a step's <c>${HOME}</c> with it.
    /// </param>
    /// <exception cref="HarnessException">A placeholder names nothing this caller can fill in.</exception>
    public static string Expand(
        string value,
        LegPaths paths,
        string setting,
        PlaceholderPolicy policy = PlaceholderPolicy.Refuse,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (value is null || (!value.Contains('{', StringComparison.Ordinal)
            && !value.Contains('}', StringComparison.Ordinal)))
        {
            return value ?? string.Empty;
        }

        return Placeholder.Replace(value, match =>
        {
            if (match.Groups["doubled"].Success)
            {
                // '{{' is how a run line writes a brace it means literally, so awk '{{print}}'
                // reaches awk as '{print}' and this vocabulary never guesses at it.
                return match.Value[..1];
            }

            if (match.Groups["other"].Success)
            {
                return match.Value;
            }

            var name = match.Groups["name"].Value;

            if (CompilerLanguage(name) is { } language)
            {
                // Null ahead of the leg's build, which is what identifies it: the name stands as written for the
                // check made then, and is filled in - or refused - by the one made once the leg is built.
                return Compiler(setting, name, language, paths) ?? match.Value;
            }

            if (Value(name, paths) is { } resolved)
            {
                return resolved;
            }

            if (extra is not null && extra.TryGetValue(name, out var supplied))
            {
                return supplied;
            }

            RefuseKnownButAbsent(setting, name, paths);
            Refuse(setting, name, policy, extra?.Keys);

            // Left exactly as written, braces and all, for whoever does own it.
            return match.Value;
        });
    }

    /// <summary>
    /// <paramref name="value"/> filled in from <paramref name="names"/> alone, by the grammar every
    /// configured string is filled in by: a doubled brace reduced to one, a <c>${...}</c> left as
    /// written, and any other name refused. For a setting that is no leg's, and so owns none of a
    /// leg's names, only the ones it is given.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="names">Every name the setting may hold, and what each is filled in with.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <exception cref="HarnessException">A placeholder names something <paramref name="names"/> does not hold.</exception>
    public static string Fill(string value, IReadOnlyDictionary<string, string> names, string setting)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(names);

        return Placeholder.Replace(value, match =>
        {
            if (match.Groups["doubled"].Success)
            {
                return match.Value[..1];
            }

            if (match.Groups["other"].Success)
            {
                return match.Value;
            }

            return names.TryGetValue(match.Groups["name"].Value, out var filled)
                ? filled
                : throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"{setting} names '{match.Value}', which nothing fills in: "
                    + (names.Count == 0
                        ? "it can hold no name in braces, and a brace it means is written twice."
                        : $"it can hold only {string.Join(", ", names.Keys.Select(name => $"{{{name}}}"))}."));
        });
    }

    /// <summary>
    /// Refuses a configured string holding a placeholder nothing replaces, without needing the
    /// values it would be expanded against.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <param name="policy">What to do with a brace group this vocabulary does not own.</param>
    /// <param name="extra">Names this caller will supply beyond the built-in ones.</param>
    /// <param name="unvalued">
    /// Names this caller declares and has no value for this time. A refusal lists them apart from the
    /// names it can fill in: left out, a name meant as one of them read as one nobody declared.
    /// </param>
    /// <param name="wslItems">
    /// The WSL items the configuration declares, where the caller knows them: a WSL item's distribution named for any
    /// other is refused, naming them. <see langword="null"/> leaves that to the expansion, which knows them.
    /// </param>
    /// <exception cref="HarnessException">A placeholder names nothing this caller can fill in.</exception>
    /// <remarks>
    /// Called when the configuration is read, where no leg has been placed and no build directory
    /// exists yet, and over an action's steps before a leg's first step runs, with the names that run
    /// supplies as <paramref name="extra"/>. A typo found when the configuration is read names the
    /// line to fix; found when the leg runs it has already cost the build that preceded it.
    /// </remarks>
    public static void RefuseUnknown(
        string? value,
        string setting,
        PlaceholderPolicy policy = PlaceholderPolicy.Refuse,
        IReadOnlyCollection<string>? extra = null,
        IReadOnlyCollection<string>? unvalued = null,
        IReadOnlyCollection<string>? wslItems = null)
    {
        foreach (var name in NamesIn(value))
        {
            if (wslItems is not null && ItemOf(name) is { } item && !wslItems.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                throw Undeclared(setting, name, item, wslItems);
            }

            if (!Known(name) && (extra is null || !extra.Contains(name)))
            {
                Refuse(setting, name, policy, extra, unvalued);
            }
        }
    }

    /// <summary>
    /// Refuses each distribution <paramref name="value"/> names that <paramref name="identity"/>'s leg has nothing for -
    /// <c>{distribution}</c> on a leg that runs in no WSL distribution, a WSL item's that is not declared or could not be
    /// read - by the rule its expansion refuses it by: asked of every line of an action before its first step runs.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <param name="identity">Which leg the line runs for.</param>
    /// <exception cref="HarnessException">A distribution is named that the leg has nothing for.</exception>
    public static void RefuseAbsentDistributions(string? value, string setting, LegIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        foreach (var name in NamesIn(value).Where(name => name == Distribution || ItemOf(name) is not null))
        {
            _ = DistributionFor(setting, name, identity);
        }
    }

    /// <summary>
    /// The distribution <paramref name="name"/> - <see cref="Distribution"/>, or a WSL item's - is filled with for
    /// <paramref name="identity"/>'s leg.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The leg has nothing for it: it runs in no WSL distribution, or the item is not declared, or its .env could not be
    /// read where the leg was placed. Each says which.
    /// </exception>
    private static string DistributionFor(string setting, string name, LegIdentity identity)
    {
        if (ItemOf(name) is { } item)
        {
            if (!identity.Distributions.TryGetValue(item, out var distribution))
            {
                throw Undeclared(setting, name, item, [.. identity.Distributions.Keys]);
            }

            return distribution ?? throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting} names '{{{name}}}', and WSL item '{item}''s .env could not be read where leg '{identity.Leg}' was placed, "
                + $"so its distribution is not known there; '{Hosts.ToolPackage.Command} legs' there says why.");
        }

        if (!identity.InWsl)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting} names '{{{Distribution}}}', and leg '{identity.Leg}' runs on {identity.Host}, which is no WSL distribution, so "
                + $"it has no distribution to put there. Name a WSL item's as '{{{ItemDistributionPrefix}<item>}}', or take the name out.");
        }

        return identity.Distribution ?? throw new HarnessException(
            HarnessExit.ConfigInvalid,
            $"{setting} names '{{{Distribution}}}', and the .env of leg '{identity.Leg}''s WSL item could not be read where it was placed, "
            + $"so its distribution is not known there; '{Hosts.ToolPackage.Command} legs' there says why.");
    }

    /// <summary>A refusal of a WSL item's distribution named for an item the configuration does not declare, naming those it does.</summary>
    private static HarnessException Undeclared(string setting, string name, string item, IReadOnlyCollection<string> declared)
        => new(
            HarnessExit.ConfigInvalid,
            $"{setting} names '{{{name}}}', and no WSL item '{item}' is declared under wslDistros: "
            + (declared.Count == 0
                ? "none is."
                : $"the declared ones are {string.Join(", ", declared.Order(StringComparer.OrdinalIgnoreCase).Select(each => $"'{each}'"))}."));

    /// <summary>
    /// Refuses a prompt's placeholder that names nothing its caller fills in: the closed check of a
    /// text whose vocabulary the caller owns alone.
    /// </summary>
    /// <param name="value">The text, a prompt or a fragment of one.</param>
    /// <param name="setting">What to call the text in a refusal.</param>
    /// <param name="filled">Exactly the names the caller fills in, and no others.</param>
    /// <exception cref="HarnessException">A placeholder names nothing this caller fills in.</exception>
    /// <remarks>
    /// Not <see cref="RefuseUnknown"/>, which reads every name of a leg as one something fills: in a
    /// prompt <c>{os}</c> is this machine's, from the engine's own values, and <c>{buildDir}</c> is a
    /// name nothing fills there. This takes exactly the names its caller gives, so the same owner
    /// serves both vocabularies without a prompt's being read against a leg's.
    /// </remarks>
    public static void RefuseUnfilled(
        string? value,
        string setting,
        IReadOnlyCollection<string> filled)
    {
        ArgumentNullException.ThrowIfNull(filled);

        foreach (var name in NamesIn(value).Where(name => !filled.Contains(name)))
        {
            var names = string.Join(", ", filled.Select(spelled => $"{{{spelled}}}"));

            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting} names '{{{name}}}', which nothing here can fill in. "
                + (filled.Count == 0
                    ? "No name can be filled in here."
                    : $"The names are {names}."));
        }
    }

    /// <summary>
    /// Every name <paramref name="value"/> asks to have filled in, in the order it names them: a
    /// doubled brace, and a group another expander owns, name none.
    /// </summary>
    /// <param name="value">The configured string.</param>
    public static IEnumerable<string> NamesIn(string? value)
        => string.IsNullOrEmpty(value)
            ? []
            : Placeholder.Matches(value)
                .Where(match => match.Groups["name"].Success)
                .Select(match => match.Groups["name"].Value);

    /// <summary>
    /// Throws where the name is one of this vocabulary's but this leg has nothing to put there, so
    /// the message says which case it is instead of claiming nothing can fill it in.
    /// </summary>
    private static void RefuseKnownButAbsent(string setting, string name, LegPaths paths)
    {
        switch (name)
        {
            case BuildDirectory when paths.BuildDirectory is null:
                // Refused, not filled in with the tree root. Substituting the tree root is exactly
                // the failure this vocabulary was written to end: ctest started at the tree root of
                // an out-of-source project reports no tests and exits in under a fifth of a second.
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{setting} names '{{{BuildDirectory}}}', and this run reaches no leg, so there is "
                    + "no build directory to put there. Run it for a leg, or take the name out.");

            case Product:
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"{setting} names '{{{Product}}}', and {paths.ProductProblem ?? "this leg has no build product"}.");

            case ResultsDirectory when paths.ResultsDirectory is null:
                // Never another directory in its place: a results file read anywhere but the one emptied for this run
                // may be any earlier run's.
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"{setting} names '{{{ResultsDirectory}}}', and only a run of a test invocation has a results directory, "
                    + "made empty for it: name it in a test invocation's args or results.");

            case ActionBuild or ActionArtifacts or StepBuild or RunArtifacts
                when paths.ActionBuild is null:
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"{setting} names '{{{name}}}', and this runner declares phases rather than an "
                    + "action, so there is no action directory to put there. Only a step of an "
                    + "action file has one.");

            case Leg or Os or Processor or Toolchain or Config or Variant or Host or RunId or Distribution
                when paths.Identity is null:
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{setting} names '{{{name}}}', and this run reaches no leg, so there is nothing "
                    + "to put there. Run it for a leg, or take the name out.");

            case Distribution:
                // Throws, saying which case it is: a leg in no WSL distribution, or one whose item could not be read.
                _ = DistributionFor(setting, name, paths.Identity!);
                break;

            case not null when ItemOf(name) is not null:
                _ = paths.Identity is { } identity
                    ? DistributionFor(setting, name, identity)
                    : throw new HarnessException(
                        HarnessExit.UsageError,
                        $"{setting} names '{{{name}}}', and this run reaches no leg, so no WSL item was read for it. "
                        + "Run it for a leg, or take the name out.");
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The program <paramref name="name"/> is filled in with - the compiler the leg's build identified for
    /// <paramref name="language"/> - or <see langword="null"/> ahead of that build, where the name stands as written.
    /// </summary>
    /// <exception cref="HarnessException">
    /// Nothing fills the name in: the run reaches no leg, the caller reads no build, or the leg's build has no one
    /// program for that language. Each says which.
    /// </exception>
    private static string? Compiler(string setting, string name, string language, LegPaths paths)
    {
        if (paths.BuildDirectory is null)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{setting} names '{{{name}}}', and this run reaches no leg, so no build identified a compiler to put "
                + "there. Run it for a leg, or take the name out.");
        }

        if (paths.Compilers is not { } compilers)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting} names '{{{name}}}', and nothing here reads which compiler the leg's build identified: only "
                + "a run line and a test invocation are filled in with one.");
        }

        return compilers.For(language) switch
        {
            null => null,
            { Program: { } program } => program,
            var none => throw new HarnessException(HarnessExit.ConfigInvalid, $"{setting} names '{{{name}}}', and {none.Problem}."),
        };
    }

    /// <summary>
    /// Throws for a name nothing can fill in, where this caller's policy says an unowned brace group
    /// is a mistake rather than somebody else's syntax.
    /// </summary>
    private static void Refuse(
        string setting,
        string name,
        PlaceholderPolicy policy,
        IEnumerable<string>? extra,
        IReadOnlyCollection<string>? unvalued = null)
    {
        // Refused under either policy, because no other vocabulary spells a name that differs from
        // one of these only in case. This is the typo a reader cannot see: '{builddir}' reaches a
        // runner as literal text and ctest answers it by reporting no tests and exiting 8.
        var miscased = All.FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));

        if (miscased is not null)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting} names '{{{name}}}', which is '{{{miscased}}}' spelled differently. "
                + $"Write it as '{{{miscased}}}'.");
        }

        if (policy == PlaceholderPolicy.LeaveAsWritten)
        {
            return;
        }

        throw Unknown(setting, name, extra, unvalued);
    }

    /// <summary>Whether <paramref name="name"/> is one this vocabulary owns.</summary>
    private static bool Known(string name) => IsOwned(name);

    /// <summary>What <paramref name="name"/> spells for this leg, or null when nothing does.</summary>
    private static string? Value(string name, LegPaths paths) => name switch
    {
        BuildDirectory => paths.BuildDirectory,
        TreeRoot => paths.TreeRoot,
        HarnessDirectory => paths.HarnessDirectory,
        ResultsDirectory => paths.ResultsDirectory,
        Product => paths.Product,
        ActionBuild => paths.ActionBuild,
        ActionArtifacts => paths.ActionArtifacts,
        StepBuild => paths.StepBuild,
        RunArtifacts => paths.RunArtifacts,
        Leg => paths.Identity?.Leg,
        Os => paths.Identity?.Os,
        Processor => paths.Identity?.Processor,
        Toolchain => paths.Identity?.Toolchain,
        Config => paths.Identity?.Config,
        Variant => paths.Identity?.Variant,
        Host => paths.Identity?.Host,
        RunId => paths.Identity?.RunId,
        Distribution => paths.Identity?.Distribution,
        _ => ItemOf(name) is { } item ? paths.Identity?.Distributions.GetValueOrDefault(item) : null,
    };

    private static HarnessException Unknown(
        string setting,
        string name,
        IEnumerable<string>? extra,
        IReadOnlyCollection<string>? unvalued)
    {
        // The family of a WSL item's distribution beside the names it follows, as a reader writes one.
        var known = All
            .SelectMany(spelled => spelled == Distribution ? [spelled, $"{ItemDistributionPrefix}<item>"] : new[] { spelled })
            .Concat(extra ?? [])
            .Select(spelled => $"{{{spelled}}}");

        var declared = unvalued is { Count: > 0 }
            ? $"; {string.Join(", ", unvalued.Select(spelled => $"{{{spelled}}}"))} "
                + $"{(unvalued.Count == 1 ? "is" : "are")} declared as well, with no value this run"
            : string.Empty;

        return new HarnessException(
            HarnessExit.ConfigInvalid,
            $"{setting} names '{{{name}}}', which nothing here can fill in. "
            + $"The names are {string.Join(", ", known)}{declared}. "
            + "Write '{{' for a brace this tool should leave alone.");
    }
}
