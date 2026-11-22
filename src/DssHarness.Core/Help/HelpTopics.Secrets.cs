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
    private static string RenderSecrets()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Where each host's connection data lives");
        builder.AppendLine();
        builder.AppendLine("config.json declares only NAMES, under sshItems and wslDistros. An address, a");
        builder.AppendLine("user, a key path and a credential are never in it: that file is tracked, and a");
        builder.AppendLine("config.json arriving through git must not be able to point the harness at a");
        builder.AppendLine("machine nobody set up here.");
        builder.AppendLine();
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemEnvFileName}");
        builder.AppendLine("                                     ADDRESS=, USER=, PORT= (optional), the");
        builder.AppendLine($"                                     superuser credential as {HostSecretsStore.SuperuserKey}= (optional),");
        builder.AppendLine($"                                     and {HostSecretsStore.RepositoryPathKey}= (optional, below)");
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemKeyFileName}");
        builder.AppendLine("                                     the private key");
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemKnownHostsFileName}");
        builder.AppendLine("                                     the host keys ssh may accept");
        builder.AppendLine($"  .harness-config/{HarnessLayout.WslDistrosDirectoryName}/<name>/{HarnessLayout.ItemEnvFileName}");
        builder.AppendLine($"                                     the distribution as {HostSecretsStore.DistributionKey}=, as wsl.exe");
        builder.AppendLine("                                     lists it, the superuser credential");
        builder.AppendLine($"                                     install-missing-tools needs, and {HostSecretsStore.RepositoryPathKey}=");
        builder.AppendLine("                                     (optional, below)");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{HostSecretsStore.RepositoryPathKey} is where that host keeps the main checkout's copy for this machine, over the "
            + "repositoryPath config.json declares for every machine: config.json is shared, and two machines sharing one "
            + "host each keep their own copies there. It is held to repositoryPath's rules - absolute or under ~/, no '.' "
            + "or '..' segment, never a home or root directory - and refused, naming the file, where it breaks one. Every "
            + $"reader of a host's copies - sync, a leg placed there, host-exec, legs, list-worktree --hosts, clean - reads this "
            + $"one answer, and '{ToolPackage.Command} legs' says each host's copy and what set it. A key the item is not read "
            + "for is refused, naming the keys it is read for, never passed over: a misspelt one would leave what it was "
            + "written to change as it was.");
        builder.AppendLine();
        builder.AppendLine("hosts.ssh and hosts.wsl are keyed by these names, and a host declared without an");
        builder.AppendLine("item is refused when config.json is read: it could never be reached, and failing");
        builder.AppendLine("later would blame ssh for a mistake in this file.");
        builder.AppendLine();
        builder.AppendLine("An .env other users can change is refused before anything connects, because");
        builder.AppendLine("whoever can change it can send the harness somewhere else. A key other users can");
        builder.AppendLine("read is refused because ssh ignores it, and so is an .env other users can read");
        builder.AppendLine($"that holds a {HostSecretsStore.SuperuserKey}, a credential already shared; one written blank holds");
        builder.AppendLine("none, and declares none. Each refusal names the command that fixes it. A");
        builder.AppendLine("credential is passed to a privileged command on standard input, and reaches no");
        builder.AppendLine("log, no argument list and no error message.");
        builder.AppendLine();
        builder.AppendLine("init writes the ignore rules that keep all of it out of git. A fresh clone plus");
        builder.AppendLine("this tree is enough for 'legs' to reach every host.");

        return builder.ToString();
    }
}
