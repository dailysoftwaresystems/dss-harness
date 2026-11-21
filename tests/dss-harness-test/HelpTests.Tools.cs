using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
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
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>
    /// The tools topic says a minVersion holds every copy a leg could start, the one that meets it raised ahead of the
    /// PATH and a leg with none turned away; that install-missing-tools judges a tool by the host's own survey, a shadowed
    /// one outdated and one it could not establish unknown, installing neither; and that the package manager an install
    /// runs is started where it was found.
    /// </summary>
    [Fact]
    public async Task TheToolsTopic_SaysAMinVersionHoldsEveryCopy_AndAManagerRunsWhereItWasFound()
    {
        var tools = Prose.Words((HelpText.Of("tools")).StandardOutput);

        foreach (var text in new[]
        {
            "The package manager an install runs is started the same way, where the search found it - brew in /opt/homebrew/bin, "
                + "which no login-free PATH on macOS names - save one run through sudo, which sudo looks up on a PATH of its own.",
            "A minVersion holds every copy of the tool a leg could start: the first one - on the PATH, then in the searched "
                + "directories - whose version meets it is the one a leg there starts",
            "That tool is found where its first copy that meets it is, and that copy's directory goes ahead of the PATH of every "
                + "process every leg on that host starts - one whose environment, or developer environment, sets PATH included",
            "A leg the tool is declared for is turned away where every copy is older, naming each copy, what it said, and - where "
                + "the tool declares an install there - the install-missing-tools command that would install one; where the copy that "
                + "meets it is behind an older one in a "
                + "directory raised for another tool, naming both - only where no order of the raised directories, nor a further "
                + "copy of a tool in the way, keeps both on copies that meet them; and where no copy could be shown to meet it, as "
                + "unknown, 'skipped-unavailable'.",
            "Where the copy that meets it is behind an older one a directory raised for another tool holds, the tool is outdated, "
                + "naming both, and nothing is installed: an install would not change which copy a leg starts.",
            "Where none met it and a copy's version could not be read, or a place a copy could be could not be looked in, the "
                + "tool is unknown and nothing is installed: nothing says it is behind.",
            "Each host is asked about its tools by its own survey, the one 'legs' runs there, so a tool is installed exactly "
                + "where a leg would be turned away over it, and never a second time where a leg there finds it.",
            "a host whose survey cannot run - one a dry run finds with no SDK yet - has every tool unknown, saying why, and "
                + "nothing is installed there.",
            "install-missing-tools asks the same survey, on the host's own PATH and on the PATH each developer environment its "
                + "legs start in sets up there; a tool the survey could not establish is unknown rather than missing, and nothing "
                + "is installed for it.",
            "A minVersion is written major.minor.patch and needs a probe to read versions with; brew installs and updates with "
                + "'brew install'",
            "A leg in a developer environment holds the copy that environment's PATH names to it, once set up.",
            "A leg turned away for a missing program, or for one below its minVersion, is 'skipped-tool-missing'",
        })
        {
            Assert.Contains(text, tools, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The tools topic says what --dry-run does, that init installs only when asked, how a tool
    /// narrows the legs that need it, and that a leg in a developer environment is told about a tool
    /// as the PATH it sets up on the leg's host holds it; the layout topic says init writes the tree it
    /// runs in and names a rule by git's own answer.
    /// </summary>
    [Fact]
    public async Task ToolsAndLayoutTopics_SayWhatInitAndInstallMissingToolsDo()
    {
        var tools = HelpText.Of("tools");
        var layout = HelpText.Of("layout");

        foreach (var text in new[]
        {
            "--install-tools' runs it too; plain init installs nothing and says how.",
            "--dry-run reaches and asks every host as a run does, and installs no tool",
            "\"toolchains\": [\"msvc\"]",
            "\"legs\": [\"win-arm\", \"gate\"]",
            "\"processors\": [\"arm64\"]",
            "\"emulators\": [\"qemu-arm64\"]",
            "a developer environment, on the PATH that environment sets up for its processor -",
            "set up on the leg's host for the look, by the DssHarness there, so cl is found where",
            "the environment could not be looked at or set up, a tool its legs need is unknown,",
            "covering no declared leg, is refused.",
            "  check-mutations",
            "          what build starts: each worker, and each arm, is built as the leg builds",
        })
        {
            Assert.Contains(text, tools.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var text in new[]
        {
            "block on later runs and leaving every other rule untouched. It then asks git which",
            "overrules does nothing there - named only where taking it out would take from git",
            "configuration, a placeholder, an action's files, an anchor registry - and no",
            "the slot a placeholder is kept in is overruled for the slot's contents and needed",
            "init writes the tree it runs in, a worktree's own included",
            $"('{GitIgnoreManager.RetiredBeginMarker}') is the managed block too: init",
            "rewrites it in place under the current markers and says so, and none of its lines",
        })
        {
            Assert.Contains(text, layout.StandardOutput, StringComparison.Ordinal);
        }
    }
}
