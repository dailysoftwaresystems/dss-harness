using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>The fallback, held to the contract's kit: every file no other implementation claims is listed, nothing in it read.</summary>
public sealed class ListedExtractorTests : CodeGraphExtractorContractTests<ListedExtractor>
{
    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("LICENSE", "Permission is granted.\n\n# not read\n"),
        new("assets/notes.txt", "class Looks { void Like(); }\n"),
    ];

    /// <summary>Whatever a file holds, the fallback declares nothing in it: it is a node, and an agent reads it whole.</summary>
    [Fact]
    public void WhateverAFileHolds_NothingInItIsDeclared()
    {
        Assert.All(Fixtures, fixture => Assert.Same(FileExtract.Empty, Extractor.Extract(new CodeFileInput(fixture.Path, fixture.Text))));
        Assert.True(Extractor.Claims.Fallback);
        Assert.False(Extractor.Outlines);
    }
}
