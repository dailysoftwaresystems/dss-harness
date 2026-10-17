using System.Globalization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Secrets;

/// <summary>
/// Reads the connection data of one host from its own directory under <c>.harness-config</c>, and
/// refuses before anything connects when that directory is not safe to use.
/// </summary>
/// <remarks>
/// The permissions are checked here rather than left to ssh because what ssh says about either file
/// points nowhere near it: a key other users can read it silently ignores, and a file other users can
/// change it reads anyway. Both are refused with the command that fixes them.
/// </remarks>
public interface IHostSecretsStore
{
    /// <summary>Reads the ssh item <paramref name="name"/>, or says why it cannot be used.</summary>
    /// <param name="layout">Where the repository's harness state lives.</param>
    /// <param name="name">The item's name, as <c>sshItems</c> declares it.</param>
    SecretsRead<SshItem> ReadSshItem(HarnessLayout layout, string name);

    /// <summary>Reads the WSL item <paramref name="name"/>, or says why it cannot be used.</summary>
    /// <param name="layout">Where the repository's harness state lives.</param>
    /// <param name="name">The item's name, as <c>wslDistros</c> declares it.</param>
    SecretsRead<WslItem> ReadWslItem(HarnessLayout layout, string name);

    /// <summary>The command that makes <paramref name="path"/> readable and writable by its owner alone.</summary>
    string HowToProtect(string path);
}

