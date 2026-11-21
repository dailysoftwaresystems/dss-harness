using NSubstitute;
using DssHarness.Core.Configuration;
using DssHarness.Core.Platform;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

public sealed class WorktreeNameTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("wt")]
    [InlineData("fix-auth")]
    [InlineData("a1")]
    [InlineData("1a")]
    [InlineData("a-b-c-d-e")]
    [InlineData("abcdefghij")]
    public void Validate_AcceptsWellFormedNames(string name)
    {
        Assert.True(WorktreeName.Validate(name).TryGetName(out var accepted, out var error), error);
        Assert.Equal(name, accepted);
    }

    /// <summary>
    /// A name's length is the path budget's to decide, and nothing else's where the repository sets no longest name: a
    /// forty-character name is taken, as one of any length is - the budget, measured against the real path where the
    /// tree is made, refuses one that would not fit. Red with a default of ten characters put back.
    /// </summary>
    [Fact]
    public void AFortyCharacterWorktreeName_IsTaken()
    {
        var name = "a-forty-character-name-for-a-long-branch";

        Assert.Equal(40, name.Length);
        Assert.Null(new WorktreeSettings().MaxNameLength);
        Assert.True(WorktreeName.Validate(name, new WorktreeSettings().MaxNameLength).TryGetName(out var accepted, out var error), error);
        Assert.Equal(name, accepted);
        Assert.True(WorktreeName.Validate(new string('a', 500)).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsMissingNames(string? name)
    {
        Assert.False(WorktreeName.Validate(name).IsValid);
    }

    [Theory]
    [InlineData("abcdefghijk")]
    [InlineData("this-name-is-far-too-long")]
    public void Validate_RejectsNamesOverTheLimitTheRepositorySets_AndSaysWhereItIsSet(string name)
    {
        Assert.False(WorktreeName.Validate(name, maxLength: 10).TryGetName(out _, out var error));

        // The refusal says where the limit is set, and what holds a name where none is.
        Assert.Contains("the limit is 10, which worktrees.maxNameLength in config.json sets", error, StringComparison.Ordinal);
        Assert.Contains("held to the path budget alone", error, StringComparison.Ordinal);
        Assert.Contains(HostPlatform.WindowsMaxPath.ToString(System.Globalization.CultureInfo.InvariantCulture), error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Fix-Auth")]
    [InlineData("fix_auth")]
    [InlineData("fix auth")]
    [InlineData("-fix")]
    [InlineData("fix-")]
    [InlineData("fix--auth")]
    [InlineData("fix.auth")]
    [InlineData("fix/auth")]
    [InlineData("fix\\auth")]
    [InlineData("..")]
    [InlineData(".hidden")]
    public void Validate_RejectsMalformedNames(string name)
    {
        Assert.False(WorktreeName.Validate(name).IsValid);
        Assert.False(WorktreeName.ValidateFormat(name).IsValid);
    }

    [Fact]
    public void Validate_AppliesTheLimitItIsGiven()
    {
        Assert.True(WorktreeName.Validate("fix-auth-flow", maxLength: 16).IsValid);

        Assert.False(WorktreeName.Validate("abcd", maxLength: 3).TryGetName(out _, out var error));
        Assert.Contains("the limit is 3", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RefusesALimitBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorktreeName.Validate("a", maxLength: 0));
    }

    [Fact]
    public void ValidateFormat_ChecksTheShape_ButNotTheLength()
    {
        // A worktree created under a longer limit must stay addressable after the limit
        // is lowered, so deletion checks the shape alone.
        Assert.True(WorktreeName.ValidateFormat(new string('a', 64)).IsValid);
    }

    [Fact]
    public void TryGetName_YieldsExactlyOneOfTheNameAndTheReason()
    {
        Assert.True(WorktreeName.ValidateFormat("ok").TryGetName(out var name, out var noError));
        Assert.Equal("ok", name);
        Assert.Null(noError);

        Assert.False(WorktreeName.ValidateFormat("Not OK").TryGetName(out var noName, out var error));
        Assert.Null(noName);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Generate_ProducesNamesThatValidate()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var generated = WorktreeName.Generate();

            Assert.Equal(WorktreeName.RandomLength, generated.Length);
            Assert.True(WorktreeName.Validate(generated).IsValid, generated);
        }
    }

    [Fact]
    public void Generate_HonoursTheLengthItIsGiven()
    {
        var generated = WorktreeName.Generate(4);

        Assert.Equal(4, generated.Length);
        Assert.True(WorktreeName.ValidateFormat(generated).IsValid, generated);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorktreeName.Generate(0));
    }

    [Fact]
    public void Generate_DoesNotRepeatItself()
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            generated.Add(WorktreeName.Generate());
        }

        // Collisions are possible in principle; a run of 200 producing duplicates
        // would mean the generator is not actually random.
        Assert.True(generated.Count > 190, $"only {generated.Count} distinct names");
    }

    /// <summary>
    /// A new name is never one of the existing names, however crowded its length: with all but one of the 1,296 names of
    /// two characters taken, the one left is drawn, every time; with every one taken, none is - said as that fact, never
    /// as a count of draws run out; and where the names taken fold case, a name taken in another case is taken. Red with
    /// the ten draws put back.
    /// </summary>
    [Fact]
    public void ANewName_IsNeverAnExistingOne()
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var all = Alphabet.SelectMany(first => Alphabet.Select(second => $"{first}{second}")).ToList();
        var taken = new HashSet<string>(all.Where(name => name != "q7"), StringComparer.Ordinal);

        Assert.Equal(1296, all.Count);

        for (var draw = 0; draw < 50; draw++)
        {
            Assert.Equal("q7", WorktreeName.Generate(2, taken));
        }

        taken.Add("q7");
        Assert.Null(WorktreeName.Generate(2, taken));

        var folded = new HashSet<string>(all.Where(name => name != "q7").Select(name => name.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);

        Assert.Equal("q7", WorktreeName.Generate(2, folded));

        // Past twice the names taken, a draw is free more often than not, and still never one of them.
        var few = new HashSet<string>(["ab", "cd", "ef"], StringComparer.Ordinal);

        for (var draw = 0; draw < 200; draw++)
        {
            var drawn = WorktreeName.Generate(2, few) ?? throw new InvalidOperationException("no name was drawn, though most are free");

            Assert.DoesNotContain(drawn, few);
            Assert.True(WorktreeName.ValidateFormat(drawn).IsValid, drawn);
        }
    }
}

