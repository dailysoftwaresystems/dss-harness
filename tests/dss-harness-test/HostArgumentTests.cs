using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>One argument of the command a run request names: text, or bytes that cross as their base64 text.</summary>
public sealed class HostArgumentTests
{
    /// <summary>
    /// Two arguments are the same where the command reads the same text from them: bytes carried are the same as the same
    /// bytes in another buffer, and as their base64 text - what a host reads them as - and never as other bytes or text.
    /// Arguments the same have the same hash.
    /// </summary>
    [Fact]
    public void Arguments_AreTheSame_WhereTheCommandReadsTheSameText()
    {
        byte[] bytes = [1, 2, 3, 250];
        var carried = HostArgument.Carrying(bytes);
        HostArgument[] same = [HostArgument.Carrying(bytes.ToArray()), HostArgument.Of(Convert.ToBase64String(bytes))];
        HostArgument[] other = [HostArgument.Carrying(new byte[] { 1, 2, 3, 251 }), HostArgument.Of("AQID+g"), HostArgument.Carrying(bytes.AsMemory(1))];

        Assert.All(same, argument => Assert.Equal(carried, argument));
        Assert.All(same, argument => Assert.Equal(carried.GetHashCode(), argument.GetHashCode()));
        Assert.All(other, argument => Assert.NotEqual(carried, argument));
        Assert.Equal(HostArgument.Of("a"), HostArgument.Of("a"));
        Assert.NotEqual(HostArgument.Of("a"), HostArgument.Of("b"));
    }

    /// <summary>
    /// An argument carrying bytes says how many it carries, never encoding them to say so: a file a sync carries, named in a
    /// line or a debugger, would otherwise be made whole as text - what carrying it a piece at a time is for. Text is itself.
    /// </summary>
    [Fact]
    public void AnArgumentCarryingBytes_SaysHowManyItCarries_WithoutEncodingThem()
    {
        Assert.Equal("67108864 byte(s), read as their base64 text", HostArgument.Carrying(new byte[64 << 20]).ToString());
        Assert.Equal("--json", HostArgument.Of("--json").ToString());
    }
}
