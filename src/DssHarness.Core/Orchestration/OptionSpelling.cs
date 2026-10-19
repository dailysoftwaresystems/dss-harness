namespace DssHarness.Core.Orchestration;

/// <summary>
/// How a refusal of a fold, a deletion's fold, a refresh or a rebase names what it was asked: by the flag the command
/// line took, or by the key a workflow's step carried it in. Each rule of those requests is written once, in the
/// request, and says it in the words of whoever wrote what it refuses.
/// </summary>
public enum OptionSpelling
{
    /// <summary>As a command line names it: <c>--accept-lost</c>.</summary>
    Flag,

    /// <summary>As a workflow's step names the key that carries it: <c>acceptLost</c>.</summary>
    Key,
}

/// <summary>The one way an option is spelt in either of a reader's words.</summary>
public static class OptionSpellings
{
    /// <summary>
    /// <paramref name="option"/> as <paramref name="spelling"/> names it: the flag itself, or the key that carries it in
    /// a workflow's step - the flag without its dashes, each word after the first begun with a capital
    /// (<c>--accept-lost</c>, <c>acceptLost</c>), as the step types' tables list the fold family's keys.
    /// </summary>
    /// <param name="spelling">Whose words the refusal is in.</param>
    /// <param name="option">The option, as the command line takes it.</param>
    public static string Spell(this OptionSpelling spelling, string option)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(option);

        if (spelling == OptionSpelling.Flag)
        {
            return option;
        }

        var words = option.TrimStart('-').Split('-', StringSplitOptions.RemoveEmptyEntries);

        return string.Concat(words.Select((word, index) => index == 0 ? word : $"{char.ToUpperInvariant(word[0])}{word[1..]}"));
    }
}
