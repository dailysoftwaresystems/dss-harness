using System.CommandLine;
using DssHarness.Core.Help;
using DssHarness.Core.Results;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness help</c>: the command line's front door on the tool's reference material, which the core owns.</summary>
internal static class HelpCommand
{
    internal const string Name = "help";

    private static readonly Argument<string?> TopicArgument = new("topic")
    {
        Description = $"Topic to explain: {string.Join(", ", HelpTopics.Described.Select(topic => topic.Name))}. Omit for an overview.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    internal static Command Create()
    {
        var command = new Command(Name, $"Explain what the answers mean, one topic at a time: {string.Join(", ", HelpTopics.Described.Select(topic => topic.Name))}.");
        command.Arguments.Add(TopicArgument);
        GlobalOptions.AddTo(command);

        command.SetAction(parseResult =>
        {
            var topic = parseResult.GetValue(TopicArgument);

            if (!HelpTopics.TryRender(topic, out var text))
            {
                // A mistyped topic is a usage error, and it goes to stderr: zero always
                // means success, and stdout stays pipeable.
                Console.Error.Write(text);
                return HarnessExit.UsageError;
            }

            Console.Out.Write(text);
            return HarnessExit.Success;
        });

        return command;
    }
}
