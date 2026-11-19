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
