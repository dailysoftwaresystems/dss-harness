using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Hosts;

/// <summary>What looking for a developer environment on a host found.</summary>
/// <remarks>
/// Three facts, kept apart as a program's are: one looked for and found, one looked for and not
/// there, and one the host could not look for. A look that failed must not read as one that looked
/// and found nothing: that sent a reader to reinstall Visual Studio over an installer that timed out.
/// </remarks>
public enum DeveloperEnvironmentFound
{
    /// <summary>An instance with what it requires is there: the one that would be set up.</summary>
    Installed,

    /// <summary>The host looked, and has none: what a leg starting in it needs is not installed there.</summary>
    Nowhere,

    /// <summary>
    /// The host could not look - its installer's vswhere would not start, did not answer, or answered
    /// in a form this build cannot read - so nothing is known either way, and no refusal may call it
    /// missing.
    /// </summary>
    Unreadable,
}

/// <summary>Whether a developer environment can be set up on a host, and which instance would be.</summary>
/// <param name="Found">What looking for it found.</param>
/// <param name="Reason">Why it cannot be set up there, naming what was looked for, when it cannot.</param>
/// <param name="InstallationPath">The instance that would be set up, when there is one.</param>
/// <param name="InstallationVersion">That instance's version, as its installer reports it.</param>
public sealed record DeveloperEnvironmentCheck(DeveloperEnvironmentFound Found, string? Reason, string? InstallationPath, string? InstallationVersion)
{
    /// <summary>
    /// The MSVC toolset of that instance that would be set up, as its <c>VC\Tools\MSVC</c> names it - the newest there the
    /// environment's <c>toolsVersion</c> names - or <see langword="null"/> where the environment names none, and the toolset
    /// is the one vcvarsall.bat sets up by default.
    /// </summary>
    /// <remarks>
    /// Carried with the instance, so the toolset set up is the one the survey found and placed the leg by, exactly as the
    /// instance is.
    /// </remarks>
    public string? ToolsVersion { get; init; }

    /// <summary>The instance that would be set up there.</summary>
    /// <param name="installationPath">Where it is.</param>
    /// <param name="installationVersion">Its version, as its installer reports it.</param>
    /// <param name="toolsVersion">
    /// The toolset of it that would be set up, as <c>VC\Tools\MSVC</c> names it, where the environment names one.
    /// </param>
    public static DeveloperEnvironmentCheck Installed(string installationPath, string? installationVersion, string? toolsVersion = null)
        => new(DeveloperEnvironmentFound.Installed, null, installationPath, installationVersion) { ToolsVersion = toolsVersion };

    /// <summary>None there, and why, naming what was looked for.</summary>
    /// <param name="reason">Why, naming what was looked for.</param>
    public static DeveloperEnvironmentCheck Nowhere(string reason) => new(DeveloperEnvironmentFound.Nowhere, reason, null, null);

    /// <summary>Whether one is there could not be established, and why.</summary>
    /// <param name="reason">Why the host could not look.</param>
    public static DeveloperEnvironmentCheck Unreadable(string reason) => new(DeveloperEnvironmentFound.Unreadable, reason, null, null);

    /// <summary>
    /// Whether it can be set up there: installed, and the instance named. What placing a leg and
    /// setting it up both ask, so a check one would take the other cannot refuse.
    /// </summary>
    [JsonIgnore]
    public bool CanSetUp => Found == DeveloperEnvironmentFound.Installed && InstallationPath is { Length: > 0 };
}

/// <summary>
/// Finds, on the machine it runs on, the instance a developer environment would be set up from:
/// for Visual Studio, the newest instance holding the component it requires, as the installer's own
/// <c>vswhere</c> reports it - and, where the environment names an MSVC toolset, the newest of those
/// holding one it names, with that toolset.
/// </summary>
/// <remarks>
/// A look, and nothing set up: the survey asks it of every host a leg naming one might land on, and
/// a host without it turns that leg away as missing a tool, naming what was looked for - never a
/// build that fails halfway for want of <c>cl</c>, or one a compiler refuses for the toolset it found.
/// </remarks>
public sealed class DeveloperEnvironmentProbe(IHostPlatform platform, IProcessRunner processRunner, IFileSystem fileSystem)
{
    /// <summary>Longest vswhere may take.</summary>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(60);

    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>
    /// Where Visual Studio's installer keeps vswhere: the one place it is always installed, since
    /// Visual Studio 2017, whatever else is on the PATH.
    /// </summary>
    public static string VsWherePath => Path.Combine(
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programs ? programs : @"C:\Program Files (x86)",
        "Microsoft Visual Studio",
        "Installer",
        "vswhere.exe");

    /// <summary>Checks <paramref name="environment"/> on this machine.</summary>
    /// <param name="environment">The developer environment.</param>
    /// <param name="cancellationToken">Stops the look.</param>
    public async Task<DeveloperEnvironmentCheck> CheckAsync(DeveloperEnvironmentConfig environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!string.Equals(environment.Kind, DeveloperEnvironmentKinds.VisualStudio, StringComparison.OrdinalIgnoreCase))
        {
            return DeveloperEnvironmentCheck.Nowhere($"this build does not know how to set up a '{environment.Kind}' environment");
        }

