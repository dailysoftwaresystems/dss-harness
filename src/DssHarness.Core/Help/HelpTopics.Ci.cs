using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    private static string RenderCi()
    {
        var builder = new StringBuilder();

        builder.AppendLine("CI legs");
        builder.AppendLine();
        builder.AppendLine("check-ci-legs reads each leg's CI verdict from the forge's job metadata - through gh,");
        builder.AppendLine("for GitHub Actions - and tells a real failure from a leg that ran out of its time");
        builder.AppendLine("budget. It knows no workflow of its own: which jobs are legs, what a leg is called,");
        builder.AppendLine("and which steps build and test it are what config.json's ci section declares, and");
        builder.AppendLine($"until they are, it refuses ({HarnessExit.Refused}) and names what to set.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Every call of gh, whoever makes it, and every git command is started so that it cannot ask anything "
            + $"({string.Join(", ", UnattendedCommands.Variables.Keys)}), and a call of gh is ended where it has not answered within the "
            + "timeoutMinutes of the workflow step that made it, where the step says one, else within "
            + $"{ReportText.Limit(GhProcess.Limit)}: a call that did not answer in time is not done "
            + $"({HarnessExit.Incomplete}), and asked again it may answer.");
        builder.AppendLine();
        builder.AppendLine("""  "ci": {""");
        builder.AppendLine("""    "workflows": [".github/workflows/ci.yml"],""");
        builder.AppendLine("""    "legJobPattern": "^test \\((?<leg>[^,)]+)(?:, (?<budget>[0-9]+)\\))?",""");
        builder.AppendLine("""    "buildStep": "Build", "testStep": "Test",""");
        builder.AppendLine("""    "workflowBudgetPattern": "leg: {leg}, minutes: (?<budget>[0-9]+)",""");
        builder.AppendLine("""    "legBudgetMinutes": 45""");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("  workflows              the workflow files whose runs are read; left out, every");
        builder.AppendLine("                         .yml and .yaml file directly in .github/workflows");
        builder.AppendLine("  legJobPattern          a .NET regular expression matched against each job's name: a");
        builder.AppendLine("                         job it matches is a leg, named by its 'leg' group, and a");
        builder.AppendLine("                         'budget' group, where it has one, is the leg's budget in");
        builder.AppendLine("                         minutes. GitHub names a matrix job '<job> (<values>)', the");
        builder.AppendLine("                         values in the order the matrix declares them, and cuts a");
        builder.AppendLine("                         long name short: keep what follows the leg's name optional,");
        builder.AppendLine("                         as above, or a leg whose name was cut is not read at all");
        builder.AppendLine("  buildStep, testStep    the steps a leg builds and tests in, by their exact names");
        builder.AppendLine("  workflowBudgetPattern  optional: a .NET regular expression matched against each");
        builder.AppendLine("                         workflow's text, for a leg whose job name gave no budget -");
        builder.AppendLine("                         one the forge cut short - with {leg} standing for the leg's");
        builder.AppendLine("                         name and a 'budget' group for the minutes");
        builder.AppendLine("  legBudgetMinutes       optional: the budget of a leg nothing else gives one");
        builder.AppendLine();
        builder.AppendLine("A leg's budget comes from its job's name, then its workflow, then legBudgetMinutes. A");
        builder.AppendLine("test step that failed at or past its budget is a possible overrun, whose budget is");
        builder.AppendLine("to be re-derived; one that failed before reaching it is a real failure, to be fixed;");
        builder.AppendLine("and one with no budget to measure it against is called neither, and counted apart.");
        builder.AppendLine($"A green leg past {CiLegsService.WarningFraction * 100:0}% of its budget is warned about. A pattern that does not");
        builder.AppendLine("compile, or lacks its group, is refused when the file is read; one that runs out of");
        builder.AppendLine("time, or a budget group that captures anything but a whole number of minutes, is");
        builder.AppendLine($"refused ({HarnessExit.ConfigInvalid}) when it is matched. Neither is ever read as no match.");
        builder.AppendLine();
        builder.AppendLine($"It exits {CiExit.LegRed} when a leg is red, and {CiExit.MatrixDidNotRun} when no job is a leg - the matrix did not run,");
        builder.AppendLine("or legJobPattern matches none of its jobs - which is never read as every leg passing.");

        return builder.ToString();
    }
}
