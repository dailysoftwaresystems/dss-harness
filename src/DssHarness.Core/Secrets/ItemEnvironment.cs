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

        // Whoever can change this file can point the connection somewhere else, so it is refused
        // before it is believed.
        if (_filePermissions.IsWritableByOthers(file))
        {
            return (null, $"other users can change '{shown}', {changeConsequence}; {protect}");
        }

        var values = new Dictionary<string, string>(
            DotEnvFile.Read(_fileSystem.ReadAllText(file)),
            StringComparer.Ordinal);

        // Readable is as bad as writable once this file holds a secret: a credential every account
        // on the machine can read has already been shared. Asked only of the keys declared secret
        // and only where the file holds one, because an address, a user name and a port are not
        // secrets, and refusing a world-readable file that holds only those would demand a
        // permission change for nothing.
        foreach (var key in expected.Where(key => key.Secret))
        {
            if (values.ContainsKey(key.Name) && !_filePermissions.IsPrivate(file))
            {
                return (null, $"other users can read '{shown}', and it holds {holdsPrefix} {key.Name}; {protect}");
            }
        }

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
