using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    /// <summary>
    /// Writes the codebase-graph topic's part on how each language is read: the line that opens it, then each
    /// implementation this build holds - its language, its version and what it claims, what it reads and what it does
    /// not see. Each implementation's own Help is what it prints, so what the topic says of a language is what its
    /// reader says of itself.
    /// </summary>
    /// <param name="builder">The topic's text.</param>
    /// <param name="extractors">The implementations this build holds.</param>
    private static void AppendLanguageReading(StringBuilder builder, CodeGraphExtractors extractors)
    {
        AppendWrapped(
            builder,
            "The implementations this build holds, each with what it reads and what it does not see. Each reads its file's "
            + "text by position, a block at a time, never as one string of the whole file: a file of any size is read, and "
            + "what reading it holds is what its reader keeps - its tokens, its nodes - and, where a library it reads through "
            + "keeps more, what that library holds:");
        builder.AppendLine();

        foreach (var extractor in extractors.All)
        {
            var claims = extractor.Claims.Fallback
                ? "every file no other claims"
                : string.Join(", ", extractor.Claims.Names.Concat(extractor.Claims.Extensions).Select(claim => claim.Value + (claim.ContentTest is null ? string.Empty : " (by a test of its text)")));

            builder.AppendLine($"  {extractor.Language} (version {extractor.Version.ToString(CultureInfo.InvariantCulture)}; {claims})");
            AppendWrapped(builder, $"    reads {extractor.Help.Reads}.");

            if (extractor.Help.Holds is { } holds)
            {
                AppendWrapped(builder, $"    holds {holds}.");
            }

            foreach (var limit in extractor.Help.DoesNotSee)
            {
                AppendWrapped(builder, $"    does not see {limit.Says}.");
            }

            builder.AppendLine();
        }
    }
}
