using DssHarness.Core.Text;

namespace DssHarness.Tests;

/// <summary>The one diff of two sequences: runs that tile both sides, anchored on items once on each side, minimal where no anchor is, with no bound on the edits.</summary>
public sealed class SequenceDiffTests
{
    /// <summary>
    /// Over seeded random sequences of many sizes and alphabets - unrelated, and one an edit of the other - the runs tile
    /// both sides in order from their starts to their ends, none is empty, no two of a kind stand side by side, an equal
    /// run's items are equal pairwise, and an unequal run's two sides share no item: the diff accounts for every item once
    /// and leaves no match between a change's own items.
    /// </summary>
    [Fact]
    public void RunsCoverBothSides_AndEqualRunsAreEqual()
    {
        var random = new Random(20261119);
        int[] alphabets = [1, 2, 3, 8, 40, 1000];
        int[] sizes = [0, 1, 2, 5, 20, 100, 400, 2000];

        foreach (var alphabet in alphabets)
        {
            foreach (var size in sizes)
            {
                for (var round = 0; round < 12; round++)
                {
                    var before = RandomItems(random, random.Next(size + 1), alphabet);
                    var after = round % 2 == 0 ? RandomItems(random, random.Next(size + 1), alphabet) : Edited(random, before, alphabet);

                    AssertTiles(before, after, SequenceDiff.Of(before, after));
                }
            }
        }
    }

    /// <summary>
    /// A regenerated file of fifty thousand lines in which every other line changed keeps every line it kept: each found
    /// equal, as its own run between the changed ones, however many edits lie between the first change and the last - no
    /// bound on the edits takes the middle as changed whole.
    /// </summary>
    [Fact]
    public void AFiftyThousandLineRegeneration_FindsItsInterleavedUnchangedLines()
    {
        const int Lines = 50_000;
        string[] before = [.. Enumerable.Range(0, Lines).Select(line => line % 2 == 0 ? $"kept {line}" : $"old {line}")];
        string[] after = [.. Enumerable.Range(0, Lines).Select(line => line % 2 == 0 ? $"kept {line}" : $"new {line}")];
        DiffRun[] expected = [.. Enumerable.Range(0, Lines).Select(line => new DiffRun(line, 1, line, 1, line % 2 == 0))];

        Assert.Equal(expected, SequenceDiff.Of(before, after));
    }

    /// <summary>
    /// Where no item is once on each side and the ends differ, the diff is Myers' alone, and it is minimal: over small random
    /// sequences of two letters, the items it finds equal are as many as the longest common subsequence a plain dynamic
    /// program counts.
    /// </summary>
    [Fact]
    public void ARegionWithNoUniqueAnchor_IsDiffedMinimally()
    {
        var random = new Random(20261120);

        for (var round = 0; round < 500; round++)
        {
            // Each side begins and ends with its own letter, twice at least: no common end, and no letter once on both sides.
            var before = Letters(random, random.Next(2, 41), 'a');
            var after = Letters(random, random.Next(2, 41), 'b');
            var runs = SequenceDiff.Of(before, after);

            Assert.Equal(LongestCommonSubsequence(before, after), runs.Where(run => run.Equal).Sum(run => run.BeforeCount));
            AssertTiles(before, after, runs);
        }
    }

    /// <summary>
    /// An item once on each side anchors the diff even where a longer common subsequence would align repeated items
    /// instead: a line moved from the top to the bottom is told moved, not taken out and put back around braces kept.
    /// </summary>
    [Fact]
    public void AnItemOnceOnEachSide_AnchorsTheDiff()
    {
        Assert.Equal(
            [new DiffRun(0, 0, 0, 3, false), new DiffRun(0, 1, 3, 1, true), new DiffRun(1, 3, 4, 0, false)],
            SequenceDiff.Of(["unique", "}", "}", "}"], ["}", "}", "}", "unique"]));
    }

    /// <summary>Two equal sequences are one equal run.</summary>
    [Fact]
    public void ADiffOfEqualSequences_IsOneEqualRun()
    {
        Assert.Equal([new DiffRun(0, 3, 0, 3, true)], SequenceDiff.Of(["x", "y", "z"], ["x", "y", "z"]));
    }

    /// <summary>A sequence against an empty one is one unequal run of its items alone, on whichever side it stands.</summary>
    [Fact]
    public void ADiffAgainstNothing_IsOneRunOfTheOtherSide()
    {
        Assert.Equal([new DiffRun(0, 0, 0, 2, false)], SequenceDiff.Of([], ["a", "b"]));
        Assert.Equal([new DiffRun(0, 2, 0, 0, false)], SequenceDiff.Of(["a", "b"], []));
    }

    /// <summary>Two empty sequences have no run at all: there is nothing to cover.</summary>
    [Fact]
    public void TwoEmptySequences_HaveNoRun()
    {
        Assert.Empty(SequenceDiff.Of(Array.Empty<string>(), Array.Empty<string>()));
    }

    /// <summary>A comparer given decides which items are equal; without one, the type's own equality does.</summary>
    [Fact]
    public void AComparerGiven_IsTheEqualityUsed()
    {
        string[] before = ["Alpha", "beta", "x"];
        string[] after = ["ALPHA", "BETA", "y"];

        Assert.Equal(
            [new DiffRun(0, 2, 0, 2, true), new DiffRun(2, 1, 2, 1, false)],
            SequenceDiff.Of(before, after, StringComparer.OrdinalIgnoreCase));
        Assert.Equal([new DiffRun(0, 3, 0, 3, false)], SequenceDiff.Of(before, after));
    }

