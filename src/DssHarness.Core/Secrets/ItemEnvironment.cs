using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Secrets;

/// <summary>One key an item's environment file declares.</summary>
/// <param name="Name">The key.</param>
/// <param name="Purpose">What it holds, said the way a refusal asks for it.</param>
/// <param name="Fallback">
/// What the key is filled with when the file leaves it out or leaves it blank, or
/// <see langword="null"/> where it is filled with nothing.
/// </param>
/// <param name="Secret">Whether what it holds is a credential, which only its owner may read.</param>
/// <param name="Optional">
/// Whether the item may hold no such key at all, and a blank one means none: a credential a machine does not
/// need, read where it is there and passed over where it is not.
/// </param>
public sealed record ItemKey(string Name, string Purpose, string? Fallback = null, bool Secret = false, bool Optional = false)
{
    /// <summary>
    /// Whether the file must declare the key: not optional, and with no fallback to fill it. One
    /// spelling of the rule, for the reading that refuses its absence and for the message that
    /// names what to declare.
    /// </summary>
    public bool Required => Fallback is null && !Optional;
}

/// <summary>
/// The rules an item's environment file is held to: who may change it, who may read it where it
/// holds a secret of the declared kind, and which of its declared keys must be there and filled in.
/// </summary>
/// <remarks>
/// <para>
/// The permissions are checked here rather than left to whatever reads the file, because a file
/// other users can change can point a connection somewhere else, and a credential every account on
/// the machine can read has already been shared. The one who refuses says how to fix it, in its own
/// words, through the fragments this is given.
/// </para>
/// <para>
/// Each expected key says whether it is a secret: the rule reads the declared kinds, not a name
/// spelled inside it, so a second reader with a secret of another name is held to it by declaring
/// the key, not by changing the rule.
/// </para>
/// </remarks>
public sealed class ItemEnvironment(IFileSystem fileSystem, IFilePermissions filePermissions)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _filePermissions = filePermissions;

    /// <summary>
    /// The command that makes a file private, said the way this system reads one - the one owner of those words, for
    /// every reader that refuses a file other users can read or change.
    /// </summary>
    /// <param name="platform">The platform the file is read on.</param>
    /// <param name="path">The file.</param>
    public static string HowToProtect(IHostPlatform platform, string path)
        => platform.Current == PlatformId.Windows
            ? $"make it private with: icacls \"{path}\" /inheritance:r /grant:r \"%USERNAME%:F\""
            : $"make it private with: chmod 600 '{path}'";

    /// <summary>
    /// The one rule of who may reach a file of values, for every reader that holds a credential in one - an item's
    /// environment file and the agent clients' key slot alike: refused where other users can change it, before a word of
    /// it is believed, and where other users can read it while it holds a secret. Readable is as bad as writable once
    /// the file holds a secret: a credential every account on the machine can read has already been shared. Asked only
    /// where it holds one, because an address, a user name, a port or a program's path is no secret, and refusing a
    /// world-readable file that holds only those would demand a permission change for nothing.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="shown">The file as the reader spells it, which is where the one fixing it has to go.</param>
    /// <param name="protect">The command that makes the file private, for a refusal to end with.</param>
    /// <param name="changeConsequence">
    /// The second half of the refusal for a file other users can change, completing <c>other users can change 'file', </c>:
    /// the reader's to name, since it knows what the file steers.
    /// </param>
    /// <param name="heldSecret">
    /// The secret the file holds, as the refusal names it after <c>and it holds </c> - the first of the reader's declared
    /// secret kind that holds something - or null where it holds none: asked only of a file others cannot change, so a
    /// reader may read the file there and not before.
    /// </param>
    /// <returns>The refusal, or null where the file may be read.</returns>
    public string? Unprotected(string file, string shown, string protect, string changeConsequence, Func<string?> heldSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(shown);
        ArgumentException.ThrowIfNullOrWhiteSpace(protect);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeConsequence);
        ArgumentNullException.ThrowIfNull(heldSecret);

        // Whoever can change this file can point what it steers somewhere else, so it is refused before it is believed.
        if (_filePermissions.IsWritableByOthers(file))
        {
            return $"other users can change '{shown}', {changeConsequence}; {protect}";
        }

        return heldSecret() is { } secret && !_filePermissions.IsPrivate(file)
            ? $"other users can read '{shown}', and it holds {secret}; {protect}"
            : null;
    }

    /// <summary>
    /// Reads an item's environment file into its declared keys, filling in the default of every key
    /// that has one, or names the one thing to fix: the file's permissions, or a key that is absent
    /// or blank.
    /// </summary>
    /// <param name="file">The environment file.</param>
    /// <param name="shown">The file as the reader spells it, which is where the one fixing it has to go.</param>
    /// <param name="protect">The command that makes the file private, for a refusal to end with.</param>
    /// <param name="changeConsequence">
    /// The second half of the refusal for a file other users can change, completing the sentence
    /// <c>other users can change 'file', </c> - for instance <c>and whoever can change it can point
    /// this host at another machine</c>. The consequence is the reader's to name: it knows what the
    /// file steers.
    /// </param>
    /// <param name="holdsPrefix">
    /// How the refusal for a readable file holding a secret names whose it is, completing <c>and it
    /// holds </c> - for instance <c>this host's</c>. Spelled by the reader, completing the sentence
    /// with the key's own name after it.
    /// </param>
    /// <param name="expected">The keys the item declares.</param>
    public (Dictionary<string, string>? Values, string Problem) Read(
        string file,
        string shown,
        string protect,
        string changeConsequence,
        string holdsPrefix,
        IReadOnlyList<ItemKey> expected)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(shown);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(protect);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(changeConsequence);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(holdsPrefix);
        ArgumentNullException.ThrowIfNull(expected);

        Dictionary<string, string>? values = null;

        // A secret key written blank holds none - an optional one means the item declares none, as the key says - so
        // it demands nothing of who may read the file.
        var unprotected = Unprotected(file, shown, protect, changeConsequence, () =>
        {
            values = new Dictionary<string, string>(DotEnvFile.Read(_fileSystem.ReadAllText(file)), StringComparer.Ordinal);

            return expected.FirstOrDefault(key => key.Secret && values.TryGetValue(key.Name, out var held) && held.Length > 0) is { } secret
                ? $"{holdsPrefix} {secret.Name}"
                : null;
        });

        if (unprotected is not null)
        {
            return (null, unprotected);
        }

        // The file is read by then: what it holds was asked once it was known to be one others cannot change.
        values ??= new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in expected)
        {
            if (!values.TryGetValue(key.Name, out var value))
            {
                value = null;
            }

            if (value is null or { Length: 0 })
            {
                if (key.Fallback is { } fallback)
                {
                    values[key.Name] = fallback;
                }
                else if (key.Required)
                {
                    return (null, value is null
                        ? $"'{shown}' declares no '{key.Name}'; add {key.Name}=, holding {key.Purpose}"
                        : $"'{key.Name}' in '{shown}' is blank; give it {key.Purpose}");
                }
            }
        }

        return (values, string.Empty);
    }
}
