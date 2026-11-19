using System.Diagnostics;
using System.Text;

namespace DssHarness.Tests;

/// <summary>
/// A long history made at once: versions of one file committed onto a repository's branch through git's own importer, a
/// process for them all, so a scene of hundreds of commits costs no more than one.
/// </summary>
internal static class GitImport
{
    /// <summary>
    /// Commits <paramref name="count"/> versions of <paramref name="path"/> onto the branch of <paramref name="repository"/>,
    /// the first on its HEAD, each holding what <paramref name="content"/> gives for its number from 1, and puts the work
    /// tree's files as its HEAD then holds them.
    /// </summary>
    /// <param name="repository">The work tree's root.</param>
    /// <param name="path">The file, from the root, with forward separators.</param>
    /// <param name="count">How many commits are made.</param>
    /// <param name="content">The file's text in each commit, given its number.</param>
    /// <param name="cancellationToken">Stops the import.</param>
    public static async Task CommitManyAsync(string repository, string path, int count, Func<int, string> content, CancellationToken cancellationToken)
    {
        var harness = new HarnessFactory();
        var head = (await harness.RunGitAsync(repository, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
        var branch = (await harness.RunGitAsync(repository, ["symbolic-ref", "HEAD"], cancellationToken)).StandardOutput.Trim();
        var stream = new StringBuilder();

        for (var at = 1; at <= count; at++)
        {
            var bytes = Encoding.UTF8.GetByteCount(content(at));
            stream.Append($"commit {branch}\ncommitter Test <test@example.com> {1_700_000_000 + at} +0000\ndata 1\nv\n");

            if (at == 1)
            {
                stream.Append($"from {head}\n");
            }

            stream.Append($"M 100644 inline {path}\ndata {bytes}\n{content(at)}\n");
        }

        var import = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var argument in new[] { "-C", repository, "fast-import", "--quiet" })
        {
            import.ArgumentList.Add(argument);
        }

        using var process = Process.Start(import)!;
        await process.StandardInput.WriteAsync(stream.ToString());
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        Assert.True(process.ExitCode == 0, $"git fast-import failed: {error}");
        await harness.RunGitAsync(repository, ["reset", "--hard", "--quiet"], cancellationToken);
    }
}
