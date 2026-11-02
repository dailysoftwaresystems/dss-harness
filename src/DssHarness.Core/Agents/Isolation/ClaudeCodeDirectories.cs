using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Agents.Isolation;

/// <summary>
/// Every Claude Code directory on this machine the tool knows of - each a login's credentials and the settings Claude Code
/// runs by - the one owner of which an agent's actions and the tool's own executions of its work are kept from alike:
/// Claude Code's own default, <c>.claude</c> in the home; the tool's own, <c>CLAUDE_CONFIG_DIR</c> of its process where
/// set; and the one the slot holds for the claude-code client's logins, under the name its <c>configDir</c> gives.
/// </summary>
/// <param name="fileSystem">Finds the tool's own Claude Code directory.</param>
/// <param name="platform">This machine, whose home holds Claude Code's own default.</param>
/// <param name="keys">Reads the slot; none where left out, which leaves the slot's directory unknown.</param>
/// <param name="environment">Reads this process's environment; its own where left out.</param>
public sealed class ClaudeCodeDirectories(IFileSystem fileSystem, IHostPlatform platform, AgentKeyStore? keys, Func<string, string?>? environment = null)
{
    /// <summary>The tool's own Claude Code directory on this machine: <c>CLAUDE_CONFIG_DIR</c> of its process where set, else <c>~/.claude</c>.</summary>
    public string Own => new Orchestration.ClaudeTranscripts(fileSystem, platform, environment).ConfigDirectory;

    /// <summary>
    /// Every Claude Code directory there is for <paramref name="config"/> over the repository whose main checkout is
    /// <paramref name="mainCheckout"/>, each once: the home's, the tool's own and the slot's. A slot that cannot be read
    /// names none.
    /// </summary>
    /// <param name="config">The configuration, whose providers name the client's setting.</param>
    /// <param name="mainCheckout">The main checkout, whose slot holds the setting.</param>
    public IReadOnlyList<string> Of(HarnessConfig config, string mainCheckout)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);

        var directories = new List<string> { Path.Combine(platform.HomeDirectory, ".claude"), Own };

        if (keys is not null
            && AgentProviders.Resolve(config).Providers is { } providers
            && providers.SettingOf(ClaudeCode.ClaudeCodeClient.ClientName, AgentClientSetting.ConfigDirKey) is { Length: > 0 } name
            && keys.Value(new HarnessLayout(mainCheckout, mainCheckout), providers.Appointed(), name).Value is { Length: > 0 } held
            && Path.IsPathFullyQualified(held))
        {
            directories.Add(held);
        }

        return [.. directories.Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))).Distinct(StringComparer.FromComparison(platform.PathComparison))];
    }
}