    /// <summary>
    /// A null item equals a null alone and is never handed to the comparer, whose hash of a null may throw, as the ordinal
    /// comparer's does.
    /// </summary>
    [Fact]
    public void ANullItem_EqualsANullAlone()
    {
        Assert.Equal(
            [new DiffRun(0, 0, 0, 1, false), new DiffRun(0, 2, 1, 2, true), new DiffRun(2, 1, 3, 0, false)],
            SequenceDiff.Of<string?>(["a", null, "b"], [null, "a", null], StringComparer.Ordinal));
    }

    /// <summary>Each item is hashed through the comparer once, however often the diff compares it: every comparison after is of numbers.</summary>
    [Fact]
    public void EachItem_IsHashedOnceThroughTheComparer()
    {
        var comparer = new CountingComparer();

        Assert.Equal(
            [new DiffRun(0, 1, 0, 0, false), new DiffRun(1, 2, 0, 2, true), new DiffRun(3, 1, 2, 1, false)],
            SequenceDiff.Of(["a", "b", "a", "c"], ["b", "a", "d"], comparer));
        Assert.Equal(7, comparer.Hashes);
    }

    /// <summary>
    /// Asserts <paramref name="runs"/> tile <paramref name="before"/> and <paramref name="after"/> in order, none empty, no
    /// two of a kind side by side, each equal run's items equal pairwise and each unequal run's two sides sharing no item.
    /// </summary>
    private static void AssertTiles<T>(T[] before, T[] after, IReadOnlyList<DiffRun> runs)
    {
        var nextBefore = 0;
        var nextAfter = 0;
        bool? previous = null;

        foreach (var run in runs)
        {
            Assert.Equal((nextBefore, nextAfter), (run.Before, run.After));
            Assert.NotEqual(previous, run.Equal);

            var older = before.Skip(run.Before).Take(run.BeforeCount).ToArray();
            var newer = after.Skip(run.After).Take(run.AfterCount).ToArray();

            if (run.Equal)
            {
                Assert.NotEqual(0, run.BeforeCount);
                Assert.Equal(run.BeforeCount, run.AfterCount);
                Assert.Equal(older, newer);
            }
            else
            {
                Assert.NotEqual(0, run.BeforeCount + run.AfterCount);
                Assert.Empty(newer.Intersect(older));
            }

            nextBefore += run.BeforeCount;
            nextAfter += run.AfterCount;
            previous = run.Equal;
        }

        Assert.Equal((before.Length, after.Length), (nextBefore, nextAfter));
    }

    /// <summary>The length of the longest common subsequence of <paramref name="before"/> and <paramref name="after"/>, by the plain dynamic program.</summary>
    private static int LongestCommonSubsequence(char[] before, char[] after)
    {
        var lengths = new int[before.Length + 1, after.Length + 1];

        for (var older = 1; older <= before.Length; older++)
        {
            for (var newer = 1; newer <= after.Length; newer++)
            {
                lengths[older, newer] = before[older - 1] == after[newer - 1]
                    ? lengths[older - 1, newer - 1] + 1
                    : Math.Max(lengths[older - 1, newer], lengths[older, newer - 1]);
            }
        }

        return lengths[before.Length, after.Length];
    }

    /// <summary><paramref name="length"/> random items of <paramref name="alphabet"/> kinds.</summary>
    private static int[] RandomItems(Random random, int length, int alphabet) => [.. Enumerable.Range(0, length).Select(_ => random.Next(alphabet))];

    /// <summary><paramref name="before"/> after a few random insertions, removals and replacements of short runs.</summary>
    private static int[] Edited(Random random, int[] before, int alphabet)
    {
        List<int> after = [.. before];

        for (var edit = random.Next(6); edit > 0; edit--)
        {
            var at = random.Next(after.Count + 1);
            var span = random.Next(1, 6);

            switch (random.Next(3))
            {
                case 0:
                    after.InsertRange(at, RandomItems(random, span, alphabet));
                    break;
                case 1:
                    after.RemoveRange(at, Math.Min(span, after.Count - at));
                    break;
                default:
                    for (var index = at; index < Math.Min(at + span, after.Count); index++)
                    {
                        after[index] = random.Next(alphabet);
                    }

                    break;
            }
        }

        return [.. after];
    }

    /// <summary><paramref name="length"/> random letters a and b, the first and the last <paramref name="ends"/>.</summary>
    private static char[] Letters(Random random, int length, char ends)
    {
        var letters = Enumerable.Range(0, length).Select(_ => random.Next(2) == 0 ? 'a' : 'b').ToArray();
        letters[0] = ends;
        letters[^1] = ends;

        return letters;
    }

    /// <summary>The ordinal comparer, counting the items it hashes.</summary>
    private sealed class CountingComparer : IEqualityComparer<string>
    {
        /// <summary>How many items were hashed.</summary>
        public int Hashes { get; private set; }

        /// <inheritdoc/>
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        /// <inheritdoc/>
        public int GetHashCode(string obj)
        {
            Hashes++;
            return StringComparer.Ordinal.GetHashCode(obj);
        }
    }
}
