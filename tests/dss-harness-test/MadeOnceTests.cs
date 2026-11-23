using DssHarness.Core.Walks;

namespace DssHarness.Tests;

/// <summary>
/// Answers made once each where making one asks for others as it is made: a key asked again while it is made is given what is
/// known of it then and never made inside itself, and an answer made from that placeholder is never kept, so no later ask reads an
/// answer made from nothing where a key's own answer stands.
/// </summary>
public sealed class MadeOnceTests
{
    /// <summary>
    /// A key whose making asks, through another, for itself is given its placeholder there: the other's answer, made from it, is
    /// given where it was asked - made once in the cycle's making however often asked there - and not kept; the key met again keeps
    /// its answer, and the other is made again at its next ask, from that answer, and kept then.
    /// </summary>
    [Fact]
    public void AnAnswerMadeFromAKeyStillBeingMade_IsGivenButNeverKept_AndIsMadeAgainFromThatKeysOwnAnswer()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b", "c", "b"],
            ["b"] = ["a"],
            ["c"] = [],
        };
        var makings = new Makings();
        var store = new MadeOnce<string, string>(StringComparer.Ordinal);
        var made = new List<string>();

        string Of(string key) => store.Of(makings, key, $"none:{key}", () =>
        {
            made.Add(key);
            return $"{key}({string.Join(',', graph[key].Select(Of))})";
        });

        Assert.Equal("a(b(none:a),c(),b(none:a))", Of("a"));
        Assert.Equal(["a", "b", "c"], made);

        // The key met again and the key that read nothing of a placeholder are kept; the one made from the placeholder is not.
        Assert.Equal(("a(b(none:a),c(),b(none:a))", "c()"), (Kept(store, "a"), Kept(store, "c")));
        Assert.False(store.TryKept("b", out _));

        Assert.Equal("b(a(b(none:a),c(),b(none:a)))", Of("b"));
        Assert.Equal("b(a(b(none:a),c(),b(none:a)))", Kept(store, "b"));
        Assert.Equal(["a", "b", "c", "b"], made);
    }

    /// <summary>
    /// A cycle through two stores that share their makings is one cycle: the answers made from the placeholder in either store are
    /// not kept, the key met again is, and an answer made inside a key itself made from a placeholder further out is not kept either.
    /// </summary>
    [Fact]
    public void ACycleThroughStoresThatShareTheirMakings_KeepsNoAnswerMadeFromThePlaceholder()
    {
        var makings = new Makings();
        var names = new MadeOnce<string, string>(StringComparer.Ordinal);
        var counts = new MadeOnce<int, string>();

        string Name(string key) => names.Of(makings, key, $"none:{key}", () => key switch
        {
            "x" => $"x({Count(1)})",
            "z" => $"z({Name("x")},{Count(2)})",
            _ => key,
        });

        string Count(int key) => counts.Of(makings, key, $"none:{key}", () => key == 1 ? $"1({Name("z")})" : $"{key}");

        Assert.Equal("x(1(z(none:x,2)))", Name("x"));

        // x was met again and keeps its answer; 1 and z were made from its placeholder; 2 read none and is kept.
        Assert.Equal("x(1(z(none:x,2)))", Kept(names, "x"));
        Assert.Equal("2", Kept(counts, 2));
        Assert.False(names.TryKept("z", out _));
        Assert.False(counts.TryKept(1, out _));

        Assert.Equal("z(x(1(z(none:x,2))),2)", Name("z"));
        Assert.Equal("z(x(1(z(none:x,2))),2)", Kept(names, "z"));
    }

    /// <summary>
    /// Cycles nested five hundred deep, each key asking the next, the one before and one key all of them share, which asks the
    /// innermost: each key is made once in the outermost key's making - a making that ends leaning further out hands what leans on
    /// it to the one it leans on, never forgetting it at once - so the makings are as many as the keys, never their square; and the
    /// outermost key alone is kept.
    /// </summary>
    [Fact]
    public void NestedCycles_MakeEachKeyOnce_InTheOutermostKeysMaking()
    {
        const int Depth = 500;
        var makings = new Makings();
        var store = new MadeOnce<int, int>();
        var made = new int[Depth + 2];

        // Keys 1 to Depth nest; key 0 is the one they share, which asks the innermost.
        int Of(int key) => store.Of(makings, key, -1, () =>
        {
            made[key]++;

            if (key == 0)
            {
                Of(Depth);
                return key;
            }

            if (key < Depth)
            {
                Of(key + 1);
            }

            if (key > 1)
            {
                Of(key - 1);
            }

            Of(0);
            return key;
        });

        Assert.Equal(1, Of(1));
        Assert.Equal(Depth + 1, made.Sum());
        Assert.All(made[..(Depth + 1)], count => Assert.Equal(1, count));
        Assert.Equal([1], Enumerable.Range(0, Depth + 1).Where(key => store.TryKept(key, out _)));
    }

    /// <summary>
    /// A memo given the makings a store shares answers each key as one of their makings: its answer to a key whose answer asks the
    /// store for a key whose making asked the memo - a cycle through both - is held, never kept, and answered again once the
    /// store's key is made; where a memo of its own would keep the answer it made from the store's placeholder.
    /// </summary>
    [Fact]
    public void AMemoGivenTheMakings_HoldsAnAnswerMadeFromAStoresPlaceholder_AndAnswersItAgain()
    {
        var makings = new Makings();
        var store = new MadeOnce<string, string>(StringComparer.Ordinal);
        var answered = 0;
        Memo<string, string>? memo = null;

        string Store(string key) => store.Of(makings, key, $"none:{key}", () => $"{key}({memo!.Of("m")})");

        memo = new Memo<string, string>(
            _ => [],
            (key, _) =>
            {
                answered++;
                return $"{key}({Store("s")})";
            },
            key => $"cycle:{key}",
            StringComparer.Ordinal,
            makings);

        Assert.Equal("s(m(none:s))", Store("s"));
        Assert.Equal(1, answered);

        Assert.Equal("m(s(m(none:s)))", memo.Of("m"));
        Assert.Equal(2, answered);
        Assert.Equal("m(s(m(none:s)))", memo.Of("m"));
        Assert.Equal(2, answered);
        Assert.Empty(memo.Cycles);
    }

    /// <summary>A making that throws leaves its key unmade and not open, so a later ask makes it afresh, never as a cycle.</summary>
    [Fact]
    public void AMakingThatThrows_LeavesItsKeyUnmade_AndNotOpen()
    {
        var makings = new Makings();
        var store = new MadeOnce<string, string>(StringComparer.Ordinal);
        var failing = true;

        string Of(string key) => store.Of(makings, key, $"none:{key}", () => key == "top"
            ? $"top({Of("below")})"
            : failing ? throw new InvalidOperationException("not yet") : "below");

        Assert.Equal("not yet", Assert.Throws<InvalidOperationException>(() => Of("top")).Message);
        Assert.False(store.TryKept("top", out _));

        failing = false;

        Assert.Equal("top(below)", Of("top"));
        Assert.Equal("top(below)", Kept(store, "top"));
    }

    /// <summary>The answer <paramref name="store"/> keeps for <paramref name="key"/>, which it must keep.</summary>
    private static TAnswer Kept<TKey, TAnswer>(MadeOnce<TKey, TAnswer> store, TKey key)
        where TKey : notnull
    {
        Assert.True(store.TryKept(key, out var answer));
        return answer;
    }
}
