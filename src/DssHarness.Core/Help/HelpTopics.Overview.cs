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
    private static string RenderOverview()
    {
        var builder = new StringBuilder();

        builder.AppendLine("DssHarness - one cross-platform tool for a repository's build, test and");
        builder.AppendLine("cross-host work. Every behaviour is declared in .harness-config/config.json;");
        builder.AppendLine("nothing about a specific repository or toolchain is built into the tool.");
        builder.AppendLine();
        builder.AppendLine("Getting started");
        builder.AppendLine($"  {ToolPackage.Command} verify-git              Check git is present and this is a repository");
        builder.AppendLine($"  {ToolPackage.Command} init                    Create .harness-config and seed config.json");
        builder.AppendLine($"  {ToolPackage.Command} legs                    Show where each leg can run, or why it cannot");
        builder.AppendLine($"  {ToolPackage.Command} install-missing-tools   Install what each leg's host is missing");
        builder.AppendLine($"  {ToolPackage.Command} {ToolUpdateService.CommandName}             Update this machine's {ToolPackage.Id}, while none runs here");
        builder.AppendLine($"  {ToolPackage.Command} sync                    Put each host's copy in step with this tree");
        builder.AppendLine($"  {ToolPackage.Command} build                   Build every selected leg");
        builder.AppendLine($"  {ToolPackage.Command} test                    Build and test every selected leg");
        builder.AppendLine($"  {ToolPackage.Command} run <runner>            Run a predefined runner across its legs");
        builder.AppendLine($"  {ToolPackage.Command} check-mutations         Prove each selected leg's tests can fail, arm by arm");
        builder.AppendLine($"  {ToolPackage.Command} clean                   Remove legs' builds, mutation workers and copies");
        builder.AppendLine($"  {ToolPackage.Command} up <name>               Bring a set-up up; '{ToolPackage.Command} down <name>' brings it down");
        builder.AppendLine($"  {ToolPackage.Command} list-worktree           Show worktrees and the copies hosts keep of them");
        builder.AppendLine($"  {ToolPackage.Command} list-orchestrator       Show orchestrators and where each agent stands");
        builder.AppendLine($"  {ToolPackage.Command} read-anchors            List the deferred work recorded as anchors");
        builder.AppendLine();
        builder.AppendLine("Every command accepts");
        builder.AppendLine("  -C, --directory <dir>              Operate on this directory (default: current)");
        builder.AppendLine("  -v, --verbose                      Show per-phase detail and child process output");
        builder.AppendLine();
        AppendWrapped(builder, StoppedDocument);
        builder.AppendLine();
        builder.AppendLine("Topics");

        foreach (var topic in Topics)
        {
            builder.AppendLine($"  {ToolPackage.Command} help {topic.Name,-19}{topic.Summary}");
        }
        builder.AppendLine();
        builder.AppendLine($"Use '{ToolPackage.Command} <command> --help' for a command's own options.");

        return builder.ToString();
    }

    private static string RenderExitCodes()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Exit codes");
        builder.AppendLine();
        builder.AppendLine("Zero always means success. A command that failed and a harness that could not");
        builder.AppendLine("run never share a code, because the remedies differ.");
        builder.AppendLine();
        builder.AppendLine("Shared by every command:");

        foreach (var description in HarnessExit.All)
        {
            builder.AppendLine($"  {description.Code,3}  {description.Name,-16} {description.Explanation}");
        }

        builder.AppendLine();
        builder.AppendLine("Codes 1-9 are reserved for a command's own contract:");
        builder.AppendLine();
        builder.AppendLine("  verify-git");

        foreach (var status in Enum.GetValues<VerifyGitStatus>())
        {
            builder.AppendLine($"    {(int)status,3}  {status}");
        }

        builder.AppendLine();
        builder.AppendLine("  read-anchor, read-anchors --lint, check-anchor-balance, check-anchor-arms, check-anchor-citations");
        builder.AppendLine($"    {AnchorExit.Findings,3}  an id was not found, the registries have problems, the balance did not hold, or a row's claim about a mutation arm does not hold");
        builder.AppendLine();
        builder.AppendLine("  legs, sync");
        builder.AppendLine($"    {LegsExit.Unavailable,3}  a leg named with --legs cannot run, or no selected leg can");
        builder.AppendLine($"    {HarnessExit.InternalError,3}  whether a leg can run was never established, through a defect in this tool");
        builder.AppendLine();
        builder.AppendLine("  install-missing-tools");
        builder.AppendLine($"    {ToolsExit.NotProvisioned,3}  a tool is missing, out of date, or could not be installed");
        builder.AppendLine();
        builder.AppendLine("  check-ci-legs");
        builder.AppendLine($"    {CiExit.LegRed,3}  a leg is red");
        builder.AppendLine($"    {CiExit.MatrixDidNotRun,3}  the matrix did not run, which is never read as every leg passing");
        builder.AppendLine($"    {HarnessExit.Incomplete,3}  a read of the forge did not answer in time: run it again");
        builder.AppendLine();
        builder.AppendLine("  build, test, run, check-mutations");
        builder.AppendLine($"    {LegExit.InputsMoved,3}  inputs-moved or unmeasured: let the tree settle, then run again; where the inputs");
        builder.AppendLine("         could not be watched, free or raise what the machine's reason names, or restore the tree");
        builder.AppendLine($"    {LegExit.Contended,3}  contended: wait for the other run");
        builder.AppendLine($"    {LegExit.Unwitnessed,3}  unwitnessed: find out what actually ran");
        builder.AppendLine($"    {LegExit.LogHeld,3}  log-held: find out which run still owns this leg's logs");
        builder.AppendLine($"    {LegExit.NotAdmitted,3}  not-admitted: wait for the heavy legs it names, free memory or room on the");
        builder.AppendLine("         filesystem it names, or raise the machine's limits ('help admission')");
        builder.AppendLine();
        builder.AppendLine("  build, test, run, clean");
        builder.AppendLine($"    {LegsExit.Unavailable,3}  no selected leg can run, and no failure turned one away");
        builder.AppendLine();
        builder.AppendLine("  check-mutations");
        builder.AppendLine($"    {LegExit.Violated,3}  violated: fix the arm's declaration, or the code it guards");
        builder.AppendLine($"    {LegExit.Survived,3}  survived: strengthen the test that should have failed");
        builder.AppendLine($"    {LegExit.Unattributed,3}  unattributed: contain the crash or hang in the case, or make the runner write");
        builder.AppendLine("         its report");
        builder.AppendLine($"    {MutationService.NothingRuns,3}  no selected leg can run, and no failure turned one away: incomplete, since");
        builder.AppendLine($"         {LegsExit.Unavailable} is an arm violated here");
        builder.AppendLine();
        builder.AppendLine($"  {FlowService.RunCommand}, {FlowService.ResumeCommand}, {FlowService.AnswerCommand}");
        builder.AppendLine($"    {FlowExit.Paused,3}  paused: a question waits - a step's, or the budget's at its reserve - for {FlowService.AnswerCommand}");
        builder.AppendLine($"         A session otherwise ends {HarnessExit.Success} done, {HarnessExit.CommandFailed} failed, {HarnessExit.Incomplete} failed where every failure that ended it");
        builder.AppendLine($"         was incomplete after its retries, and {HarnessExit.Cancelled} at a safe pause or a stop");
        builder.AppendLine();
        builder.AppendLine("host-exec returns the exit code of the command it ran on the host, unchanged, or");
        builder.AppendLine($"{HarnessExit.HostUnavailable} when nothing ran there, or the command never reported how it finished.");

        return builder.ToString();
    }
}
