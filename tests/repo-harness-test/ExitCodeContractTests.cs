using RepoHarness.Core.Anchors;
using RepoHarness.Core.Ci;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Tools;


namespace RepoHarness.Tests;

/// <summary>
/// The exit-code contract is a promise to callers, and the parts of it that are
/// conventions rather than types are pinned here so they become build failures.
/// </summary>
public sealed class ExitCodeContractTests
{
    [Fact]
    public void SharedCodes_NeverIntrudeOnThePerCommandRange()
    {
        // Codes 1-9 belong to individual commands (see VerifyGitStatus). A shared
        // code landing there would silently collide with a command's own contract,
        // and HarnessExit.All is built by reflection, so a new constant joins the
        // shared set without anyone deciding it should.
        foreach (var description in HarnessExit.All)
        {
            Assert.True(
                description.Code == 0 || description.Code >= 10,
                $"{description.Name} = {description.Code} intrudes on the per-command range 1-9");
        }
    }

    [Fact]
    public void SharedCodes_AreUnique()
    {
        var duplicates = HarnessExit.All
            .GroupBy(description => description.Code)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(d => d.Name))}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EverySharedCode_IsExplained()
    {
        foreach (var description in HarnessExit.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(description.Explanation),
                $"{description.Name} has no explanation, so 'help exit-codes' shows a blank line");
        }
    }

    [Fact]
    public void SuccessIsZero_Everywhere()
    {
        Assert.Equal(0, HarnessExit.Success);
        Assert.Equal(0, (int)VerifyGitStatus.Success);
    }

    [Fact]
    public void PerCommandCodes_StayInsideTheirRange()
    {
        foreach (var status in Enum.GetValues<VerifyGitStatus>())
        {
            var value = (int)status;
            Assert.True(value is >= 0 and <= 9, $"{status} = {value} is outside the per-command range");
        }

        Assert.InRange(AnchorExit.Findings, 1, 9);
        Assert.InRange(LegsExit.Unavailable, 1, 9);
        Assert.InRange(ToolsExit.NotProvisioned, 1, 9);
        Assert.InRange(CiExit.LegRed, 1, 9);
        Assert.InRange(CiExit.MatrixDidNotRun, 1, 9);
        Assert.InRange(LegExit.InputsMoved, 1, 9);
        Assert.InRange(LegExit.Contended, 1, 9);
        Assert.InRange(LegExit.Unwitnessed, 1, 9);
        Assert.InRange(LegExit.LogHeld, 1, 9);
        Assert.InRange(LegExit.NotAdmitted, 1, 9);
        Assert.InRange(LegExit.Violated, 1, 9);
        Assert.InRange(LegExit.Survived, 1, 9);
        Assert.InRange(LegExit.Unattributed, 1, 9);
    }

    [Fact]
    public void EveryLegVerdict_MapsToACodeThatSaysWhatToDoNext()
    {
        // The whole reason the leg commands have a contract of their own: a reader who cannot tell
        // which of these fired cannot pick the remedy, and four of them call for four different ones.
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.InputsMoved));
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.Unmeasured));
        Assert.Equal(LegExit.Contended, Verdicts.ExitCodeFor(LegVerdict.Contended));
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(LegVerdict.Unwitnessed));
        Assert.Equal(LegExit.LogHeld, Verdicts.ExitCodeFor(LegVerdict.LogHeld));
        Assert.Equal(LegExit.NotAdmitted, Verdicts.ExitCodeFor(LegVerdict.NotAdmitted));
        Assert.Equal(LegExit.Violated, Verdicts.ExitCodeFor(LegVerdict.Violated));
        Assert.Equal(LegExit.Survived, Verdicts.ExitCodeFor(LegVerdict.Survived));
        Assert.Equal(LegExit.Unattributed, Verdicts.ExitCodeFor(LegVerdict.Unattributed));

        // And the three that reuse a shared code, because their remedy is the shared one.
        Assert.Equal(HarnessExit.CommandFailed, Verdicts.ExitCodeFor(LegVerdict.Failed));
        Assert.Equal(HarnessExit.Refused, Verdicts.ExitCodeFor(LegVerdict.RefusedLocked));
        Assert.Equal(HarnessExit.InternalError, Verdicts.ExitCodeFor(LegVerdict.Poisoned));
    }

    /// <summary>
    /// Within one command no exit code has two meanings. A command that runs legs exits with the code its run's worst
    /// verdict decides, or with the one it gives where no selected leg can run and none was turned away by a failure -
    /// which is therefore the code of no verdict its legs reach, save those that mean the same: nothing failed, and a
    /// leg reached no verdict. A build, a test and a run give 1 there, which no verdict of theirs is. A sweep's arms
    /// reach violated, which is 1: so a sweep gives what such a run is, incomplete.
    /// </summary>
    [Theory]
    [InlineData(false, LegsExit.Unavailable)]
    [InlineData(true, HarnessExit.Incomplete)]
    public void WhereNoSelectedLegCanRun_ACommandsCode_MeansNothingElseOfItsLegs(bool sweeps, int expected)
    {
        var code = sweeps ? MutationService.NothingRuns : new LegRunRequest(".", null) { Workload = LegWorkload.BuildAndTest }.NothingRunsExit;
        var reached = Verdicts.All.Where(info => sweeps || !info.OfASweep).ToList();

        Assert.Equal(expected, code);
        Assert.All(
            reached.Where(info => info.ExitCode == code),
            info => Assert.True(
                Verdicts.ReachedNone(info.Verdict),
                $"{code} is what the command gives where no selected leg can run, and what its legs' {info.Display} decides"));

        // What only a sweep reaches is what an arm's judge alone decides, and each has a code no other verdict has.
        var ofASweep = Verdicts.All.Where(info => info.OfASweep).ToList();

        Assert.Equal([LegVerdict.Violated, LegVerdict.Survived, LegVerdict.Unattributed], ofASweep.Select(info => info.Verdict));
        Assert.All(ofASweep, info => Assert.Single(Verdicts.All, other => other.ExitCode == info.ExitCode));
    }

    /// <summary>
    /// The exit-code topic lists a command's own codes under the commands that give them, no code twice under one
    /// heading: the verdicts every leg-running command reaches, the three only a sweep reaches under check-mutations
    /// alone, and under each what it gives where no selected leg can run.
    /// </summary>
    [Fact]
    public async Task ExitCodeTopic_ListsNoCodeTwiceUnderOneHeading_AndASweepsOwnUnderCheckMutations()
    {
        var result = await CliRunner.RunAsync(["help", "exit-codes"], TestContext.Current.CancellationToken);
        var blocks = Blocks(result.StandardOutput);

        Assert.All(blocks, block => Assert.Equal(block.Value.Distinct(), block.Value));

        var ofASweep = Verdicts.All.Where(info => info.OfASweep).Select(info => info.ExitCode).ToList();
        var ofEvery = Verdicts.All
            .Where(info => info is { IsFailure: true, OfASweep: false, ExitCode: >= 1 and <= 9 })
            .Select(info => info.ExitCode)
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(ofEvery, blocks["build, test, run, check-mutations"]);
        Assert.Equal([LegsExit.Unavailable], blocks["build, test, run, clean"]);
        Assert.Equal([.. ofASweep.Order(), MutationService.NothingRuns], blocks["check-mutations"]);

        foreach (var line in new[]
        {
            $"    {LegsExit.Unavailable,3}  no selected leg can run, and no failure turned one away",
            $"    {MutationService.NothingRuns,3}  no selected leg can run, and no failure turned one away: incomplete, since",
            $"         {LegsExit.Unavailable} is an arm violated here",
        })
        {
            Assert.Contains(line, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExitCodeTopic_DocumentsEveryPerCommandCode()
    {
        // The shared table is generated; these are prose, so nothing but a test keeps a new
        // per-command code from shipping undocumented.
        var result = await CliRunner.RunAsync(["help", "exit-codes"], TestContext.Current.CancellationToken);

        foreach (var expected in new[] { "install-missing-tools", "check-ci-legs", "build, test, run, check-mutations", "legs, sync" })
        {
            Assert.Contains(expected, result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var verdict in new[] { "inputs-moved", "contended", "unwitnessed", "log-held", "not-admitted", "violated", "survived", "unattributed" })
        {
            Assert.Contains(verdict, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The command a reader is told to type is spelt as the package installs it, lower case: a Linux filesystem is
    /// case-sensitive, and the product's name, capitalised, runs nothing there.
    /// </summary>
    [Fact]
    public void ANotInitialisedRepository_SaysToTypeTheCommandAsInstalled()
        => Assert.Contains($"run '{ToolPackage.Command} init'", HarnessExit.Describe(HarnessExit.NotInitialized)?.Explanation, StringComparison.Ordinal);

    /// <summary>
    /// The codes the exit-code topic lists under each heading of its commands' own contracts, in the order it lists them:
    /// a heading is a line indented two that starts with a letter, and each code a number ending in the seventh column,
    /// which a line a meaning wraps onto never has.
    /// </summary>
    private static Dictionary<string, List<int>> Blocks(string topic)
    {
        var blocks = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        List<int>? codes = null;

        foreach (var line in topic.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.Length > 2 && line.StartsWith("  ", StringComparison.Ordinal) && char.IsLetter(line[2]))
            {
                blocks[line.Trim()] = codes = [];
            }
            else if (System.Text.RegularExpressions.Regex.Match(line, @"^ {4}(?<code>[ \d]{2}\d)  \S") is { Success: true } listed)
            {
                codes?.Add(int.Parse(listed.Groups["code"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (line.Length > 0 && line[0] != ' ')
            {
                codes = null;
            }
        }

        return blocks;
    }

    [Fact]
    public void Describe_FindsASharedCode_AndIgnoresACommandSpecificOne()
    {
        Assert.Equal(nameof(HarnessExit.Refused), HarnessExit.Describe(HarnessExit.Refused)?.Name);

        // 2 is verify-git's "not a repository"; it is not a shared code.
        Assert.Null(HarnessExit.Describe(2));
    }
}
