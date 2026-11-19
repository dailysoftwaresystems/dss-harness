using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The policy decides what a command may name however large it is written - a find expression of a hundred thousand
/// terms, a brace word of forty groups, a removal over a hundred thousand entries - by reading it whole, never by a bound
/// past which it is denied. A test here watches the heap, so the class runs alone (<see cref="MemoryMeasured"/>).
/// </summary>
[Collection(MemoryMeasured.Name)]
public sealed class AgentPolicyPatternTests
{
    /// <summary>
    /// A find expression of a hundred thousand terms is read and asked of every entry of its walk on a stack of a
    /// megabyte: its reader and its evaluator keep their operators on stacks of their own. Red with the recursive reader
    /// (Negated, Primary and List calling each other a level a term), which overflowed the stack and ended the process.
    /// </summary>
    [Fact]
    public async Task AFindExpressionOfAHundredThousandTerms_IsDecided()
    {
        Assert.Equal(new ChildRunAnswer(0, "allowed", ""), await ChildRun.MethodAsync(typeof(AgentPolicyPatternTests), nameof(DecideAHundredThousandTerms), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="AFindExpressionOfAHundredThousandTerms_IsDecided"/>: the decision, on a small stack.</summary>
    internal static string DecideAHundredThousandTerms() => SmallStack.Run(() =>
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        File.WriteAllText(Path.Combine(worktree, "kept.txt"), "x");
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);
        var expression = string.Join(" -o ", Enumerable.Range(0, 100_000).Select(at => $"-name x{at}"));

        return policy.Run($"find . {expression} -delete") is AgentPolicyAnswer.Denied denied ? denied.Reason : "allowed";
    });

    /// <summary>
    /// A brace word of forty groups is decided by its pattern, never spelled out: forty choices, read against the names
    /// that matter where it stands - a slot's below it, the directory's own - and one spelling for every other, so a word
    /// spelling 2^40 paths is allowed where none is refused and refused where one names the slot, the heap never holding
    /// more than the pattern. Red with <c>BraceBound</c>, which denied the word as undecided, or with spelling every word
    /// out, which never ends.
    /// </summary>
    [Fact]
    public void ABraceWordOfFortyGroups_IsDecidedWithoutExpanding()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);
        var forty = string.Concat(Enumerable.Repeat("{a,b}", 40));
        AgentPolicyAnswer allowed;
        AgentPolicyAnswer denied;

        using (var heap = new HeapWatch())
        {
            allowed = policy.Run($"cat {forty} {forty}/x.txt");
            denied = policy.Run($"cat {{{forty},.harness-config}}/workflows/.env/keys.env");

            Assert.True(heap.Growth < 64L * 1024 * 1024, $"deciding forty groups grew the heap by {heap.Growth:N0} bytes: {heap.Where}");
        }

