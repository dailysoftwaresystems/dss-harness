using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What a workflow's name may be, and where the file it names lives.
/// </summary>
/// <remarks>
/// <para>
/// One directory per workflow, <c>workflows/&lt;name&gt;/&lt;name&gt;.yml</c>, as an action owns one
/// directory of its own: a workflow's directory holds every file its steps name - its agent files,
/// its prompts, its schemas, its bar - so two workflows' files never sit side by side with nothing
/// saying which belonged to which.
/// </para>
/// <para>
/// The rules are split in two on purpose, as <see cref="Runners.ActionPath"/> splits them:
/// <see cref="Problem"/> reads a spelling and nothing else, so the configuration reader applies it
/// with no file system at hand; what only the file system knows - that the file is there, and that
/// nothing it names leads out of the workflow's own directory - is checked by the validation
/// service, which reads files.
/// </para>
/// </remarks>
public static class WorkflowName
{
    /// <summary>
    /// What is wrong with <paramref name="name"/> as a workflow's name, or <see langword="null"/>
    /// when nothing is.
    /// </summary>
    /// <param name="name">A name under <c>workflows</c> in <c>config.json</c>.</param>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"'{name}' is empty; a workflow is named in one word, as '{Expected("<name>")}'";
        }

        if (PlatformPaths.IsRootedOnAnyPlatform(name))
        {
            return $"'{name}' is an absolute path; a workflow is named in one word, as '{Expected("<name>")}'";
        }

        var segments = name.Split('/', '\\');

        if (segments.Length > 1)
        {
            return $"'{name}' holds a path separator; a workflow is named in one word, as "
                + $"'{Expected("<name>")}', and its files live in its directory of that name";
        }

        if (name.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            return $"'{name}' holds a character no file name can carry on every platform this tool "
                + $"runs on; a workflow is named in one word, as '{Expected("<name>")}'";
        }

        // The workflows directory keeps this name for the agent clients' key slot, beside the
        // workflows' own directories (HarnessLayout.WorkflowsEnvSlotName): a slot, never a workflow's
        // files. Said for itself, ahead of the rule on a leading dot that refuses it as well, so the
        // reason given is the one that holds.
        if (string.Equals(name, HarnessLayout.WorkflowsEnvSlotName, StringComparison.Ordinal))
        {
            return $"'{name}' is the name the workflows directory keeps for the slot the run's keys "
                + "are read from; call the workflow something else, as "
                + $"'{Expected("<name>")}'";
        }

        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)
            || name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith(" ", StringComparison.Ordinal))
        {
            return $"'{name}' starts or ends with a dot or a space; a workflow is named in one word, "
                + $"as '{Expected("<name>")}'";
        }

        return null;
    }

    /// <summary>
    /// How a workflow named <paramref name="name"/> is spelled, as a refusal shows it.
    /// </summary>
    /// <param name="name">The workflow's name.</param>
    public static string Expected(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return $"{HarnessLayout.WorkflowsDirectoryRelative}/{name}/{name}{HarnessLayout.WorkflowFileExtension}";
    }
}
