using System.Text.Json.Serialization;

namespace DssHarness.Core.Pages;

/// <summary>
/// One section of an answer: a list of entries the pages hold whole, up to <see cref="Size"/> of them a page, its
/// caption said on every page that holds entries of it - so a page read alone still names what its entries are of.
/// </summary>
/// <typeparam name="TEntry">The entries' kind.</typeparam>
/// <param name="Caption">The line above the section's entries, where one is given.</param>
/// <param name="Entries">The section's entries, in their stable order.</param>
/// <param name="Size">How much of the section a page holds, in its entries' <see cref="IPageEntry.Counts"/>.</param>
public sealed record PageSection<TEntry>([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Caption, IReadOnlyList<TEntry> Entries, int Size)
    where TEntry : IPageEntry
{
    /// <summary>What is wrong with the section; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Size < 1 ? "a section's size is at least one"
            : Caption is { } caption && (caption.Length == 0 || caption.Contains('\n') || caption.Contains('\r')) ? "a section's caption is one line, and never empty"
            : Entries is null || Entries.Any(entry => entry is null) ? "a section holds a list of entries, none of them null"
            : Entries.Select(entry => entry.Problem()).FirstOrDefault(problem => problem is not null);
}

/// <summary>What one page gave of one section: its caption, and the entries - or the pieces of one - the page holds of it.</summary>
/// <typeparam name="TEntry">The entries' kind.</typeparam>
/// <param name="Caption">The section's caption, where it has one.</param>
/// <param name="Entries">What the page gave of the section, in order.</param>
public sealed record PageOfSection<TEntry>(string? Caption, IReadOnlyList<TEntry> Entries)
    where TEntry : IPageEntry;

/// <summary>An answer asked as its pages give it.</summary>
/// <typeparam name="TEntry">The entries' kind.</typeparam>
/// <param name="Text">The answer for the page asked, as the model reads it.</param>
/// <param name="Page">The page given.</param>
/// <param name="Pages">How many pages the answer holds; one where all of it was given at once.</param>
/// <param name="Total">
/// How many results the whole answer holds, counted once, never estimated: a count that may pass what an int holds, as a
/// text's lines may.
/// </param>
/// <param name="Sections">What the page gave of each section, in the answer's order: none for a page refused.</param>
/// <param name="Note">
/// What the page's header says after its counts, and a refused page after its range: why the page was made again rather
/// than read from what page 1 kept, or that its result's file could not be written; null where there is nothing to say.
/// </param>
public sealed record PageAnswer<TEntry>(string Text, int Page, int Pages, long Total, IReadOnlyList<PageOfSection<TEntry>> Sections, string? Note)
    where TEntry : IPageEntry
{
    /// <summary>Why the page asked is none of the answer, where it is not; <see langword="null"/> where it is.</summary>
    public string? Refusal { get; init; }

    /// <summary>The entries - and the pieces of entries - the page gave, every section's in order.</summary>
    public IReadOnlyList<TEntry> Entries => [.. Sections.SelectMany(section => section.Entries)];
}