        if (!string.Equals(_platform.PlatformKey, PlatformNames.Windows, StringComparison.Ordinal))
        {
            return DeveloperEnvironmentCheck.Nowhere($"Visual Studio is set up on windows, and this host runs {_platform.PlatformKey}");
        }

        var vswhere = VsWherePath;
        ProcessResult result;

        try
        {
            // JSON rather than one property, so the instance and its version come from one answer;
            // UTF-8, so a path the console's code page cannot spell arrives whole. The newest alone
            // where any instance with the component will do; every one of them, newest first - the
            // order -latest takes its one from - where the toolset decides which.
            result = await _processRunner
                .RunAsync(
                    new ProcessRequest
                    {
                        FileName = vswhere,
                        Arguments = [environment.ToolsVersion is null ? "-latest" : "-sort", "-products", "*", "-requires", environment.RequiresComponent, "-format", "json", "-utf8"],
                        Timeout = ProbeBudget,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExecutableNotFoundException)
        {
            return DeveloperEnvironmentCheck.Nowhere($"Visual Studio's installer is not there: '{vswhere}' was not found");
        }
        catch (ProgramStartException ex)
        {
            return DeveloperEnvironmentCheck.Unreadable($"'{vswhere}' could not be started: {ex.Message}");
        }

        if (result.TimedOut || result.ExitCode != 0)
        {
            return DeveloperEnvironmentCheck.Unreadable(HostProbes.Failure($"'{vswhere}' could not list the Visual Studio instances", result));
        }

        return Read(result.StandardOutput, environment);
    }

    /// <summary>
    /// The instance vswhere's JSON answer names that <paramref name="environment"/> would be set up from, or why
    /// none would be: its first, where the environment names no toolset; otherwise the first, in vswhere's order,
    /// whose <c>VC\Tools\MSVC</c> holds a toolset the environment names, with the newest of those.
    /// </summary>
    /// <param name="json">What vswhere printed.</param>
    /// <param name="environment">The environment it was asked about, whose component and toolset a refusal names.</param>
    internal DeveloperEnvironmentCheck Read(string json, DeveloperEnvironmentConfig environment)
    {
        IReadOnlyList<(string Path, string? Version)> instances;

        try
        {
            instances = Instances(json);
        }
        catch (JsonException ex)
        {
            return DeveloperEnvironmentCheck.Unreadable($"vswhere answered in a form this build cannot read: {ex.Message}");
        }

        if (instances.Count == 0)
        {
            return DeveloperEnvironmentCheck.Nowhere($"no Visual Studio instance there has the component '{environment.RequiresComponent}'");
        }

        if (environment.ToolsVersion is not { } toolsVersion)
        {
            return DeveloperEnvironmentCheck.Installed(instances[0].Path, instances[0].Version);
        }

        var held = new List<string>();

        foreach (var (path, version) in instances)
        {
            var directory = Path.Combine(path, "VC", "Tools", "MSVC");
            IReadOnlyList<string> toolsets;

            try
            {
                toolsets = VisualStudioToolsets.Of(_fileSystem.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>());
            }
            catch (DirectoryNotFoundException)
            {
                // An instance with the component and no toolset at all, or one removed since vswhere listed it: it holds
                // none, which is a fact about it rather than a look that failed.
                toolsets = [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not known either way: an older instance chosen past this one might be the wrong one.
                return DeveloperEnvironmentCheck.Unreadable($"the MSVC toolsets in '{directory}' could not be listed: {ex.Message}");
            }

            if (VisualStudioToolsets.Newest(toolsVersion, toolsets) is { } toolset)
            {
                return DeveloperEnvironmentCheck.Installed(path, version, toolset);
            }

            held.Add($"'{path}' holds {(toolsets.Count == 0 ? "none" : string.Join(", ", toolsets))}");
        }

        return DeveloperEnvironmentCheck.Nowhere(
            $"no Visual Studio instance there with the component '{environment.RequiresComponent}' holds MSVC toolset {toolsVersion}, "
            + $"which toolsVersion names: {string.Join("; ", held)}");
    }

    /// <summary>
    /// The instances vswhere's JSON answer names, in its order, each with its version where it gives one: every entry of
    /// its array naming a path, and none where it is not an array.
    /// </summary>
    /// <param name="json">What vswhere printed.</param>
    /// <exception cref="JsonException">It is not JSON.</exception>
    private static List<(string Path, string? Version)> Instances(string json)
    {
        using var document = JsonDocument.Parse(json);
        var instances = new List<(string Path, string? Version)>();

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return instances;
        }

        foreach (var instance in document.RootElement.EnumerateArray())
        {
            if (instance.ValueKind == JsonValueKind.Object
                && instance.TryGetProperty("installationPath", out var path)
                && path.ValueKind == JsonValueKind.String
                && path.GetString() is { Length: > 0 } installationPath)
            {
                var version = instance.TryGetProperty("installationVersion", out var declared) && declared.ValueKind == JsonValueKind.String
                    ? declared.GetString()
                    : null;

                instances.Add((installationPath, version));
            }
        }

        return instances;
    }
}