        Assert.Equal(AgentPolicyAnswer.Allow, allowed);
        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(denied).Rule);
    }

    /// <summary>
    /// A walk that removes what it matches is asked of every entry of a git directory it reaches, however many: a
    /// hundred thousand entries and one more, the last the only one it matches, is refused for that one, and one that
    /// matches none of them is allowed. Red with <c>MaxWalkEntries</c>, which denied a walk past twenty thousand entries as
    /// undecided.
    /// </summary>
    [Fact]
    public void ARemovalOverAHundredThousandEntries_IsDecidedOnEvery()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var git = Directory.CreateDirectory(Path.Combine(worktree, ".git")).FullName;
        var policy = Policy(new WideDirectory(new PhysicalFileSystem(FilePermissionsFactory.Create()), git, 100_000, "zz-last"), worktree);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("find . -name 'yy*' -delete"));
        Assert.Contains("zz-last", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("find . -name 'zz*' -delete")).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fifty moves joined by <c>;</c> - each held where it goes and failed where it stood, so the shell may stand in any of
    /// 2^50 places - are followed as one form whose fifty steps may each be passed, and a path is decided at one place of
    /// each way those places stand, as at every one: a read of the slot fifty levels up, which only the place every move
    /// held reaches, is refused, one two levels up refused from the places two moves deep, and the same moves making
    /// something allowed - in well under a second. Red with each move followed from every place the one before held and
    /// failed, one by one, which never ends.
    /// </summary>
    [Fact]
    public void FiftyMovesJoinedBySemicolons_AreDecidedAsOneForm()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);
        var moves = string.Join("; ", Enumerable.Range(0, 50).Select(at => $"cd d{at}"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var allowed = policy.Run($"{moves}; make");
        var deepest = policy.Run($"{moves}; cat {string.Concat(Enumerable.Repeat("../", 50))}.harness-config/workflows/.env/keys.env");
        var twoDeep = policy.Run($"{moves}; cat ../../.harness-config/workflows/.env/keys.env");
        var tooDeep = policy.Run($"{moves}; cat {string.Concat(Enumerable.Repeat("../", 51))}tree/x/.harness-config/workflows/.env/keys.env");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"deciding fifty moves took {clock.Elapsed}");
        Assert.Equal(AgentPolicyAnswer.Allow, allowed);
        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(deepest).Rule);
        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(twoDeep).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, tooDeep);
    }

    /// <summary>
    /// A word of forty names each followed by a dot pattern that may match <c>..</c> - 2^40 places, a step back up folding
    /// the name before it away - is decided as reading every one would decide it, by the forms of the places it reaches: the
    /// slot beyond them, which only the reading of every pattern as <c>..</c> reaches, is refused, and the same word ending
    /// in a plain file allowed - in well under a second. Red with the dot readings spelled out one combination at a time,
    /// which never ends.
    /// </summary>
    [Fact]
    public void FortyDotPatternsAmongNames_AreDecidedByTheFormsTheyReach()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);
        var forty = string.Concat(Enumerable.Range(0, 40).Select(at => $"d{at}/.?/"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var refused = policy.Run($"cat {forty}.harness-config/workflows/.env/keys.env");
        var allowed = policy.Run($"cat {forty}x.txt");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"deciding forty dot patterns took {clock.Elapsed}");
        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(refused).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, allowed);
    }

    /// <summary>
    /// A path of sixty short-name components, each of which may shorten two guarded names, is decided by the ways its 3^60
    /// readings stand to the policy, never one reading at a time, and decided as reading every one would decide it: a
    /// <c>Read</c> rule over one of those names refuses the path whose last name it names and allows the same path ending
    /// in another, a command's write of it beneath the tree is refused as git's own and one beneath no tree allowed - all of
    /// it in well under a second. Red with <c>Spelled</c> deciding every reading <see cref="WindowsNames.Lengthened"/>
    /// gives, which never ends.
    /// </summary>
    [Fact]
    public void ASixtyComponentShortNamePath_IsDecidedByHowItsReadingsStand()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a short name as each name it may shorten.");

        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var elsewhere = Directory.CreateDirectory(temp.Combine("elsewhere")).FullName;
        var policy = new AgentPolicy(
            new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
            HostDoubles.Platform(PlatformId.Windows),
            PolicyGrounds.Of(worktree, [worktree], [worktree], [], [], [new AgentRestriction("Read(**/.github/secret)", AgentPolicySource.Config)]));
        var sixty = string.Concat(Enumerable.Repeat(@"\GIT~1", 60));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var refused = policy.Read(worktree + sixty + @"\secret");
        var allowed = policy.Read(worktree + sixty + @"\public");
        var written = policy.Run($"echo x > \"{worktree + sixty}\\x\"");
        var writtenElsewhere = policy.Run($"echo x > \"{elsewhere + sixty}\\x\"");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"deciding sixty short names took {clock.Elapsed}");
        Assert.Equal("Read(**/.github/secret)", Assert.IsType<AgentPolicyAnswer.Denied>(refused).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, allowed);
        Assert.Equal(AgentPolicy.GitsOwnRule, Assert.IsType<AgentPolicyAnswer.Denied>(written).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, writtenElsewhere);
    }

    /// <summary>
    /// A pattern read a segment at a time (<c>GlobPattern.Begin</c>, <c>Next</c>, <c>Ends</c>) decides what reading the
    /// path whole decides, for every pattern shape - anchored or not, naming a directory or not, <c>**</c> leading, inside
    /// and trailing - against every path of up to four segments over its names. Red with a trailing <c>**</c> matched by the
    /// segment it begins at, or with a matched directory forgotten by the segments after it.
    /// </summary>
    [Fact]
    public void APatternReadASegmentAtATime_DecidesAsTheWholePath()
    {
        string[] patterns = ["a", "a/", "*.txt", "/a", "a/b", "a/b/", "**/b", "a/**", "a/**/b", "**/a/**", "a/*/b/", "**"];
        string[] names = ["a", "b", "x.txt", "A"];
        var paths = new List<string[]> { Array.Empty<string>() };

        for (var length = 1; length <= 4; length++)
        {
            paths.AddRange(paths.Where(path => path.Length == length - 1).SelectMany(path => names.Select(name => (string[])[.. path, name])).ToList());
        }

        foreach (var text in patterns)
        {
            var pattern = DssHarness.Core.Repository.GlobPattern.Parse(text);

            foreach (var path in paths.Where(path => path.Length > 0))
            {
                foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
                {
                    var stands = pattern.Begin();

                    foreach (var segment in path)
                    {
                        stands = pattern.Next(stands, segment, comparison);
                    }

                    Assert.True(
                        pattern.Matches(string.Join('/', path), comparison) == pattern.Ends(stands),
                        $"'{text}' against '{string.Join('/', path)}' ({comparison}): whole {pattern.Matches(string.Join('/', path), comparison)}, a segment at a time {pattern.Ends(stands)}");
                }
            }
        }
    }

    /// <summary>
    /// A command whose braces spell two scripts, each written before it by the string, has both decided: the second,
    /// which reads the slot, is refused though the first is allowed - never only the first written script a reading runs.
    /// Red with the written-script look returning the first alone, which allowed the command.
    /// </summary>
    [Fact]
    public void ACommandRunningTwoWrittenScripts_HasBothDecided()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);

        var answer = policy.Run(
            "cat <<< 'cat x.txt' > a.sh; cat <<< 'cat .harness-config/workflows/.env/keys.env' > b.sh; sh {a,b}.sh");

        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(answer).Rule);
    }

    /// <summary>
    /// A command whose readings write one file - one appending, so leaving its text unknown, one overwriting it with a
    /// text it knows - leaves what the file holds unknown: the script it runs is undecided, never decided as the last
    /// reading's text alone. Red with <c>StringState.Record</c> keeping the last reading's write, which allowed a script
    /// of the text only one reading gives the file.
    /// </summary>
    [Fact]
    public void TwoReadingsWritingOneFileWithDifferentTexts_LeaveItsTextUnknown()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);

        var answer = policy.Run("p={-a,zz}; tee <<< 'cat x.txt' $p s.sh; sh s.sh");

        Assert.Equal(AgentPolicy.UndecidedRule, Assert.IsType<AgentPolicyAnswer.Denied>(answer).Rule);
    }

    /// <summary>
    /// A command that reads thirty variables, each holding every place a move's readings reach - two places each, so
    /// 2^30 ways one value may be made - is decided by the forms of those places, never by a reading per way: the
    /// thirty read together are allowed where every place they reach is, and the slot one of them reaches refused - in
    /// well under a second. Red with a reading spelled for every way the variables' choices may be made, which never
    /// ends.
    /// </summary>
    [Fact]
    public void ThirtyVariablesOfEveryPlace_AreDecidedAsTheirForms()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        Directory.CreateDirectory(Path.Combine(worktree, "a"));
        Directory.CreateDirectory(Path.Combine(worktree, "b"));
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);
        var thirty = string.Join("; ", Enumerable.Range(1, 30).Select(at => $"x{at}=$PWD"));
        var read = string.Join(' ', Enumerable.Range(1, 30).Select(at => $"$x{at}/f.txt"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var allowed = policy.Run($"cd a || cd b; {thirty}; cat {read}");
        var refused = policy.Run($"cd a || cd b; {thirty}; cat $x1/../.harness-config/workflows/.env/keys.env");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"deciding thirty variables took {clock.Elapsed}");
        Assert.Equal(AgentPolicyAnswer.Allow, allowed);
        Assert.Equal("the credential slots", Assert.IsType<AgentPolicyAnswer.Denied>(refused).Rule);
    }

    /// <summary>
    /// Forty brace groups that are not plain - a quoted character among them, a value after an <c>=</c>, a star at their
    /// end - are decided by their pattern: a witness per guarded place the star may reach, the names that matter for the
    /// rest, so a word spelling 2^40 paths is refused where one names the slot, allowed where none does, in well under a
    /// second. Red with every spelling read one at a time, which never ends.
    /// </summary>
    [Fact]
    public void FortyNonPlainBraceGroups_AreDecidedByTheirPattern()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);
        var forty = string.Concat(Enumerable.Repeat("{a,b}", 40));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var quoted = policy.Run($"cat 'q'{forty}");
        var valued = policy.Run($"tar --f={forty} --delete x");
        var starred = policy.Run($"cat {forty}*");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"deciding forty non-plain groups took {clock.Elapsed}");
        Assert.Equal(AgentPolicyAnswer.Allow, quoted);
        Assert.Equal(AgentPolicyAnswer.Allow, valued);
        Assert.Equal(AgentPolicyAnswer.Allow, starred);
    }

    /// <summary>
    /// Both definitions of a command's readings decide alike: every way the variables' choices may be made, one reading
    /// each and every spelling of a word decided one at a time, against the cores and the spellings that decide - random
    /// small commands over programs, moves, variables, braces, patterns and the slot, seeded so a failure repeats. Red
    /// with a probe that answers anything its spelling-true reading would not.
    /// </summary>
    [Fact]
    public void BothDefinitionsOfAReading_DecideAlike()
    {
        using var temp = new TempDirectory();
        var worktree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        Directory.CreateDirectory(Path.Combine(worktree, "a"));
        Directory.CreateDirectory(Path.Combine(worktree, "b"));
        Directory.CreateDirectory(Path.Combine(worktree, ".harness-config", "workflows", ".env"));
        var policy = Policy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), worktree);
        var random = new Random(20261119);

        string Word()
            => random.Next(6) switch
            {
                0 => "x.txt",
                1 => "{a,b}/f.txt",
                2 => "$p/f.txt",
                3 => "$p/../.harness-config/workflows/.env/keys.env",
                4 => "a/*",
                _ => "'q'",
            };

        for (var made = 0; made < 60; made++)
        {
            var command = string.Join(' ', Enumerable.Range(0, random.Next(1, 4)).Select(_ => Word()));
            var whole = $"cd a || cd b; p={{a,b}}; cat {command}";

            AgentPolicyAnswer Probes()
            {
                AgentPolicy.EveryReading = false;
                AgentPolicy.EverySpelling = false;

                return policy.Run(whole);
            }

            AgentPolicyAnswer Every()
            {
                AgentPolicy.EveryReading = true;
                AgentPolicy.EverySpelling = true;

                return policy.Run(whole);
            }

            var probes = Probes();
            AgentPolicyAnswer every;

            try
            {
                every = Every();
            }
            finally
            {
                AgentPolicy.EveryReading = false;
                AgentPolicy.EverySpelling = false;
            }

            Assert.True(
                probes.Refusal() is { } one && every.Refusal() is { } two ? one.Rule == two.Rule : probes.Refusal() is null && every.Refusal() is null,
                $"'{whole}': the probes {(probes.Refusal() is { } answer ? $"refuse it as {answer.Rule}" : "allow it")}, every reading {(every.Refusal() is { } other ? $"refuses it as {other.Rule}" : "allows it")}");
        }
    }

    /// <summary>A policy over <paramref name="worktree"/>, which it reads and writes, a slot in it and git's own directory kept.</summary>
    private static AgentPolicy Policy(IFileSystem fileSystem, string worktree)
        => new(
            fileSystem,
            HostDoubles.Platform(PlatformId.Linux),
            PolicyGrounds.Of(worktree, [worktree], [worktree], [Path.Combine(worktree, ".harness-config", "workflows", ".env")], [], []));

    /// <summary>A disk whose <paramref name="directory"/> lists <paramref name="entries"/> files more than it holds, and then <paramref name="last"/>.</summary>
    private sealed class WideDirectory(IFileSystem inner, string directory, int entries, string last) : PassThroughFileSystem(inner)
    {
        public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
            => !recursive && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase)
                ? Enumerable.Range(0, entries).Select(at => Path.Combine(directory, $"f{at:D6}")).Append(Path.Combine(directory, last)).Concat(base.EnumerateFiles(path, recursive))
                : base.EnumerateFiles(path, recursive);
    }
}
