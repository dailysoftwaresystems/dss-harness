using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DssHarness.Core.Platform;
using DssHarness.Core.Text;

namespace DssHarness.Core.Worktrees;

/// <summary>
/// Validates and generates worktree names.
/// </summary>
/// <remarks>
/// A name's length is the path budget's to decide, measured against the real path: a worktree's build tree sits below
/// <c>&lt;worktrees.root&gt;/&lt;name&gt;</c>, and on Windows the whole path, plus <c>build/&lt;variant&gt;</c> for the
/// longest variant this machine builds, plus the longest path the build system generates below that, must stay under
/// the 260 character limit. Exceeding it does not fail as a clean error: it surfaces as compile errors in files the
/// worktree never touched. A repository may set a longest name of its own besides (<c>worktrees.maxNameLength</c>), a
/// naming policy; where it sets none, no length is refused for itself.
/// </remarks>
public static class WorktreeName
{
    /// <summary>
    /// Length of a generated name, where the configured longest name allows it. Not a limit: a name is drawn from outside
    /// the names already taken (<see cref="Generate(int, IReadOnlySet{string})"/>), so its length only makes a draw that
    /// meets one rare, never what keeps two apart.
    /// </summary>
    public const int RandomLength = 10;

    /// <summary>
    /// Characters a generated name is drawn from: lowercase alphanumerics, so a
    /// generated name always satisfies <see cref="ValidateFormat"/> by construction.
    /// </summary>
    private const string RandomAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A name's shape: lowercase letters and digits joined by single hyphens.</summary>
    private static readonly Regex Pattern = LinearRegex.Compile(@"^[a-z0-9]+(-[a-z0-9]+)*\z");

    /// <summary>
    /// Validates a name's shape and, where the repository sets one, its length against <paramref name="maxLength"/>:
    /// <c>worktrees.maxNameLength</c>, a naming policy of its own. Where it sets none, no length is refused here: the path
    /// budget, measured against the real path where the tree is made, is what decides.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="maxLength">The longest name the repository permits, or <see langword="null"/> where it sets none.</param>
    public static WorktreeNameResult Validate(string? name, int? maxLength = null)
    {
        if (maxLength is { } set)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(set, 1, nameof(maxLength));
        }

        var format = ValidateFormat(name);
        if (!format.TryGetName(out var accepted, out _))
        {
            return format;
        }

        if (maxLength is { } most && accepted.Length > most)
        {
            return WorktreeNameResult.Invalid(
                $"'{accepted}' is {accepted.Length} characters; the limit is {most}, which worktrees.maxNameLength in "
                + "config.json sets. Without it, a name is held to the path budget alone, which keeps every build path under "
                + $"the Windows {HostPlatform.WindowsMaxPath} character limit.");
        }

        return format;
    }

    /// <summary>
    /// Validates a name's shape only: lowercase letters and digits joined by single
    /// hyphens. Separate from <see cref="Validate"/> because the shape never depends on
    /// configuration while the permitted length does, so the shape can be checked
    /// before a repository has even been located.
    /// </summary>
    public static WorktreeNameResult ValidateFormat(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return WorktreeNameResult.Invalid("A worktree name is required.");
        }

        if (!Pattern.IsMatch(name))
        {
            return WorktreeNameResult.Invalid(
                $"'{name}' is not a valid name. Use lowercase letters and digits "
                + "separated by single hyphens, for example 'fix-auth'.");
        }

        return WorktreeNameResult.Valid(name);
    }

    /// <summary>
    /// Generates a random name of <paramref name="length"/> characters, so a throwaway
    /// worktree does not need one invented. <see cref="Generate(int, IReadOnlySet{string})"/>
    /// draws one that no existing name is.
    /// </summary>
    public static string Generate(int length = RandomLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        var characters = new char[length];

        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = RandomAlphabet[RandomNumberGenerator.GetInt32(RandomAlphabet.Length)];
        }

        return new string(characters);
    }

    /// <summary>
    /// A random name of <paramref name="length"/> characters that is none of <paramref name="taken"/>, or
    /// <see langword="null"/> where every name of that length is taken - the one way there is none, said as that fact.
    /// </summary>
    /// <remarks>
    /// Never a count of draws that gives up on a name that is there to be had. Where the names of that length are no more
    /// than twice the names taken, each is asked of the set, and one of those it lacks is picked at random, so a crowded
    /// length costs one look a name and never a draw that misses: the looks are at most twice the names taken. Where they
    /// are more, a name is drawn until a draw is no name of the set, each draw free with a chance above a half, so fewer
    /// than two draws are expected. Names are compared as the set compares them: a set that folds case takes a name in
    /// either case.
    /// </remarks>
    /// <param name="length">How many characters the name has.</param>
    /// <param name="taken">Every name already in use, compared as the file system that holds them compares names.</param>
    public static string? Generate(int length, IReadOnlySet<string> taken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentNullException.ThrowIfNull(taken);

        // The names of this length a draw can give, counted only as far as twice the set reaches: past that, a draw misses
        // less often than it hits.
        var crowd = 2L * taken.Count;
        var possible = 1L;

        for (var place = 0; place < length && possible <= crowd; place++)
        {
            possible *= RandomAlphabet.Length;
        }

        if (possible > crowd)
        {
            while (true)
            {
                var drawn = Generate(length);

                if (!taken.Contains(drawn))
                {
                    return drawn;
                }
            }
        }

        var free = new List<string>();
        var characters = new char[length];

        for (var number = 0L; number < possible; number++)
        {
            var rest = number;

            for (var place = length - 1; place >= 0; place--)
            {
                characters[place] = RandomAlphabet[(int)(rest % RandomAlphabet.Length)];
                rest /= RandomAlphabet.Length;
            }

            var name = new string(characters);

            if (!taken.Contains(name))
            {
                free.Add(name);
            }
        }

        return free.Count == 0 ? null : free[RandomNumberGenerator.GetInt32(free.Count)];
    }
}

/// <summary>
/// Outcome of validating a worktree name. Constructed only through the factories,
/// so a result always carries either a name or a reason and never neither.
/// </summary>
public sealed record WorktreeNameResult
{
    private WorktreeNameResult(string? name, string? error)
    {
        Name = name;
        Error = error;
    }

    /// <summary>Whether the name is usable.</summary>
    public bool IsValid => Name is not null;

    /// <summary>The accepted name, when valid.</summary>
    public string? Name { get; }

    /// <summary>What is wrong with it, when invalid.</summary>
    public string? Error { get; }

    /// <summary>An accepted name.</summary>
    public static WorktreeNameResult Valid(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new WorktreeNameResult(name, null);
    }

    /// <summary>A rejected name, with the reason.</summary>
    public static WorktreeNameResult Invalid(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new WorktreeNameResult(null, error);
    }

    /// <summary>
    /// Yields the name when valid and the reason when not, so a caller never has to
    /// assert which of the two is present.
    /// </summary>
    public bool TryGetName(
        [NotNullWhen(true)] out string? name,
        [NotNullWhen(false)] out string? error)
    {
        name = Name;
        error = Error;
        return name is not null;
    }
}
