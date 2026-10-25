using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// How one instance of a step is named: a step's name with the groups around it, a <c>repeat</c>'s iteration, a
/// <c>forEach</c>'s item, a step's visits - and the same id as it names its directory, an item's brackets written
/// <c>@</c>.
/// </summary>
public sealed class FlowInstanceIdTests
{
    /// <summary>The spec's own id is well formed, and each mark is read where it sits.</summary>
    [Fact]
    public void TheSpecsOwnId_IsWellFormed()
    {
        const string id = "rounds#2.items[2].fix#3.review.correctness";

        Assert.True(FlowInstanceId.IsWellFormed(id));
        Assert.Null(FlowInstanceId.Problem(id));
    }

    /// <summary>An id is the same text as its directory, an item's brackets written <c>@</c>, and back again.</summary>
    [Fact]
    public void AnIdAndItsDirectoryName_SayEachOther()
    {
        const string id = "rounds#2.items[2].fix#3.review.correctness";

        Assert.Equal("rounds#2.items@2.fix#3.review.correctness", FlowInstanceId.DirectoryOf(id));
        Assert.Equal(id, FlowInstanceId.IdOf("rounds#2.items@2.fix#3.review.correctness"));
        Assert.Equal("items@0.items@3", FlowInstanceId.DirectoryOf("items[0].items[3]"));
        Assert.Equal("items[0].items[3]", FlowInstanceId.IdOf("items@0.items@3"));
    }

    /// <summary>A visit is written from the second: the first visit of a step is its id without one.</summary>
    [Theory]
    [InlineData("rounds#2.items[2].verify+2", true)]
    [InlineData("rounds#2.items[2].mend+3", true)]
    [InlineData("verify", true)]
    [InlineData("verify+1", false)]
    [InlineData("rounds#0", false)]
    [InlineData("items[0]", true)]
    [InlineData("items[-1]", false)]
    [InlineData("rounds..fix", false)]
    [InlineData("Round", false)]
    [InlineData("", false)]
    public void AVisitIsWrittenFromTheSecond_AndEveryMarkFromItsOwnStart(string id, bool wellFormed)
        => Assert.Equal(wellFormed, FlowInstanceId.IsWellFormed(id));

    /// <summary>A refused id says what its segment should have said.</summary>
    [Fact]
    public void ARefusedId_SaysWhatIsWrongWithIt()
    {
        Assert.Equal(
            "'verify+1': the first visit of a step is its id without a visit, and the second is written +2",
            FlowInstanceId.Problem("verify+1"));
        Assert.Equal("'rounds#0': an iteration is counted from 1", FlowInstanceId.Problem("rounds#0"));
    }

    /// <summary>
    /// Every count is written one way - digits alone, no sign, no space, no leading zero - so one instance is never
    /// named by two ids, and an id written any other way says how its mark is written.
    /// </summary>
    [Theory]
    [InlineData("rounds#02", "'rounds#02': an iteration is written #n, from 1")]
    [InlineData("rounds#+2", "'rounds#+2': an iteration is written #n, from 1")]
    [InlineData("rounds# 2", "'rounds# 2': an iteration is written #n, from 1")]
    [InlineData("items[01]", "'items[01]': an item is written [n], an index from 0")]
    [InlineData("items[ 1]", "'items[ 1]': an item is written [n], an index from 0")]
    [InlineData("items[+1]", "'items[+1]': an item is written [n], an index from 0")]
    [InlineData("verify+02", "'verify+02': a visit is written +n, from 2")]
    [InlineData("verify+ 2", "'verify+ 2': a visit is written +n, from 2")]
    public void ACountWrittenAnyOtherWay_IsRefused(string id, string problem)
        => Assert.Equal(problem, FlowInstanceId.Problem(id));

