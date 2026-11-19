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
    /// The agent-clients topic says the agents' policy whole: one policy on every client, default allow except the
    /// root actions and the restrictions, each root action named, the slot and the Claude Code directories refused,
    /// the command read whole (with what it cannot read said), policy-not-sandbox, and the fail-closed rule.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysTheAgentsPolicy()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains("What an agent may do is one policy, the same on every client: everything is allowed except", prose, StringComparison.Ordinal);
        Assert.Contains(
            "A restriction is one string, Run(git push *), Write(.github/**), Read(secrets/**) or Web(pastebin.com): Run a "
                + "command prefix, its trailing * any arguments and the bare command too; Write and Read path patterns as "
                + "gitignore reads them",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("Declared under agentPolicy.restrictions in config.json", prose, StringComparison.Ordinal);
        Assert.Contains("agentPolicy.webDomains is the allow list beside them", prose, StringComparison.Ordinal);
        Assert.Contains("curl, wget, Invoke-WebRequest, Invoke-RestMethod, iwr, irm, http, https, gh api - is", prose, StringComparison.Ordinal);
        Assert.Contains("the root actions (sudo, su, doas, pkexec, run0, gsudo, sudoedit, runas,", prose, StringComparison.Ordinal);
        Assert.Contains("psexec, PowerShell's -Verb RunAs, wsl -u root, systemd-run, schtasks with /ru, wherever", prose, StringComparison.Ordinal);
        Assert.Contains("writes outside an agent's writable roots, every read and write of a", prose, StringComparison.Ordinal);
        Assert.Contains(
            "any command naming one - by ~ or $HOME, as a redirect's file, an option's value after an = or a PowerShell parameter's :, a value glued to "
                + "a short option (ssh -i./key), the file an @ or a < makes curl, httpie or gh read (-d @x, -F f=@x;type=y), a word of a script, a "
                + "variable the command string sets read with each value it gives it (p=../..; cat $p/x), a pattern's segment that may match .. (.?, "
                + ".[.], .*) read as .. too - and any word that names a directory holding one whole (.harness-config, .., ~, /, or a pattern that may "
                + "match one) to a program that may read what it holds, for a program handed a directory may walk it: but the words of a program that "
                + "only names, tests or lists the paths it is given (echo, printf, test, ls, du, df, stat, realpath, mkdir, which, a find that runs, "
                + "deletes and writes nothing, and their kind), a word that moves a command (a cd's, git -C's, tar -C's, make -C's), which reads nothing "
                + "where it goes, a word that names where a program writes, which the write rules decide, and the agent's working directory and those "
                + "above it, which reach the slots only through it - every command over the tree names it (.) and every program given no path works "
                + "over it. A recursive removal or a move of a directory that holds a tree's .git, the tool's state, a slot, a Claude Code directory or a place the user keeps credentials in "
                + "(rm -rf ., Remove-Item -Recurse, mv, rsync --delete, git clean -dx), the working directory among them, is refused, and so is a find "
                + "or fd walk that may remove or move one or what one holds - its -delete, a remover its -exec, -execdir or -ok runs, fd's -x and -X - "
                + "read as the walk reaches it: its depth, prunes, excludes and name, path, regex, kind and emptiness filters read, what a guarded "
                + "directory holds read as it stands, so find . -name '*.pyc' -delete runs. "
                + "This tool's own verbs and run-side-task, run by any program the policy reads or from a response file, or by a verb the shell expands "
                + "(run-side-$T, dssharness $V) - its verb the first word after its name, for it takes no option before one - are refused, and so is a "
                + "command that sets or takes away DSSHARNESS_UNDER_AGENT, the mark of every program an agent starts - its assignment, empty or not, "
                + "unset, env -u, export -n and declare and their kind, a name reference to it, cmd's set, csh's setenv, setx, the name read, mapfile, "
                + "printf -v, getopts, wait -p, vared, a for or a select sets, one an arithmetic assigns (let, ((...))), a PowerShell item cmdlet's env: "
                + "item, its Environment:: path among them, or a start with no environment (env -i, env -, exec -c) - and one that may: any of those "
                + "places given a name the shell expands (unset ${v}AGENT, env -u \"$v\", unset $(env | grep ...), zsh's unset -m pattern), a "
                + "PowerShell item cmdlet given a path it computes, a wildcard that may match it (Env:DSSHARNESS_*, a relative one where the string "
                + "names env:) or none where the string names env:; and any other word that names it, but a word of data - a program that runs none "
                + "of its words (grep -rn DSSHARNESS_UNDER_AGENT src, echo \"$DSSHARNESS_UNDER_AGENT\"), a git command that reads only what git "
                + "tracks (git log -S), a word holding a blank - which takes nothing away - whatever any switch says: an agent",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "agent's words. Nor is git or gh called by a name none of its own commands is - an alias, an extension - on any ground, as the reading "
                + "of a command below says.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "This is policy, not a sandbox: it decides what a call names, never what a program reads by itself - a program an agent runs opens files "
                + "as the user it runs as, and one that walks its working directory unnamed (an archive of ., a search given no path) reaches a "
                + "credential slot's files where that directory holds one, as a side-task's main tree does; what it is never given is a key in its "
                + "environment, and what no file tool and no command's word the policy reads names is the slot itself, in any root, or a directory "
                + "that holds one to a program that may read it, but that working directory and those above it. The words it does not read - a path a "
                + "command computes, the code an interpreter is given, a script file's inside, what a process apart from the agent's tree does - are "
                + "where a policy of words ends.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"On every ground, worktree rules or none, git and gh are called by their own commands: a git command whose first word, a name a setting "
                + $"of git's may have, is none of git's own ({string.Join(", ", AgentPolicy.GitCommands)}) and no program git-<name> git finds",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"nor leave its step's limits - nor do the gh commands that hand its work to another agent or install what the next launch loads ({string.Join(", ", AgentPolicy.GhOutOfStepCommands)}), by gh's own aliases too - nor does any command print a credential, whatever the worktree rules say ({string.Join(", ", AgentPolicy.CredentialPrintingCommands)}; a * any helper's name, and the words after a command met anywhere after it, options before or between them, and a word the shell expands among them, wherever the expansion stands in it, held as one that may be them): a credential leaves its slot only by the key store that holds it, never through an agent's words.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("every read and write of a credential slot or a Claude Code directory of the run's, or of a place the user keeps credentials in (the isolation's list, below), and every search over one", prose, StringComparison.Ordinal);
        Assert.Contains(
            "a refusal's places compare in any case on every system. On Windows each segment of a path is read as the name Windows reaches by it "
                + "before any refusal compares it - in a command's words, its moves, its writes and its removals and in a file tool's path alike: its "
                + "trailing dots and spaces dropped (.git., k. .), an NTFS stream's or attribute's suffix dropped (.git::$INDEX_ALLOCATION, k::$DATA), "
                + "and a short name (GIT~1, harnes~1, in any case) read as its long name where the path is there - the system's own answer, whatever "
                + "short names the volume gives - and, where it is not, as each guarded name it could shorten as well, for a command may make the "
                + "place before the word is read (git init sub and then sub/GIT~1/hooks); HEAD~1 and their kind, which shorten no guarded name, are "
                + "the words they are.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            @"On Windows a device prefix (\\?\, \\.\, \??\) is dropped from a path and from what the runtime makes of it - one naming no "
                + "drive and no share, a volume by its id or a pipe, is a place it cannot tell, and denied - and a shell's "
                + @"spelling of a drive (/c/x, /cygdrive/c/x, /mnt/c/x) is read both as the drive (C:\x) and as the directory it names on the "
                + "current drive, an action allowed only where both are; a cd is followed the same way - pushd, PowerShell's Set-Location and "
                + "Push-Location, the directory env -C or pwsh -WorkingDirectory starts its command in, and the one git -C, tar -C or --directory, "
                + "make -C, ninja -C and patch -d work in, in every spelling, glued or apart, each word it may go to; a cd to a name under a CDPATH - "
                + "the one the agent's commands start with or the one the string sets - read in each of its directories too, a cd given no directory "
                + "going to the HOME the command or the string sets, cd - back to the OLDPWD the string sets or a place a move of the string's left, "
                + "and one whose place this does not know - cd - with neither, a CDPATH, OLDPWD or HOME the string computes - denied - from every "
                + "place the command reached before it, a command after && standing only where the one before it held and one after || only where "
                + "it failed, to every place it reaches, however many, none denied for their number.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("No agent starts while this tool itself runs elevated: its step is recorded never started, saying so, before any client is taken.", prose, StringComparison.Ordinal);
        Assert.Contains("a comment and a here-document's lines never read as commands", prose, StringComparison.Ordinal);
        Assert.Contains(
            "wrappers (env, nohup, time, timeout, nice, xargs, command, builtin, exec, eval, busybox) stripped, what "
                + "xargs reads an expansion among the words it runs, env's options read by GNU env's own table, a long one by any prefix that "
                + "names it alone (--spl, --u, --chd), one that names none or more than one denied; and it goes into sh -c and every other "
                + "shell's (fish's -C scripts too, its options read as fish reads them)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a variable - so set, or declared by export and its kind, cmd's set, fish's set, csh's setenv, setx or PowerShell's env: items - "
                + "whose value is a program git or another tool runs (GIT_SSH_COMMAND, GIT_PAGER,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("refused as such ('git $VERB' under Run(git push *))", prose, StringComparison.Ordinal);
        Assert.Contains("a file:// address is read as the path it names", prose, StringComparison.Ordinal);
        Assert.Contains(
            "What it cannot read whole it denies, said here, not guessed at: a shell that reads its "
                + "script from an input it is given (curl ... | sh), from a process substitution or from another open descriptor (/dev/fd/3, <&3), "
                + "an env -S string holding a backslash, whose escapes are env's own, an alias it gives, a script another program fills in "
                + "(find -exec sh -c 'echo {}'), PowerShell's code no word spells (a static method, an instance method "
                + "but a string's, a collection's or a date's, a function's or an alias's text, a class, using, its stop-parsing --%, "
                + "a script Invoke-Expression is handed by no one string, and a command line Start-Process, WMI, the task scheduler or Add-Type is handed), and a program named by a variable, a substitution, "
                + "a pattern or a brace, an option of git's, gh's, fish's or Windows PowerShell's own before "
                + "its command that this does not know, sed running what it reads or a script the shell fills in, at given its job by a file or its "
                + "input, a configuration file ssh -F names (but none, /dev/null), whose settings may run commands, an env long option that names "
                + "none of env's or more than one, a script file the string writes and runs whose text no here-document gave - a file the string "
                + "writes known by the path it reaches from each place its command stands, a device it writes to (/dev/null, and NUL where cmd or "
                + "PowerShell writes it on Windows) none, a run word - the program, the one a runner such as stdbuf or taskset runs past its own "
                + "options, or a script file named or given as a shell's input (bash < x.sh) - that file only where it reaches that path or where a "
                + "target the shell expands may be it, its literal end the word's (make > \"$LOG\"; bash scripts/report.sh runs), and any such file "
                + "where the shell expands the program or the script file itself, never another word a runner is given (ssh's remote command, an "
                + "argument) - a tilde whose variable the string set to a value it computes an expansion as a target and as a run word (HOME=$(pwd), "
                + "then ~/x.sh) - a terminal "
                + "session's program given what to run "
                + "or type (tmux, screen, zellij, byobu), and crontab, launchctl or systemctl installing or starting a job, each running what it is "
                + "given apart from the agent; what it does not see, a policy of words and not a sandbox, is the inside of a script file a command "
                + "runs, the code an interpreter is given (python -c, perl -e, node -e, awk's system), a build file make, ninja or cmake runs, what an "
                + "archive or a patch holds, a path a command computes ($(printf ...)), a program another runs from words that do not name it, and "
                + "what a process apart from the agent's tree does.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "and the command lines trap, watch, script and flock run, the command sed's e command and flag run, ssh's and wsl's command and a "
                + "WSL distribution's own launcher's (ubuntu run, ubuntu2204.exe -c) - with none, the script its input gives where a pipe, a "
                + "redirect, a group's input or an argument list's written input gives one, read as a shell's is, and nothing where none is given "
                + "(the string's first command), ssh -n and -W among them - the commands ssh's -o settings run (ProxyCommand, LocalCommand, "
                + "KnownHostsCommand, RemoteCommand), a string a program runs as a shell's script (npx's and npm exec's -c and --call, each of "
                + "concurrently's, hyperfine's and watchexec's commands, nodemon's --exec, entr's with -s, cargo watch's -s), read as sh -c's and as "
                + "cmd /c's, and the job at and batch take from a here-document or a here-string, a script the string writes to a file before it runs "
                + "it read as the here-document that wrote it,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a pathspec git expands itself is read as git reads it, quoted or not - its wildcards matching a dot-name, :/ and :(top) the "
                + "repository's root, its icase and glob magic - where git reaches what it does not track (add -f, grep --no-index or --untracked, "
                + "clean, stash -u or -a, diff --no-index), while a directory a git command that reads only what git tracks (status, diff, log, "
                + "ls-files, grep and their kind) is given is no read of a slot it holds, a quoted pattern of one the text it spells, and a directory "
                + "an --exclude or --exclude-dir gives is what a program leaves out, never a read of what it holds.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("$((...)), ((...)) and $[...] read as arithmetic, their substitutions as commands, ((...)) the command let given its expression", prose, StringComparison.Ordinal);
        Assert.Contains(
            "a variable - so set, or declared by export and its kind, cmd's set, fish's set, csh's setenv, setx or PowerShell's env: items - whose "
                + "value is a program git or another tool runs (GIT_SSH_COMMAND, GIT_PAGER, GIT_EDITOR, EDITOR, PAGER, PROMPT_COMMAND and their kind) "
                + "read as that program's command line, a prompt (PS0, PS1, PS2, PS4) as the text an interactive shell expands,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a cmdlet's quoted ones too, which the cmdlet expands itself; a variable the string sets (p=../..) read in each later word with every "
                + "value it may hold, however many, one value of it in every word that reads it, $PWD and a leading ~+ as each place the command stands in and each value the string gives PWD, a ~- and the directory stack's ~N, ~+N and ~-N as places it does not know where the shell expands them - a word's start, an assignment's value - and as the text they spell where it does not (git log @~1, cat '~1') - and each, beside ~+, a variable wherever a word the shell expands is read, the string able to give it any word - and a ~ as each HOME "
                + "the string, the command or a command that runs it sets, while a variable it does not set and a substitution are read as the text "
                + "they spell, for their value is the agent's environment's or a program's, which no word of the string gives;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a command find -exec, fd -x, rg --pre or --hostname-bin, parallel or an option of a git subcommand runs - a pack "
                + "program a transport runs (--upload-pack, --receive-pack, --exec, clone's -u), difftool's --extcmd, rebase's "
                + "--exec, grep's --open-files-in-pager, bisect run, submodule foreach, filter-branch's filters and send-email's "
                + "commands - read as its own",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("The worktree rules (agentPolicy.worktreeRules, true where nothing is said)", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"git's state-writing verbs ({string.Join(", ", AgentPolicy.GitStateVerbs)}) and the forms of its other commands that write the same "
                + $"({string.Join(", ", AgentPolicy.GitStateForms)}, the option met wherever it stands among the command's words, by any cluster that holds "
                + "its letter or any start of its name git takes, and a word the shell expands held as one that may be it where its expansion may give it - "
                + "a word it begins, quoted or not, one after a start the option's own may have (-f$X), or one the shell splits into words of their own where it "
                + "stands unquoted (--depth=$n), the refusal saying that quoting it lets it through - never a quoted one inside a word that begins otherwise "
                + $"(--depth=\"$n\", \"refs/heads/$b:refs/remotes/o/$b\"), nor a pattern whose every match begins so (refs/heads/*)) are refused, and so are the gh commands that run them or write gh's own configuration ({string.Join(", ", AgentPolicy.GhStateCommands)}; "
                + "a command gh names by an alias of its own, pr co for pr checkout among them, read as the command it names, and "
                + "where gh reads its configuration from - GH_CONFIG_DIR, XDG_CONFIG_HOME, AppData, HOME - set for a command the gh config set it is) - reading git",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"and so are the gh commands that merge or start CI ({string.Join(", ", AgentPolicy.NeverCiCommands)}), the gh api endpoints and GraphQL "
                + "mutations that reach the same, gh alias set, and, where the request writes, the gh api endpoints "
                + "that push, open a pull request or make a release - for never merges,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"and a gh command named by none of gh's own ({string.Join(", ", AgentPolicy.GhCommands)}) an alias, gh's own co among them, or an extension, "
                + "as gh extension exec runs one, where gh runs - its command's own program, or past one that runs the words after it "
                + $"({string.Join(", ", CommandAnalysis.CommandRunners)}), never among another program's words, its data: each runs a command line from a "
                + "configuration file or an extension no rule reads, one an agent may have written, and is refused; an alias the command gives itself "
                + "(git -c alias.x=... x) is read as the command it is.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "on Windows, where a program splits its own command line and a Cygwin or MSYS program - told by the runtime beside it, cygwin1.dll or "
                + "msys-2.0.dll, the program the list starts and one a wrapper among its words starts both asked - expands a pattern or a brace in it, "
                + "those are read as the expansions they may be for such a program and for one this cannot find, while gh, git, dotnet and every other "
                + "program take them as the text they are",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A star, a question mark or a bracket matches a name that begins with a dot only where its shell would: a segment that begins with a dot "
                + "itself; anywhere in a string any of whose words names dotglob or globdots as bash and zsh read an option's name, in any case, with or "
                + "without its underscores and its no (shopt -s dotglob, BASHOPTS=dotglob, setopt GLOB_DOTS, unsetopt NO_GLOB_DOTS), or names "
                + "GLOBIGNORE or BASHOPTS at all; in a command PowerShell or cmd reads, whose wildcards list every name, or one given PowerShell's "
                + "-Force - so rm -rf * and build/* run, .g* is git's own, and a segment that may match .. (.?, .[.], .*) is read as .. as well, as a "
                + "shell whose globskipdots is off expands it; a parenthesis after a command's word, which bash refuses and zsh reads as a pattern's "
                + "group or a glob qualifier (rm -rf (.git|x), *(D)), is denied.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "What a command writes is each file its output redirects name and each a writing program's words name - a copy's destination however an "
                + "option names it (cp, install and ln's -t and --target-directory, alone, glued or in a cluster, else the last word; Copy-Item's "
                + "-Destination by any prefix or glued after its :, else its second word), each writer's options that take a value known to it, so cp x "
                + "dst --suffix y writes dst, and an option it does not know making every word but its first a destination; and the output a program's "
                + "option or word names: curl -o, wget -O, tar's archive and the directory it extracts into (-C, --one-top-level), unzip -d, sort -o, "
                + "patch -o and the directory patch -d writes in, xxd's output file, sed's w, "
                + "robocopy's and xcopy's destination, dotnet -o, a compiler's -o, ssh-keygen -f, Invoke-WebRequest -OutFile, Expand-Archive "
                + "-DestinationPath, Tee-Object and their kind - and a pattern among them is refused where it may reach git's own files or the state; a "
                + "command that removes or moves a directory whole - rm -r, Remove-Item -Recurse, rd /s, mv, Move-Item, rsync --delete, "
                + "git clean -dx - is refused where the directory holds a tree's .git, the state, a credential slot, a Claude Code directory or a place the user keeps credentials in, the "
                + "working directory among them, and a find or fd walk where it may remove or move one or what one holds.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a -c or --config-env of any key but the ones that only shape what git prints, --exec-path, clone's own -c or "
                + "--config and clone's and init's --template, GIT_CONFIG, the names through which git finds its programs and "
                + "hooks, and the homes it reads its global configuration from - "
                + "HOME, XDG_CONFIG_HOME, and the HOMEDRIVE, HOMEPATH and USERPROFILE git on Windows takes one from) is the git config it is",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-Expression's one string - by whatever name it is called, the call operator's string or a module's name before it among "
                + "them - read as a script of its own",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "git's own files - a .git, file or directory, at any depth beneath a root and in any case, a nested repository's and a "
                + "submodule's too (Write(**/.git/**)), beneath each tree the agent touches, the main checkout a worktree was made from among "
                + "them - are not written by a tool or a command, whatever protectedPaths says",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("an agent does not edit its own policy or what the next launch loads", prose, StringComparison.Ordinal);
        Assert.Contains("The protected paths (agentPolicy.protectedPaths, true where nothing is said)", prose, StringComparison.Ordinal);
        Assert.Contains("written out once as rules, each said whole: Write(.git/**), Write(.config/git/**),", prose, StringComparison.Ordinal);
        Assert.Contains("Write(Gemfile.lock), Write(composer.json), Write(composer.lock).", prose, StringComparison.Ordinal);
        Assert.Contains("Claude Code's own guard stays Claude Code's", prose, StringComparison.Ordinal);
        Assert.Contains("A step's writes is the one source that narrows", prose, StringComparison.Ordinal);
        Assert.Contains("the reporter's reads are narrowed to the run's directory alone", prose, StringComparison.Ordinal);
        Assert.Contains(
            @"an ask rule for every tool the launch has, and the restriction rules as deny rules, absolute once for each root in the POSIX "
                + @"form Claude Code matches (//c/x for C:\x), a Write rule as the Edit rule Claude Code holds its file tools' writes to",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("This is policy, not a sandbox", prose, StringComparison.Ordinal);
        Assert.Contains("Where this tool cannot decide a call it denies it, never allows", prose, StringComparison.Ordinal);

        // Beneath the policy, the operating system's isolation: the mechanism, the read-only machine and what is written,
        // what it hides and seals, the anchors and their cost, Claude Code inside or out, the probe, the mode and its codes,
        // the records, where it ends, and the verbs refused for it - named from the one lists the code holds.
        Assert.Contains(
            "On Linux, WSL 2's included, the mechanism is the system's bubblewrap 0.8.0 or newer - the first bwrap in an absolute "
                + "directory of the PATH outside the tree, none where the user could write it or a directory above it, for the tool "
                + "runs it outside - on macOS the system's own sandbox, /usr/bin/sandbox-exec, and on Windows Microsoft's MXC process "
                + "container, made through MXC's own library; each is what the contained step becomes in the child's place - on "
                + "Windows this tool again (isolate-exec), inside the step's job, which starts the program inside a container of its "
                + "own and hands back its streams and its exit code - so no child runs outside it for an instant. Inside it the "
                + "machine is read-only.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Beneath the policy, the operating system isolates every command an agent runs, so a command spelt past a reader of "
                + "words still reads no credential slot, and writes none of git's code-running files, none of its pointers to another "
                + "repository and none of the tool's state; what it leaves that git outside would go on with as it lies - an "
                + "operation's state, a replacement - fails what ran once it has, and the rest it writes is the work itself, whose "
                + "limits are said below.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Written are the agent's writable roots - its worktree, its work directory - and what git writes of a tree it "
                + "writes: its own git directory alone. A main checkout's .git is written whole, and an agent's worktree a "
                + "repository of its own, for git makes its lock files directly in it, but a main checkout's commondir - made "
                + "naming the directory itself, which git takes as no pointer, where a file made would lead every git to "
                + "another repository's own files, and never made in a repository that keeps its refs in a reftable, where "
                + "any file there breaks every commit git makes on a branch - and its worktrees/; a linked worktree's own git directory is its "
                + "directory under the common directory's worktrees/, and the common directory itself - its objects/, its "
                + "refs/ and reftable/ wherever the repository keeps them, its logs/ and Git LFS's lfs/ where there is one - "
                + "is read-only to the work: git add, commit, fetch, rm and status write the objects, the index and the refs "
                + "in a main checkout and in an agent's repository, while in a linked worktree - an agent an earlier version "
                + "made, a leg pinned to a plain worktree - git add and git fetch fail there, said at each launch over one "
                + "and on each leg's line over one; "
                + "and in a main checkout what writes another worktree's git directory: git worktree add, move, remove and "
                + "prune, git gc where there are linked worktrees. The refs written are those of a repository whose git "
                + "directory the work writes whole - the main checkout's over a side-task, a leg in place and a run's commit "
                + "and push hooks, an agent's own in its repository - every branch, "
                + "tag, stash and remote-tracking ref, and their reflogs - which a command spelt past the words policy can move; "
                + "a run's push sends the commit its commit step recorded, never what the branch names later. Written too are a "
                + "temporary directory of the launch's own, named by TMPDIR - on Linux the system's /tmp inside, so nothing another process "
                + "keeps there is reached, on Windows MXC's own; and the declared tool caches,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "each pointed by its variable into the cache directory of the child's write scope - the places it writes and the "
                + "Unix sockets outside it reaches - under the isolation's own (dssharness-isolation/caches under XDG_CACHE_HOME, "
                + "else ~/.cache - ~/Library/Caches on macOS - and under LOCALAPPDATA on Windows, each scope's directory beside a "
                + ".scope file naming its places), so a package one child plants is run only by a child that writes everything it "
                + "writes - the next launch of the agent, the next run of the leg - never by one that writes another tree or the "
                + "main checkout, a commit's or a push's hooks among them, or reaches the user's SSH agent, and a scope's caches are "
                + "taken away once none of its places is left:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            string.Join(", ", DssHarness.Core.Agents.Isolation.IsolationCaches.Declared.Select(cache => $"{cache.Variable} ({cache.Tool})"))
                + ". The user's own caches stay read-only, for a cache is code a later build runs as it lies; Cargo, Gradle and pub "
                + "keep theirs in a home the variable moves whole, so what the user keeps there - a registry, a property, a tool "
                + "pub activated - is not read inside,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Moving XDG_CACHE_HOME moves what the user installed under it too: on Linux Playwright's browsers are read where they "
                + $"are ({DssHarness.Core.Agents.Isolation.IsolationCaches.PlaywrightBrowsersVariable} at the user's ms-playwright, "
                + "where it is there and the variable names none already), so a browser installed inside fails; everything else there "
                + "- Hugging Face's models, pre-commit's environments - is fetched again into the scope's own.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Hidden - an empty place under bubblewrap, refused by macOS's sandbox, denied by MXC's container - are every credential "
                + "slot of the main checkout and every Claude Code directory - ~/.claude, the tool's own and the one the slot names for "
                + "the claude-code client's logins, one list for a launch and for the tool's executions alike - with ~/.claude.json "
                + "beside each, the shell snapshots Claude Code sources read again, and on Windows a claude-code launch's own read, "
                + "below. Hidden too, from every isolated command and every execution of the agents' work alike,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "leaves the launch or the execution with no isolation, saying which. Written by nobody, inside what is written, are "
                + "git's code-running files and its pointers to its own "
                + "directories and to other content: the hooks git runs and the common directory's hooks/ beside them, which git runs "
                + "again once core.hooksPath is gone; the common directory's config, config.worktree and info/; its refs/replace/ and "
                + "objects/info/, whose replacements and alternates would put other content in place of what every checkout reads; "
                + "its own commondir - made, where a main checkout's .git holds none, naming that directory itself "
                + $"({DssHarness.Core.Agents.Isolation.AgentIsolation.ItsOwnDirectory}), which git takes as no pointer, and in a "
                + "repository that keeps its refs in a reftable, where any commondir file in a git directory the work writes "
                + "breaks every commit git makes on a branch, never made and held absent instead - macOS's sandbox and MXC's "
                + "container denying its creation with nothing at the path, and bubblewrap, whose every rule is a mount that "
                + "holds no path that is not there, leaving the ground with no isolation - and its "
                + "worktrees/; each linked worktree's config.worktree, commondir and gitdir; every linked worktree's .git file, the "
                + "agent's own and every other git lists; and every git directory's modules/ whole - the common one's and each linked "
                + "worktree's - which holds the submodules' own git directories, so a submodule's git fails inside. Written by nobody "
                + "too are every configuration file git says it reads there (git "
                + "config --show-origin), every file one includes whether its condition holds now or not - an onbranch: a later "
                + "checkout makes true among them -",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Hidden too, from every isolated command and every execution of the agents' work alike, are the places the user keeps "
                + "credentials in: ssh's keys (~/.ssh but its known_hosts and config), AWS's credentials and sign-in caches (~/.aws, "
                + "$AWS_SHARED_CREDENTIALS_FILE, $AWS_CONFIG_FILE), Azure's sign-in tokens (~/.azure, $AZURE_CONFIG_DIR), gh's login "
                + "($XDG_CONFIG_HOME/gh, $GH_CONFIG_DIR, %APPDATA%/GitHub CLI on Windows), Kubernetes' cluster credentials (~/.kube, "
                + "$KUBECONFIG, each it lists), Docker's registry logins (~/.docker/config.json, $DOCKER_CONFIG/config.json), the logins "
                + "curl and git read (~/.netrc, ~/_netrc on Windows), git's credential store (~/.git-credentials, "
                + "$XDG_CONFIG_HOME/git/credentials), GnuPG's private keys (~/.gnupg/private-keys-v1.d, ~/.gnupg/secring.gpg, "
                + "$GNUPGHOME/private-keys-v1.d, $GNUPGHOME/secring.gpg, %APPDATA%/gnupg/private-keys-v1.d on Windows, "
                + "%APPDATA%/gnupg/secring.gpg on Windows), Google Cloud's credentials (~/.config/gcloud, $CLOUDSDK_CONFIG, "
                + "%APPDATA%/gcloud on Windows), npm's tokens (~/.npmrc, $NPM_CONFIG_USERCONFIG, $npm_config_userconfig), PyPI's upload "
                + "tokens (~/.pypirc), Cargo's registry tokens (~/.cargo/credentials.toml, ~/.cargo/credentials, "
                + "$CARGO_HOME/credentials.toml, $CARGO_HOME/credentials), Maven's master password (~/.m2/settings-security.xml), "
                + "Gradle's properties with their publishing and signing credentials (~/.gradle/gradle.properties, "
                + "$GRADLE_USER_HOME/gradle.properties) - $NAME where this tool's own environment names one, $XDG_CONFIG_HOME "
                + "~/.config where it names none, %APPDATA% AppData\\Roaming in the home where it names none. ssh's known_hosts and "
                + "config are read again, for ssh reads them itself to reach a host - but under MXC's container, which grants nothing "
                + "beneath a denial, where ssh's directory is hidden whole. agentPolicy.hiddenPlaces adds more and takes none away: "
                + "each a whole path or one under ~/, a directory where it ends with a separator, else a file. The words policy "
                + "refuses a file tool's read and write in one, a search over one, and a command that names one - ssh's own files "
                + "among them, which inside only the program that needs them reads - or a directory that holds one and may read it, "
                + "as it refuses a slot. Left out, for builds read them: ~/.nuget/NuGet/NuGet.Config and "
                + "%APPDATA%/NuGet/NuGet.Config, for every restore reads its package sources there and refuses one it cannot read; "
                + "~/.m2/settings.xml, for every Maven build reads its mirrors and repositories there and refuses one it cannot read - "
                + "a password either holds is better given as a value. A build that reads a hidden place fails inside - a private "
                + "registry's token in ~/.npmrc, a cloud's key in ~/.aws, a git dependency fetched over ssh with the user's own key - "
                + "so hand it the secret as a value instead: the runner's environment carries it, read by this tool. A place that is "
                + "not there is made, empty, only where a command could make it, inside a place written: the home is read alone by "
                + "every isolated child, so nothing is made there and the user's home is left as it is. One that holds a tree, or a "
                + "place written, leaves the launch or the execution with no isolation, saying which.",
            prose,
            StringComparison.Ordinal);
        Assert.All(DssHarness.Core.Agents.Isolation.UserCredentials.Declared, place => Assert.Contains(place.Spelled, prose, StringComparison.Ordinal));
        Assert.All(DssHarness.Core.Agents.Isolation.UserCredentials.LeftOut, place => Assert.Contains(place.Spelled, prose, StringComparison.Ordinal));
        Assert.Contains(
            "No directory above a guarded place whose own directory is written, and no writable root itself, is renamed or removed - "
                + "a place moved away would take its rule with it and leave its name free to be made again, and a root put back as a "
                + "link would lead the next launch's rules elsewhere. A launch has no isolation - which the mode below turns into a "
                + "refusal or a notice - where its places cannot be held: git cannot say where a tree keeps its files (git 2.31 or "
                + "newer answers) or list its repository's replacements, a main checkout's .git holds a "
                + $"{DssHarness.Core.Agents.Isolation.AgentIsolation.CommonDirectoryFile} leading anywhere but to itself - to another "
                + "repository's own files - or holds one at all, or none and the mechanism holds no path that is not there, in a "
                + "repository that keeps its refs in a reftable, where any commondir file in a git directory the work writes breaks "
                + "every commit git makes on a branch, a file git's configuration includes is not there inside a place the agent "
                + "writes, where a "
                + "command could make it (an empty file includes nothing), a link stands where the agent writes on the way to a "
                + "guarded place, or, under bubblewrap, a guarded place stands on a file system that finds a name in any case - a "
                + "Windows drive under /mnt - where a mount holds one spelling alone. Once a launch or one of the tool's executions has "
                + "run under a mechanism, however it ended, the tool looks at what no rule could hold before anything of its own goes "
                + "on over the trees: the state of an operation under way - "
                + $"{string.Join(", ", DssHarness.Core.Agents.Isolation.AgentIsolation.OperationState.Select(name => name + "/"))} - "
                + "made or changed in a tree's own git directory it wrote, which git's next continue outside, the user's or an "
                + "editor's, goes on with as it lies, running each exec line its todo holds; or a replacement made or moved in a "
                + "repository whose refs it wrote, as git lists them whatever keeps them - loose, packed-refs or a reftable - which "
                + "every git started outside would read in an object's place. Either fails the step, the leg, the commit or the push, "
                + "naming it and the remedy - remove the state, never continue or abort it, for an abort puts the branch where the "
                + "state says - as does a git that cannot list the replacements, which is waited for to its end however many refs it lists. Every git "
                + "of this tool's own runs with no file-system monitor, no object replaced "
                + $"({DssHarness.Core.Git.GitOwnSettings.NoReplaceVariable}=1) and, in a tree an isolated agent or execution works "
                + "over - a worktree or the main checkout - with the git directories git named before it ran (GIT_DIR, "
                + "GIT_COMMON_DIR), whatever the tree's own files say later; otherwise it reads the repository's configuration as git "
                + "does, its filters among it, which is why every configuration file is written by nobody. The user's own git, an "
                + "editor and a prompt read the repository as it stands: the refs and objects the work wrote, an operation's state it "
                + "left until a person removes it, and a repository it made inside its tree, whose own configuration - an fsmonitor, a "
                + "filter - git started in that directory reads and runs, as it does any repository's.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "the user's runtime directory whole - their service manager, message bus and agents' sockets - and the container "
                + "engines' sockets are hidden; where the kernel has Landlock's scope (Linux 6.12 or newer) the contained step enters "
                + "it first, so no abstract Unix socket outside is reached and no process outside signalled, and where it has not, the "
                + "record says so, and every launch, in its stream, and every leg, on its line, says as it starts what is left "
                + "reached.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "macOS's sandbox denies every operation it does not allow - never allowing all but a list, which would leave "
                + "LaunchServices free to start a program outside it - and allows what a build, a test and a shell do as the user: its "
                + "own processes, the system's settings by the names a build asks - never another process's arguments or environment "
                + "- a terminal, the network, the services that resolve names and trust certificates, every file read and no Unix "
                + "socket but the name resolver's and those under the launch's temporary directory, each place ruled at its real "
                + "spelling (/var is /private/var there); it is no mechanism where this tool already runs inside a sandbox, for macOS "
                + "starts none inside another.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Under bubblewrap the child has a user namespace of its own that allows no further one - a claude-code launch's outer "
                + "layer among them, for nothing is started inside it - and a process namespace with its own /proc, so "
                + "no command reaches another process's environment;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "MXC's container grants nothing it is not told to: every fixed drive is read, each writable root written, each place "
                + "written by nobody read, each hidden place denied outright. It meets a place by its path alone and refuses no rename "
                + "alone, so each directory above a guarded place whose own holder is written is read alone, every entry it holds "
                + "written again as the profile has it - nothing is made or removed directly inside it, so git makes no lock file in a "
                + ".git, or a linked worktree's directory under it, that holds a sealed place: git add, commit, fetch and push fail "
                + "there - in a worktree, over a side-task's main tree, and in a run's commit and push steps - which each record says "
                + "as the anchors' cost.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A claude-code launch is given no tool of Claude Code's own - --tools names none but a side-task's own web tools - so "
                + "Claude Code runs no command and opens no file of the agent's in its own process: the engine's tool server serves "
                + "the tool runtime's in their place (view_file, find_files, search_files, read_result, create_file, replace_in_file, "
                + "insert_in_file, create_directory, delete_file, delete_directory, move_path, run_command and the rest), each file read "
                + "through the file the engine opened, decided again by where the system names it, and each written, made, moved or "
                + "deleted relative to its directory held from the writable root it was decided in with no link on the way, so a "
                + "link a command - one left running in the background among them - puts on a path between the decision and the act "
                + "reaches nothing. Claude Code's own process runs apart from the agent's commands, reading its login and the key it "
                + "is handed there. On Linux it runs inside an outer layer of bubblewrap, which holds its process alone: the machine "
                + "read-only, every credential slot hidden, git's code-running files and the tool's state sealed as for the commands, "
                + "and written but its writable roots, its own temporary directory in the launch's own records - named to it by "
                + "CLAUDE_CODE_TMPDIR, so its temp is put away with the launch - the declared caches, Claude Code's own directories "
                + "and ~/.claude.json, without which it does not start. Where Claude Code never ran, its directory and .claude.json "
                + "(holding {}) are made first, for inside it can make neither. On macOS, whose sandbox starts none inside another, "
                + "Claude Code's own process runs outside every layer, and each launch says so as it starts: there it runs with the "
                + "user's rights, though no command of the agent's and no file of its runs there. Claude Code's own sandbox is turned "
                + "off in its settings, for none nests in this tool's.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "On Windows, where nothing becomes another program, the launch runs whole inside MXC's container with Claude Code's "
                + "directories read alone, its temporary directory MXC's own inside it: a login whose token has run out is not renewed "
                + "inside until a claude run outside renews it - written, they would let what runs inside plant what Claude Code runs "
                + "outside later. Where there is no isolation at all - preferred, on a machine with none - the tool runtime serves the "
                + "launch all the same, the words policy alone between the agent and the machine. Every client's run_command, Claude "
                + "Code's among them, starts each command inside it.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "on the tree's own file system under its runs directory: a canary in a hidden place unread, by its name and by another "
                + "case of it, a place written by nobody unwritten by either, a place outside every open one unwritten, an open place "
                + "written - on Windows after MXC's library says this Windows has the container (Windows 11 24H2 build 26100.9278 or "
                + "later, or 26H2 build 26300.9550 or later), and followed by each of node, git and Git Bash's sh found on this machine "
                + "started inside it and asked to answer, one that does not making none, said with how it ended. MXC 1.0.1 ends every "
                + "program of Git Bash's with 0xC0000142, a library that would not start, so where Git Bash is installed Windows has "
                + "none yet; from WSL 2, bubblewrap isolates.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "agentPolicy.isolation says what happens where this machine has none: required, where nothing is said, starts no agent "
                + $"that may run a command - its step ends failed ({HarnessExit.ToolMissing}), the advisor gives no advice, run-side-task "
                + $"exits {HarnessExit.ToolMissing}, each saying why and the remedy (on Ubuntu, where "
                + "/proc/sys/kernel/apparmor_restrict_unprivileged_userns is 1, the bwrap-userns-restrict AppArmor profile; on Windows "
                + "the builds MXC needs, or WSL 2); preferred "
                + "runs it on the words policy alone, said at every launch. An agent that may run no command - the reporter - needs "
                + "none, and none is probed.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "the mechanism and its version - macOS's own for its sandbox, MXC's library's for its container - what it leaves reached "
                + "and what its anchors cost, or why there was none. Where it ends: the machine stays readable as the user reads it but "
                + "the hidden places - a credential kept anywhere else among it - and the system's own credential stores are services, "
                + "not places: on macOS the login keychain is reached through the system's security service, which the sandbox lets "
                + "through for the trust of certificates; on Windows MXC's container shows the Credential Manager empty; on Linux the "
                + "keyring sits behind the user's message bus, hidden with the runtime directory. The network is theirs, and what no "
                + "rule at a path reaches - a hard link made before the launch, another path bound to the same files - is reached; "
                + "inside, no hard link can be made from the machine's files into a written place: under bubblewrap each place is a "
                + "mount of its own and a link fails across them, macOS's sandbox denies every operation it does not allow and no "
                + "rule of the profile's allows one, and MXC's container grants the machine read alone, which a hard link needs "
                + "written - each held by a canary in its real suite, whose failure would name the link allowed. git's "
                + "refs and object store, and Git LFS's, are written as git needs them in each ground's own repository - the "
                + "main checkout's over a side-task, a leg in place and a run's commit and push hooks, an agent's repository "
                + "its own, a linked worktree's none, its common directory read-only to the work: so a command over the main "
                + "checkout can move any branch, tag, stash or remote-tracking ref, which a person's checkout outside then takes, "
                + "and, as git's checkout reads an object as it lies without hashing it again, put other "
                + "content under a name the store already holds, which a checkout outside then writes.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "so each is refused to every agent by name, a side-task's as well as a step's: "
                + $"{string.Join(", ", DssHarness.Core.Agents.Policy.AgentPolicy.SlotOrStateVerbs)}; those that only read, and those "
                + $"that write only the tree's own files, stay open. {WorkflowValidationService.CommandName} and {WorkflowExportService.CommandName} "
                + "are among them: where a person's run of either would fetch a remote workflow into the workflow cache, an agent's - "
                + $"told as {DssHarness.Core.Agents.SideTasks.SideTaskService.RunCommand} tells it, by {DssHarness.Core.Agents.AgentChildEnvironment.AgentMarker}, "
                + "which its every child carries, or a process of this tool's above it in the process tree, one whose tree cannot be read "
                + "taken as no person's - reads the cache alone and writes nothing to it, and one the cache does not hold is refused saying what "
                + $"made the command an agent's and that a person fills the cache from {DssHarness.Core.Agents.AgentAncestryAnswer.APersonsShell}, with "
                + $"'dssharness {WorkflowValidationService.CommandName}' or 'dssharness {WorkflowExportService.CommandName} --from'.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(WorkflowValidationService.CommandName, DssHarness.Core.Agents.Policy.AgentPolicy.OpenVerbs);
        Assert.Contains(WorkflowExportService.CommandName, DssHarness.Core.Agents.Policy.AgentPolicy.OpenVerbs);

        // The tool's own executions of the agents' work inside the same isolation, the codes the mode turns into its own.
        Assert.Contains(
            "is held inside it, its own profile from the leg's ground, built as a launch's is: the machine read-only; every "
                + "credential slot of the main checkout, every Claude Code directory and every place the user keeps credentials in "
                + "hidden; the tree the leg works in - "
                + "the step's tree or its copy under .legs - a sweep's workers under .m, the build directory of the leg's "
                + "run of each runner action it runs, its own and a run check's ({actionBuild}, each step's {stepBuild} in it), a "
                + "temporary directory of its own and the declared tool caches written, and inside them git's code-running files, "
                + "the configuration directory - but those build directories, under it - and the tool's state written by nobody.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The run's records are no place the work writes: every log, the ledger, the run's claim and its stop are the tool's "
                + "own, written from what each phase prints. What the tool itself reads out of, or writes into, a place such work "
                + "writes - a runner's step's own directory and its kept outputs, a test's evidence kept on failure, a sweep's sites mutated and put back in "
                + "its workers, a sync's every read, write, removal and listing in a copy - goes through no link below that "
                + "place's root",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "where the leg's work is held inside it under required, each runs from an empty directory outside every tree "
                + "(outside/ under the isolation's own), so a program that looks for its project where it runs or above it - a "
                + "compose file, a package's scripts, a makefile, a repository - finds none of the agents', and each is read first "
                + "as the policy reads an agent's command - its words, every command it runs, every path a word names, a "
                + "source:target's and a search path's parts among them, and every value the leg's environment gives it. One that "
                + "names a place in the tree the leg works in, the run's or the main checkout, or a directory holding one, that "
                + "names a place by what its shell would expand, that moves to a directory that is no whole path, or that cannot "
                + "be read whole skips the leg (skipped-unavailable) before any set-up comes up; one that fails says where it ran, "
                + "where nothing of the tree is found - keep what a line reads outside the trees and name it by its whole path, or "
                + "bring what it serves up by hand.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "an ssh host holds none of this machine's slots, state or git's files, which a sync never carries, for an ssh host "
                + "is taken to be another machine that reaches none of this one's files - one that is this machine, or a WSL "
                + "distribution, a virtual machine or a container reached over ssh that shares its drives, cannot be told from "
                + "another by its name or its address, which ssh's own configuration may point anywhere and a loopback port may "
                + "forward anywhere, and runs the agents' work outside the isolation, this machine's files written: name no such "
                + "host for a run's legs where the agents' work must be held;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a WSL distribution runs on this machine and reaches its drives under /mnt, written, its slots and every tree's git "
                + "files among them, so a run's step whose legs' work is held inside the isolation skips such a leg under required "
                + "(skipped-unavailable) and runs it there under preferred, its line saying so.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "one over ssh signs through the user's SSH agent alone, for their keys under ~/.ssh are hidden, its known_hosts and "
                + "config read. Under bubblewrap and macOS's sandbox a push to "
                + "a remote over ssh (ssh://, git+ssh:// or host:path, as git reads its push address), and a commit the repository "
                + "signs with an ssh key (commit.gpgsign with gpg.format ssh), reach the one Unix socket outside that is the user's SSH "
                + $"agent's ({DssHarness.Core.Agents.Isolation.AgentIsolation.SshAgentSocketVariable}), wherever it stands - inside "
                + "ssh's hidden directory among it, named to them at its real spelling, so a link there leads nowhere hidden - their "
                + "hooks with them - and no other; every other commit and push, and every leg, reaches none, so a hook reaches the "
                + "user's SSH agent only where git's own work does; MXC's container is handed none, and as it hides ~/.ssh whole a "
                + "push over ssh inside it finds neither key nor known_hosts and fails - a push over https, whose credential this "
                + "tool hands in as values, works there. A commit the repository signs with "
                + "gpg or x509 reaches no agent of the user's where its socket is hidden - in the user's runtime directory under "
                + "bubblewrap, and every one under macOS's sandbox and MXC's container - and fails there, as does one signed with an "
                + "ssh key under MXC. One signed with an ssh key whose user.signingKey names a file under ~/.ssh fails as well, for "
                + "the file is hidden: name the public key itself (key:: and its line) and keep the private key in the agent. Every "
                + "other git command of this tool's runs none "
                + "of the repository's hooks - core.hooksPath pointed at the null device, set as every git of the tool's sets its own "
                + "settings, through GIT_CONFIG_PARAMETERS - so a fold, a refresh, a rebase, a sync or a question of the tool's never "
                + "runs the agents' work as a side effect of a checkout, an index written or a ref moved;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "agentPolicy.isolation decides each as it decides a launch: where this machine has none, "
                + $"{DssHarness.Core.Agents.Isolation.AgentIsolationMode.Required} fails the step ({HarnessExit.ToolMissing}) with nothing run - a leg whose own cannot "
                + $"be had, its tree one git does not answer for, is skipped-unavailable - and {DssHarness.Core.Agents.Isolation.AgentIsolationMode.Preferred} runs it "
                + "outside, each leg's line saying so. The step answers isolation: the mode, with the mechanism and its version, what "
                + "it leaves reached and what its anchors cost, or why there was none.",
            prose,
            StringComparison.Ordinal);

        // The architecture names each owner of the isolation by the type that is it.
        var architecture = Prose.Document("docs", "architecture.md");

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Processes.Bubblewrap),
            nameof(DssHarness.Core.Processes.SandboxExec),
            $"{nameof(DssHarness.Core.Processes.SandboxExec)}.{nameof(DssHarness.Core.Processes.SandboxExec.Policy)}",
            $"{nameof(DssHarness.Core.Processes.IsolationProfile)}.{nameof(DssHarness.Core.Processes.IsolationProfile.Anchors)}",
            nameof(DssHarness.Core.Processes.IsolationProfile),
            nameof(DssHarness.Core.Processes.IIsolationMechanism),
            nameof(DssHarness.Core.Processes.Landlock),
            nameof(DssHarness.Core.Platform.MacSandboxHost),
            nameof(DssHarness.Core.Processes.MxcProcessContainer),
            $"{nameof(DssHarness.Core.Processes.MxcProcessContainer)}.{nameof(DssHarness.Core.Processes.MxcProcessContainer.AnchorReadAlone)}",
            nameof(DssHarness.Core.Processes.IsolateExec),
            nameof(DssHarness.Core.Platform.WindowsContainerHost),
            nameof(DssHarness.Core.Platform.IsolationPlatform),
            $"{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches)}.{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches.Declared)}",
            nameof(DssHarness.Core.Agents.Isolation.IsolationDecision),
            $"{nameof(DssHarness.Core.Agents.Isolation.LaunchIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.LaunchIsolation.ForClaudeCode)}",
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeShell),
            $"{nameof(DssHarness.Core.Processes.IsolatedShell)}.{nameof(DssHarness.Core.Processes.IsolatedShell.Run)}",
            $"{nameof(DssHarness.Core.Processes.IsolatedShell)}.{nameof(DssHarness.Core.Processes.IsolatedShell.Marked)}",
            $"{nameof(DssHarness.Core.Processes.IsolatedShell)}.{nameof(DssHarness.Core.Processes.IsolatedShell.Prepared)}",
            nameof(DssHarness.Core.Processes.IsolatedShell.ReceiptOf),
            nameof(DssHarness.Core.Processes.IsolatedShell.EndFileOf),
            nameof(DssHarness.Core.Processes.IsolatedShellHanding),
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools.FileTools)}",
            $"{nameof(DssHarness.Core.Git.GitOwnSettings)}.{nameof(DssHarness.Core.Git.GitOwnSettings.Apply)}",
            $"{nameof(DssHarness.Core.Git.GitOwnSettings)}.{nameof(DssHarness.Core.Git.GitOwnSettings.Arguments)}",
            $"{nameof(DssHarness.Core.Git.GitOwnSettings)}.{nameof(DssHarness.Core.Git.GitOwnSettings.NoRecursion)}",
            nameof(DssHarness.Core.FileSystem.UnfollowedFiles),
            nameof(DssHarness.Core.FileSystem.HeldDirectory),
            $"{nameof(DssHarness.Core.FileSystem.IFileSystem)}.{nameof(DssHarness.Core.FileSystem.IFileSystem.Hold)}",
            nameof(DssHarness.Core.FileSystem.LinkOnTheWayException),
            nameof(DssHarness.Core.Agents.Isolation.IsolationHidden),
            $"{nameof(DssHarness.Core.Agents.Isolation.IAgentIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.IAgentIsolation.Hidden)}",
            $"{nameof(DssHarness.Core.Sync.SyncServe)}.{nameof(DssHarness.Core.Sync.SyncServe.Pull)}",
            $"{nameof(DssHarness.Core.Sync.ISyncTransport)}.{nameof(DssHarness.Core.Sync.ISyncTransport.PullFileAsync)}",
            $"{nameof(DssHarness.Core.Orchestration.OrchestratorLayout)}.{nameof(DssHarness.Core.Orchestration.OrchestratorLayout.HandedDirectory)}",
            nameof(DssHarness.Core.Mutations.WorkerSite),
            nameof(DssHarness.Core.SetUps.SetUpConfinement),
            $"{nameof(DssHarness.Core.Agents.Isolation.ExecutionIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.ExecutionIsolation.InDistribution)}",
            nameof(DssHarness.Core.Git.GitDirectoryPins),
            nameof(DssHarness.Core.Git.GitHooks),
            $"{nameof(DssHarness.Core.Agents.Isolation.ClaudeCodeDirectories)}.{nameof(DssHarness.Core.Agents.Isolation.ClaudeCodeDirectories.Of)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation.CommonDirectoryFile)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation.ItsOwnDirectory)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation.OperationState)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.AgentIsolation.IncludeTargets)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.IsolationDecision)}.{nameof(DssHarness.Core.Agents.Isolation.IsolationDecision.AfterwardAsync)}",
            $"{nameof(DssHarness.Core.Git.IGitClient)}.{nameof(DssHarness.Core.Git.IGitClient.ParentsOfAsync)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches)}.{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches.Installed)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches)}.{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches.ScopeName)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches)}.{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches.Prepared)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches)}.{nameof(DssHarness.Core.Agents.Isolation.IsolationCaches.Sweep)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.ExecutionIsolation)}.{nameof(DssHarness.Core.Agents.Isolation.ExecutionIsolation.Outside)}",
            $"{nameof(DssHarness.Core.Legs.LegWorkload)}.{nameof(DssHarness.Core.Legs.LegWorkload.WritesIn)}",
            $"{nameof(DssHarness.Core.Agents.Isolation.ExecutionGround)}.{nameof(DssHarness.Core.Agents.Isolation.ExecutionGround.ReachesSshAgent)}",
            $"{nameof(DssHarness.Core.Git.GitSsh)}.{nameof(DssHarness.Core.Git.GitSsh.PushesOverSshAsync)}",
            $"{nameof(DssHarness.Core.Git.GitSsh)}.{nameof(DssHarness.Core.Git.GitSsh.SignsCommitsWithSshAsync)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }

        // The side-tasks topic says the same of what a side-task's words are held to, and where that reading ends.
        Assert.Contains(
            "and run-side-task itself, refused to every agent where a word of a command or of a script the policy reads names it or a verb the "
                + "shell expands may, and refusing itself where it is started under one, whose mark (DSSHARNESS_UNDER_AGENT) a command that sets "
                + "it, takes it away, may take it away or names it but as data is refused, or where a process of this tool's is among the ones that started it - the "
                + "process tree, walked parent by parent, a process that took a freed id never read as a parent, the tool's own runtime told by the "
                + "file it runs where the table gives one (the installed tool, a build's program, the runtime running the tool's assembly) and by "
                + "its name only where it gives none, so a launcher of the tool's name - a script, another program's shim - is none; one detached from the agent's tree "
                + "(at, a tmux server, a double fork) is told by the mark alone, and a tree that could not be read is refused saying why; each "
                + "refusal says which made the command an agent's (the mark, the process of this tool's by its name and id, so a side-task any "
                + "process of this tool's starts is refused, or why the tree could not be read) and that a person starts one from "
                + $"{DssHarness.Core.Agents.AgentAncestryAnswer.APersonsShell} - a "
                + "side-task is a person's own conversation, and no agent starts another. The "
                + "main tree is the side-task's working directory and holds the credential slots: a command over it whole - named . or given no "
                + "path, an archive of it or a search - is not told from one over its sources, and reaches their files; and code an interpreter "
                + "runs (python -c, perl -e, awk's system) or a process apart from the agent's tree does what no word the policy reads says. This "
                + "is a policy of what a command names, not a sandbox; beneath it, where this machine has one, the operating system's isolation "
                + "('help agent-clients') hides every credential slot, and every place the user keeps credentials in, from each command - one over the main tree whole finds each slot empty, or is refused it - "
                + "and keeps it from git's code-running files and the tool's state.",
            Prose.Words(HelpText.Of("side-tasks").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic's protected-path list is the one the policy holds, word for word: a change in it is a
    /// change someone made, in both places at once or in neither.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_PinsTheProtectedPaths()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        foreach (var rule in AgentPolicy.ProtectedPathRules)
        {
            Assert.Contains($"Write({rule})", prose, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The README and the architecture say the agents' policy as the help does: one policy, default allow except the
    /// root actions and the restrictions, the command read whole, and the fail-closed rule - and the architecture
    /// names its owners (<c>AgentPolicy</c>, <c>CommandAnalysis</c>, <c>GlobPattern</c>).
    /// </summary>
    [Fact]
    public async Task TheDocumentsSayTheAgentsPolicy()
    {
        Assert.Contains("What an agent may do is one policy, the same on every client", Prose.Document("README.md"), StringComparison.Ordinal);
        Assert.Contains("A repository declares them under `agentPolicy.restrictions`", Prose.Document("README.md"), StringComparison.Ordinal);
        Assert.Contains("This is policy, not a sandbox", Prose.Document("README.md"), StringComparison.Ordinal);
        Assert.Contains("Where this tool cannot decide a call it denies it, never allows", Prose.Document("README.md"), StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains("What an agent of a run may do is one policy, `Agents\\Policy\\AgentPolicy`, the same on every client", architecture, StringComparison.Ordinal);
        Assert.Contains("read whole before it is matched, `Agents\\Policy\\CommandAnalysis`", architecture, StringComparison.Ordinal);
        Assert.Contains("no agent starts while the tool itself runs elevated (`IHostPlatform.IsPrivilegedProcess`", architecture, StringComparison.Ordinal);
        Assert.Contains("Where the policy cannot decide a call it denies it, never allows", architecture, StringComparison.Ordinal);

        // The credential-printing refusal and the quote-aware reading of expansions, in every document that says them.
        Assert.Contains(
            "and so is any command that prints a credential (`gh auth token`, `gh auth status --show-token` or `-t`, "
                + "`gh auth git-credential get`, `gh config get oauth_token`, `git credential fill`, and any helper's "
                + "`git credential-* get` - `credential-store`, `credential-manager`, `credential-osxkeychain` and the rest - each "
                + "met by its words anywhere after its command, options before or between them; an alias given one - `git config "
                + "alias.cf 'credential fill'`, `git -c alias...`, `gh alias set` - is read as the command it names where it is "
                + "given, and `gh alias import` is refused)",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains("(`AgentPolicy.CredentialPrintingCommands`, pinned by a test and printed by `help agent-clients`", architecture, StringComparison.Ordinal);
        Assert.Contains(
            "an expansion is read only where the shell would make it, for the reader keeps, per word, which of its parts were quoted",
            architecture,
            StringComparison.Ordinal);
    }
}
