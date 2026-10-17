using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// A process context whose current directory a test sets: the process's own until one is set, so a test that says where
/// a command was typed from needs no process directory of its own, and one that says nothing is answered exactly as the
/// real reader answers.
/// </summary>
public sealed class FixedProcessContext : IProcessContext
{
    /// <summary>The directory to answer with; the process's own where a test has set none.</summary>
    public string? Fixed { get; set; }

    /// <inheritdoc/>
    public string CurrentDirectory => Fixed ?? Environment.CurrentDirectory;
}
