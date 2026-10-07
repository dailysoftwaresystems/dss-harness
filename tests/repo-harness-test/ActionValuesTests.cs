using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>The values an action reads, and what keeps a secret out of everything that prints.</summary>
public sealed class ActionValuesTests
{
    /// <summary>
    /// A line too long to keep whole is cut where no secret is parted: before the last characters that could still be the
    /// start of one - as many as the longest, less one - and then before every secret the text holds that would still span
    /// the cut, until none does; with no secret, where it is full. Each side masked alone then masks what the whole would.
    /// </summary>
    [Theory]
    [InlineData("xxxxxxxxxxxxxxxxxxxx", 12)]
    [InlineData("xxxxxxxxxxxxxxxxxxTOP", 13)]
    [InlineData("xxxxxxxxxxxxxTOPSECRETxx", 13)]
    [InlineData("xxxxxxxxxxxxxxTOPSECRET", 14)]
    [InlineData("xxxxxxxxxTOPSECRETxxxxx", 9)]
    [InlineData("xxxxxxxTOPSECRETxxKEY12x", 16)]
    [InlineData("xxxxxxxxxxxxKEY12xxxxxxx", 12)]
    [InlineData("xxxxxxxxxxxxxKEY12xxxxxx", 13)]
    [InlineData("xxxxxxxTOPSECRETKEYxxxxx", 7)]
    [InlineData("TOPSECRET", 0)]
    public void Cut_PartsNoSecret(string text, int cut)
    {
        // RETKEY begins inside TOPSECRET: moved before the one, the cut is spanned by the other.
        var values = new ActionValues(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "TOPSECRET", ["B"] = "KEY12", ["C"] = "RETKEY" });

        Assert.Equal(cut, values.Cut(text));

        if (cut > 0)
        {
            Assert.Equal(values.Redact(text), values.Redact(text[..cut]) + values.Redact(text[cut..]));
        }

        Assert.Equal(20, ActionValues.Empty.Cut("xxxxxxxxxxxxxxxxxxxx"));
    }

    [Fact]
    public async Task ReadAsync_MergesEveryFileInADirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "CORPUS_ROOT=corpus\n# a comment\n\nJOBS=8\n");
        temp.WriteFile("env/b.env", "TARGET=all\n");

        var values = await ReadAsync(temp);

        Assert.Equal("corpus", values.Values["CORPUS_ROOT"]);
        Assert.Equal("8", values.Values["JOBS"]);
        Assert.Equal("all", values.Values["TARGET"]);
    }

    [Fact]
    public async Task ReadAsync_StripsAMatchingPairOfQuotes_AndKeepsAHashInsideAValue()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "PADDED=\"  spaced  \"\nSINGLE='quoted'\nTOKEN=ab#cd\n");

        var values = await ReadAsync(temp);

        Assert.Equal("  spaced  ", values.Values["PADDED"]);
        Assert.Equal("quoted", values.Values["SINGLE"]);

        // A generated credential contains '#' often enough that stripping it would produce a value
        // that is almost right, which fails later and somewhere else.
        Assert.Equal("ab#cd", values.Values["TOKEN"]);
    }

    [Fact]
    public async Task ReadAsync_Refuses_ANameDefinedTwiceInOneDirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "JOBS=8\n");
        temp.WriteFile("env/b.env", "JOBS=4\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => ReadAsync(temp));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("'JOBS' is defined in", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nothing can say which value a run used", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_Refuses_ANameDefinedInBothDirectories()
    {
        // The dangerous one: redaction masks the secret's value while the run used the plain one.
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "TOKEN=plain\n");
        temp.WriteFile("secrets/a.env", "TOKEN=s3cret\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => ReadAsync(temp));

        Assert.Contains("'TOKEN' is defined in", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plain value is one a run line can name, so one named like a name this tool fills in is refused,
    /// as an input so named is: a run line naming it got this tool's value, or this one on a leg with
    /// none there, while the environment held this one.
    /// </summary>
    [Theory]
    [InlineData("product")]
    [InlineData("buildDir")]
    [InlineData("config")]
    public async Task ReadAsync_Refuses_APlainValueNamedLikeANameThisToolFillsIn(string name)
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", $"{name}=mine\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => ReadAsync(temp));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains($"defines '{name}', a name this tool already fills in", exception.Message, StringComparison.Ordinal);
        Assert.Contains(temp.Combine("env", "a.env"), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a plain value: a secret never fills a run line, so one so named is answered one way only,
    /// by the environment. And only the name as this tool spells it, since a run line's names are
    /// matched exactly.
    /// </summary>
    [Fact]
    public async Task ReadAsync_Accepts_ASecretOrAnotherSpelling_NamedLikeOne()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "PRODUCT=mine\n");
        temp.WriteFile("secrets/a.env", "config=s3cret\n");

        var values = await ReadAsync(temp);

        Assert.Equal("mine", values.Values["PRODUCT"]);
        Assert.Contains("config", values.SecretNames);
    }

    [Fact]
    public async Task ReadAsync_Refuses_ALineThatDefinesNothing()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "JOBS 8\n=nothing\nexport TOKEN=x\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => ReadAsync(temp));

        Assert.Contains("line 1: no '='", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 2: the name before '=' is empty", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 3: 'export TOKEN' is not a name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ReadsNothing_WhenTheDirectoriesAreAbsent()
    {
        using var temp = new TempDirectory();

        var values = await ReadAsync(temp);

        Assert.Empty(values.Values);
        Assert.Empty(values.SecretNames);
    }

    [Fact]
    public async Task ASecretNeverReachesARenderedCommand_AnErrorMessage_OrThePlainValues()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "USER=dev\n");
        temp.WriteFile("secrets/a.env", "TOKEN=s3cret-value\nPREFIX=s3cret\n");

        var values = await ReadAsync(temp);

        Assert.Equal(["PREFIX", "TOKEN"], values.SecretNames.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("s3cret", string.Join('|', values.Values.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", values.ToString(), StringComparison.Ordinal);

        var rendered = values.RedactAll(["curl", "--header", "Authorization: s3cret-value", "--user", "dev"]);

        Assert.Equal(["curl", "--header", "Authorization: ***", "--user", "dev"], rendered);

        // Longest first, so masking the shorter secret does not leave the longer one's tail behind.
        Assert.Equal(
            "curl failed: *** was rejected",
            values.Redact("curl failed: s3cret-value was rejected"));
    }

    [Fact]
    public void RevealSecrets_IsTheOnlyWayToTheRawValues()
    {
        var values = new ActionValues(
            new Dictionary<string, string> { ["USER"] = "dev" },
            new Dictionary<string, string> { ["TOKEN"] = "s3cret" });

        Assert.True(values.IsSecret("token"));
        Assert.False(values.Values.ContainsKey("TOKEN"));
        Assert.Equal("s3cret", values.RevealSecrets()["TOKEN"]);

        // The copy handed out cannot be used to reach back into the values.
        Assert.NotSame(values.RevealSecrets(), values.RevealSecrets());
    }

    [Fact]
    public void Redact_LeavesTextAloneWhenThereAreNoSecrets()
    {
        Assert.Equal("cmake --build build", ActionValues.Empty.Redact("cmake --build build"));
        Assert.Equal(string.Empty, ActionValues.Empty.Redact(null));
    }

    private static Task<ActionValues> ReadAsync(TempDirectory temp)
        => new ActionValuesReader(
                new PhysicalFileSystem(FilePermissionsFactory.Create()),
                new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false))
            .ReadAsync(temp.Combine("env"), temp.Combine("secrets"), TestContext.Current.CancellationToken);
}