    /// <summary>
    /// A visit holds its iterations or items, so a segment's visit is written first, outer to inner: a repeat run
    /// again (<c>rounds+2#1</c>) and a forEach expanded anew (<c>items+2[0]</c>, <c>items+2@0</c> on disk) are well
    /// formed, and say their directory and back again.
    /// </summary>
    [Theory]
    [InlineData("rounds+2#1", "rounds+2#1")]
    [InlineData("items+2[0]", "items+2@0")]
    [InlineData("rounds+3#2.items+2[1].fix+2#1.review.correctness", "rounds+3#2.items+2@1.fix+2#1.review.correctness")]
    public void AVisitHoldingAnIterationOrAnItem_IsWrittenFirst_AndSaysItsDirectory(string id, string directory)
    {
        Assert.Null(FlowInstanceId.Problem(id));
        Assert.Equal(directory, FlowInstanceId.DirectoryOf(id));
        Assert.Equal(id, FlowInstanceId.IdOf(directory));
    }

    /// <summary>The other order is refused, naming the right one, so no instance is ever named two ways.</summary>
    [Theory]
    [InlineData("rounds#1+2", "'rounds#1+2': a visit is written before the iteration or item it holds: 'rounds+2#1'")]
    [InlineData("items[0]+2", "'items[0]+2': a visit is written before the iteration or item it holds: 'items+2[0]'")]
    public void AVisitWrittenAfterWhatItHolds_IsRefused_NamingTheRightOrder(string id, string problem)
        => Assert.Equal(problem, FlowInstanceId.Problem(id));

    /// <summary>A mark written twice, or an iteration beside an item, names no instance, and is refused.</summary>
    [Theory]
    [InlineData("fix+2+3")]
    [InlineData("rounds#1#2")]
    [InlineData("items[0][1]")]
    [InlineData("fix#1[0]")]
    [InlineData("fix+2#1+3")]
    [InlineData("items[1]x")]
    public void AMarkWrittenTwice_IsRefused(string id)
        => Assert.Equal(
            $"'{id}': a segment of an id is a step's name, then at most a visit (+n) and an iteration (#n) or an item ([n]), in that order",
            FlowInstanceId.Problem(id));

    /// <summary>A segment's name is spelt as a step's is, to its end: one that ends in a line's end is no step's name.</summary>
    [Fact]
    public void ASegmentEndingInANewLine_IsNoStepsName()
        => Assert.Equal(
            "'verify\n': a segment of an id is a step's name, then at most a visit (+n) and an iteration (#n) or an item ([n]), in that order",
            FlowInstanceId.Problem("verify\n"));

    /// <summary>
    /// Two instances are ordered by the first group instance around them they differ in: an earlier visit, then an
    /// earlier iteration, comes first - whatever item or step it holds - and another item of a forEach never does; two
    /// visits of one step by their visits; two steps in the same group instances are left to their declarations.
    /// </summary>
    [Theory]
    [InlineData("fix#1.gate", "fix#2.commit", true)]
    [InlineData("fix#2.gate", "fix#1.commit", false)]
    [InlineData("rounds#1.items[1].fold", "rounds#2.items[0].commit", true)]
    [InlineData("rounds+2#1.fold", "rounds#3.commit", false)]
    [InlineData("rounds#3.fold", "rounds+2#1.commit", true)]
    [InlineData("items[0].fold", "items[1].commit", false)]
    [InlineData("items[1].fold", "items[0].commit", false)]
    [InlineData("items+2[1].fold", "items+3[0].commit", true)]
    [InlineData("commit", "commit+2", true)]
    [InlineData("commit+3", "commit+2", false)]
    [InlineData("commit", "commit", false)]
    [InlineData("items[0].fix#2.check", "items[0].fix#10.commit", true)]
    [InlineData("gate", "commit", null)]
    [InlineData("items[0].fold", "items[0].commit", null)]
    [InlineData("prep", "items[0].commit", null)]
    public void TwoInstances_AreOrderedByTheGroupInstancesAroundThem(string earlier, string later, bool? precedes)
        => Assert.Equal(precedes, FlowInstanceId.Precedes(earlier, later));
}
