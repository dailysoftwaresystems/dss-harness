using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Secrets;

/// <summary>One name the configuration appoints a value of the slot for, with what it holds and who appoints it.</summary>
/// <param name="Name">The variable's name, as the slot holds it.</param>
/// <param name="By">Who appoints it, as a refusal names them - for instance <c>provider 'zai'</c>.</param>
/// <param name="What">What its value is, as a refusal asks for it - for instance <c>the Z.AI API key</c>.</param>
/// <param name="Secret">Whether what it holds is a credential, which only its owner may read; the default, for every key.</param>
public sealed record AppointedKey(string Name, string By, string What, bool Secret = true);

/// <summary>What reading the slot found: the names it holds, each with the file that declares it - and never a value.</summary>
/// <param name="Files">Every file of the slot, in the order they are read.</param>
/// <param name="Names">The names the slot declares holding something, in the order their files were read.</param>
/// <param name="Origins">The file each name was declared in.</param>
/// <param name="Problems">Every problem the files have, in the order they were found; empty where they read.</param>
public sealed record AgentSlotPresent(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Names,
    IReadOnlyDictionary<string, string> Origins,
    IReadOnlyList<string> Problems)
{
    /// <summary>Whether a name is declared there, holding something.</summary>
    public bool Holds(string name) => Names.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One key asked of the slot: its value, or the one thing to fix.</summary>
/// <param name="Value">The key's value, where the slot declares one that holds something; null otherwise.</param>
/// <param name="Problem">The refusal where the slot does not answer the key: its own sentence, never a value; null where it answers.</param>
public sealed record AgentKeyAnswer(string? Value, string? Problem);

/// <summary>
/// The agent clients' key slot, beside the host items' own store: the names the configuration appoints are read from
/// the git-ignored directory of dotenv files <c>.harness-config/workflows/.env/</c> - every file merged by the one
/// rule for such a directory - and from nowhere else. No key or setting is ever read from the process environment.
/// </summary>
/// <remarks>
/// <para>
/// What a name holds never leaves through <see cref="Present"/>: a listing asks which names are there, and the answer
/// holds names and files alone, so nothing the tool writes for a person to read could carry a value. A value is
/// answered only to the one that runs with it, through <see cref="Value"/>, which also asks who may read the file it
/// was declared in - the item rules of <see cref="ItemEnvironment"/>, asked here of a directory the same way, in the
/// same words through <see cref="ItemEnvironment.HowToProtect"/>.
/// </para>
/// <para>
/// A name the slot holds that nothing appoints is not refused: the slot is the main checkout's and serves every tree,
/// and a branch whose configuration appoints one name more must not stop the others. A missing key is refused by the
/// one that needs the value, with the sentence <see cref="Missing"/> builds - the one builder of it, asked by name or
/// by a provider's whole list.
/// </para>
/// </remarks>
public sealed class AgentKeyStore(IFileSystem fileSystem, IFilePermissions filePermissions, IHostPlatform platform)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _filePermissions = filePermissions;
    private readonly IHostPlatform _platform = platform;

    /// <summary>What a provider's key holds, as a refusal asks for it: the company's own name, whose API it is.</summary>
    public static string WhatHolds(AgentProviderConfig provider)
        => provider.Type switch
        {
            "anthropic" => "the Anthropic API key",
            "zai" => "the Z.AI API key",
            _ => $"a key of '{provider.Name}'",
        };

    /// <summary>
    /// Which names the slot declares and which file declares each, with every problem its files have: never a value.
    /// The problems are the reader's own to report, together, in the order they were found.
    /// </summary>
    /// <param name="layout">The repository's layout: the slot is the main checkout's, so worktrees share it.</param>
    public AgentSlotPresent Present(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var directories = new DotEnvDirectories(
            _fileSystem,
            (name, first, again) => $"'{name}' is declared in '{first}' and in '{again}': a name is declared once",
            skipFile: HarnessLayout.GitKeepFileName);
        var declared = directories.Read(layout.WorkflowsEnvSlot);

        // The slot's own placeholder - the one file of it git tracks - declares nothing and is not a file of it. A name
        // written blank holds none, as it does where the value is read: a listing that called it present would say a
        // machine had a key it has not.
        IReadOnlyList<string> files = _fileSystem.DirectoryExists(layout.WorkflowsEnvSlot)
            ? [.. _fileSystem.EnumerateFiles(layout.WorkflowsEnvSlot, recursive: false)
                .Where(file => !string.Equals(Path.GetFileName(file), HarnessLayout.GitKeepFileName, StringComparison.Ordinal))
                .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal)]
            : [];

        return new AgentSlotPresent(
            files,
            [.. declared.Where(pair => pair.Value.Length > 0).Select(pair => pair.Key)],
            directories.Origins,
            directories.Problems);
    }

    /// <summary>
    /// One key's value, with the rules of a value read asked of the file that declares it: whoever can change it can
    /// point a run at another key, and a key every account on the machine can read has already been shared. Blank is
    /// none, so a name written blank is as absent as one not written at all.
    /// </summary>
    /// <param name="layout">The repository's layout: the slot is the main checkout's, so worktrees share it.</param>
    /// <param name="appointed">Every name the configuration appoints, of providers and clients both: the refusal of a missing name ends with the names the slot holds that none of them appoints, which is where a misspelt one shows.</param>
    /// <param name="name">The name asked, among <paramref name="appointed"/>.</param>
    public AgentKeyAnswer Value(HarnessLayout layout, IReadOnlyList<AppointedKey> appointed, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(appointed);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var directories = new DotEnvDirectories(
            _fileSystem,
            (key, first, again) => $"'{key}' is declared in '{first}' and in '{again}': a name is declared once",
            skipFile: HarnessLayout.GitKeepFileName);
        var declared = directories.Read(layout.WorkflowsEnvSlot);

        if (directories.Problems is [var problem, ..])
        {
            return new AgentKeyAnswer(null, problem);
        }

        var where = declared.Keys.FirstOrDefault(held => string.Equals(held, name, StringComparison.OrdinalIgnoreCase));

        if (where is null || declared[where] is not { Length: > 0 })
        {
            var of = appointed.FirstOrDefault(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase));

            return new AgentKeyAnswer(null, Missing(Present(layout), appointed, [name], of ?? new(name, "the configuration", "its key's value")));
        }

        var file = directories.Origins[where];

        if (_filePermissions.IsWritableByOthers(file))
        {
            return new AgentKeyAnswer(null, $"other users can change '{file}', and whoever can change it can point a run at another key; {ItemEnvironment.HowToProtect(_platform, file)}");
        }

        if (!_filePermissions.IsPrivate(file))
        {
            return new AgentKeyAnswer(null, $"other users can read '{file}', and it holds {where}; {ItemEnvironment.HowToProtect(_platform, file)}");
        }

        return new AgentKeyAnswer(declared[where], null);
    }

    /// <summary>
    /// The one builder of the sentence that says a key no file of the slot declares: the names, who appoints them, what
    /// to add and what it holds, the steps that would read it where the asker names them, and the names the slot holds
    /// that nothing appoints - where a misspelt one shows. <c>validate-workflow</c> says it of every name a step's model
    /// would read and the slot lacks; a run that refuses a missing key will be refused with it.
    /// </summary>
    /// <param name="present">What the slot was read to hold.</param>
    /// <param name="appointed">Every name the configuration appoints, of providers and clients both.</param>
    /// <param name="missing">The names to say, in the order their provider lists them.</param>
    /// <param name="of">Who appoints them and what they hold, as the sentence names both.</param>
    /// <param name="readBy">The steps that would read them, as the sentence names each; none where the asker names none.</param>
    public static string Missing(
        AgentSlotPresent present,
        IReadOnlyList<AppointedKey> appointed,
        IReadOnlyList<string> missing,
        AppointedKey of,
        IReadOnlyList<string>? readBy = null)
    {
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(appointed);
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(of);

        var slot = HarnessLayout.WorkflowsEnvSlotRelative + "/";
        var unappointed = string.Join(", ", present.Names
            .Where(held => !appointed.Any(key => string.Equals(key.Name, held, StringComparison.OrdinalIgnoreCase))));
        var one = missing.Count == 1;
        var declared = Joined(missing.Select(name => $"'{name}'"), "or");
        var add = Joined(missing.Select(name => $"{name}="), "or");

        return $"no file under '{slot}' declares {declared}, the name{(one ? string.Empty : "s")} {of.By} appoints "
            + $"for its key{(one ? string.Empty : "s")} (its apiKeyNames); add {add} to a file there, holding {of.What} "
            + $"(it is read from this slot and nowhere else)"
            + (readBy is not [_, ..] ? string.Empty : $". The steps that would read {(one ? "it" : "one of them")}: {string.Join(", ", readBy)}")
            + (unappointed.Length == 0 ? string.Empty : $". Declared there and appointed by nothing: {unappointed}");
    }

    /// <summary>A list said as the tool says one: comma-separated, the last apart, the separator before it.</summary>
    private static string Joined(IEnumerable<string> items, string last)
    {
        var list = items.ToList();

        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            _ => $"{string.Join(", ", list[..^1])} {last} {list[^1]}",
        };
    }
}
