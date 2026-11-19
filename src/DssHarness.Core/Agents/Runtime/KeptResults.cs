using System.Collections.Concurrent;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The whole results of a conversation's calls, a page at a time: what <c>read_result</c> reads. A call's whole result is
/// the file beside the stream its records keep it in (<see cref="AgentCallRecords.ResultFile"/>) - an answer too large for
/// its event, a command's log - and its pages are laid from that file once (<see cref="TextPages"/>), not numbered, at the
/// room of a tool's answer to a model, and kept for the conversation's life under the call's own key
/// (<see cref="PageKey.OfCall"/>) in the conversation's kept answers, the step's <c>graph-pages</c> directory, which is put
/// away when the conversation ends - so every page of a result, a resumed conversation's among them, is read by seeking to
/// its mark. A page asked of a file that changed since its pages were laid is laid again, and says so
/// (<see cref="TextPages.Relaid"/>).
/// </summary>
/// <remarks>
/// A file's digest is held by how it stands (<see cref="FileStanding"/>): it is laid again - which hashes it, in the one
/// pass that lays it - only where its standing changed since it was last laid, never hashed whole for each page asked.
/// </remarks>
/// <param name="fileSystem">The file system the results are read through.</param>
/// <param name="stepDirectory">The step's directory, whose <c>tool-results</c> hold the conversation's whole results.</param>
/// <param name="pages">The conversation's kept answers, where each result's pages are kept.</param>
public sealed class KeptResults(IFileSystem fileSystem, string stepDirectory, KeptAnswers<TextPagesKept> pages)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly string _stepDirectory = stepDirectory ?? throw new ArgumentNullException(nameof(stepDirectory));
    private readonly KeptAnswers<TextPagesKept> _pages = pages ?? throw new ArgumentNullException(nameof(pages));

    /// <summary>How each call's result stood when its pages were last laid, and the digest they were laid at, by the call's id.</summary>
    private readonly ConcurrentDictionary<string, Laid> _laid = new(StringComparer.Ordinal);

    /// <summary>The characters a page of a result gives its lines: a tool's answer to a model, less what its header and its notes take.</summary>
    public static int Room { get; } = PageRoom.For(PageRoom.ModelAnswer, []);

    /// <summary>
    /// The whole results of the conversation whose step records are in <paramref name="stepDirectory"/>, their pages kept
    /// in its kept answers (<see cref="OrchestratorLayout.StepPagesDirectoryName"/>): the directory the engine puts away
    /// when the conversation ends, beside the pages its graph answers keep, under keys none of them holds.
    /// </summary>
    /// <param name="fileSystem">The file system the results are read through.</param>
    /// <param name="stepDirectory">The step's directory.</param>
    public static KeptResults Of(IFileSystem fileSystem, string stepDirectory)
    {
        ArgumentNullException.ThrowIfNull(stepDirectory);

        return new KeptResults(
            fileSystem,
            stepDirectory,
            new KeptAnswers<TextPagesKept>(Path.Combine(stepDirectory, OrchestratorLayout.StepPagesDirectoryName), fileSystem, TextPagesKept.Contract, new TextPagesFreshness()));
    }

    /// <summary>What <c>read_result</c> is refused with for an id no result is kept for.</summary>
    /// <param name="id">The id asked.</param>
    public static string NoneKept(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return $"no result is kept for '{ReportText.Printable(id)}' in this conversation";
    }

    /// <summary>
    /// The <paramref name="page"/> of the whole result of the call <paramref name="id"/>: from the pages kept where the
    /// file is the one they were laid on, laid again - and said so, where pages laid on the file as it was are kept - where
    /// it is not; null where no result is kept for the id.
    /// </summary>
    /// <param name="id">The call's id, as its client named it.</param>
    /// <param name="page">The page asked, from 1: one past what an int counts is past the last, said by its own number.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The result's file does not read as text.</exception>
    public async Task<PageAnswer<TextEntry>?> ReadAsync(string id, long page, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        var file = AgentCallRecords.ResultFile(_stepDirectory, id);

        if (_fileSystem.StandingOf(file) is not { } standing)
        {
            return null;
        }

        var key = PageKey.OfCall(id);
        string? note = null;

        if (!(_laid.TryGetValue(id, out var laid) && laid.Standing == standing && _pages.Find(key, laid.Digest) is { Kept: { } kept } && kept.Path == file))
        {
            // Laid again, which reads the file once: pages kept on the same bytes - by a resumed conversation - are the
            // pages its earlier ones were given from, and pages kept on other bytes say the text changed.
            var fresh = await TextPages.LayAsync(_fileSystem, file, key, $"read_result {ReportText.Printable(id)}", Room, numbered: false, cancellationToken).ConfigureAwait(false);
            var found = _pages.Find(key, fresh.Digest);

            if (found.Kept is { } same && same.Path == file)
            {
                kept = same;
            }
            else
            {
                // Kept for a resume; where the disk refuses it, the page that laid them says so, and they are held here.
                kept = fresh;
                note = found.Moved ? TextPages.Relaid : null;

                if (_pages.Keep(fresh) is { } unkept)
                {
                    note = note is null ? TextPages.Unkept(unkept) : $"{note} · {TextPages.Unkept(unkept)}";
                }
            }

            _laid[id] = new Laid(standing, fresh.Digest);
        }

        return await TextPages.PageAsync(_fileSystem, kept, page, numbered: false, note, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How a result's file stood when its pages were laid, and the digest of the bytes they were laid on.</summary>
    private sealed record Laid(FileStanding Standing, string Digest);
}
