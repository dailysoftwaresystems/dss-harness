using System.CommandLine;

namespace DssHarness.Cli;

/// <summary>
/// The <c>--legs</c> option every command that selects legs takes, made in one place so each takes its values alike: a
/// leg or a leg set per value, several to one token with commas or one token each, as the core resolves them
/// (<c>LegSelection.Resolve</c>). Made anywhere else, an option that leaves out the second spelling refuses
/// <c>--legs a b</c> while its description promises it.
/// </summary>
internal static class LegSelectionOption
{
    /// <summary>The option's name.</summary>
    internal const string Name = "--legs";

    /// <summary>A command's <c>--legs</c>, its description stating both spellings.</summary>
    /// <param name="selects">What the legs named select, before the two spellings.</param>
    /// <param name="without">What the command acts on where the option is left out.</param>
    internal static Option<string[]> Create(string selects = "Only these legs or leg sets", string without = "every declared leg") => new(Name)
    {
        Description = $"{selects}: --legs a,b or --legs a b. Without it, {without}.",
        AllowMultipleArgumentsPerToken = true,
    };
}
