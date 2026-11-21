using DssHarness.Core.FileSystem;
using DssHarness.Core.Pages;

namespace DssHarness.Tests;

/// <summary>
/// The answers a conversation keeps, of any kind: each written to its file and read back by a store made again over its
/// directory - a resumed conversation's; one kept under another state never given, and said to have moved; every one kept
/// however many there are; and the directory put away whole when the conversation ends.
/// </summary>
public sealed class KeptAnswersTests
{
    private static readonly IFileSystem Disk = new PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create());

    /// <summary>The contract an answer of these tests is kept with: a line of a state file's.</summary>
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<Answer> Json = KeptAnswers.Contract<Answer>(JsonStateFile.LineOptions);

    /// <summary>
    /// An answer one store kept is read back by a store made again over the same directory, as a resumed conversation
    /// makes it: its file - the ask's key and the kept extension, one line compressed - is what answers, whole.
    /// </summary>
    [Fact]
    public void AKeptAnswer_IsReadBackAfterAResume()
    {
        using var temp = new TempDirectory();
        var kept = new Answer(PageKey.OfCall("toolu_01"), new string('a', 64), "the whole answer");

        Assert.Null(Store(temp.Path).Keep(kept));

        var found = Store(temp.Path).Find(kept.Key, kept.State);

        Assert.Equal(new KeptFind<Answer>(kept, null), found);
        Assert.Equal([kept.Key + KeptAnswers.Extension], Directory.EnumerateFiles(temp.Path).Select(Path.GetFileName));
    }

    /// <summary>
    /// An answer kept under one state is never given to an ask under another - in the process that kept it, or a resumed
    /// one - and the store says it moved, with no why of a file that does not read; under its own state it is given.
    /// </summary>
    [Fact]
    public void AnAnswerKeptUnderAnotherState_IsNotGiven_AndSaysMoved()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        var kept = new Answer(PageKey.OfCall("toolu_02"), new string('a', 64), "laid on the first state");
        store.Keep(kept);

        var moved = new KeptFind<Answer>(null, null) { Moved = true };

        Assert.Equal(moved, store.Find(kept.Key, new string('b', 64)));
        Assert.Equal(moved, Store(temp.Path).Find(kept.Key, new string('b', 64)));
        Assert.Equal(new KeptFind<Answer>(kept, null), store.Find(kept.Key, kept.State));
    }

    /// <summary>
    /// Ten thousand answers are all kept, however many there are: each in its file and each held in memory, so every one
    /// is found by the store that kept it and by a store made again over its directory.
    /// </summary>
    [Fact]
    public void TenThousandAnswers_AreAllKept()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        var answers = Enumerable.Range(0, 10_000).Select(at => new Answer(PageKey.OfCall($"call_{at}"), new string('a', 64), $"answer {at}")).ToList();

        Assert.All(answers, answer => Assert.Null(store.Keep(answer)));
        Assert.Equal(10_000, Directory.EnumerateFiles(temp.Path).Count());

        var resumed = Store(temp.Path);

        Assert.All(answers, answer => Assert.Equal(new KeptFind<Answer>(answer, null), store.Find(answer.Key, answer.State)));
        Assert.All(answers, answer => Assert.Equal(new KeptFind<Answer>(answer, null), resumed.Find(answer.Key, answer.State)));
    }

    /// <summary>
    /// A conversation's directory is put away whole when it ends - every answer it kept, of every kind kept there - and one
    /// already put away, or never made, is no failure.
    /// </summary>
    [Fact]
    public void PutAway_RemovesTheConversationsDirectory()
    {
        using var temp = new TempDirectory();
        var directory = temp.Combine("graph-pages");
        Store(directory).Keep(new Answer(PageKey.OfCall("toolu_03"), new string('a', 64), "a call's answer"));
        Store(directory).Keep(new Answer(PageKey.Of("find", new System.Text.Json.Nodes.JsonObject { ["query"] = "M" }), new string('a', 64), "an ask's answer"));

        Assert.Equal(2, Directory.EnumerateFiles(directory).Count());
        Assert.Null(KeptAnswers.PutAway(directory, Disk));
        Assert.False(Directory.Exists(directory));
        Assert.Null(KeptAnswers.PutAway(directory, Disk));
        Assert.Null(KeptAnswers.PutAway(temp.Combine("never-made"), Disk));
    }

    /// <summary>A store of these tests' answers over <paramref name="directory"/>.</summary>
    private static KeptAnswers<Answer> Store(string directory) => new(directory, Disk, Json, new Freshness());

    /// <summary>An answer kept for an ask, under the state it was computed on.</summary>
    /// <param name="Key">The ask's key.</param>
    /// <param name="State">The state it was computed on.</param>
    /// <param name="Text">What it holds.</param>
    public sealed record Answer(string Key, string State, string Text);

    /// <summary>What tells whether an answer of these tests still answers: its key, a text it holds, its state.</summary>
    private sealed class Freshness : IKeptFreshness<Answer>
    {
        public string KeyOf(Answer kept) => kept.Key;

        public string? Problem(Answer kept) => string.IsNullOrEmpty(kept.Text) ? "it holds no text" : null;

        public string StateOf(Answer kept) => kept.State;
    }
}
