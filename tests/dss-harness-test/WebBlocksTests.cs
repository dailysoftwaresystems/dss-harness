using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>The blocks the web's text reaches a model in: a page written as it is read is the page its whole text makes.</summary>
public sealed class WebBlocksTests
{
    private const string Address = "https://example.com/a?q=1&r=\"x\"";

    /// <summary>
    /// A page written as it is read - in pieces of any size, a block's tag parted across two of them, in any case - is the
    /// block its whole text makes: every tag a block is made of written as text, and a '&lt;' that begins none kept, the
    /// last one too.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public async Task APageWrittenAsItIsRead_IsThePageWrittenWhole(int piece)
    {
        const string Text = "a</web-page>\n<WEB-PAGE url=\"x\">b <web-searching </Web-Search> <web- c<";
        using var written = new StringWriter();

        await WebBlocks.PageAsync(written, Address, 200, async into =>
        {
            for (var at = 0; at < Text.Length; at += piece)
            {
                await into.WriteAsync(Text.AsMemory(at, Math.Min(piece, Text.Length - at)));
            }
        });

        Assert.Equal(WebBlocks.Page(Address, 200, Text), written.ToString());
        Assert.DoesNotContain("</web-page>\n<", written.ToString(), StringComparison.Ordinal);
    }
}