/// <summary>
/// The budget is arithmetic over string lengths, so it is tested with a substitute
/// platform: every case runs on every operating system, whatever limit the machine has.
/// </summary>
public sealed class PathBudgetTests
{
    private const int Limit = 260;
    private const int Reserve = 163;
    private const int Margin = 20;

    /// <summary>A path one character under the limit fits, and the name it was built from is the longest that does.</summary>
    [Fact]
    public void Check_AllowsAPathOneUnderTheLimit()
    {
        // 69 + separator + 5 = 75 characters, and 75 + separator + 163 + 20 = 259.
        var directory = DirectoryOf(parentLength: 69, nameLength: 5);

        var result = Budget(Limit).Check(directory, Reserve, Margin);

        Assert.True(result.IsWithinBudget);
        Assert.Equal(259, result.RequiredLength);
        Assert.Equal(Limit, result.Limit);
        Assert.Equal(5, result.AvailableNameLength);
        Assert.Contains("needs 259 characters, under the limit of 260", result.Describe(directory), StringComparison.Ordinal);
    }

    /// <summary>
    /// A path of exactly the limit does not fit: Windows' 260 counts the NUL that ends a path, so it
    /// takes 259 characters at most. The refusal says what would fit.
    /// </summary>
    [Fact]
    public void Check_RefusesAPathThatReachesTheLimit_AndSaysWhatWouldFit()
    {
        var directory = DirectoryOf(parentLength: 69, nameLength: 6);

        var result = Budget(Limit).Check(directory, Reserve, Margin);

        Assert.False(result.IsWithinBudget);
        Assert.Equal(260, result.RequiredLength);
        Assert.Contains("must stay under 260", result.Describe(directory), StringComparison.Ordinal);
        Assert.Contains("at most 5 characters", result.Describe(directory), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_SaysNoNameFits_WhenTheParentIsAlreadyTooLong()
    {
        var directory = DirectoryOf(parentLength: 100, nameLength: 1);

        var result = Budget(Limit).Check(directory, Reserve, Margin);

        Assert.False(result.IsWithinBudget);
        Assert.True(result.AvailableNameLength <= 0);
        Assert.Contains("No name fits", result.Describe(directory), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_IsUnbounded_WhereThePlatformHasNoLimit()
    {
        var directory = DirectoryOf(parentLength: 5000, nameLength: 10);

        var result = Budget(platformLimit: null).Check(directory, Reserve, Margin);

        Assert.True(result.IsWithinBudget);
        Assert.Null(result.Limit);
        Assert.Null(result.AvailableNameLength);
        Assert.Contains("not subject to a path length limit", result.Describe(directory), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_UsesAConfiguredLimit_InPlaceOfThePlatforms()
    {
        var tight = Budget(platformLimit: null).Check(DirectoryOf(70, 7), Reserve, Margin, limit: 260);
        var relaxed = Budget(Limit).Check(DirectoryOf(200, 10), Reserve, Margin, limit: 4096);

        Assert.False(tight.IsWithinBudget);
        Assert.Equal(260, tight.Limit);
        Assert.True(relaxed.IsWithinBudget);
        Assert.Equal(4096, relaxed.Limit);
    }

    /// <summary>
    /// What the reserve names hangs off the directory after one separator, which the budget counts:
    /// left to each caller, it was counted by the one whose reserve named a build directory and
    /// missed wherever that caller added nothing, so a path one character over was taken to fit. The
    /// twelve characters it then needs fit under a limit of 13, and not at one of 12.
    /// </summary>
    [Theory]
    [InlineData(13, true)]
    [InlineData(12, false)]
    public void Check_CountsTheSeparatorBetweenTheDirectoryAndWhatIsReservedBelowIt(int limit, bool fits)
    {
        var directory = DirectoryOf(parentLength: 3, nameLength: 2);

        var result = Budget(platformLimit: null).Check(directory, reserve: 5, margin: 0, limit);

        Assert.Equal(fits, result.IsWithinBudget);
        Assert.Equal(directory.Length + 1 + 5, result.RequiredLength);
        Assert.Equal(limit - 1 - (1 + 5) - 4, result.AvailableNameLength);
    }

    /// <summary>A directory spelled with a separator at its end is the same directory, counted once.</summary>
    [Fact]
    public void Check_CountsATrailingSeparatorOnce()
    {
        var directory = DirectoryOf(parentLength: 3, nameLength: 2);

        Assert.Equal(
            Budget(Limit).Check(directory, Reserve, Margin).RequiredLength,
            Budget(Limit).Check(directory + Path.DirectorySeparatorChar, Reserve, Margin).RequiredLength);
    }

    /// <summary>
    /// Nothing reserved below the directory needs no separator to reach it: the directory alone fits
    /// under a limit one longer than itself, which a separator counted anyway would reach.
    /// </summary>
    [Fact]
    public void Check_AddsNoSeparator_WhenNothingIsReserved()
    {
        var directory = DirectoryOf(parentLength: 3, nameLength: 2);

        Assert.True(Budget(platformLimit: null).Check(directory, reserve: 0, margin: 0, limit: directory.Length + 1).IsWithinBudget);
    }

    [Fact]
    public void Check_RefusesInputsThatWouldDisableTheArithmetic()
    {
        var budget = Budget(Limit);
        var directory = DirectoryOf(10, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Check(directory, reserve: -1, Margin));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Check(directory, Reserve, margin: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Check(directory, Reserve, Margin, limit: 0));
    }

    private static PathBudget Budget(int? platformLimit)
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.MaxPathLength.Returns(platformLimit);
        return new PathBudget(platform);
    }

    private static string DirectoryOf(int parentLength, int nameLength)
        => new string('p', parentLength) + Path.DirectorySeparatorChar + new string('n', nameLength);
}
