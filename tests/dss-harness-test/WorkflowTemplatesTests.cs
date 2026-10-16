using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The default workflow this build ships: embedded whole under the workflow's own layout, read back and held to every
/// rule a repository's is, and written by init only where the repository has no workflows directory at all.
/// </summary>
public sealed class WorkflowTemplatesTests
{
    /// <summary>
    /// This build's own default workflow holds: parsed and validated against every rule a repository's is held to -
    /// a build whose own workflow does not hold is a build that says so, never one that writes a definition it refuses.
    /// </summary>
    [Fact]
    public void ThisBuildsOwnDefaultWorkflow_HoldsAgainstEveryRule()
    {
        var definition = WorkflowTemplates.DefaultDefinition();

        Assert.Equal(WorkflowTemplates.DefaultWorkflowName, definition.Name);
        Assert.Equal(94, Count(definition.Steps));
        Assert.Equal(31, Count(definition.Steps, "reason"));
        Assert.Equal(5, definition.Inputs.Count);
    }

    /// <summary>
    /// The embedded set is whole: every file the templates declare is in the assembly, and every file the assembly
    /// carries under the workflow's root is one the templates declare - no orphan the definition cannot name, no named
    /// file the build lacks.
    /// </summary>
    [Fact]
    public void TheEmbeddedSet_IsWholeAndNoMore()
    {
        var carried = DirectoryDeclarations();

        Assert.Equal(
            [.. WorkflowTemplates.Files.Order(StringComparer.Ordinal)],
            [.. carried.Order(StringComparer.Ordinal)]);

        return;

        static IReadOnlyList<string> DirectoryDeclarations()
        {
            // The names this assembly carries, read back as EnsureDefault writes them: the workflow's
            // own files below its name, with '/' whatever separator the building machine used.
            var prefix = typeof(WorkflowTemplates).Assembly.GetName().Name + ".WorkflowTemplates/";

            return
            [
                .. typeof(WorkflowTemplates).Assembly.GetManifestResourceNames()
                    .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(BelowTheWorkflowsName)
                    .Select(name => name.Replace('\\', '/')),
            ];

            static string BelowTheWorkflowsName(string resource)
            {
                var below = resource[(resource.LastIndexOf(".WorkflowTemplates/", StringComparison.Ordinal) + ".WorkflowTemplates/".Length)..];
                var cut = below.IndexOfAny(['/', '\\']);

                return cut < 0 ? below : below[(cut + 1)..];
            }
        }
    }

    /// <summary>
    /// The default workflow is written where the repository has no workflows directory at all, whole, and nothing is
    /// written where it has one: a repository that has made its own arrangement keeps it, whatever it is.
    /// </summary>
    [Fact]
    public void TheDefaultWorkflow_IsWrittenOnce_WhereTheRepositoryHasNone_AndNeverIntoOneItHas()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var workflows = temp.Combine("tree", ".harness-config", "workflows");

        var written = WorkflowTemplates.EnsureDefault(workflows, fileSystem);

        Assert.Equal([.. WorkflowTemplates.Files.Select(file => $".harness-config/workflows/default/{file}")], written);
        Assert.Equal(WorkflowTemplates.Files.Count, Directory.GetFiles(workflows, "*", SearchOption.AllDirectories).Length);
        Assert.Contains("name: default", File.ReadAllText(Path.Combine(workflows, "default", "default.yml")), StringComparison.Ordinal);

        // A repository's own arrangement, however empty, is never written into.
        using var kept = new TempDirectory();
        var arranged = kept.Combine("tree", ".harness-config", "workflows");
        Directory.CreateDirectory(arranged);

        Assert.Empty(WorkflowTemplates.EnsureDefault(arranged, fileSystem));
        Assert.Empty(Directory.GetFiles(arranged, "*", SearchOption.AllDirectories));
    }

    private static int Count(IReadOnlyList<WorkflowStep> steps, string? type = null)
        => steps.Count(step => type is null || step.Type == type)
            + steps.Where(step => step.Steps.Count > 0).Sum(step => Count(step.Steps, type));
}
