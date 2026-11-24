using System.Globalization;
using System.Text;
using DssHarness.Core.Walks;

namespace DssHarness.Tests;

/// <summary>
/// Answers made once each where making one asks for others as it is made: a key asked again while it is made is given what is
/// known of it and never made inside itself, every key of a cycle keeps nothing known of it whatever key of the cycle is asked
/// first - with what the whole cycle read - and each key is made once, nothing kept ever made again.
/// </summary>
public sealed class MadeOnceTests
{
    /// <summary>
    /// A key whose making asks, through another, for itself is given its placeholder there, and so is every later ask for that
    /// other while the cycle is not closed: both end kept as their placeholders, each made once, and a key that read no
    /// placeholder keeps what it made - so no later ask reads an answer made from nothing where a key's own answer stands, and
    /// none is made again.
    /// </summary>
    [Fact]
    public void AKeyOfACycle_IsGivenItsPlaceholderAtEveryAsk_AndKeepsIt()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b", "c", "b"],
            ["b"] = ["a"],
            ["c"] = [],
        };
        var makings = new Makings();
        var store = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
        var made = new List<string>();
        var reads = new List<string>();

        string Read(string asker, string key)
        {
            var given = store.Of(makings, key, () =>
            {
                made.Add(key);
                return $"{key}({string.Join(',', graph[key].Select(asked => Read(key, asked)))})";
            });

            reads.Add($"{asker} read {key}: {given}");
            return given;
        }

        Assert.Equal("none:a", Read("outside", "a"));
        Assert.Equal(["a", "b", "c"], made);

        // Every read of a key being made, or made and of a cycle not closed yet, gave what the key ends kept as.
        Assert.Equal(("none:a", "none:b", "c()"), (Kept(store, "a"), Kept(store, "b"), Kept(store, "c")));

        // Kept, all three: a later ask makes nothing.
        Assert.Equal("none:b", Read("outside", "b"));
        Assert.Equal(["a", "b", "c"], made);
        Assert.Equal(
            ["b read a: none:a", "a read b: none:b", "a read c: c()", "a read b: none:b", "outside read a: none:a", "outside read b: none:b"],
            reads);
    }

    /// <summary>Small graphs that hold a cycle, each written <c>key&gt;the keys it asks, in turn;…</c>, and what every key is kept as.</summary>
    public static TheoryData<string, string[]> CyclicGraphs()
        => new()
        {
            // Two keys, each asking the other.
            { "a>b;b>a", ["a=none:a", "b=none:b"] },
            // A cycle of two and a third key both of them ask.
            { "a>b,t;b>a,t;t>", ["a=none:a", "b=none:b", "t=t()"] },
            // Two cycles joined by a bridge - b asks c - and a key outside asking one key of each.
            { "a>b;b>a,c;c>d;d>c;o>a,c", ["a=none:a", "b=none:b", "c=none:c", "d=none:d", "o=o(none:a,none:c)"] },
            // A key asking itself.
            { "s>s,t;t>", ["s=none:s", "t=t()"] },
        };

    /// <summary>
    /// A cycle's keys keep the same answers whatever key of them is asked first, and each key is made once however many are
    /// asked: which key happens to be asked first must not decide what any of them is.
    /// </summary>
    [Theory]
    [MemberData(nameof(CyclicGraphs))]
    public void ACyclesKeys_KeepTheSameAnswersWhateverKeyIsAskedFirst_AndEachKeyIsMadeOnce(string written, string[] kept)
    {
        var graph = Graph(written);
        var keys = graph.Keys.Order(StringComparer.Ordinal).ToArray();
        var once = Enumerable.Repeat(1, keys.Length).ToArray();

        foreach (var order in Orders(keys))
        {
            var answers = Asked(keys, order, (key, of) => $"{key}({string.Join(',', graph[key].Select(of))})");

            Assert.Equal(kept, answers.Kept);
            Assert.Equal(once, answers.Makings);
        }
    }

    /// <summary>
    /// What a making asks depends on what it is given, and it is given each key's final answer: a key asks for another only where
    /// that one answers nothing known - which, of a cycle, it does whatever key is asked first - so the answers kept do not depend
    /// on the order either, and each key is made once.
    /// </summary>
    [Fact]
    public void AGraphWhoseAsksDependOnTheAnswers_KeepsTheSameAnswersWhateverKeyIsAskedFirst()
    {
        string[] keys = ["x", "y", "z"];
        var once = Enumerable.Repeat(1, keys.Length).ToArray();

        // x asks y, and z where y is known as nothing; y and z each ask x.
        string Make(string key, Func<string, string> of)
            => key == "x"
                ? of("y") is var y && y == "none:y" ? $"x({y},{of("z")})" : $"x({y})"
                : $"{key}({of("x")})";

        foreach (var order in Orders(keys))
        {
            var answers = Asked(keys, order, Make);

            Assert.Equal(["x=none:x", "y=none:y", "z=none:z"], answers.Kept);
            Assert.Equal(once, answers.Makings);
        }
    }

    /// <summary>
    /// A cycle through stores that share their makings is one cycle: every key of it, in either store, keeps its placeholder, and
    /// a key that read no placeholder keeps what it made; nothing is made again at a later ask.
    /// </summary>
    [Fact]
    public void ACycleThroughStoresThatShareTheirMakings_AnswersEveryKeysPlaceholder_AndKeepsWhatReadNoneOfIt()
    {
        var makings = new Makings();
        var names = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
        var counts = new MadeOnce<int, string>(key => $"none:{key}");
        var made = new List<string>();

        string Name(string key) => names.Of(makings, key, () =>
        {
            made.Add(key);
            return key switch
            {
                "x" => $"x({Count(1)})",
                "z" => $"z({Name("x")},{Count(2)})",
                _ => key,
            };
        });

        string Count(int key) => counts.Of(makings, key, () =>
        {
            made.Add($"{key}");
            return key == 1 ? $"1({Name("z")})" : $"{key}";
        });

        Assert.Equal("none:x", Name("x"));

        // x, 1 and z are of the cycle and keep their placeholders; 2 read none of it and keeps what it made.
        Assert.Equal(("none:x", "none:z"), (Kept(names, "x"), Kept(names, "z")));
        Assert.Equal(("none:1", "2"), (Kept(counts, 1), Kept(counts, 2)));
        Assert.Equal(["x", "1", "z", "2"], made);

        Assert.Equal(("none:z", "none:1", "2"), (Name("z"), Count(1), Count(2)));
        Assert.Equal(["x", "1", "z", "2"], made);
    }

    /// <summary>
    /// Cycles nested five hundred deep, each key asking the next, the one before and one key all of them share, which asks the
    /// innermost: every key is of the one set, is made once and keeps its placeholder, so the makings are as many as the keys,
    /// never their square, and every key is kept.
    /// </summary>
    [Fact]
    public void NestedCycles_MakeEachKeyOnce_AndAnswerEveryKeysPlaceholder()
    {
        const int Depth = 500;
        var makings = new Makings();
        var store = new MadeOnce<int, int>(_ => -1);
        var made = new int[Depth + 2];

        // Keys 1 to Depth nest; key 0 is the one they share, which asks the innermost.
        int Of(int key) => store.Of(makings, key, () =>
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

        Assert.Equal(-1, Of(1));
        Assert.Equal(Depth + 1, made.Sum());
        Assert.All(made[..(Depth + 1)], count => Assert.Equal(1, count));
        Assert.Equal(0, made[Depth + 1]);
        Assert.All(Enumerable.Range(0, Depth + 1), key => Assert.Equal(-1, Kept(store, key)));
    }

    /// <summary>
    /// One key asking each key of a five-hundred-key cycle in turn makes each once: the first asked is made inside the asker's
    /// making, reads its placeholder where it comes round, and every key of the cycle keeps its placeholder with the set closed
    /// before the next is asked - so one ask costs as many makings as there are keys, never their square (125,751 when a cycle's
    /// keys were made again for each asked) - and the asking key keeps the answer it made from their placeholders.
    /// </summary>
    [Fact]
    public void AKeyAskingEachKeyOfACycleInTurn_MakesEachOnce_AndKeepsItsAnswerMadeFromTheirPlaceholders()
    {
        const int Length = 500;
        var (of, made, store) = Ring(Length);

        Assert.Equal($"o({string.Join(',', Enumerable.Range(1, Length).Select(at => $"none:a{at}"))})", of("o"));
        Assert.Equal(Length + 2, made.Values.Sum());
        Assert.All(made.Values, count => Assert.Equal(1, count));
        Assert.Equal($"o({string.Join(',', Enumerable.Range(1, Length).Select(at => $"none:a{at}"))})", Kept(store, "o"));
        Assert.All(Enumerable.Range(1, Length), at => Assert.Equal($"none:a{at}", Kept(store, $"a{at}")));
        Assert.Equal("none:c", Kept(store, "c"));
    }

    /// <summary>
    /// A cycle's keys asked from outside, first one end and then the other and so on, are made once in all: the first asked is
    /// made inside nothing, and each later ask finds every key kept - where an answer made from a placeholder was forgotten once
    /// the making it leaned on ended, each new entry point broke the cycle at another key and made its keys again, about half
    /// their square.
    /// </summary>
    [Fact]
    public void ACyclesKeysAskedFromAlternatingEnds_AreMadeOnceInAll()
    {
        const int Length = 500;
        var (of, made, store) = Ring(Length);
        var alternating = Enumerable.Range(1, Length / 2).SelectMany(at => new[] { $"a{at}", $"a{Length + 1 - at}" });

        foreach (var key in alternating)
        {
            of(key);
        }

        Assert.Equal(Length + 1, made.Values.Sum());
        Assert.Equal(0, made["o"]);
        Assert.All(made.Where(entry => entry.Key != "o"), entry => Assert.Equal(1, entry.Value));
        Assert.All(Enumerable.Range(1, Length), at => Assert.Equal($"none:a{at}", Kept(store, $"a{at}")));
        Assert.Equal("none:c", Kept(store, "c"));
    }

    /// <summary>
    /// A making that throws inside a cycle is unmade with what was made inside the one it was made in and not closed yet: the
    /// thrower, the making that asked for it and the one that reached back to the making outside are all made afresh at their
    /// next ask, while what closed inside stays kept, the makings outside are as they were - still being made, so an ask inside
    /// them for the unmaken one is answered as a cycle's again - and nothing kept was made while the thrower ran.
    /// </summary>
    [Fact]
    public void AMakingThatThrowsInsideACycle_UnmakesWithItWhatLeanedFurtherOut_KeepsWhatClosedInsideIt_AndLeavesTheMakingsOutsideOpen()
    {
        var makings = new Makings();
        var store = new MadeOnce<string, string>(key => $"none:{key}", (_, made, _) => $"cycle:{made}", StringComparer.Ordinal);
        var made = new Dictionary<string, int>(StringComparer.Ordinal);
        var reads = new List<string>();
        var failing = true;

        string Of(string asker, string key)
        {
            try
            {
                var given = store.Of(makings, key, () => Make(key));
                reads.Add($"{asker} asked {key}: {given}");
                return given;
            }
            catch (InvalidOperationException ex)
            {
                reads.Add($"{asker} asked {key}: {ex.Message}");
                throw;
            }
        }

        // a is the making outside: it goes on past the throw. h asks a, so it leans on a; t holds the thrower u and k, which
        // closes alone inside t.
        string Make(string key)
        {
            made[key] = made.GetValueOrDefault(key) + 1;

            return key switch
            {
                "a" => $"a({Caught("t")},{Of("a", "h")})",
                "h" => $"h({Of("h", "a")})",
                "t" => $"t({Of("t", "h")},{Of("t", "k")},{Of("t", "u")})",
                "u" when failing => throw new InvalidOperationException("not yet"),
                _ => $"{key}()",
            };
        }

        string Caught(string key)
        {
            try
            {
                Of("a", key);
                return "nothing thrown";
            }
            catch (InvalidOperationException)
            {
                return "caught";
            }
        }

        Assert.Equal("cycle:a(caught,none:h)", Of("outside", "a"));

        Assert.Equal(
            ["h asked a: none:a", "t asked h: none:h", "t asked k: k()", "t asked u: not yet", "a asked t: not yet", "h asked a: none:a", "a asked h: none:h",
                "outside asked a: cycle:a(caught,none:h)"],
            reads);

        // u and t are unmade; h was unmade with t and made again inside a, which it found still being made; k was made once.
        Assert.Equal((1, 1, 2, 1, 1), (made["a"], made["t"], made["h"], made["k"], made["u"]));
        Assert.Equal(("cycle:a(caught,none:h)", "cycle:h(none:a)", "k()"), (Kept(store, "a"), Kept(store, "h"), Kept(store, "k")));
        Assert.False(store.TryKept("t", out _));
        Assert.False(store.TryKept("u", out _));

        // What closed inside the thrower, and what closed with the making outside, is kept: asking again makes nothing.
        Assert.Equal(("k()", "cycle:h(none:a)"), (Of("outside", "k"), Of("outside", "h")));
        Assert.Equal((1, 1, 2, 1, 1), (made["a"], made["t"], made["h"], made["k"], made["u"]));

        // The thrower and the making it was inside are made afresh once what they ask is answered, from what is kept now.
        failing = false;
        Assert.Equal("t(cycle:h(none:a),k(),u())", Of("outside", "t"));
        Assert.Equal("t(cycle:h(none:a),k(),u())", Kept(store, "t"));
        Assert.Equal("u()", Kept(store, "u"));
        Assert.Equal((1, 2, 2, 1, 2), (made["a"], made["t"], made["h"], made["k"], made["u"]));
        Assert.Equal(
            ["h asked a: none:a", "t asked h: none:h", "t asked k: k()", "t asked u: not yet", "a asked t: not yet", "h asked a: none:a", "a asked h: none:h",
                "outside asked a: cycle:a(caught,none:h)", "outside asked k: k()", "outside asked h: cycle:h(none:a)",
                "t asked h: cycle:h(none:a)", "t asked k: k()", "t asked u: u()", "outside asked t: t(cycle:h(none:a),k(),u())"],
            reads);
    }

    /// <summary>
    /// What a cycle read is kept with every key of it: a store may keep beside each member's placeholder what the whole set
    /// read, made once for the set and the same object in every member's kept answer, so that a key outside the set that asked
    /// for the member the set closed on reads the whole set through it.
    /// </summary>
    [Fact]
    public void AKeysOfACycle_KeepTheSameObject_HoldingWhatEveryMemberRead()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b"],
            ["b"] = ["a"],
            ["c"] = ["a"],
            ["d"] = ["c"],
        };
        var makings = new Makings();
        var store = new MadeOnce<string, (string Value, List<string> Reads)>(
            key => ($"none:{key}", []),
            (key, made, cycle) =>
            {
                var read = cycle.Shared(() => new List<string>());
                read.AddRange(made.Reads);
                return ($"none:{key}", read);
            },
            StringComparer.Ordinal);
        var made = new List<string>();

        (string Value, List<string> Reads) Of(string key) => store.Of(makings, key, () =>
        {
            made.Add(key);
            var reads = new List<string> { $"read:{key}" };
            var given = graph[key].Select(Of).ToList();
            reads.AddRange(given.SelectMany(answer => answer.Reads));
            return ($"{key}({string.Join(',', given.Select(answer => answer.Value))})", reads);
        });

        var d = Of("d");

        Assert.Equal("d(c(none:a))", d.Value);
        Assert.Equal(["read:d", "read:c", "read:b", "read:a"], d.Reads);

        // a and b are of the cycle and keep one object holding what both made; c and d read it through a, once the set closed.
        Assert.Same(Kept(store, "a").Reads, Kept(store, "b").Reads);
        Assert.Equal(("none:a", "none:b"), (Kept(store, "a").Value, Kept(store, "b").Value));
        Assert.Equal(["read:b", "read:a"], Kept(store, "a").Reads);
        Assert.Equal("c(none:a)", Kept(store, "c").Value);
        Assert.Equal(["read:c", "read:b", "read:a"], Kept(store, "c").Reads);

        // Each made once, the one that asks before the one it asks for.
        Assert.Equal(["d", "c", "a", "b"], made);
    }

    /// <summary>
    /// A store that refuses, throwing, to keep one key of a cycle as the cycle closes unmakes the whole set, the members kept
    /// before the one it refused with it: none of it stays kept with what part of the set read, and the next ask makes every
    /// member afresh, each keeping its placeholder with what the whole set read.
    /// </summary>
    [Fact]
    public void ACycledThatThrowsForOneKeyOfACycle_UnmakesTheWholeSet_AndTheNextAskMakesEveryMemberAfresh()
    {
        var asks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["R"] = "A",
            ["A"] = "B",
            ["B"] = "C",
            ["C"] = "R",
        };
        var makings = new Makings();
        var refusing = true;
        var store = new MadeOnce<string, (string Value, List<string> Reads)>(
            key => ($"none:{key}", []),
            (key, made, cycle) =>
            {
                if (key == "A" && refusing)
                {
                    throw new InvalidOperationException("not yet");
                }

                var read = cycle.Shared(() => new List<string>());
                read.AddRange(made.Reads);
                return ($"none:{key}", read);
            },
            StringComparer.Ordinal);
        var made = new List<string>();

        (string Value, List<string> Reads) Of(string key) => store.Of(makings, key, () =>
        {
            made.Add(key);
            var given = Of(asks[key]);
            var reads = new List<string> { $"read:{key}" };
            reads.AddRange(given.Reads);
            return ($"{key}({given.Value})", reads);
        });

        // The set closes from C down to R, so A is refused once C and B were kept: none of the four stays kept.
        Assert.Equal("not yet", Assert.Throws<InvalidOperationException>(() => Of("R")).Message);
        Assert.Equal(["R", "A", "B", "C"], made);
        Assert.All(asks.Keys, key => Assert.False(store.TryKept(key, out _)));

        // Made afresh, each once more, every one keeping its placeholder with the same object holding what all four read.
        refusing = false;
        var r = Of("R");

        Assert.Equal("none:R", r.Value);
        Assert.Equal(["read:C", "read:B", "read:A", "read:R"], r.Reads);
        Assert.All(asks.Keys, key => Assert.Same(r.Reads, Kept(store, key).Reads));
        Assert.Equal(("none:R", "none:A", "none:B", "none:C"), (Kept(store, "R").Value, Kept(store, "A").Value, Kept(store, "B").Value, Kept(store, "C").Value));
        Assert.Equal(["R", "A", "B", "C", "R", "A", "B", "C"], made);
    }

    /// <summary>
    /// Where the store refuses to keep the outermost making of a cycle, the last the set is closed on - once every other member
    /// was kept - the whole set is unmade all the same: none of it stays kept with what part of the set read, and the next ask
    /// makes every member afresh.
    /// </summary>
    [Fact]
    public void ACycledThatThrowsForACyclesOutermostMaking_UnmakesTheWholeSet_AsWell()
    {
        var makings = new Makings();
        var refusing = true;
        var store = new MadeOnce<string, string>(
            key => $"none:{key}",
            (key, made, _) => key == "a" && refusing ? throw new InvalidOperationException("not yet") : $"cycle:{made}",
            StringComparer.Ordinal);
        var made = new Dictionary<string, int>(StringComparer.Ordinal);

        string Of(string key) => store.Of(makings, key, () =>
        {
            made[key] = made.GetValueOrDefault(key) + 1;
            return $"{key}({Of(key == "a" ? "b" : "a")})";
        });

        Assert.Equal("not yet", Assert.Throws<InvalidOperationException>(() => Of("a")).Message);
        Assert.Equal((1, 1), (made["a"], made["b"]));
        Assert.False(store.TryKept("a", out _));
        Assert.False(store.TryKept("b", out _));

        refusing = false;
        Assert.Equal(("cycle:a(none:b)", "cycle:b(none:a)"), (Of("a"), Of("b")));
        Assert.Equal((2, 2), (made["a"], made["b"]));
    }

    /// <summary>
    /// A memo given the makings a store shares answers each key as one of their makings, by the one rule: its key and the store's
    /// key, a cycle through both, each keep their placeholders, whichever is asked first - the memo's key recorded as met again
    /// where the store's making asked for it - and nothing is answered twice.
    /// </summary>
    [Fact]
    public void AMemoGivenTheMakings_AnswersACycleThroughAStoreByTheOneRule_AndAnswersNothingTwice()
    {
        // The store asked first: its making asks the memo, whose answer asks the store back.
        {
            var makings = new Makings();
            var store = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
            var answered = 0;
            var stepped = 0;
            Memo<string, string>? memo = null;

            string Store(string key) => store.Of(makings, key, () =>
            {
                stepped++;
                return $"{key}({memo!.Of("m")})";
            });

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

            Assert.Equal("none:s", Store("s"));
            Assert.Equal((1, 1), (answered, stepped));
            Assert.Equal("none:s", Kept(store, "s"));
            Assert.Empty(memo.Cycles);

            Assert.Equal("cycle:m", memo.Of("m"));
            Assert.Equal("cycle:m", memo.Of("m"));
            Assert.Equal((1, 1), (answered, stepped));
        }

        // The memo asked first: its answer asks the store, whose making asks the memo's key again.
        {
            var makings = new Makings();
            var store = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
            var answered = 0;
            var stepped = 0;
            Memo<string, string>? memo = null;

            string Store(string key) => store.Of(makings, key, () =>
            {
                stepped++;
                return $"{key}({memo!.Of("m")})";
            });

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

            Assert.Equal("cycle:m", memo.Of("m"));
            Assert.Equal((1, 1), (answered, stepped));
            Assert.Equal(["m"], memo.Cycles);
            Assert.Equal("none:s", Kept(store, "s"));

            Assert.Equal("none:s", Store("s"));
            Assert.Equal("cycle:m", memo.Of("m"));
            Assert.Equal((1, 1), (answered, stepped));
        }
    }

    /// <summary>
    /// A memo's frame whose answer throws inside a cycle takes with it every frame made inside the one it was made in that leaned
    /// further out: none of them is kept, so a later ask answers them afresh, while the store's making outside stays open and
    /// ends with the set closed as the one rule says.
    /// </summary>
    [Fact]
    public void AMemoFrameThatThrowsInsideACycle_UnmakesWithItTheFramesThatLeanedFurtherOut_AndLeavesTheStoresMakingOutsideOpen()
    {
        var makings = new Makings();
        var store = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
        var answered = new List<string>();
        var failing = true;
        Memo<string, string>? memo = null;

        // The store's making asks the memo for m, goes on past the throw, and asks it for p again.
        string Store(string key) => store.Of(makings, key, () =>
        {
            try
            {
                return $"{key}({memo!.Of("m")},nothing thrown)";
            }
            catch (InvalidOperationException)
            {
                return $"{key}({memo!.Of("p")},caught)";
            }
        });

        // m depends on p, then q: p asks the store back and ends leaning on it; q throws, the first time.
        memo = new Memo<string, string>(
            key => key == "m" ? ["p", "q"] : [],
            (key, before) =>
            {
                answered.Add(key);
                return key switch
                {
                    "p" => $"p({Store("s")})",
                    "q" when failing => throw new InvalidOperationException("not yet"),
                    _ => $"{key}({string.Join(',', before)})",
                };
            },
            key => $"cycle:{key}",
            StringComparer.Ordinal,
            makings);

        Assert.Equal("none:s", Store("s"));
        Assert.Equal(["p", "q", "p"], answered);
        Assert.Equal("none:s", Kept(store, "s"));
        Assert.Empty(memo.Cycles);

        failing = false;
        Assert.Equal("m(cycle:p,q())", memo.Of("m"));
        Assert.Equal("cycle:p", memo.Of("p"));
        Assert.Equal(["p", "q", "p", "q", "m"], answered);
    }

    /// <summary>A making that throws leaves its key unmade and not open, so a later ask makes it afresh, never as a cycle.</summary>
    [Fact]
    public void AMakingThatThrows_LeavesItsKeyUnmade_AndNotOpen()
    {
        var makings = new Makings();
        var store = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
        var failing = true;

        string Of(string key) => store.Of(makings, key, () => key == "top"
            ? $"top({Of("below")})"
            : failing ? throw new InvalidOperationException("not yet") : "below");

        Assert.Equal("not yet", Assert.Throws<InvalidOperationException>(() => Of("top")).Message);
        Assert.False(store.TryKept("top", out _));

        failing = false;

        Assert.Equal("top(below)", Of("top"));
        Assert.Equal("top(below)", Kept(store, "top"));
    }

    /// <summary>The graph a test writes as <c>key&gt;the keys it asks, in turn;…</c>.</summary>
    /// <param name="written">The graph, one <c>key&gt;dependencies</c> a part, parted by semicolons.</param>
    private static Dictionary<string, string[]> Graph(string written)
        => written.Split(';').ToDictionary(
            asks => asks.Split('>')[0],
            asks => asks.Split('>')[1].Split(',', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

    /// <summary>Every order of <paramref name="keys"/>, each key once in each.</summary>
    /// <param name="keys">The keys.</param>
    private static IEnumerable<string[]> Orders(string[] keys)
    {
        if (keys.Length == 0)
        {
            return [[]];
        }

        return keys.SelectMany((key, at) => Orders([.. keys.Where((_, other) => other != at)]).Select(rest =>
        {
            string[] order = [key, .. rest];
            return order;
        }));
    }

    /// <summary>
    /// A store of its own over <paramref name="make"/>, asked for each key of <paramref name="order"/>: what each key of
    /// <paramref name="keys"/> is kept as, and how many times each was made.
    /// </summary>
    /// <param name="keys">The keys of the graph, whose order the answers are in.</param>
    /// <param name="order">The keys asked, in turn.</param>
    /// <param name="make">A key's answer from what it is given for the keys it asks.</param>
    private static (string[] Kept, int[] Makings) Asked(string[] keys, string[] order, Func<string, Func<string, string>, string> make)
    {
        var makings = new Makings();
        var store = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
        var made = keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);

        string Of(string key) => store.Of(makings, key, () =>
        {
            made[key]++;
            return make(key, Of);
        });

        foreach (var key in order)
        {
            Of(key);
        }

        return ([.. keys.Select(key => $"{key}={Kept(store, key)}")], [.. keys.Select(key => made[key])]);
    }

    /// <summary>
    /// A store of the graph <c>a1</c> to <c>a<paramref name="length"/></c>, each key asking the next, then the one before it, then
    /// <c>c</c>, which asks the last; and <c>o</c>, which asks <c>a1</c> to the last in turn: a cycle every key of which is asked
    /// from the key before it and the one after, on makings of the store's own.
    /// </summary>
    /// <param name="length">How many keys the cycle has.</param>
    private static (Func<string, string> Of, Dictionary<string, int> Made, MadeOnce<string, string> Store) Ring(int length)
    {
        var makings = new Makings();
        var store = new MadeOnce<string, string>(key => $"none:{key}", comparer: StringComparer.Ordinal);
        var made = Enumerable.Range(1, length).Select(at => $"a{at}").Append("c").Append("o").ToDictionary(key => key, _ => 0, StringComparer.Ordinal);

        string Of(string key) => store.Of(makings, key, () =>
        {
            made[key]++;
            List<string> asks = key switch
            {
                "o" => [.. Enumerable.Range(1, length).Select(at => $"a{at}")],
                "c" => [$"a{length}"],
                _ => [],
            };

            if (key is not ("o" or "c"))
            {
                var at = int.Parse(key[1..], CultureInfo.InvariantCulture);

                if (at < length)
                {
                    asks.Add($"a{at + 1}");
                }

                if (at > 1)
                {
                    asks.Add($"a{at - 1}");
                }

                asks.Add("c");
            }

            // Five hundred makings nest behind these asks, each on the caller's stack: the answer is built in this frame, for a
            // join over a projection would put more frames in every one of them.
            var answer = new StringBuilder().Append(key).Append('(');
            var separator = string.Empty;

            foreach (var ask in asks)
            {
                answer.Append(separator).Append(Of(ask));
                separator = ",";
            }

            return answer.Append(')').ToString();
        });

        return (Of, made, store);
    }

    /// <summary>The answer <paramref name="store"/> keeps for <paramref name="key"/>, which it must keep.</summary>
    private static TAnswer Kept<TKey, TAnswer>(MadeOnce<TKey, TAnswer> store, TKey key)
        where TKey : notnull
    {
        Assert.True(store.TryKept(key, out var answer));
        return answer;
    }
}
