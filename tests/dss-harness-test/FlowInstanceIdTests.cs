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
}