/// <inheritdoc cref="IHostSecretsStore"/>
public sealed class HostSecretsStore(IFileSystem fileSystem, IFilePermissions filePermissions, IHostPlatform platform)
    : IHostSecretsStore
{
    /// <summary>The key naming the machine an ssh item reaches.</summary>
    public const string AddressKey = "ADDRESS";

    /// <summary>The key naming the user ssh logs in as.</summary>
    public const string UserKey = "USER";

    /// <summary>The key naming the port ssh connects to; left out, it is <see cref="SshItem.DefaultPort"/>.</summary>
    public const string PortKey = "PORT";

    /// <summary>The key naming the distribution a WSL item reaches.</summary>
    public const string DistributionKey = "DISTRO";

    /// <summary>
    /// The key holding the superuser credential a privileged install needs. Optional: a host whose
    /// superuser needs no password declares none, and one that does is refused by name rather than
    /// attempted with an empty password, which locks an account that counts failures.
    /// </summary>
    public const string SuperuserKey = "SUDO_PASSWORD";

    /// <summary>The keys an ssh item's <c>.env</c> declares.</summary>
    private static readonly ItemKey[] SshKeys =
    [
        new(AddressKey, "the machine's address"),
        new(UserKey, "the user ssh logs in as"),
        new(PortKey, "the port ssh connects to", SshItem.DefaultPort.ToString(CultureInfo.InvariantCulture)),
        new(SuperuserKey, "the superuser credential a privileged install needs", Secret: true, Optional: true),
    ];

    /// <summary>The keys a WSL item's <c>.env</c> declares.</summary>
    private static readonly ItemKey[] WslKeys =
    [
        new(DistributionKey, "the distribution, spelt as WSL lists it"),
        new(SuperuserKey, "the superuser credential a privileged install needs", Secret: true, Optional: true),
    ];

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _filePermissions = filePermissions;
    private readonly IHostPlatform _platform = platform;

    /// <summary>The rules an item's environment file is held to, in one place.</summary>
    private readonly ItemEnvironment _environment = new(fileSystem, filePermissions);

    public SecretsRead<SshItem> ReadSshItem(HarnessLayout layout, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var directory = layout.SshItemDirectory(name);
        var keyFile = Path.Combine(directory, HarnessLayout.ItemKeyFileName);
        var knownHostsFile = Path.Combine(directory, HarnessLayout.ItemKnownHostsFileName);

        try
        {
            var (values, problem) = ReadEnvironment(layout, directory, SshKeys);

            if (values is null)
            {
                return new SecretsRead<SshItem>(null, problem);
            }

            if (Absent(layout, keyFile, "it holds the private key ssh offers this host") is { } noKey)
            {
                return new SecretsRead<SshItem>(null, noKey);
            }

            if (Absent(layout, knownHostsFile, "it holds the host keys ssh may accept for this host") is { } noHostKeys)
            {
                return new SecretsRead<SshItem>(null, noHostKeys);
            }

            // A key other users can read, ssh ignores, with a warning that says nothing about what that
            // does to the connection.
            if (!_filePermissions.IsPrivate(keyFile))
            {
                return new SecretsRead<SshItem>(
                    null,
                    $"other users can read '{Show(layout, keyFile)}', so ssh ignores it; {HowToProtect(keyFile)}");
            }

            // Whoever can change the known hosts can add a key and have another machine answer as this one.
            if (_filePermissions.IsWritableByOthers(knownHostsFile))
            {
                return new SecretsRead<SshItem>(
                    null,
                    $"other users can change '{Show(layout, knownHostsFile)}', and whoever can change it can have "
                    + $"another machine answer as this host; {HowToProtect(knownHostsFile)}");
            }

            var declaredPort = values[PortKey];

            if (!int.TryParse(declaredPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
            {
                return new SecretsRead<SshItem>(
                    null,
                    $"'{PortKey}' in '{Show(layout, EnvironmentFile(directory))}' is '{declaredPort}', which is not a "
                    + $"port number; give it a number from 1 to 65535, or leave it out for {SshItem.DefaultPort}");
            }

            return new SecretsRead<SshItem>(
                new SshItem
                {
                    Name = name,
                    Address = values[AddressKey],
                    User = values[UserKey],
                    Port = port,
                    KeyFile = keyFile,
                    KnownHostsFile = knownHostsFile,
                    Superuser = Credential(values),
                },
                string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This host's problem, not every host's: a file this user cannot read, or whose permissions
            // cannot be read, stops only the hosts that depend on it.
            return new SecretsRead<SshItem>(null, $"'{Show(layout, directory)}' could not be read: {ex.Message}");
        }
    }

    public SecretsRead<WslItem> ReadWslItem(HarnessLayout layout, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var directory = layout.WslDistroDirectory(name);

        try
        {
            var (values, problem) = ReadEnvironment(layout, directory, WslKeys);

            return values is null
                ? new SecretsRead<WslItem>(null, problem)
                : new SecretsRead<WslItem>(
                    new WslItem { Name = name, Distribution = values[DistributionKey], Superuser = Credential(values) },
                    string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SecretsRead<WslItem>(null, $"'{Show(layout, directory)}' could not be read: {ex.Message}");
        }
    }

    public string HowToProtect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return _platform.Current == PlatformId.Windows
            ? $"make it private with: icacls \"{path}\" /inheritance:r /grant:r \"%USERNAME%:F\""
            : $"make it private with: chmod 600 '{path}'";
    }

    /// <summary>
    /// Reads an item's <c>.env</c> into its declared keys, filling in the default of every key that has
    /// one, or names the one thing to fix: the directory, the file, its permissions, or the key that is
    /// absent or blank.
    /// </summary>
    private (Dictionary<string, string>? Values, string Problem) ReadEnvironment(
        HarnessLayout layout,
        string directory,
        IReadOnlyList<ItemKey> expected)
    {
        var file = EnvironmentFile(directory);
        var required = string.Join(" and ", expected.Where(key => key.Required).Select(key => key.Name));

        if (!_fileSystem.DirectoryExists(directory))
        {
            // In a copy on a host it is absent by design, not left undone: connection data is gitignored and
            // never transferred, so that a host holds no key of its own. Telling the reader to create it there
            // would have the host reach a machine over ssh - itself, where it is the host the leg names. So
            // the fact is said alone, and the connector says why, as it does for every kind of host a copy
            // cannot reach (HostConnector.SyncedCopyNotice).
            return _fileSystem.FileExists(layout.SyncedCopyMarkerFile)
                ? (null, $"'{Show(layout, directory)}' does not exist")
                : (null, $"'{Show(layout, directory)}' does not exist; create it, with a "
                    + $"'{HarnessLayout.ItemEnvFileName}' declaring {required}");
        }

        if (Absent(layout, file, $"it declares {required}") is { } noFile)
        {
            return (null, noFile);
        }

        return _environment.Read(
            file,
            Show(layout, file),
            HowToProtect(file),
            "and whoever can change it can point this host at another machine",
            "this host's",
            expected);
    }

    private string? Absent(HarnessLayout layout, string path, string purpose)
        => _fileSystem.FileExists(path) ? null : $"'{Show(layout, path)}' does not exist; {purpose}";

    private static HostCredential? Credential(IReadOnlyDictionary<string, string> values)
        => values.TryGetValue(SuperuserKey, out var value) && value.Length > 0 ? new HostCredential(value) : null;

    private static string EnvironmentFile(string directory) => Path.Combine(directory, HarnessLayout.ItemEnvFileName);

    /// <summary>A path as this repository spells it, which is where the reader has to go to fix it.</summary>
    private static string Show(HarnessLayout layout, string path)
        => Path.GetRelativePath(layout.MainCheckoutRoot, path).Replace('\\', '/');
}
