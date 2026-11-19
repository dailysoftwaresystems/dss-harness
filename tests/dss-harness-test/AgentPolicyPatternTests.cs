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
