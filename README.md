# DssHarness

One cross-platform CLI for a repository's worktrees, builds, tests and cross-host
work — configured entirely from a file in the repository, not compiled into the tool.

```bash
dotnet tool install --global dssharness
dssharness --help
```

To update it later, on a machine anything else may be running it on:

```bash
dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool --wait
```

That runs the newest release beside the installed one and updates it only once no `dssharness`
process is running on the machine, naming each it waits for. A bare `dotnet tool update` typed
beside a running command takes the tool away from every command typed meanwhile, and on Windows
fails. `dssharness update-tool` is the same door typed short, where the system lets a running
program be replaced; on Windows it changes nothing and says the line above, with the options it
was typed with. Once the update has begun the command does not stop it, and says it done only once
the tool is listed as the new release.

## Why

A repository's build and test work usually accumulates as a pile of paired
`.sh`/`.ps1` scripts. They drift apart, they encode one repository's facts, and they
fail differently on every platform — a Windows path limit here, a frozen macOS bash
there, a missing `rsync` somewhere else.

`DssHarness` replaces that with a single tool whose behaviour is declared in
`.harness-config/config.json`. **Nothing about a specific repository, language or
toolchain is built into the tool.** If a behaviour cannot be expressed in
configuration, that is a defect.

## Getting started

```bash
dssharness verify-git     # is git installed, and is this a repository?
dssharness init           # create .harness-config in this tree and seed config.json
dssharness legs           # where each leg can run, or why it cannot
dssharness help           # reference material: exit codes, config, legs, layout
```

Every CMake configure is asked which compilers it resolved, and each leg's line names them —
`compiler: MSVC 19.51.36231 (C, CXX)` — so every verdict says which compiler produced it; a
toolchain's `compilerId` fails a leg CMake configured with another. A language only a dependency's
`project()` enables — C, where a C++ project fetches googletest — is identified from CMake's own
record of it, because CMake's answer gives it no id.

`init`'s `msvc` toolchain names the `visualStudio` developer environment, so an MSVC leg builds from
a plain shell: the host that runs it runs Visual Studio's own `vcvarsall.bat` for the leg's processor
and starts every process of the leg in what it set, and the leg's line names the instance and tools
it used. A host without Visual Studio's C++ build tools turns the leg away as a tool missing.

`init` inspects the repository and seeds a configuration that already matches it —
a CMake project gets toolchains and `ctest`, a .NET solution gets `dotnet test` — with
legs for the operating system and processor of the machine it ran on. With no project
detected it seeds no legs, and `legs` fails until some are declared.

## Commands

| Command | Does |
|---|---|
| `init [--install-tools]` | Create `.harness-config` in the tree it runs in, a worktree's included, seed `config.json`, add ignore rules; installs tools only when asked |
| `verify-git` | Check git is installed and this is a repository |
| `create-worktree <name>` | Create a worktree (`--random` generates the name) |
| `delete-worktree <name> [--force]` | Remove a worktree and everything under it, its copies on hosts, and the copies kept in each - the mutation workers in `.mutations`, and on this machine the leg copies in `.legs`; refuses one holding work that would be lost, a locked one, one whose evidence directories hold measurements, or one with a copy in it that a sweep or a run still holds or that cannot be removed, without `--force`; an agent's repository of its own is deleted whole, the commits made in it that no ref of the main checkout's reaches counted as work that would be lost |
| `list-worktree [--hosts] [--json]` | List existing worktrees with the commit each was made from, the copies hosts keep of them, and the copies left by worktrees that are gone; `--hosts` also asks each host what it keeps, and how large each copy is; `--json` says of each whether it is one of git's linked worktrees (`"kind": "worktree"`) or an agent's repository of its own (`"kind": "repository"`) |
| `create-orchestrator <o> --model <id> [--parallel N]` | Create an orchestrator under `.orchestrators`; `--parallel` (4 unless given) is the most agents with a worktree at once |
| `create-agent <o> <a> --model <id> [--empty \| --no-worktree]` | Create an agent: its record, its worktree at `<worktrees.root>/<o>/<a>` - a git repository of its own that borrows the main checkout's objects, not one of git's linked worktrees, so `git worktree list` in the main checkout does not show it and nothing done in it reaches the main checkout's refs or objects - and its seed, the main tree's uncommitted state handed to it; `--no-worktree` makes the engine's own kind, a record and two directories that read the main tree and record no session - a person has no use for one by hand |
| `seed-agent <o> <a> [--empty] [--force]` | Seed a live agent again, with every path the main tree holds otherwise than the agent shares it, committed or not; refused over changes of its own without `--force`; a file written after it was weighed stops it, exit 21, never written over |
| `refresh-agent <o> <a> [<path>... \| --all] [--except <path>]... [--stale <path>]... [--merge <path>]... [--apply] [--json]` | Copy into a live agent every path under the paths - the anchor registries' directory by default, anywhere in the tree with `--all` - that the main tree holds otherwise than the agent shares it, committed or not, recorded as handed to it; a path the agent already holds as the main tree does is recorded as shared; refused over a path the agent changed, naming each, unless `--except` leaves it as the agent changed it, `--stale` hands it the main tree's copy where the main tree's history holds the agent's very bytes at that path, or `--merge` writes a clean three-way merge of the two into its worktree as its change, the path recorded as handed the main tree's copy; the arm registry both changed is merged by arm, unasked, and taken where the result reads as a registry; a file the agent's session wrote after it was weighed stops it, exit 21, never written over |
| `rebase-agent <o> <a> [--apply] [--json] [--merge <path>] [--settled <path>]` | Move a live agent's base to the main tree's HEAD: what the main tree committed since comes in as git holds it, its own changes and what it was handed stay; a path both changed is refused with what a three-way merge of it gives, unless `--merge` names it and its merge is clean - written into the worktree as the agent's own change on the new base - or `--settled` keeps the copy reconciled by hand; the arm registry is merged by arm, unasked, and taken where the result reads as a registry; refused over anything of its own what comes in would go over, a file renamed in case alone over the agent's copy at its old name, or its deletion of it, among it; a file the agent's session wrote after it was weighed stops it, exit 21, never written over; run again after one that stopped part way - the command its last words name - it finishes it |
| `fold-agent <o> <a> [--apply] [--json] [--merge <path>] [--settled <path>] [--new <ID>] [--accept-lost <ID>:<cell>] [--accept-over <ID>:<cell>]` | Fold an agent's own work into the main tree and apply the rows it filed, every refusal named before anything is written and the rows all or nothing; a path both trees changed is refused with what a three-way merge of it gives, and a clean merge written into both trees only where `--merge` names it - the arm registry is merged by arm, unasked, and taken where the result reads as a registry; a path reconciled in the main tree by hand and named with `--settled` is handed to the agent as the main tree holds it and recorded as shared; a row is made only where `--new` names it, and a cell that does not keep its stored text written only where `--accept-lost` names it; a row an earlier fold applied is weighed cell by cell against what the registries took since - their change kept where the agent made none, and a cell both changed otherwise written only where `--accept-over` names it; a rename of case alone the agent made is refused, saying to `git mv -f` it in the main tree, and once made there is weighed against the base's file under its old name; its worktree is kept |
| `delete-agent <o> <a> [--apply] [--json] [--merge <path>] [--settled <path>] [--new <ID>] [--accept-lost <ID>:<cell>] [--accept-over <ID>:<cell>] [--discard-uncommitted]` | Fold what is left and apply its rows, keep its evidence and transcripts, then remove its worktree and its copies on hosts; an agent with no worktree is marked deleted in one write, nothing folded and no transcript looked for |
| `list-orchestrator [<o>] [--steps] [--questions] [--json]` | List orchestrators, their agents and where each stands; for a run, its own lines from `flow.json` and what its engine is doing - none, running, pausing, recording what a complete pause left running, or gone - each step instance with its state, visit, attempt, agent and directory with `--steps`, and each question and what became of it with `--questions` |
| `run-orchestrator <workflow> [--run <name>] [--input k=v]... [--json]` | Start a run of a declared workflow: validate it, make the run - `r<N>`, one more than the highest there, or `--run`'s name, refused where it is taken - claim it for this process, and drive it until nothing more can run: done `0`, failed `20` (`21` where every failure that ended it was incomplete), paused on a question `1`, paused or stopped on request `130`; `k=@<file>` gives a text input a file's text, relative to `--directory`. Refused (13) while another run of the repository is unfinished with work in the tree |
| `resume-orchestrator <run> [--json]` | Go on with a run from its records alone, where it stood - what a stop, a crash or an answer left - under the definition it began with: what was cut off, and a failure that ended the run, run again (a failure answered `fail` stands, and a step a plan update ended in order stays ended), what the run owed is made first, and every question and answer is kept; it goes on beside an open question, and is refused (13), having written nothing, only where nothing can run until one is answered (a budget question open pauses it instead); refused (13) too where the repository's entry for it changed since, or where a record the run is made with is missing, named |
| `answer-orchestrator <run> <answer> [--question <id>] [--text <text> \| --text-file <file>] [--again] [--json]` | Answer a question of a run: handed to the engine running it through `inbox.json`, which takes it at the poll that first reads it, or else a session of its own; `--again` changes the last answer a person gave - the set the command shows - within the minutes the workflow's `amend.changedAnswerMinutes` names and once, both answers kept in `questions.json` and a `pause` step whose answer changed visited again done at once with the new one; where the run has begun the scope its plan updates are planned in, the change raises the workflow's planning step and the run is held while it plans (or is queued behind an update under way) |
| `amend-orchestrator-request <run> [<text> \| --file <path>] [--json]` | Amend the request a run was started with: handed to the engine running it through `inbox.json` and taken at the poll that first reads it, weighed against the run as it then stands, or else recorded in `updates.json` as a plan update `queued`, taken first when the run is next gone on with - by `resume-orchestrator` or an answer; where no plan holds it, every step that starts reads the input `amend.input` names with its amendments, in `{input.<name>}` and every expression alike (a `preAnswer`'s quote is looked for in the value as given), and where the plan's scope has begun the workflow's planning step is raised - a new visit of it in the newest instance of its scope, ahead of everything else - the run held while it plans (nothing started outside that scope, what is in flight untouched), its prompts reading `{update.cause}`, `{update.since}` and `{update.work}`, and its `amend.then` running the check it names again until the scope rests and the plan stands - or the step fails and its `onFailure` gives the update up, no plan holding the change; once the plan stands the pieces its `amend.scope` reads are placed - an `added` piece one more work item of the work-item group, a `changed` or `removed` piece against the instance that carries its work item (given the piece's item where it waits, withdrawn before it began, or brought to rest and visited again in its place with its agent where it has begun - a step of a group nested in it, at any depth, ended with it, and each such group stopped with it - the instance it ends staying ended), checked by `validate-plan` with the items of the round they join; a `told` piece tells - a reason step in flight that it names sent one message into the conversation it is in (landing when its calls finish, never cutting its turn), a step waiting on a question told with the answer, one that starts later in named work reading it in a part of its own prompt, who was not concerned and what was not delivered recorded; a reason step ended in order is told its piece first, as the lead of its handoff question; where the round a piece joins has ended the work is owed to the loop's next iteration, which runs whatever its `until` says - a loop that ended done runs again for it - one iteration added only at the loop's bound for the update - and with no loop around the work-item group, a round of the group added for the work, run once more through the check that opens a round and every step between it and the group, and carrying the owed work alone (an iteration or a round once per update, at most `amend.maxAddedIterations` over the run, a change given while a round added waits to open, before the check that opens it has started, joining that round - after that, refused until the round has opened, which it then joins - carried at the next session's start where a crash came first, work owed to a loop at its bound with none left to add left to the question its bound asks, which the loop reaches whatever its `until` says - the update's entry saying so once that question is asked, so no later session runs again a loop its answer ended - and an update whose work only one more would carry given up with why where none is left or a group around the work-item group ended otherwise than done) - and the report and `list-orchestrator` say every piece, where it was carried out and how that ended, the iteration or round added, and a loop's bound asked for the work; one update at a time, one that arrives while another is under way queued behind it, and one the run can no longer take at its turn recorded `refused` with why; refused (13) where the run is done, past the step its `amend.before` names, past its work items (the last round it can run ended, of a loop at its bound with every iteration `amend.maxAddedIterations` allows, or of a group with no loop around it that runs no round more - every round allowed added and none waiting to open, or a round added waiting to open whose check has run, or a group around it ended otherwise than done), or the workflow's `amend` names no `input`; the command starts nothing |
| `pause-orchestrator <run> [--all] [--json]` | Ask the engine running a run for the safe pause through `stop.json` - its agents hand off, the machine steps finish unless `--all`, then the run is paused - and wait until it is complete |
| `stop-orchestrator <run> [--now] [--json]` | Ask the engine running a run to stop where it stands, and wait 30 s; `--now` ends the engine's process - only on this machine, and only the process its owner names by its start |
| `ask-orchestrator-status <run> [--json]` | The tool's own question of a run - what is happening now, what is done, what is left, what it waits for, and when it may finish - answered with the engine's facts (computed from the run's records, stamped with the moment they were read) and, where the workflow's `reporter` names one, its words: the facts first, then the model's, then what the answer cost - the person's, never the run's. Reads only: nothing of the run is written or locked but the command's own record under the run's `asks/`; where no model words the facts, they are still the answer, the command says why no model did, and it exits 0. `--json` gives the facts and the answer apart; an orchestrator that holds no run is answered with `list-orchestrator`'s facts of it, said to be such |
| `ask-orchestrator <run> "<question>" [--json]` | A person's question about a run, answered from its records by the workflow's `reporter` over the engine's facts; reads only, as `ask-orchestrator-status` does. Refused where no model can answer: 12 where the workflow names no reporter or the reporter's own files do not read, 13 where the key is not there, 14 where npm or Claude Code is missing or too old, 20 where the model failed, 21 where it could not be reached or its account is at its limit; neither command installs Claude Code or brings one up to date |
| `run-side-task [--prompt <text> \| --prompt-file <path>] [--resume <task>] [--name <task>] [--model <code> \| --tier <tier>] [--client <client>] [--effort <level> \| --effort <code>=<level>]... [--json]` | An agent outside every workflow: a conversation with a person over the repository's main tree, with no step and no limit of a run's - every action still decided by the engine before it runs and recorded, the built-in refusals kept (root actions, every credential slot, Claude Code directory and place the user keeps credentials in - to its file tools, and to every word of its commands, a directory that holds one but the main tree it works in among them - git's own files, the tool's state, a git or `gh` command called by an alias or an extension, `run-side-task` itself where a word of a command or of a script the policy reads names it or a verb the shell expands may, which also refuses itself started under any agent - told by its mark and by the process tree, a process of this tool's among the ones that started it, parent by parent, a process that took a freed id never read as a parent, the tool's own runtime told by the file it runs where the table gives one (the installed tool, a build's program, the runtime running the tool's assembly) and by its name only where it gives none, so a launcher of the tool's name - a script, another program's shim - is none; one detached from the agent's tree (`at`, a `tmux` server, a double fork) told by the mark alone, and a tree that could not be read refused saying why, each refusal naming what made it an agent's (the mark, the process of this tool's by its name and id - one any process of this tool's starts among them - or why the tree could not be read) and a shell of the person's own, which no process of this tool's started and which carries no mark, as where one starts - and a command that sets the mark, takes it away, or may take it away or names it but as data), the tool's own verbs open to it as the person's are, but those that read a credential slot or write its state - a policy of what a command names, not a sandbox: a command over the whole main tree, `.` or no path, reaches the slots' files beneath it, and code an interpreter runs (`python -c`, `perl -e`, awk's `system`) or a process apart from the agent's tree does what no word the policy reads says, where the operating system's isolation beneath it (below) does not hide every slot from each command and keep it from git's code-running files and the tool's state; its `web_search` asks every configured source turned on and one more - `native`, the provider's own search: a request of the harness's own on the Claude API, asked with the model and key serving the side-task when the agent asks and through that key's gate as every request is, its cost counted then to that model and key and a failure of it - a gate held past the longest wait among them - one failed part saying why, Claude Code's own `WebSearch` kept in its tools, none on Z.AI. A chat in the terminal by default - the agent's text streamed, each action one line as it is decided, Ctrl+C interrupting a turn where it stands - on a client that stores its conversations what was said kept up to its last whole turn and what the cut request used kept as the turn's - and a second ending the chat as `/exit` does - a turn under way cut and its result read within 10 s, the lines the agent had not taken dropped, so what the turn used, cost and spent is kept, and a launch still waiting its turn at the gate, its wait said, never begun - each interrupt saying whether a turn of the agent's was under way to cut, or none was and nothing is cut - a first one while a launch waits at the gate among them, the launch begun once the wait passes - a move to a later model of its tier going on from the conversation so far - a one shot's turn and each message to a client that stores its conversations tried again 3 times on its model first, as a step's, a chat's launch held open never tried again but moved at its first failure, decline, limit, refused key or model that cannot be started - and where no turn runs, while it is set up or a model made ready, an interrupt stopping it, a one shot's first and a chat's second - a line ending in `\` going on to the next, `/exit`, `/model`, `/effort` (a level, `<code>=<level>`, or `default`) - a new model keeping only the levels that name it and the one for the rest, a launch at work giving way as the person leaving does and the lines it never took going on to the new one - `/records`, `/help` - or one message with `--prompt`/`--prompt-file`, answered once with the agent's final text (`--json` its one document, and a usage error without a prompt), Ctrl+C there cutting its turn - a Claude Code launch's at that turn's result, an API client's request left where it stands, what the provider had said it used counted and a cost it had said nothing of unknown - what it used kept, and ending it interrupted (130), a launch still waiting its turn at the gate or to be tried again never begun. Named by when it began - with its first words in one shot; a chat, named before its first message is typed, by the time alone - unless `--name` names it, a name another side-task holds refused (13) and that one's records named as its. `--resume` opens one again, going on with its stored conversation - or from the conversation so far where its model's client is handed none - on what the person last set it to run on, its tier with the tier's later models or the model named outright, unless `--model` or `--tier` names another; refused (13) where it names no side-task or another process holds it open, and `--name` beside it a usage error; what it spends is counted against its key. Exits 0 ended as asked - a chat 0 however the person leaves it once it has begun, 130 where interrupts stop it before it began, while it is set up or opened again - 10 usage (an effort a model does not take among them), 12 the configuration (in one shot an agent the configuration keeps from starting among it), 13 refused (a name taken, an unignored `.side-tasks`, started under an agent or its starting processes unread, an update of Claude Code refused, and in one shot a key missing or the tool running elevated), 14 a client that cannot start where the entry that would serve first is on it - a later entry on it passed over - or, under `agentPolicy.isolation` `required`, a machine that cannot isolate the agent's commands, 21 the install of Claude Code timed out or could not reach the registry; in one shot besides, 20 the model failed past its retries and fallback, 21 cut short by the account's limit, a stall or the clock, 130 interrupted; the records under `.side-tasks/<task>/` are named on every exit - or said to be none where it stopped before any were written - and kept until a person removes them (`help side-tasks`) |
| `list-side-tasks [--json]` | The repository's side-tasks, newest first: each by its name, when it began and last spoke, its model, its turns and cost, and whether it is open now |
| `validate-workflow [<workflow>] [--json]` | Check the workflows `workflows` in config.json declares: that each yml definition reads, holds and names files that are there, each step's schema one the engine reads (`help workflows`). A remote use is resolved - its ref to a commit, read from the workflow cache where it holds that commit and fetched into it where not - and each resolution printed with its source; wherever the check went to resolve a remote use, the line it ends on names the workflow cache's directory, whether the workflows hold or not, and `--json` names it as `cache` on every exit. Nothing is run |
| `export-workflow <workflow> --to <dir> [--shipped] [--json]` | Copy a workflow - its yml and every file it names, a used workflow's where it sits under the folder - into `<dir>/<workflow>/`: a declared one once it holds (12 where it does not), byte for byte, or with `--shipped` the one this tool ships, to make a version of your own. Refused (13) where that folder is there, or where the workflow uses one outside its folder or a remote one; a file that cannot be written takes back what was written |
| `export-workflow --from <use> --to <dir> [--json]` | Vendor a remote workflow, as a use names it (`owner/repo/path/workflow.yml@ref`), at its resolved commit: every file of its directory and a note of where it came from, for review, so a team can use it as `./`; a workflow name or `--shipped` beside it, or a `--from` that names no remote workflow, is a usage error (10), and the folder is looked at before anything is fetched. The workflow cache it was fetched into is named in text and in `--json` (`cache`) |
| `workflow-cache list [--json]` / `workflow-cache prune --unused-days N [--json]` | The hash cache of remote workflows, its directory named in text and `--json`: each entry it holds - where it came from, its commit, its files and bytes, the refs whose newest resolution it is - and every file of its own it could not read; and the removal of the entries no run used for N days (required, 0 to 36,500), the resolutions that named them and the blobs no entry left holds, ending incomplete (21) where a file could not be removed or read, the cache could not be named, or another process held its lock |
| `list-agent-providers [--template] [--json]` | List the agent sections config.json declares: each provider, its keys' names in the order they are tried with whether the slot holds each - `present` or `missing`, never a value - its models and whether each is priced, the tiers, the clients and each web-search source's key; exit 0 whatever is missing; `--template` prints the sections as `init` writes them into a new config, to paste into an existing one (`help agent-clients`) |
| `delete-orchestrator <o> [--delete-evidence]` | Delete an orchestrator once no agent of it stands in the way - a live one with no worktree goes with it - and nothing is left below its directory under the worktrees root - no worktree, and no directory with a `.git` of its own, each named and refused (13) until then; its agents' evidence, transcripts, steps and sessions go only with `--delete-evidence` |
| `check-root-litter` | Report files left loose at the root of the checkout, ignored ones included |
| `write-anchor <id> --priority P --trigger TEXT` | Add an anchor: to the pending registry, or to done when closed |
| `set-anchor <id>` | Change an anchor; changing its status moves it between registries |
| `read-anchor <id>...` | Show anchors in full |
| `read-anchors` | List anchors, or check the registries with `--lint` |
| `check-anchor-balance` | Fail a change that creates more anchors than its work closes |
| `check-anchor-arms` | Hold anchor rows' claims about mutation arms to the mutation record |
| `check-anchor-citations` | Check every anchor cited in the declared roots resolves to a row |
| `fix-line-endings [--all \| --changed]` | Apply the line-ending policy `.gitattributes` declares; `--check` refuses instead |
| `check-ci-legs` | Report each CI leg, separating a real failure from a budget overrun, by the job and step names `ci` declares (`help ci`) |
| `legs [--legs a,b]` | Measure the hosts and show where each leg can run, or why it cannot |
| `install-missing-tools [--legs a,b] [--dry-run]` | Install or update what each configured leg's host is missing; `--dry-run` names each command and runs none |
| `update-tool [--wait] [--tool-path <directory>]` | Update this machine's installed DssHarness to the newest release, only while no other `dssharness` process runs here: refused naming each (13), or waited for with `--wait`; refused while another update changes the same directory (13, with `--wait` or not); on Windows run it from beside the installed tool, as above (`help legs`) |
| `sync` | Put a host's copy of this tree in step with it, deletions included: each worktree has a copy of its own; `--dry-run` lists what it would write and delete and exits as the sync would; `--pull <path>` instead brings a file back from it, or every file below a directory named with a `/` at its end |
| `build [--legs a,b] [--time] [--keep-going]` | Build every selected leg, in its own variant-keyed build directory; `--keep-going` has each build go on past the units that fail to build, so a red leg names every one (`help legs`) |
| `test [--legs a,b] [--time] [--set-up] [--keep-going]` | Build and test every selected leg, with a witness for each verdict; `--set-up` brings up the set-ups its test invocation names around each leg's work, on the machine that runs it, and down after (`help setups`) |
| `run <runner> [--legs a,b] [--time] [--input name=value] [--set-up] [--keep-going]` | Run a predefined runner across the legs it declares, giving its action's inputs values for this run; `--set-up` brings up the set-ups the runner and its run checks' runners name around each leg's work, and down after (`help setups`). A run line names what only the tool knows of a leg - its build directory, the file its build makes, and the compiler its build identified, `{compiler_C}` or `{compiler_CXX}`, which is gcc on one leg and cl on another - and a step naming one of those has its leg built first (`help runners`) |
| `check-mutations [--legs a,b] [--arms a,b] [--self-test] [--list] [--pending]` | Prove each selected leg's tests can fail: in worker copies of its tree, mutate each arm the registry declares, build it, witness every object depending on the site rebuilt, run its test binary whole and judge what reddened; `--self-test` sweeps the fixture the tool carries instead, through each leg's toolchain; `--list` says, without sweeping, each arm's last verdict on each leg and whether anything it was judged on has moved - of what a test binary reads at run time, only the files `mutations.runReads` names for its runner; `--pending` sweeps only the arms with no passed that still holds, and with nothing pending passes, sweeping nothing (`help mutations`) |
| `stop-run <run> [--now] [--json]` | Stop a `build`, `test`, `run` or `check-mutations` run from another terminal, typed in the tree that ran it: asked of its dispatcher, which stops every leg where it stands as an interrupt does and writes its ledger, and waited for 60 s - said stopped only where its dispatcher took the stop; `--now` ends the dispatcher's process where it stands, with all it started - only on this machine, and only the process its owner file names by its start. On Windows a dispatcher that ends in any way, a kill among them, ends its local legs with it |
| `query-codebase-graph <run> (--find <query> \| --node <symbol> \| --related <symbol> --relation <relation> [--depth <n>] \| --outline [<path or symbol>] \| --body <symbol>...) [--agent <agent>] [--json]` | Read a run's codebase graph by hand: one of the `graph` tool's read operations on the base or an agent's view, printing the answer an agent would be given - each node with its handle and `path:start-end #hash` - or with `--json` the same in one document. It reads only: no sync, no lock, nothing written; a view not made yet is said and the base answered; 13 where the run has no graph |
| `clean [--legs a,b] [--dry-run] [--former-workers]` | Remove each selected leg's build directory, the mutation workers its sweeps and self-tests keep in its tree, and the copies of its tree it works in (`.legs`), wherever the leg runs, so a full disk can be freed; `--dry-run` says what each holds and exits as the clean would; `--former-workers` removes only the workers an earlier version kept beside the repository's trees, here and on each host (`help space`) |
| `up <name> [--input name=value] [--json]` | Bring a set-up up on this machine: run the `up` command line `setUp` declares for it, at the root of the tree - a program and its arguments, no shell, a line per platform where they differ, its declared inputs filled in with what `--input` gives over their defaults (`help setups`) |
| `down <name> [--input name=value] [--json]` | Bring a set-up down: run its `down` command line there, with the values it was brought up with; refused (13) while a leg of a run on this machine still uses it in that tree - in any of the repository's, for one the machine shares - whose last leg brings it down |
| `host-exec --ssh <name> \| --wsl [<distro>] -- <command>` | Run a DssHarness command on an ssh host or in a WSL distribution |
| `help [topic]` | Explain exit codes, configuration, legs, disk space, heavy-leg admission, worktrees, orchestrators, workflows, the codebase graph, the web search, agent clients, anchors, layout, secrets, tools, runners, set-ups, verdicts, mutation testing and CI legs |

Every command takes `-C, --directory <dir>` and `-v, --verbose`.

Zero means every selected leg reached a verdict and none failed. A run where nothing failed but
some leg never reported, or left a set-up it brought up running, exits `21` and names those legs: a leg that did no work proves nothing
about the code, so it is never counted among the legs that passed. A command that changes an
orchestrator or an agent exits `21` too when it stops part way - an agent closed and its worktree
not yet removed, a fold or a hand-over half written, a move of an agent's base half made, an
orchestrator half deleted; run again, it finishes. Without `--apply`, `refresh-agent`,
`rebase-agent`, `fold-agent` and `delete-agent` are dry runs, and a dry run exits as its `--apply`
would: `0` only when that `--apply` would go through, `10` where its arguments are a usage error,
which that `--apply` answers before it takes anything, `13` where it would be refused - a consent
nobody gave, or a run holding what the `--apply` needs, included - and `21` where a deletion's
`--apply` would go as far as the removal and leave it unfinished, over a copy a run or a sweep
holds or copies kept in the worktree it cannot remove as they stand. A run holding a tree the
`--apply` takes, or a host's copy of the worktree, is named on a `held now:` line, and one holding
a leg copy or a mutation worker kept in the worktree in that copy's own words; `--json` answers
with one document whose `request` member says what is wanted of you - `sound` (nothing; wait
where `heldNow` names a holder), `needs-consent` or `refused` - and whose `heldNow` holds each
holder, those copies' among them. Beside them `needs` names each consent the request lacks
(`fold-new-rows`, `fold-lost-rows`, `fold-over-cells`, `fold-two-changes`), `refusals` each reason
it is refused for (`conflict` where a path both trees changed has a merge that conflicts, `other`
for anything else), `folded` what a fold's `--apply` wrote, merged, handed and applied, and `planned` what the
verb's plan names - what a rebase takes and keeps, a refresh or a fold hands, each merges, a fold writes and deletes,
the registry files its rows change and the rows it applies - and what each consent it lacks and each conflict it meets
is about. A refresh or a rebase over a path both trees changed whose merge is clean lacks `--merge` as a fold does. An
`--apply` refused at its hold answers the same document, `sound`, with the run it met in
`heldNow`. Either way a dry run still prints all it weighed.
`dssharness help orchestrators` explains seeding, rebasing, folding and deleting agents, and the five verbs of a run.

A run's session - `run-orchestrator`, `resume-orchestrator`, `answer-orchestrator` - takes its terminal's interrupts
itself: the first Ctrl+C (off Windows, SIGTERM or a hang-up as well) asks the safe pause - each agent is asked for its
handoff in its own conversation and answers within the workflow's `handoffSeconds` (180 where it says none), the machine
steps finish, then the run is paused - and starts no clock of its own; the second stops the run where it stands: a step whose runner
does not end within 90 s is recorded stopped all the same, and the command ends within 120 s whatever is left - the
engine's 90 s and 30 s to write the end - which `resume-orchestrator` goes on from. Every session writes the run's report
(`report.md`, `report.json`) and names each record it wrote, in text and in `--json`.

`--time` pulls each phase's own timing marks out of its output, using `buildTimingRegex`,
`testTimingRegex` or `runTimingRegex`, and prints them under the ledger as a `TIMINGS` block naming
the leg and phase that reported each one. `host-exec` needs no `--time` of its own: everything after
`--` is the command run on the host, so `host-exec --ssh vps -- test --legs a,b --time` asks for it
there.

A leg whose build failed names each unit it failed to build - by its source, as ninja's manifest names
the source a failed step compiles, or by the target make says failed - counts them on its line, lists
them under the ledger, and carries them in `--json` as `failedUnits`; where which units failed is not
read, as of MSBuild, dotnet and dart, the line says so. ninja and make stop at their first failures,
so a build of theirs says that every unit it never reached was not built, and how many of the steps it
had to run it ran where ninja's status says it. `--keep-going`, on `build`, `test` and `run` - and
`keepGoing` on a workflow's `build`, `test` and `run` steps - has each leg's build go on past its
failing units: ninja is given `-k 0` and make `-k`, MSBuild, dotnet and dart go on by themselves and
are given nothing, and a leg whose toolchain names no generator, or one no way is known for - NMake's
and Xcode's - is refused (13) before anything runs, as is the option on a run that builds none of its
legs (10). `dssharness help legs` lists each generator's way.

## Running legs at once

Every leg-running command dispatches its legs in parallel and waits for all of them, reporting each
one live rather than buffering until the end. Output is written a whole line at a time, and every
line says which leg it came from — a streamed child line under `-v` is tagged `<leg>/<phase>:`, so
three hosts building at once stay readable.

Legs are chunked by the **physical machine** they run on. A local leg and every WSL leg are one
machine, because WSL runs on it; each ssh host is its own. `defaults.maxParallelLegs` caps how many
run at once *on any one machine*, so a busy laptop is not asked for more than it has while the
remote hosts sit idle, and `defaults.maxParallelLegsTotal` caps the whole fleet for what it shares
even when its machines do not — a license server, a network share, a sync's bandwidth.

Both are counted by one command. Where a machine declares `admission` — under `defaults`, or its own
under `hosts.local` or an ssh host — its heavy legs (a build, a test, a runner that requires the build
or says `"heavy": true`, a run of an action step or a runner's own phase that names `{product}`,
`{buildDir}` or one of the leg's compilers — which builds its leg first — or of a step that says
`heavy: true`) share it across every
command this user runs there: each waits for one of its `heavyLegs` slots, in the order they asked,
then for the memory in use to fall below `maxMemoryPercent`, then - where its build's need is known -
for room on the filesystem it fills beside what the other admitted legs claim, says who holds each slot
while it waits, and names on its line how long it waited, the memory it started at and the room it
claimed. A WSL distribution's legs take this machine's slots, and claim room on the drive where WSL
keeps its disk; an ssh host takes its own. One that waited `maxWaitMinutes` - a wait with only its own
command's legs ahead of it not counting - is `not-admitted`, exit 7, naming what held the slots and
where they are recorded, the memory it waited on, or the room and who claimed it, and nothing of it
ran. On Windows, a leg about to wait on the memory has WSL's page cache dropped first - as root, at
most once a minute - and reads the memory again once what was dropped has come back, a minute later
at the most; a WSL leg drops it as it ends. While a heavy leg builds, each filesystem it fills - for a
WSL leg, the Windows drive its disk grows on too - is held to `minFreeGiB` (2 GiB): one that falls
under it, read every 15 seconds and again as a build or a write of its fails, stops the build,
`stopped`, what it built left for `clean`; a build under it as it starts writes nothing. Run
`dssharness help admission` for the rules.

## Design

Three principles the implementation actually holds to:

**Fail loud.** Zero always means success, and "the thing you asked about failed"
never shares an exit code with "the harness could not run" — the remedies differ.
Run `dssharness help exit-codes` for the full table, which is generated from the
code rather than written by hand. A configuration file with an unknown key, a key
written twice, a list given empty where leaving it out means every one or a set the tool chooses, or a reference to
something undeclared is rejected when it is read, with every problem listed at once.

**One behaviour everywhere.** Windows, macOS and Linux run the same code path. The
operating system is learned in one platform layer, beside a few primitives that each ask
the runtime for one fact and are named in the [architecture](docs/architecture.md);
everything above that layer is platform agnostic. CI runs the whole suite on all three, on
both x86_64 and arm64.

**Witness the work, never the exit code.** A build passes only when every file the project's
`buildOutputs` names is there afterwards — a build tool exits 0 having produced nothing often
enough that the code alone is not evidence. An entry is a path, or a mapping of platform to path
where the platforms disagree about what the same target is called (`app` against `app.exe`), and
an entry naming no path for a platform some leg builds on is refused when `config.json` is read.
A test passes only where its runner printed its `successPattern`, and `countPattern` reads how
many tests ran: a leg that ran a different number than the other legs of its project and
`testSet` is marked on its own line, never as a timing and never changing its verdict. A set
that differs on purpose, such as a platform's own tests, is named with `testSet` on that
platform's test invocation. A test invocation that does not pass may name what it keeps as
evidence: `keepOnFailure`, paths under the leg's build directory — files, directories or
patterns — copied into `kept-on-failure/` beside the leg's log when the verdict is anything
but a pass, within `keepOnFailureCapBytes` kept in all (64 MiB by default; a cap where no
`keepOnFailure` applies is refused). A pass keeps nothing, and a later step reads nothing from
it: it is evidence beside the verdict, never an output. Only a file of its own is copied — a
link is never kept through, wherever on the way from the build directory it stands — measured
by what it holds as it is read, and never past the cap. A directory that cannot be read is
passed by and named where an entry could have named what it holds, and a stop names what it
left. The leg's line names the directory and the counts — files kept, refused over the cap,
unreadable, left as links, naming nothing, or left uncopied by a stop, and directories that
could not be read.

**Refuse early, with the arithmetic.** `create-worktree` will not create a worktree
whose files or build paths cannot fit inside Windows' path limit, because that failure
otherwise appears as compile errors in files the worktree never touched. It says how
long a name would fit instead. The name limit and both sides of the budget are set
in `config.json`.

**One seam to the behaviour.** The command line parses arguments, owns the console and
reports the exit code; everything a command does lives in the Core library behind a use
case - one interface per operation, its request and its report the contract - so the same
behaviour is reachable from a terminal today and from a front end later, and the boundary
is enforced by tests that read the built command line itself. The
[architecture](docs/architecture.md) says the whole of it.

## Layout

```
.harness-config/config.json                  tracked; the whole contract
.harness-config/workflows/<name>/<name>.yml  tracked; one directory per workflow, holding its definition and every file that definition names - its agents, prompts and schemas ('dssharness help workflows'); each missing file written by init where config.json declares the implementation one
.harness-config/workflows/.env/              contents ignored, .gitkeep tracked; the agent clients' keys, NAME=value files, every file merged, no key read from anywhere else; no workflow may be named .env
.harness-config/skills/<name>/               tracked; the tool's own place a step's prompt names a skill from; init creates nothing there ('dssharness help skills')
.harness-config/runner/actions/<name>/<name>.yml tracked; one directory per action, holding its steps and whatever those steps run
.harness-config/runner/.env/                 contents ignored, .gitkeep tracked; values actions read
.harness-config/runner/.secrets/             contents ignored, .gitkeep tracked; secret values actions read
.harness-config/sshItems/<name>/             ignored, and written by you, not init: .env (address, user, port), .key, known_hosts
.harness-config/wslDistros/<name>/           ignored; .env (distribution, credential)
.worktrees/                                  contents ignored, .gitkeep tracked; the worktrees, at worktrees.root, which init writes as this
.orchestrators/                              contents ignored, .gitkeep tracked; what each orchestrator and its agents keep, in the main checkout, and never sent to a host by sync ('dssharness help orchestrators')
.orchestrators/<o>/agent.json                ignored; the orchestrator's record
.orchestrators/<o>/logs/<name>.jsonl         ignored; a line for each run that reached it or one of its agents, refusals included; a dry run writes none
.orchestrators/<o>/plans/<name>/             ignored; its plans, and each agent's
.orchestrators/<o>/work/<agent>/             ignored; an agent's scratch and task files
.orchestrators/<o>/agents/<agent>/           ignored; an agent's record, seed, the bytes it shares, rows, the rows applied, kept evidence
.orchestrators/<o>/agents/<agent>/steps/<instance>/ ignored; one step the agent runs, or that runs in or for its worktree: everything of the step, its handoff.md where one stopped it, and for a reason step the policy.json it started under - the agents' configuration read when each step starts, as its prompts are, so an edit of config.json reaches the steps that start after it and never one under way - and, where the step has a codebase graph, its graph.jsonl (a line for each launch's first message, each call of the graph tool, each escape of its narrowed reads) and its working-set.json, the seeds of the packs of the steps after it
.orchestrators/<o>/agents/<agent>/sessions/<id>/ ignored; API clients only: one conversation with the provider, in the provider's own form
.orchestrators/<o>/steps/<instance>/         ignored; one step of a run that no agent owns, the create-agent that makes an agent included
.orchestrators/<o>/.../<instance>/children/<session>.json ignored; in either kind of step, on Linux and macOS: a session a contained child of the step leads while it runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a leg to a host leads while anything is in it, ended by the next command over the orchestrator - and by stop-orchestrator --now - once its starter has gone
.orchestrators/<o>/asks/                     ignored; one directory for each call of ask-orchestrator and ask-orchestrator-status: the facts it was given, the files of its reporter's attempt, the conversations its client stores (sessions/) and ask.json; the engine never reads it
.orchestrators/<o>/web/searches.jsonl        ignored; a run's web search: what each source found once for the run, and each query it answered, which the same query again is answered from; written by every client whose step searches
.orchestrators/.web-index/<source>/          ignored; the harness's own index of a sitemap search source's site: its state, its pages and the pages it could not read, and its fill's record of each address it asked
.orchestrators/key-spend.jsonl               ignored; what each key spent, by its name and never its value: a line for each key and model of each entry of a step's order or of a side-task's turn that ran, its lines older than seven days dropped when it is next written, read where balanceKeyUsage chooses a key ('dssharness help agent-clients')
.side-tasks/                                 contents ignored, .gitkeep tracked; the side-tasks' records, in the main checkout, and never sent to a host by sync ('dssharness help side-tasks')
.side-tasks/<task>/                          ignored; one side-task: its task.json, stored stream, transcript, denials and web, its conversation where its client stores one, and its own codebase graph (graph/, its map and each call of the graph tool) where it was given one; <task>.owner.json beside it while a side-task holds it open
.mutations/                                  contents ignored, .gitkeep tracked; the tree's mutation workers, in every tree, never sent to a host by sync ('dssharness help mutations')
.mutations/<key><mark>-<n>/                  ignored; a worker, a repository of its own, and its claim beside it
.legs/                                       contents ignored, .gitkeep tracked; the copies of the tree its legs work in, in every tree, never sent to a host by sync
.harness-config/runs/                        ignored; one directory of records per run, in the tree that ran it
.harness-config/lock.json                    ignored; records in-progress runs
.harness-config/deps/<name>/                 a mutation worker's alone, never sent by a sync nor deleted by one: its own copy of each dependency source its leg's build fetched ('dssharness help mutations')
.harness-config/synced-copy.json             a host's copy's alone: the mark the sync that made it leaves, which tells a synced copy from a tree of its own
.harness-config/host-copies/                 ignores itself; which hosts hold a copy of which worktree, in the main checkout
.harness-config/mutation-record/             ignores itself; each leg's verdicts of its mutation arms, with what each was judged on, in the main checkout ('help mutations')
.plans/_deferred-anchor-registry.md          tracked; live anchors (anchors.pendingAnchorsPath)
.plans/_deferred-anchor-registry-done.md     tracked; closed anchors (anchors.doneAnchorsPath)
```

The worktrees root is `worktrees.root` in `config.json`, shown here as `init` writes it. A
configuration that names none keeps worktrees under `.harness-config/worktrees`, which spends
15 more characters of the path budget before a worktree's own name.

A sweep keeps its workers in the tree it sweeps, and a leg that works in a copy keeps it in its
tree's `.legs`; either is refused (exit 13), nothing made, in a tree whose git does not ignore
that slot: run `init` once in each repository and commit what it writes - each agent's branch
needs that commit before it can sweep or have a leg work in a copy. Workers an earlier version kept
beside a tree, `<tree>.mutation-*`, on this machine and on every host, are no workers to a sweep:
`clean --former-workers --dry-run` lists them with their sizes, beside each tree of the repository
here and on each host a selected leg runs on, and beside each one deleted since, and
`clean --former-workers` removes them - one a
sweep of that version still claims is kept. Until then `list-worktree` warns of one beside a
worktree, and `delete-orchestrator` is refused (13) while one is beside an agent's worktree. Each
worker's build is made once again.

Secrets and the run lock live only in the main checkout. A worktree receives the tracked
part of `.harness-config` through git but never the ignored part, so both resolve back to
the originating checkout. What a tree makes for itself - its runs' records, its mutation
workers in `.mutations`, its legs' copies in `.legs` - is ignored too, and kept in that tree.
The run lock knows a tree on this machine by where it leads, every link followed, so
worktrees kept on another disk through a worktrees root that is a link, and copies kept
through a `.legs` or `.mutations` that is one, are held, and asked about, as one tree however
a command spells them - a build before this one's too, since the lock keeps each tree as its
run spelled it.

## Legs and hosts

A leg says what it needs, never where it runs: an operating system, a processor, and
optionally the emulator it runs through, which rules out running it natively. Hosts are
measured before anything starts: this machine first, then WSL distributions and ssh hosts
only for the legs this machine cannot take, and a WSL distribution only for a Linux leg.
Each leg runs on the first host that can take it - its operating system, its processor and
its emulator - and is turned away there when that host lacks a program its command requires.

```json
{
  "defaults": { "localCopy": true },
  "hosts": {
    "wsl": { "Ubuntu": { "repositoryPath": "~/src/app" } },
    "ssh": { "mac-mini": { "repositoryPath": "~/src/app", "env": { "CCACHE_DIR": "/Users/dev/.cache/app-ccache" } } }
  },
  "emulators": {
    "rosetta": {
      "hostOs": "macos", "hostProcessor": "arm64", "processor": "x86_64",
      "launcher": ["arch", "-x86_64"],
      "witness": { "command": ["/usr/bin/uname", "-m"], "pattern": "^x86_64$" }
    }
  },
  "legs": {
    "linux-release": { "os": "linux", "processor": "x86_64", "config": "release" },
    "mac-x64-release": { "os": "macos", "processor": "x86_64", "emulator": "rosetta", "config": "release", "localCopy": false }
  }
}
```

```bash
dssharness legs                                       # every leg: where it runs, or why it cannot
dssharness legs --legs linux-release,mac-x64-release
dssharness host-exec --ssh mac-mini -- verify-git
```

Where this machine runs a leg itself, it works in its tree or in a copy of it kept in the tree's
`.legs`: said by `--local-copy` or `--in-place` on the command for one run, then by a runner's
`writesTree`, which works in place and is refused with `--local-copy` or on a leg a host would
run, then by the leg's own `localCopy` - `false` on a leg turns a
`true` default off for it - then by `defaults.localCopy`, and by none of them, its tree. The copy
is made from the run's one reading of the tree and kept
warm between runs, so a fold, a commit or an edit while the leg runs ends nothing of the leg's;
the first build in one starts cold, files git ignores are not in it, and `clean` removes it
(`help legs`).

No leg, `host-exec` or sync a host runs for this machine outlives the command that asked for it.
Stopping that command ends its legs on every host at once; and a command killed where nothing
closes its end of the connection leaves legs that are cancelled within two and a half minutes, on
an ssh host as in a WSL distribution, because the host stops hearing the beat this machine writes
while it is there (`help legs`). Only what holds a host awake between commands
(`holdAwakeSeconds`) is started to outlast the command that asked, and ends on its own seconds.

A host's section can also give its own `buildCores` and `testCores`, and an `env` that every
process a leg starts there sees - each build phase, the test runner, each step of a runner - as
the lowest layer, beneath the variant's, the test invocation's and the runner's own. A `PATH`
set there is where that host finds those programs, so none of them is required of it before a
leg starts: each is the run's to find - except in a developer environment, whose `PATH`, built
over the host's, is looked in before the leg starts. A directory a tool's `minVersion` raised
goes ahead of it all the same (see below).

A toolchain can name a developer environment that its legs start in, declared once and set up on
the host that runs each leg, over that host's `env` and beneath everything more specific:

```json
{
  "developerEnvironments": { "visualStudio": { "kind": "visualStudio" } },
  "toolchains": {
    "msvc": { "platforms": ["windows"], "generator": "Ninja", "env": { "CC": "cl", "CXX": "cl" }, "developerEnvironment": "visualStudio" }
  }
}
```

Every host a leg might land on is asked, through Visual Studio's installer, whether it has an
instance with `requiresComponent` - the C++ build tools unless another is named - and one without
turns the leg away as a tool missing; one that could not look leaves it unavailable. On the host
that runs the leg, the instance its survey found has its `vcvarsall.bat` run once for the leg's
processor, cross-compiling where the host's differs, and what it set is what the leg's build, tests
and runner steps start with. A `vcvarsall.bat` that fails, prints an `[ERROR`, or sets up another
processor fails the leg before anything of it starts. The programs the leg starts are then looked
for on the `PATH` it set up - Visual Studio carries `cl` and `link`, and CMake and Ninja with its
CMake component - and one missing there skips the leg as a tool missing, named, before anything of
it starts. The leg's line names the environment -
`developer environment: visualStudio (Visual Studio 18.0.11205.157, MSVC 14.50.35717, amd64)` - and
`--json` carries it as `developerEnvironment`.

A developer environment may also hold its legs to one MSVC toolset, with the optional
`toolsVersion`, named as `VC\Tools\MSVC` names its toolsets: by two numbers (`14.44`), for the
newest of that line, or by all three (`14.44.35207`), for that one - for a compiler that accepts
only some MSVC releases, such as a CUDA toolkit's `nvcc`, which refuses one newer than it knows.
Left out, `vcvarsall.bat` sets up the instance's default toolset. Only its form is checked when
`config.json` is read: no toolset is built into the tool. The survey then takes the newest instance
holding a toolset it names, with the newest of those, and a host with none turns the leg away as a
tool missing, naming what each instance holds. `vcvarsall.bat` is run with `-vcvars_ver=` and that
toolset, and one that sets up another fails the leg; the leg's line names the MSVC set up. Two
environments of one kind keep two toolsets apart on one machine:

```json
{
  "developerEnvironments": {
    "visualStudio": { "kind": "visualStudio" },
    "cuda": { "kind": "visualStudio", "toolsVersion": "14.44" }
  }
}
```

An ssh host's connection data lives in its own directory under `.harness-config/sshItems/`,
which git ignores: an `.env` naming the address, the user and the port, a `.key`, and a
`known_hosts`. `config.json` declares only the directory names, under `sshItems`, so the
tracked configuration never carries an address, a user, a key path or a credential. A WSL
distribution has the same shape under `.harness-config/wslDistros/`. ssh runs in batch mode,
so it never waits at a prompt, and a key other users can read is refused before anything
connects, with the command that fixes it.

Every WSL distribution and ssh host runs DssHarness itself, installed from nuget.org at
this machine's exact version: a host that is behind is updated, never downgraded. Betas are
released on GitHub rather than nuget.org, so only a stable build can bring a host to its
version. `install-missing-tools` installs the .NET SDK, and everything `tools` declares an
`install` for, on every configured leg's host; a `tools` entry with no `install` is an
allowlist entry, reported when missing and never installed. An install that needs a superuser
takes the password from that host's own `.env`, as `SUDO_PASSWORD`, and asks for it at the
terminal when none is declared — once per host, held in memory for that command alone and never
written anywhere. A run with no terminal, a run answering with `--json`, and a run given
`--no-prompt` all refuse instead, naming what would fix it; running the harness as root needs no
password at all, which is usually the answer in CI. An entry may name the platforms it
is needed on — `"platforms": ["windows"]` — and a host whose platform it does not name is never
asked about it, so a repository can declare both a Windows compiler and a POSIX one. It may
narrow that further, to `toolchains`, `legs`, `processors` and `emulators`, so `cl` scoped to
`msvc` is never reported missing on a MinGW leg of the same machine. A `minVersion`, written
`major.minor.patch` and read with the entry's `probe`, holds every copy a leg could start: the
first copy that meets it - on the `PATH`, then in the searched directories - is the one every leg
on that host starts, its directory put ahead of the `PATH` where an older copy comes first, and a
leg the tool is declared for is turned away where every copy is older, naming each and the
`install-missing-tools` command that installs one (`help tools`). Each leg is told about a tool
as it will find it, by the survey `legs` runs on its host: a leg whose toolchain names a developer
environment, on the `PATH` that environment sets up there for its processor, so `cl` is found
where Visual Studio keeps it - and every other leg on its host's own `PATH`. A tool is installed
exactly where a leg would be turned away over it: an install runs once on a host, whichever of its
legs asked first, and each leg is then told what it finds; a tool the survey could not establish
is unknown, and nothing is installed for it. `--dry-run` asks every host and installs no tool,
naming each command that would run; the survey it asks puts DssHarness on a host as `legs` does,
so a host with no .NET SDK yet has every tool unknown. `sync` creates the host's copy
of the tree it runs in and keeps it in step, deletions included: the main checkout's at the host's
`repositoryPath`, and each worktree's beside it, so worktrees do not wait for each other on a host.
The copy's git HEAD names the commit this tree is at, so a step that asks git about HEAD is answered
on a host as it is here: the commit alone by default, and every commit behind it with
`"sync": { "history": "full" }`. A clone the sync took over has its HEAD moved, detached, where it
names another commit, and no branch, tag or commit of it changed; given a commit without the one
before it, git reads it as a shallow repository from then on, which the sync says, with what undoes
it.
Deleting a worktree removes its copies from the hosts that hold one, and fails, naming it, while one
stays. A file crosses whole, either way, so one larger than 804,519,909 bytes is refused by name
before anything is sent. An
emulator counts only once its witness proves it runs programs for its processor.

`legs` runs the witness of each emulator the selected legs use, and every command that reaches
a WSL distribution or an ssh host installs or updates DssHarness there, as `config.json` declares. That is the
trust building the repository already asks for. Run `dssharness help legs` for the rules.

## Workflows

A workflow is a flow of typed steps that depend on one another by name - triage, a plan, builds,
tests, reviews, a pull request - declared by name under `workflows` in `config.json` and defined by
a yml file of its own, `.harness-config/workflows/<name>/<name>.yml`, whose directory holds every
file the definition names: its agent files, its prompts, its schemas. `init` writes one, the
implementation workflow, and declares it in the config it creates. Where `config.json` declares
`implementation`, `init` writes each of its files that is missing - the whole set where the
repository has no definition of it yet, and where it has one only the shipped files that definition
names, the workflows it uses among them - a shipped one taken out whole read from the copy `init`
writes, so it comes back whole in one run - so a file taken out with its step stays out - and keeps each one that is there, whatever it holds, so an `init` that stopped part way is finished by the next - where a file is
missing and the definition there does not read, or uses a workflow of the repository's that does not
resolve, init refuses (12, naming each problem; 13 where it cannot be read), since
which files it names cannot be told; nothing that exists is ever
overwritten, and a config that does not declare it gets none of it. A config written before
workflows declares none: add `"workflows": ["implementation"]` and run `init` to have it. A file a
repository's own definition names that this tool ships elsewhere now - one of the item pipeline's,
moved into its own folder, which an older release shipped beside the definition - is not brought
back, for this build's copy is written for the new layout, and `init` says so in a note, naming
where it ships it now. A file of that older layout still there that the definition reads no more -
a prompt the repository made its own before the move - is read by nothing, so a change made to it
reaches no step, and `init` says so in a note, naming the file read now.

A step gives a failure nothing handles to the workflow's `recover` by saying `onFailure: recover`,
itself or through a group around it: steps written once that find what went wrong and put it right.
When they are done the run goes where the recovery's `onSuccess` says - `failed-step`, the step that
gave it its failure, which runs again from where it stopped - and a failure of the recovery's own
work does what its `onFailure` says once its steps' retries are spent: `pause` or `stop` - never the recovery again, which would be it calling itself. A step
that fails again comes back by its own `onFailure`, until it passes or a person is asked; past
`maxVisits` the run asks, with the advisor's recommended fix. A reasoning step is never recovered: a
failed agent is tried again (`retries`, waiting `backoffSeconds`, twice as long each time) and on
each model of its fallback, quota or a provider that fails pauses the run where it can resume, and
what still fails is asked (`onFailure: pause`). The implementation workflow says all of it on its steps, so a run of it
ends done or asks only what it must. It builds first: its baseline is split in two - a `build` step, which fails red
and is taken by the recovery and built again - and the `test` step it was, which waits for the build - and the flow
makes its codebase graph after that build, beside the baseline's tests and the CI read, passed over where it cannot be
made. Every reason step after it is served from the graph as the twenty-run measurement of 2026-11-11
set each type's value (graph arm against `graph: off`, five requests on this repository and five on a
small C repository, the same models and efforts): the steps that work on code keep the tool - the
implementer and everything that resumes it, the six reviewers, the self-audit, the diagnosis, and the
pre-existing-issue triage, which measured cheaper - the reviewers, the self-audit, the diagnosis and
the steps that verify and fix starting their packs from their changes (`graphFrom: changes`); the
request triage and the plan audit, whose work is text the map does not light, ask none, each having
measured costlier with no quality gained; the prioritiser and a review's scope are given the map alone
(the scope's whole overhead a fifth of a cent beside the review rounds it steers); and the scribe's
steps, which write the run's records, ask none of it (`dssharness help codebase-graph`). The planner
keeps the tool, its default, as it was: the measurement found it costlier with the graph and did not
move it.

An issue a reasoning step meets that was there before the run, it reports in its answer
(`preExisting`) and never fixes itself; `onPreExisting` names the step that triages each one. That
step, at the flow's top level, says `raised: preExisting` - raised once per issue (a repeat of the
same title at the same place raises nothing while a triage of it holds the issue, the run's records
saying so, unless it stops the work of a step that triage will not run again; one whose triage
failed, or met again after its fix was done, is raised anew), never scheduled, as the recovery is not - and
`onAddress: recover`: it reproduces the issue on the run's base commit,
gives the recovery one that is in a step's way, related to the work or critical, to fix at its root,
and lists the rest, proven, with a title and a description, in the run's `pre-existing-issues.json`,
shown in the pull request's body and the handoff. Where a workflow has such a step, every other reasoning step names it, so no issue is lost. The
engine raises the triage for each issue a reasoning step's answer reports under `preExisting`, as the shipped schemas spell it, and keeps the list.

A reasoning step's `effort` is one level for every model it may run on, or a map giving each its own -
`all` for the rest and a model's code for one; the shipped workflow runs its heaviest steps at
`xhigh` and its other agents at `high`, or `low` on the `fast` tier, every model of a step's tier at
the one level. Where a step gives a model none, its agent file's map or level is read the same way
below it, and a model neither names runs at its own default. Each model's level is checked against
what that model takes, and `validate-workflow` says, under the line that says a definition holds,
every step's models and levels, each provider's prices for the models its steps may run on and the
date they were read, and each key a step's model would read that the slot lacks. A step's order over
its tier's models is entries of a model and one of its provider's keys, each model's keys before the
next model, and a model named outright - on the step or in its agent file - runs on that model with
none after its own keys; `help agent-definitions` says what an agent file is and takes.

A reasoning step that says `ask: true` may put its questions to a person: the step pauses on them,
and the answer - a free text, or `decide`, which leaves them to the agent and what it recommended -
go on in the same attempt, `askRounds` times at most, on the two API clients as the next message of
the stored conversation and on Claude Code as a new launch whose continuation record ends with the
questions word for word. What an agent assumed without asking is kept either way - read as
`<step>.assumptions` and named in the run's own `run.assumptions`, so an unattended run stops for
nothing and hides nothing. A limit of a step's own - its turns, its budget, a truncated answer, a
full context - asks: `retry` begins a new attempt, `more` (offered only where a stored conversation
of the attempt can be handed) grants the bound once again in the same conversation, `compact` goes
on from the engine's reduction of it, and a configuration may answer any of it beforehand. Every
account a step could use at its limit, and a provider's service failing past the client's own tries,
pause the run safely instead - never a failure - and the resume runs that very step. What a step
spends is charged to the run's `budget` turn by turn as its agent works - every token of every kind,
and its cost at the configuration's prices for the model serving it - and each launch is held to
what is left of the run's dollars before its reserve where that is less than its agent's own
`maxBudgetUsd`, or where it names none: a launch that bound ended has reached the run's reserve, not
the step's own budget, so the workflow's answers may allow more and the step runs again given its
handoff, or else the run pauses for its budget - and a launch whose share was reckoned before more was
granted runs again given its handoff, its share reckoned anew. A turn whose cost could not be counted
leaves the run's dollars known only as at least what was counted: status, the report, `flow.json` and
every `--json` say so, `uncounted` beside `usd` naming why; in a run budgeted in dollars such a turn
reaches the run's reserve for the grant that stands, so the run asks, and a grant of more lets it go
on, each launch held to what is left as far as the dollars were counted. Where an agent
gave no handoff of its own - a limit ended its run, a crash cut the attempt off, its handoff turn
passed its bound or failed - the engine writes one from the stored stream of the step, its first
line saying why: the files the attempt wrote, the last commands it ran and how each ended, its last
messages word for word, and the steps before it that left notes, named by step; a step's `writes`
given as an expression is read where the step starts, the names it yielded narrowing what it may
write as a written-out list does.

A `pause` step asks and asks only: its question filled as a template is filled, and the questions an
upstream step wrote (`questions`) shown under its own - the shipped `blocked` shows what triage
found that only a person can lift - the step's answer answering them together. What the person gave
the run answers first, where they gave it: the first `preAnswer` entry whose `when` holds answers at
once, as a configured answer would, and an entry that quotes words holds only where they stand in the
input they are said to be of - the shipped approval is pre-answered by `preapproved = 'yes'` or
by the request's own words, quoted and looked for. The repository's bar for a recommendation -
`recommendations.md` in the workflow's folder, or the file its `recommendations` key names - is read
by `{recommendations}` in a reason step's prompt, live each time it is given, so a change holds from
the next question on; where the folder has none, a sentence says so and a recommendation says it
follows none. Beside it a workflow declares texts (`texts:`), each name a markdown file of its own
directory read in a prompt as `{text.<name>}` the same live way - the used workflow's own first
where the reading step is one of its, and the run's where none between them declares the name - and
the shipped flow declares its `recommendations` and its `closure` there, `closure.md` being the
repository's own from the day init writes it: the universe its decisions range over, and what else
judges them. No prompt the tool ships reads `{text.closure}` yet, and the file says so: what a
repository writes there reaches an agent once a prompt of its own reads it.

Before a person is left with a question the engine raises itself, the orchestrator's `advisor`
recommends on it: one reason step that only reads, run wherever the asking step works with its
records and its own conversations under `advice/<n>/` of the asking step's own directory, numbered
after those already there, its prompt reading the question (`{question.step}`,
`{question.kind}`, `{question.problem}`, `{question.answers}`, `{question.record}`), the run's
inputs and `{recommendations}`, its answer held to the engine's own schema - one of the question's
answers, with a text where that answer takes one. Unlike a run's reasoning step, it only finds the
Claude Code its model is reached through and never installs or updates it: where that is missing or
too old, the advice was not obtained, and the question says why. The question is open while the advice runs, so a
person who is there answers without waiting, and their answer ends the asking - the question says
so, and the step that asked starts again only once the advice's runner has ended; a pause or a stop
ends a running advice too, its question saying the recommendation is sought at the resume. What
every advice spent is counted against the run's budget, an ended one's among them. Where none was
obtained - a bar that could not be read among the reasons - the question says so, and where no
advisor is named it says that plainly. The advisor takes no question a step's own agent asked, nor
a `pause` step's that shows an upstream step's questions (they carry their recommendations), nor one
the run's input answers, nor the budget's own, nor a kind its `kinds` leaves out. A `test` step's
`filter` names the tests it runs; its `filterWhen` is an
expression that switches it - where it reads false the step gives no filter and every test of its
legs runs, as the shipped `verify` steps do where the run's input `affectedTests` is off, each item
tested on its own tests only where the run turns it on (`--input affectedTests=on`).

The models a step runs on are the configuration's, under `providers`: each provider is a company
whose models the tool will reach - its API's address, its keys' names, its client - and each model's
entry says what it takes, holds and costs, with the date its numbers were read. A provider's keys
are `apiKeyNames`, the names of the variables that hold them in the git-ignored slot
`.harness-config/workflows/.env/`, in the order they are tried; a second key of one company is a
second name in the list, and no key is read from the process environment - a CI machine writes the
file. `list-agent-providers` says it all without saying one key: each provider, client and tier, for
every appointed key and token `present` or `missing`, and for each setting a client reads from the
slot and each web-search source's key `present` or `absent` (none is needed), exit 0 whatever is
missing; `--template` prints the agent sections as `init` writes them into a new config, to paste
into an existing one - outside a repository too, and where the configuration it is to repair does
not load. A name
declared in two of the slot's files is refused as the runner's value directories refuse one, a line
whose name is no variable's name (`[A-Z][A-Z0-9_]*`) is refused by its line number alone - it may be
part of a key pasted on its own - and a file where the slot's directory should be is refused rather
than read as an empty slot. Where a value of the slot is read - a step's key as the run starts its
agent, a search source's, a setting such as the program a client starts - a file other users can
change, or can read while it holds a key, is refused with the command
that fixes it; a setting, such as `CLAUDE_EXECUTABLE`, is no key. `validate-workflow`
names every key a step's model would read that no file of the slot declares, with the steps that
would read it, and still passes - a machine that only checks a configuration needs no key. A model
reached through `claude-api` or `glm` reads its provider's keys; one reached through `claude-code`
signs in by the client's `auth` (`agentClients.claude-code.auth`): `login`, the machine's login,
where nothing is said, reading no key; `apiKey`, the provider's keys in their order; `oauthToken`
or `authToken`, the token the slot holds under the entry's `token` (`CLAUDE_CODE_OAUTH_TOKEN` or
`ANTHROPIC_AUTH_TOKEN` where it names none) - a name and never a token, appointed once beside
every provider's keys. Its `configDir` and `executable` name the variables of the slot that hold a
specific login's Claude Code directory and a program to start in place of npm's
(`CLAUDE_CONFIG_DIR` and `CLAUDE_EXECUTABLE` where they name none), appointed once the same way and
never a key. The credential slots - the runner's two
value directories and this one among them - are never handed to an agent, seeded from the main tree
or folded back into it, whatever git or the ignore block says.

Choosing a key is the engine's, where it runs a run's steps: a step starts on the first key of its
model's provider the slot holds and no remembered limit covers, or - `balanceKeyUsage` on (off by
default) - on the one that spent least in the last seven days, keys that spent alike in the list's
order, whatever tier, model, step, run or side-task spent it; the key is chosen when the step starts
and kept for it, and a limit or a rejected key moves it to the next in the same order. What a key
spent is what the engine counts for each request served with it, at the configuration's prices - or,
for a Claude Code launch, what the launch reports - recorded by the key's name, never its value, in
`.orchestrators/key-spend.jsonl`, the repository's records across runs: one line for each key and
model of each entry of a step's order that ran, so a run that moved keys while it worked books what
each key spent to that key, read for a key's week, its lines older than seven days dropped when the
file is next written. What the same key spends elsewhere - another repository, another machine,
another tool - is not seen. Each request an API client sends, and each Claude Code launch, goes
through the gate of its provider and key (the client's parallel bound, its optional pacing buckets
and a 429's cool-down, below), beside the orchestrator's `maxReasoningAgents`.

The Claude Code this tool uses is the one npm installs, and no other: the tool asks npm where its
global packages are and starts the program the package names, by its full path - never a launcher,
never a batch file, and on Windows `npm.cmd` itself is never started: the tool does what that file
does and starts npm's own `npm-cli.js` with node. A run that needs a Claude model through the
`claude-code` client needs npm, and no other run does. Where npm has no Claude Code, or one older
than this tool needs, it is installed or updated with
`npm install -g @anthropic-ai/claude-code@2.1.287` - never with sudo, never past the version
needed, never beside a running session of that installation: a process that runs its program or
node running its script, a `claude` that node runs, or a `claude` whose program the machine will
not say (another user's, on Linux), each named in the refusal; a `claude` installed any other way
whose program the machine names does not count, and a process table that could be read by names
alone refuses the change, saying why. The change runs under the same machine-wide lock and the
same never-stopped, read-back sequence `update-tool` has: after a change that failed, as after
one that ran, it says what npm holds since.
A run takes that step before a reasoning step that reaches Claude Code starts its agent, and
`run-side-task` before its chat, each with its refusals. The run's advisor and its reporter only
read, so they only find Claude Code and never take that step: where it is missing or too old, no
advice is obtained - the question says why - and no model words the reporter's answer, the status
answering with the facts alone and `ask-orchestrator` refused. A program the slot names (`CLAUDE_EXECUTABLE`, or the name the client's
`executable` gives) is a person's own choice:
started as named and never installed or updated, and a slot that cannot give it is refused, never
passed over for npm's. A client's `update` key (`agentClients.<client>.update`, true by default)
and `DISABLE_UPDATES` in the tool's own environment turn installing and updating off, and a run
that then needs one that is missing or too old is refused with the command to run by hand.

What an agent may do is one policy, the same on every client: everything is allowed except the
root actions, the places no agent reaches, and the restrictions - which only ever refuse, from
every source at once, and never remove one another's. A restriction is one string,
`Run(git push *)`, `Write(.github/**)`, `Read(secrets/**)` or `Web(pastebin.com)`: `Run` a command
prefix (its trailing `*` any arguments, the bare command too), `Write` and `Read` path patterns as
gitignore reads them, matched against the path as it stands under each of the agent's roots, and
`Web` one domain, the domain and every name under it, or `Web(*)` the web whole. A repository
declares them under `agentPolicy.restrictions`, and a workflow's definitions and steps add their
own to them, all applied together; `agentPolicy.webDomains` is the allow list beside them of the
pages an agent reads and the results a search gives, a search source's own requests held to the
`Web(...)` rules alone (left out, any address, the machine's own and its network's among them), and
a command that runs a program which fetches from the network - `curl`, `wget`, `Invoke-WebRequest`,
`Invoke-RestMethod`, `iwr`, `irm`, `http`, `https`, `gh api` - is decided as a web request too, by
each address it names, a proxy, a `curl --connect-to`'s or `--resolve`'s target and the proxy its
environment names among them; one that reads what it reaches from a file (`curl --config`,
`wget --input-file` or `--execute`) is refused where the web is held to any rule. An address whose
slashes after its scheme readers read apart - any scheme but its two slashes and a host after them
(`http:/\host`, `gopher:/host`, `http:///host`), or no slash after an `http`, `https`, `ws`, `wss`
or `ftp` scheme (`https:host`) - or whose host is spelled beyond ASCII (its `xn--` form is read as
written), names no host the policy can decide, and is denied. Refused always, not configurable: the
root actions (`sudo`, `su`, `doas`, `pkexec`, `run0`, `gsudo`, `sudoedit`, `runas`, `psexec`,
PowerShell's `-Verb RunAs`, `wsl -u root`, `systemd-run`, `schtasks` with `/ru`, wherever a command
names one), writes outside an agent's writable roots, every read and write of a credential slot or
a Claude Code directory of the run's and every search over one, any command naming one - by `~` or
`$HOME`, as a redirect's file, an option's value after an `=` or a `:`, a value glued to a short
option (`ssh -i./key`), the file an `@` or a `<` makes curl, httpie or gh read (`-d @x`,
`-F f=@x;type=y`), a word of a script, a variable the command string sets read with each value it
gives it (`p=../..; cat $p/x`), a pattern's segment that may match `..` (`.?`, `.[.]`, `.*`) read as
`..` too - or naming a directory that holds one whole (`.harness-config`, `..`, `~`, `/`, a pattern
that may match one) to a program that may read what it holds: not to one that only names, tests or
lists the paths it is given (`echo`, `printf`, `test`, `ls`, `du`, `df`, `stat`, `realpath`,
`mkdir`, `which`, a `find` that runs, deletes and writes nothing, and their kind), not in a word that
moves a command (a `cd`'s, `git -C`'s, `tar -C`'s, `make -C`'s) or names where it writes, which the
write rules decide, and not where it is the agent's working directory or one above it, which reaches
the slots only through that working directory (`.`, and `..` over a side-task's main tree); a
recursive removal or a move of a directory that holds a tree's `.git`, the tool's state, a slot or a
Claude Code directory (`rm -rf .`, `Remove-Item -Recurse`, `mv`, `rsync --delete`, `git clean -dx`),
the working directory among them, and a `find` or `fd` walk that may remove or move one - its
`-delete`, a remover its `-exec`, `-execdir` or `-ok` runs on what it finds, fd's `-x` and `-X` - read
as the walk reaches it, its start points, its depth, its prunes and excludes and its name, path,
regex, kind and emptiness filters read, and what a guarded directory holds read as it stands (so
`find . -name '*.pyc' -delete` runs, and `find . -name config -exec rm {} +` is refused where `.git`
holds a `config`); this tool's own verbs and `run-side-task`, run by any program the
policy reads or from a response file, or by a verb the shell expands (`run-side-$T`,
`dssharness $V`), its verb the first word after its name, for it takes no option before one; a command that sets
or takes away `DSSHARNESS_UNDER_AGENT`, the mark of every program an agent starts - its assignment,
empty or not, `unset`, `env -u`, `export -n` and `declare` and their kind, a name reference to it,
cmd's `set`, csh's `setenv`, `setx`, the name `read`, `mapfile`, `printf -v`, `getopts`, `wait -p`,
`vared`, a `for` or a `select` sets, one an arithmetic assigns (`let`, `((...))`), a PowerShell item
cmdlet's `env:` item, its `Environment::` path among them, or a start with no environment (`env -i`,
`env -`, `exec -c`) - and one that may: any of those places given a name the shell expands
(`unset ${v}AGENT`, `env -u "$v"`, `unset $(env | grep ...)`, zsh's `unset -m` pattern), a
PowerShell item cmdlet given a path it computes, a wildcard that may match it (`Env:DSSHARNESS_*`, a
relative one where the string names `env:`, as a move into it does) or none where the string names
`env:`; and any other word that names it, but a word of data - a program that runs none of its words
(`grep -rn DSSHARNESS_UNDER_AGENT src`, `echo "$DSSHARNESS_UNDER_AGENT"`), a git command that reads only
what git tracks (`git log -S`), a word holding a blank - which takes nothing away; and a
`git` or `gh` command called by a name none of its own commands
is - an alias or an extension (`gh extension exec` too), whose command line a configuration file holds - whatever any switch
says (a program `git` finds as `git-<name>`, `git lfs`, is its own) - and so are the `gh`
commands that hand an agent's work to another agent or install what the next launch loads (`gh copilot`,
`gh agent-task create`, `gh skill install`, `gh skill update`, `gh skill publish`) - and so is any
command that prints a credential (`gh auth token`, `gh auth status --show-token` or `-t`,
`gh auth git-credential get`, `gh config get oauth_token`, `git credential fill`, and any helper's
`git credential-* get` - `credential-store`, `credential-manager`, `credential-osxkeychain` and the
rest - each met by its words anywhere after its command, options before or between them; an alias
given one - `git config alias.cf 'credential fill'`, `git -c alias...`, `gh alias set` - is read as
the command it names where it is given, and `gh alias import` is refused): a
credential leaves its slot only by the key store that holds it, never through an agent's words. No agent
starts while this tool itself runs elevated: its step is recorded never started, saying so, before
any client is taken. A path is decided as asked and as resolved, and a refusal's places compare in
any case on every system. On Windows each segment of a path is read as the name Windows reaches by
it before any refusal compares it - in a command's words, its moves, its writes and its removals and
in a file tool's path alike: its trailing dots and spaces dropped (`.git.`, `k. .`), an NTFS
stream's or attribute's suffix dropped (`.git::$INDEX_ALLOCATION`, `k::$DATA`), and a short name
(`GIT~1`, `harnes~1`, in any case) read as its long name where the path is there - the system's own
answer, whatever short names the volume gives - and, where it is not, as each guarded name it could
shorten as well, for a command may make the place before the word is read (`git init sub` and then
`sub/GIT~1/hooks`); `HEAD~1` and their kind, which shorten no guarded name, are the words they are.
On Windows a device prefix
(`\\?\`, `\\.\`, `\??\`) is dropped from a path and from what the runtime makes of it - one naming
no drive and no share, a volume by its id or a pipe, is a place it cannot tell, and denied - and a
shell's spelling of a drive (`/c/x`, `/cygdrive/c/x`, `/mnt/c/x`) is read both as the drive (`C:\x`)
and as the directory it names on the current drive, an action allowed only where both are; a `cd`
is followed the same way - `pushd`, PowerShell's `Set-Location` and `Push-Location`, the directory
`env -C` or `pwsh -WorkingDirectory` starts its command in, and the one `git -C`, `tar -C` or
`--directory`, `make -C`, `ninja -C` and `patch -d` work in, in every spelling, glued or apart, each
word it may go to; a `cd` to a name under a `CDPATH` - the one the agent's commands start with or
the one the string sets - read in each of its directories too, a `cd` given no directory going to the
`HOME` the command or the string sets, `cd -` back to the `OLDPWD` the string sets or a place a move
of the string's left, and one whose place this does not know - `cd -` with neither, a `CDPATH`,
`OLDPWD` or `HOME` the string computes - denied - from every place the command reached
before it, a command after `&&` standing only where the one before it held and one after `||` only
where it failed, up to 64 distinct places, past which the command is denied. A
shell command is read whole before it is matched, as the shell's own parser reads it - split on
`;`, `&&`, `||`, `|`, `&` and line ends, a comment and a here-document's lines never commands, the
bodies of `$(...)`, backticks, `<(...)`, subshells, functions, loops and branches read as commands
of their own, each substitution's word kept as the expansion it is, a `for`'s word list read for
the paths it hands its body, a redirect kept apart wherever it stands - decided by the file it
names, never among the words a rule meets, and a quoted word spelled as one a word - the `NAME=value`
(and `NAME+=value`) words before a program kept as its environment and a variable - so set, or
declared by `export` and its kind, cmd's `set`, fish's `set`, csh's `setenv`, `setx` or PowerShell's
`env:` items - naming a program git or another tool runs
(`GIT_SSH_COMMAND`, `GIT_PAGER`, `EDITOR`, `PROMPT_COMMAND` and their kind) read as that program's
command line, a prompt (`PS0`, `PS1`, `PS2`, `PS4`) as the text an interactive shell expands, as
is the value of git's own setting (`-c`, `--config-env`, `GIT_CONFIG_*`) of a key git runs as one,
wrappers (`env`, `nohup`, `time`, `timeout`, `nice`, `xargs`, `command`, `builtin`, `exec`,
`eval`, `busybox`) stripped - env's options read by GNU env's own table, a long one by any prefix
that names it alone (`--spl`, `--u`, `--chd`), one that names none or more than one denied - and it
goes into `sh -c` and every other
shell's (fish's `-C` scripts too, its options read as fish reads them), the here-string, here-document or file a shell reads as the last input its descriptor 0
is given (`/dev/stdin` and PowerShell's `-` among its names), `cmd /c`, `powershell -Command` and a
decoded `-EncodedCommand`, and what `trap`, `watch`, `script` and `flock` run, the command sed's
`e` command and flag run, `ssh`'s and `wsl`'s command and a WSL distribution's own launcher's
(`ubuntu run`, `ubuntu2204.exe -c`) - with none, the script its input gives where a pipe, a redirect,
a group's input or an argument list's written input gives one, read as a shell's is, and nothing where
none is given (the string's first command), `ssh -n` and `-W` among them - the commands ssh's `-o`
settings run (`ProxyCommand`, `LocalCommand`, `KnownHostsCommand`, `RemoteCommand`), a string a
program runs as a shell's script (npx's and `npm exec`'s `-c` and `--call`, each of concurrently's,
hyperfine's and watchexec's commands, nodemon's `--exec`, entr's with `-s`, cargo watch's `-s`), read
as `sh -c`'s and as `cmd /c`'s, and the job `at` and `batch` take from a here-document or a here-string, a script the
string writes to a file before it runs it read as the here-document that wrote it, by each shell's
own quoting - PowerShell by a reader of its own grammar: its commands, pipelines, strings, here-strings,
variables, `$(...)`, `@(...)`, `(...)`, script blocks, hashtables, assignments and keywords'
conditions and blocks, each statement a block holds read as one that may run, `Invoke-Expression`'s
one string - by whatever name it is called, the call operator's string or a module's name before
it among them - read as a script of its own - to four levels, and into a shell another program runs with its script
(`find -exec sh -c`, find's words read to their `;`), a command `find -exec`, `fd -x`,
`rg --pre` or `--hostname-bin`, `parallel` or an option of a git subcommand runs - a pack program a transport runs
(`--upload-pack`, `--receive-pack`, `--exec`, clone's `-u`), difftool's `--extcmd`, rebase's `--exec`, grep's
`--open-files-in-pager`, `bisect run`, `submodule foreach`, filter-branch's filters and send-email's commands - read as its
own, each word it fills in an expansion, `env -S`'s
string split by env's own rules, and `$((...))`, `((...))` and `$[...]` read as arithmetic, their
substitutions as commands, `((...))` the command `let` given its expression - so `git -C x push` is `git push`, and `env A=1 git push` and
`A=1 /usr/bin/git push` are too; every word of a command is tried as the program of the words
after it - but a word of a program that runs none of its words, its data (`grep`, `rg`, `echo`,
`cat`, `which` and their kind) - a program read by its name in any case, a cmd line's `@` dropped, `git-push` read as `git push`. A word the shell expands where a rule reads a literal one
makes the command one the rule may be, refused as such (`git $VERB` under `Run(git push *)`) - and the
expansion is read only where the shell would make it: the reader keeps, for each word, which of its
parts were quoted, so `git 'p*sh'` is a word no rule meets and `gh api 'repos/o/r/pulls?state=open'`
an endpoint that names no merge, while a double-quoted `$x` is a variable still and an unquoted `*`
a glob - each word's quoting taken by where it stands, never lent to it by another word spelled
alike, and a PowerShell bareword's wildcards open as pwsh on Linux and macOS expands them, a
cmdlet's quoted ones too, which the cmdlet expands itself; a variable the string sets
(`p=../..`) read in each later word with each value it gives it, up to 16
readings, `$PWD` and a leading `~+` as each place the command stands in and each value the string
gives `PWD`, a `~-` and the directory stack's `~N`, `~+N` and `~-N` as places it does not know where
the shell expands them - a word's start, an assignment's value - and as the text they spell where it
does not (`git log @~1`, `cat '~1'`) - and
each, beside `~+`, a variable wherever a word the shell expands is read, the string able to give it
any word - and a `~` as each `HOME` the string, the
command or a command that runs it sets, while a variable it does not set and a
substitution are read as the text they spell, for their value is the agent's environment's or a
program's, which no word of the string gives; a `*`, a `?` or a bracket matches a name that begins
with a dot only where its shell would - a segment that begins with a dot; any word of a string that
names `dotglob` or `globdots` as bash and zsh read an option's name, in any case, with or without
its underscores and its `no` (`shopt -s dotglob`, `BASHOPTS=dotglob`, `setopt GLOB_DOTS`,
`unsetopt NO_GLOB_DOTS`), or names `GLOBIGNORE` or `BASHOPTS` at all; a command PowerShell or cmd
reads, whose wildcards list every name, or one given `-Force` - so `rm -rf *` and `build/*` run,
`.g*` is git's own, and a segment that may match `..` is read as `..` as well, as a shell whose
`globskipdots` is off expands it; a parenthesis after a command's word, which bash refuses and zsh
reads as a pattern's group or a glob qualifier (`rm -rf (.git|x)`, `*(D)`), is denied; an argument
list crosses no shell, so a variable in one is the text it spells - but on
Windows, where a program splits its own command line and a Cygwin or MSYS program - told by the
`cygwin1.dll` or `msys-2.0.dll` beside it - expands a pattern or a brace in it, those are read as the
expansions they may be for such a program and one that cannot be found, `gh`, `git` and the rest
taking them as they are - and what cmd reads again - a batch argument's `%`
among it - it expands wherever it stands; a program is never named by a pattern, however its
characters are quoted, for PowerShell's call operator finds `& 'C:\Git\cmd\gi?.exe'` by its
wildcards - and
a pattern, a brace or a `file://` address that may name a credential slot or a Claude Code
directory, or a directory holding one, is refused as naming it; a pathspec git expands itself is read
as git reads it, quoted or not - its wildcards matching a dot-name, `:/` and `:(top)` the
repository's root, its `icase` and `glob` magic - where git reaches what it does not track (`add -f`,
`grep --no-index` or `--untracked`, `clean`, `stash -u` or `-a`, `diff --no-index`), while a
directory a git command that reads only what git tracks (`status`, `diff`, `log`, `ls-files`, `grep`
and their kind) is given is no read of a slot it holds, a quoted pattern of one the text it spells, and
a directory an `--exclude` or `--exclude-dir` gives is what a program leaves out, never a read of
what it holds. What it cannot read whole it denies, said in help, not guessed
at: a script nested past four levels, a shell that reads its script from an input it is given
(`curl ... | sh`), a process substitution or another open descriptor (`/dev/fd/3`, `<&3`), an
`env -S` string holding a backslash, whose escapes are env's own, an alias it gives, a script another
program fills in (`find -exec sh -c 'echo {}'`), PowerShell's code no word spells (a static
method, an instance method but a string's, a collection's or a date's, a function's or an alias's
text, a class, `using`, its stop-parsing `--%`, a script `Invoke-Expression` is handed by no one
string, and a command line `Start-Process`, WMI, the task
scheduler or `Add-Type` is handed), a
program named by a variable, a substitution, a
pattern or a brace, an option of git's, gh's, fish's or Windows PowerShell's own before its command that it does not know,
sed running what it reads or a script the shell fills in, `at` given its job by a file or its input,
a configuration file `ssh -F` names (but none, `/dev/null`), whose settings may run commands, an env
long option that names none of env's or more than one, a script file the string writes and runs
whose text no here-document gave - a file the string writes known by the path it reaches from each
place its command stands, a device it writes to (`/dev/null`, and `NUL` where cmd or PowerShell writes
it on Windows) none, a run word - the program, the one a runner such as stdbuf or taskset runs past its
own options, or a script file named or given as a shell's input (`bash < x.sh`) - that file only where it
reaches that path or where a target the shell expands may be it, its literal end the word's
(`make > "$LOG"; bash scripts/report.sh` runs), and any such file where the shell expands the program
or the script file itself, never another word a runner is given (ssh's remote command, an argument) - a tilde whose variable
the string set to a value it computes an expansion as a target and as a run word (`HOME=$(pwd)`, then
`~/x.sh`) -
a terminal session's
program given what to run or type (`tmux`, `screen`, `zellij`, `byobu`), and `crontab`, `launchctl`
or `systemctl` installing or starting a job, each running what it is given apart from the agent;
what it does not see, a policy of words and not a sandbox, is the inside of a script file a command
runs, the code an interpreter is given (`python -c`, `perl -e`, `node -e`, awk's `system`), a build
file `make`, `ninja` or `cmake` runs, what an archive or a patch holds, a path a command computes
(`$(printf ...)`), and what a process apart from the agent's tree does. The worktree rules
(`agentPolicy.worktreeRules`, on unless turned off) keep an agent's worktree a thing the engine
can measure: git's state-writing verbs (`AgentPolicy.GitStateVerbs`, listed by
`help agent-clients`) and the forms of git's other commands that write the same - a fetch's `--update-head-ok`,
wherever it stands among its words (`AgentPolicy.GitStateForms`), a word the shell expands held as one that may be it
where its expansion may give it (`git fetch $OPT`, `-f$X`, and an unquoted one the shell may split into a word of its own,
`--depth=$n`, the refusal saying that quoting it lets it through), never a quoted one inside a word that begins
otherwise (`--depth="$n"`, `"refs/heads/$b:refs/remotes/o/$b"`) nor a pattern whose every match begins so
(`refs/heads/*`) - a git configured where it runs, by `-c` or `--config-env` of any key but
the ones that only shape what git prints, by clone's own `-c` or `--config` and clone's and init's `--template`, or by
its environment - a home it reads its global
configuration from (`HOME`, `XDG_CONFIG_HOME`, Windows' `USERPROFILE`) among it - the `git config`
it is and still the command it runs - the `gh` commands that run them or write gh's own configuration
(`gh pr checkout`, `gh pr close`, `gh issue develop`, `gh repo set-default`, `gh repo fork`, `gh repo create`,
`gh repo rename`, `gh auth setup-git`, `gh pr create`, `gh pr merge`, `gh repo sync`, `gh config set`; a command by one of gh's own aliases - `gh pr co` - read as
the command it names, and `GH_CONFIG_DIR` or another place gh reads its configuration from, set for a command, the
`gh config set` it is), and the `gh` commands that merge or start CI (`gh pr merge`, `gh pr update-branch`, `gh repo sync`,
`gh workflow run`, `gh run rerun`, `gh pr create`, `gh release create`, `gh release edit`), the `gh api` endpoints and
GraphQL mutations that reach the same - a ref, a file, a pull request or a release written through the API among them - and `gh alias set` are refused (reading
git stays allowed), git's own
files - a `.git`, file or directory, at any depth beneath a root and in any case, a nested
repository's and a submodule's too (`Write(**/.git/**)`), beneath the worktree and the main checkout
it was made from - are not written by a tool or a command (a redirect, a writer's file, a copy's
destination however an option names it - `cp -t`, `--target-directory`, `Copy-Item -Destination`,
each writer's options that take a value known to it, so `cp x dst --suffix y` writes `dst`, and an
option it does not know making every word but its first a destination - and the output a
program's option or word names: `curl -o`, `wget -O`, `tar`'s archive and the directory it extracts
into (`-C`, `--one-top-level`), `unzip -d`, `sort -o`, `patch -o` and the directory `patch -d` writes
in, xxd's output file, sed's `w`, robocopy's and xcopy's destination, `dotnet -o`,
a compiler's `-o`, `ssh-keygen -f`, `Invoke-WebRequest -OutFile`,
`Expand-Archive -DestinationPath`, `Tee-Object` and their kind), nor removed or moved whole by a command that
removes or moves a directory holding them, whatever `protectedPaths` says, and an agent does not edit its own policy
or what the next launch loads (`.harness-config/config.json`, `.harness-config/orchestrators/**`,
`.harness-config/skills/**` - but for a step whose `writes` names one skill's own directory by
name, `.harness-config/skills/<name>/...`, which opens that skill alone -
`.harness-config/runner/**`, `.claude/settings*.json`, `.mcp.json`). Whatever any switch says, the
tool's own state is written by its verbs alone, a place of the agent's own within it, as its work
directory is, the one exception. The protected paths (`agentPolicy.protectedPaths`, on unless turned off) are the
writes Claude Code guards - `.git/**`, `.vscode/**`, `.husky/**` and the rest of its list, which
`help agent-clients` prints and a test pins - refused on every client, so a role is not stopped
on one where it passes on another; turning them off takes this tool's refusal away, and Claude
Code's own guard stays Claude Code's. A step's `writes` is the one source that narrows: where a
step has them, a write under its worktree that matches none of their patterns is denied; the
reporter's reads are narrowed to the run's directory alone, with no command, no write and no web
at all. For Claude Code the same policy is rendered, never re-decided, into the launch's
settings: an ask rule for every tool, and the restrictions as deny rules, absolute once for each
root in the POSIX form Claude Code matches (`//c/x` for `C:\x`), a `Write` rule as the `Edit` rule
Claude Code holds its file tools' writes to, a read of each credential slot and Claude Code
directory beside them; a call those rules refuse before the engine is asked is counted and
recorded as Claude Code's own denial. This is policy, not
a sandbox: it decides what a call names, never what a program reads by itself - a program an agent
runs opens files as the user it runs as, and one that walks its working directory unnamed (an
archive of `.`, a search given no path) reaches a credential slot's files where that directory holds
one, as a side-task's main tree does; what it is never given is a key in its environment, and what
no file tool and no command's word the policy reads names is the slot itself, in any root, or a
directory that holds one to a program that may read it, but that working directory and those above
it. The words it does not read - a path a command computes, the code an interpreter is given, a
script file's inside, what a process apart from the agent's tree does - are where a policy of words
ends. Where this tool cannot decide a call it denies it, never allows: nothing
runs while the host that would answer is silent or gone. The policy is built and holds every
action behind it; the `claude-code` client asks it of every call, the tool runtime below decides
by it each call it runs - the web's of the `claude-code` client, and its file tools' where Claude
Code's own process runs apart from the isolation, served through the engine's tool server, and every
call of the API clients, the `claude-api` and `glm` clients below - for every
agent a command of this tool runs: a run's reasoning steps, its advisor and its reporter, and a
side-task.

Beneath the policy, the operating system isolates every command an agent runs, so a command spelt
past a reader of words still reads no credential slot, and writes none of git's code-running files,
none of its pointers to another repository and none of the tool's state; what it leaves that git
outside would go on with as it lies - an operation's state, a replacement - fails what ran once it
has, and the rest it writes is the work itself, whose limits are said below. On Linux, WSL 2's included, the mechanism is the system's
bubblewrap 0.8 or newer -
the first `bwrap` in an absolute directory of the PATH outside the tree, none where the user could
write it or a directory above it, for the tool runs it outside - on macOS the system's own sandbox,
`/usr/bin/sandbox-exec`, and on Windows Microsoft's MXC process container, made through MXC's own
library (`Microsoft.Mxc.Sdk`, MIT, named in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES)); each is what
the contained step becomes in the child's place - on Windows this tool again, inside the step's job,
which starts the program inside a container of its own and hands back its streams and its exit code -
so no child runs outside it for an instant.

Inside it the machine is read-only. Written are the agent's writable roots - its worktree, its work
directory - and what git writes of a tree it writes: its own git directory alone. A main checkout's `.git`
is written whole, and an agent's worktree a repository of its own, for git makes its lock files directly
in it, but a main checkout's `commondir` - made naming the directory itself, which git takes as no
pointer, where a file made would lead every git to another repository's own files, and never made in a
repository that keeps its refs in a reftable, where any file there breaks every commit git makes on a
branch - and its `worktrees/`; a linked worktree's own git directory is its directory under the common directory's
`worktrees/`, and the common directory itself - its `objects/`, its `refs/` and `reftable/` wherever
the repository keeps them, its `logs/` and Git LFS's `lfs/` where there is one - is read-only to the
work: `git add`, `commit`, `fetch`, `rm` and `status` write the objects, the index and the refs in a
main checkout and in an agent's repository, while in a linked worktree - an agent an earlier version
made, a leg pinned to a plain worktree - `git add` and `git fetch` fail there, said at each launch
over one and on each leg's line over one; and in a main checkout what writes another worktree's git directory: `git worktree add`,
`move`, `remove` and `prune`, `git`
gc where there are linked worktrees. The refs written are those of a repository whose git directory
the work writes whole - the main checkout's over a side-task, a leg in place and a run's commit and
push hooks, an agent's own in its repository - every branch, tag,
stash and remote-tracking ref, and their reflogs - which a command spelt past the words policy can
move; a run's push sends the commit its commit step recorded, never what the branch names later.
Written too are a temporary directory of the launch's own, named by `TMPDIR` - on Linux the system's `/tmp` inside, so nothing another
process keeps there is reached, on Windows MXC's own; and the declared tool caches, each pointed by
its variable into the cache directory of the child's write scope - the places it writes and the Unix
sockets outside it reaches (`IsolationCaches.ScopeName`) - under the isolation's own
(`dssharness-isolation/caches` under `XDG_CACHE_HOME`, else `~/.cache` - `~/Library/Caches` on macOS -
and under `LOCALAPPDATA` on Windows, each scope's directory beside a `.scope` file naming its places),
so a package one child plants is run only by a child that writes everything it writes - the next
launch of the agent, the next run of the leg - never by one that writes another tree or the main
checkout, a commit's or a push's hooks among them, or reaches the user's SSH agent, and a scope's
caches are taken away once none of its places is left (`IsolationCaches.Sweep`): `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_PLUGINS_CACHE_PATH`, `npm_config_cache`,
`YARN_CACHE_FOLDER`, `PIP_CACHE_DIR`, `UV_CACHE_DIR`, `CARGO_HOME`, `GOMODCACHE`, `GOCACHE`,
`GRADLE_USER_HOME`, `CCACHE_DIR`, `npm_config_store_dir` (pnpm's store), `YARN_GLOBAL_FOLDER` (Yarn 2
and later), `BUN_INSTALL_CACHE_DIR`, `PUB_CACHE` (Dart's and Flutter's) and `XDG_CACHE_HOME`
(`IsolationCaches.Declared`, listed by `help agent-clients`). The user's own caches stay read-only,
for a cache is code a later build runs as it lies; Cargo, Gradle and pub keep theirs in a home the
variable moves whole, so what the user keeps there - a registry, a property, a tool pub activated - is
not read inside, and Maven's local repository has none, so a Maven build names another with
`-Dmaven.repo.local`. Moving `XDG_CACHE_HOME` moves what the user installed under it too: on Linux
Playwright's browsers are read where they are (`PLAYWRIGHT_BROWSERS_PATH` at the user's `ms-playwright`, where it is there and the
variable names none already), so a browser installed inside fails; everything else there - Hugging
Face's models, pre-commit's environments - is fetched again into the scope's own. Hidden - an
empty place under bubblewrap, refused by macOS's sandbox, denied by MXC's container - are every
credential slot of the main checkout and every Claude Code directory - `~/.claude`, the tool's own
and the one the slot names for the `claude-code` client's logins (`ClaudeCodeDirectories`, one list
for a launch and for the tool's executions alike) - with `~/.claude.json` beside each, the shell
snapshots Claude Code sources read again, and on Windows a `claude-code` launch's own read, below.
Hidden too, from every isolated command and every execution of the agents' work alike, are the places
the user keeps credentials in (`UserCredentials.Declared`, listed by `help agent-clients`): `~/.ssh`
but its `known_hosts` and `config`, which ssh reads itself to reach a host; `~/.aws`, `~/.azure`,
gh's `~/.config/gh`, `~/.kube`, `~/.docker/config.json`, `~/.netrc` (and `~/_netrc` on Windows),
`~/.git-credentials` and git's `~/.config/git/credentials`, GnuPG's `private-keys-v1.d` and
`secring.gpg`, `~/.config/gcloud`, `~/.npmrc`, `~/.pypirc`, Cargo's `credentials.toml`, Maven's
`settings-security.xml` and Gradle's `gradle.properties` - each also where its tool's own variable moves
it in this tool's environment (`GNUPGHOME`, `DOCKER_CONFIG`, `GH_CONFIG_DIR`, `KUBECONFIG`,
`AWS_SHARED_CREDENTIALS_FILE` and their kind), `XDG_CONFIG_HOME` in place of `~/.config`, and on
Windows gh's, GnuPG's and Google Cloud's under `%APPDATA%`. Under MXC's container, which grants nothing
beneath a denial, `~/.ssh` is hidden whole. `agentPolicy.hiddenPlaces` adds more and takes none away:
each a whole path or one under `~/`, a directory where it ends with a separator, else a file - refused
given empty, and where an entry is no whole path, climbs with `..`, holds a pattern, or is a root or
the home itself. The words policy refuses a file tool's read and write in one, a search over one, and
a command that names one - ssh's own files among them, which inside only ssh reads - or a directory
that holds one and may read it, as it refuses a slot. Left out, for builds read them: NuGet's
`~/.nuget/NuGet/NuGet.Config` (`%APPDATA%\NuGet\NuGet.Config` on Windows), whose package sources
every restore reads, and Maven's `~/.m2/settings.xml`, whose mirrors and repositories every Maven
build reads - each refuses a file it cannot read - so a password either holds is better given as a
value (NuGet's `NuGetPackageSourceCredentials_<source>`, Maven's `${env.NAME}`). A build that reads a
hidden place fails inside - a private registry's token in `~/.npmrc`, a cloud's key in `~/.aws`, a
git dependency fetched over ssh with the user's own key: hand it the secret as a value in the
runner's environment instead, which this tool reads and gives its steps. A place that is not there is
made, empty, only where a command could make it, inside a place written: every isolated child reads
the home alone, so nothing is made there and the user's home is left as it is. One that holds a tree,
or a place written, leaves the launch or the execution with no isolation, saying which.
Written by nobody, inside what is written, are git's code-running files and its pointers to its own
directories and to other content: the hooks git runs and the common directory's `hooks/` beside them,
which git runs again once `core.hooksPath` is gone; the common directory's `config`,
`config.worktree` and `info/`; its `refs/replace/` and `objects/info/`, whose replacements and
alternates would put other content in place of what every checkout reads; its own `commondir` -
made, where a main checkout's `.git` holds none, naming that directory itself (`.`), which git takes
as no pointer, and in a repository that keeps its refs in a reftable, where any commondir file in a
git directory the work writes breaks every commit git makes on a branch, never made and held absent
instead - macOS's sandbox and MXC's container denying its creation with nothing at the path, and
bubblewrap, whose every rule is a mount that holds no path that is not there, leaving the ground
with no isolation - and its `worktrees/`; each linked worktree's `config.worktree`, `commondir` and
`gitdir`; every linked worktree's `.git` file, the agent's own and every other git lists; and every
git directory's `modules/` whole - the common one's and each linked worktree's - which holds the
submodules' own git directories, so a submodule's git fails inside. Written by nobody too are every configuration file git
says it reads there (`git config --show-origin`), every file one includes whether its condition holds
now or not - an `onbranch:` a later checkout makes true among them - each tree's `.harness-config`,
the tool's state and the launch's own directory. Each place not there inside a
written place and under no guarded one is made, empty, before the launch, so no command makes it
and has it read or run outside later - but a state file, which the tool reads as meaning something,
is never made, and the words policy alone guards it. No directory above a guarded place whose own directory is written, and
no writable root itself, is renamed or removed - a place moved away would take its rule with it and
leave its name free to be made again, and a root put back as a link would lead the next launch's
rules elsewhere. A launch has no isolation - which the mode below turns into a refusal or a notice -
where its places cannot be held: git cannot say where a tree keeps its files (git 2.31 or newer
answers) or list its repository's replacements, a main checkout's `.git` holds a `commondir`
leading anywhere but to itself - to another repository's own files - or holds one at all, or none
and the mechanism holds no path that is not there, in a repository that keeps its refs in a
reftable, where any commondir file in a git directory the work writes breaks every commit git
makes on a branch, a file git's configuration
includes is not there inside a place the agent writes, where a command could make it (an empty file
includes nothing), a link stands where the agent writes on the way to a guarded place, or, under
bubblewrap, a guarded place stands on a file system that finds a name in any case - a Windows drive
under `/mnt` - where a mount holds one spelling alone. Once a launch or one of the tool's executions
has run under a mechanism, however it ended, the tool looks at what no rule could hold before
anything of its own goes on over the trees: the state of an operation under way - `rebase-merge/`,
`rebase-apply/`, `sequencer/` - made or changed in a tree's own git directory it wrote, which git's
next continue outside, the user's or an editor's, goes on with as it lies, running each `exec` line
its todo holds; or a replacement made or moved in a repository whose refs it wrote, as git lists them
whatever keeps them - loose, `packed-refs` or a reftable - which every git started outside would read
in an object's place. Either fails the step, the leg, the commit or the push, naming it and the
remedy - remove the state, never continue or abort it, for an abort puts the branch where the state
says - as does a git that cannot list the replacements within a minute
(`IsolationDecision.AfterwardAsync`). Every git of this tool's own runs with no file-system monitor
(`core.fsmonitor=false`), no object replaced (`GIT_NO_REPLACE_OBJECTS=1`) and, in a tree an isolated
agent or execution works over - a worktree or the main checkout - with the git directories git
named before it ran (`GIT_DIR`, `GIT_COMMON_DIR`), whatever the tree's own files say later
(`GitOwnSettings`); and none of them runs a submodule's own configuration - a repository an agent
made in its tree and put in the index is one, its clean filters among it: none recurses into a
submodule (`submodule.recurse=false` but for a commit's and a push's, which run inside), and every
status and diff tells a submodule by the commit its entry records against its HEAD
(`--ignore-submodules=dirty`, given on the command line, which no `.gitmodules` outranks), never
asking its own git about its work tree. So a submodule's own uncommitted edits are no change to a
status of the tool's: deleting a worktree asks each checked-out submodule whose repository is among
its superproject's modules for its own, and names one whose repository is anywhere else - a `.git`
of its own in the work tree - as a change never looked into, which stops the deletion as any
uncommitted change does. Otherwise a git of the tool's reads the repository's configuration as git
does, its filters among it, which is why every configuration file is written by nobody. The user's
own git, an editor and a prompt read the repository as it stands: the refs and objects the work
wrote, an operation's state it left until a person removes it, and a repository it made inside its
tree, whose own configuration - an fsmonitor, a filter - git started in that directory reads and
runs, as it does any repository's.

Under bubblewrap the child has a user namespace of its own that allows no further one
(`--disable-userns`) - but a `claude-code` launch's outer layer, below, which is left one to make each
command's own layer in - and a process namespace with its own `/proc`, so no command reaches another
process's environment; each place is a mount, each directory above one a mount point no command
moves; WSL's interoperation sockets and shared directory (`/run/WSL`, `/mnt/wsl`, and `WSL_INTEROP`
taken from its environment), screen's sessions, the user's runtime directory whole - their service
manager, message bus and agents' sockets - and the container engines' sockets are hidden; where the
kernel has Landlock's scope (Linux 6.12 or newer) the contained step enters it first, so no abstract
Unix socket outside is reached and no process outside signalled, and where it has not, the record
says so, and every launch, in its stream, and every leg, on its line, says as it starts what is left
reached.
macOS's sandbox denies every operation it does not allow - never allowing all but a list,
which would leave LaunchServices free to start a program outside it - and allows what a build, a test
and a shell do as the user: its own processes, the system's settings by the names a build asks -
never another process's arguments or environment - a terminal, the network, the services that
resolve names and trust certificates, every file read and no Unix socket but the name resolver's and
those under the launch's temporary directory, each place ruled at its real spelling (`/var` is
`/private/var` there); it is no mechanism where this tool already runs inside a sandbox, for macOS
starts none inside another. MXC's container grants nothing it is not told to: every fixed drive is
read, each writable root written, each place written by nobody read, each hidden place denied
outright. It meets a place by its path alone and refuses no rename alone, so each directory above a
guarded place whose own holder is written is read alone, every entry it holds written again as the
profile has it - nothing is made or removed directly inside it, so git makes no lock file in a
`.git`, or a linked worktree's directory under it, that holds a sealed place: `git add`, `commit`,
`fetch` and `push` fail there - in a worktree, over a side-task's main tree, and in a run's `commit`
and `push` steps - which each record says as the anchors' cost. The child has the environment the
step was given and the container's own user interface - no clipboard, no input sent to another
window - which a program that loads Windows' user interface library needs to start at all. On each,
the network is the user's, the loopback both ways.

On Linux and macOS a `claude-code` launch's own process runs apart from its commands, reading its
login and the key it is handed there, and every shell command it starts is handed into a layer of
its own through its shell prefix (`CLAUDE_CODE_SHELL_PREFIX`: this tool and `--isolated-shell`, which
becomes the mechanism with `/bin/bash` inside, the launch's isolation read from the file
`DSSHARNESS_ISOLATED_SHELL` names) - so no command it runs reaches its directories, its login or its
environment. On Linux Claude Code's own process runs inside an outer layer of bubblewrap: the
machine read-only, every credential slot hidden, git's code-running files and the tool's state
sealed as for its commands, and written but its writable roots, its temporary directory, the
declared caches, Claude Code's own directories and `~/.claude.json` - without which it fails every
shell command - and the step's `claude-receipts` and `claude-tmp`; the outer layer alone is left a
user namespace to make each command's layer in, and the mounts it laid are locked into every
namespace made inside it, so none is undone there. A command Claude Code ran without its prefix
therefore still runs inside the outer layer, never with the user's rights - though there it reads
Claude Code's login and environment and could write its directories. Where Claude Code never ran,
its directory and `.claude.json` (holding `{}`) are made first, for inside it can make neither. Where
this machine's bubblewrap cannot start again inside a layer of its own - an AppArmor profile that
denies its children every capability, as Ubuntu's `bwrap-userns-restrict` does - the probe says so,
and Claude Code's own process runs outside every layer, as on macOS, whose sandbox starts none inside
another; there a command Claude Code ran without its prefix runs outside with the user's rights, and
each launch says so as it starts. In or out of a layer, Claude Code opens no file of the agent's in
its own process: its file tools are
not given, `--tools` names its shell alone, and the engine's tool server serves the tool runtime's
(`view_file`, `find_files`, `search_files`, `create_file`, `replace_in_file`, `insert_in_file`,
`create_directory`, `delete_file`, `delete_directory`, `move_path`) in their place, each file read
through the file the engine opened, decided again by where the system names it, and each written,
made, moved or deleted relative to its directory held from the writable root it was decided in with
no link on the way, so a link a command - one left running in the background among them - puts on a
path between the decision and the act reaches nothing. Each shell command the engine allows is given back with a receipt at its head
(`: dssharness-receipt <32 hexadecimal digits>;`, the shell's no-op), which `--isolated-shell` writes
in the step's `claude-receipts` directory, where no command handed over writes, before it runs the
command - the command itself run without that mark, and without the variables that hand it over, so
nothing it is started with names its receipt or where receipts are written. A call that comes back
with none - a Claude Code that skipped its prefix - ends the launch failed at once, and one run in
the background is waited for within 20 s; however a launch ends - at a turn's result, Claude Code
gone or its output cut - each allowed call whose receipt is not there yet is waited for within 20 s
before anything of it is ended, one with none by then ending it failed, and where the launch was
stopped or ended at once, each is said. A receipt proves only that Claude Code handed the command
over, and only once it has run: it stops nothing, and a command run without the prefix runs where
Claude Code's own process runs, where it could write a receipt of its own - so what holds such a
command is the outer layer on Linux, and nothing on macOS or where bubblewrap starts none inside
itself.
Claude Code's own temporary directory is the step's `claude-tmp`, where no command writes but the
one file each ends by writing its working directory to - made empty, given that command alone,
written in place, never removed or replaced - so nothing Claude Code writes or reads there in its own process
is a command's to replace with a link; it goes with the launch. Claude Code's own sandbox is turned
off in its settings, for none nests in a command's layer; where this tool runs as a runtime handed its
assembly, or from a path holding ` -`, neither of which the prefix can name, the launch is not
started, saying why. On Windows, where nothing becomes another program and every command Claude Code
runs is Git Bash's, the launch runs whole inside MXC's container with its directories read alone: its
commands read its login there, and a login whose token has run out is not renewed inside until a
`claude` run outside renews it - written, they would let a command plant what Claude Code runs
outside later. There its own file tools are given, for they open each file inside the container,
which holds every place they reach as it holds the commands, and its temporary directory is MXC's
own inside it; nothing is handed over, so no receipt is asked. Where there is no isolation at all -
`preferred`, on a machine with none - Claude Code's own file tools open each file by the path the
engine decided, the words policy alone between them. The API clients' `run_command` starts each
command inside it. The mechanism is proved once per process, before the first launch whose agent may
run a command, on the tree's own file system under its runs directory: a canary in a hidden place
unread, by its name and by another case of it, a place written by nobody unwritten by either, a place
outside every open one unwritten, an open place written - on Windows after MXC's library says this
Windows has the container (Windows 11 24H2 build 26100.9278 or later, or 26H2 build 26300.9550 or
later), and followed by each of node, git and Git Bash's `sh` found on this machine started inside it
and asked to answer, one that does not making none, said with how it ended; on Linux bubblewrap,
once proved, is started again inside an outer layer of its own around a shell that must end 0, and
where it does not, it is the mechanism all the same, a `claude-code` launch's own process outside
every layer, said with how the trial ended. MXC 1.0.1 ends every
program of Git Bash's with `0xC0000142`, a library that would not start, so where Git Bash is
installed Windows has none yet; from WSL 2, bubblewrap isolates. `agentPolicy.isolation` says what
happens where this machine has none: `required`, where nothing is said, starts no agent that may run
a command - its step ends failed (14), the advisor gives no advice, `run-side-task` exits 14, each
saying why and the remedy (on Ubuntu, where `kernel.apparmor_restrict_unprivileged_userns` is 1, the
`bwrap-userns-restrict` AppArmor profile; on Windows the builds MXC needs, or WSL 2); `preferred`
runs it on the words policy alone, said at every launch. An agent that may run no command - the
reporter - needs none, and none is probed. Each launch's records say what it ran under - `isolation`
in `result.json` and in each turn of a side-task's `task.json`: the mode, with the mechanism and its
version - macOS's own for its sandbox, MXC's library's for its container - what it leaves reached
(`leaves`) and what its anchors cost (`anchorCost`), or why there was none. Where it ends: the
machine stays readable as the user reads it but the hidden places - a credential kept anywhere else
among it - and the system's own credential stores are services, not places: on macOS the login
keychain is reached through the system's security service, which the sandbox lets through for the
trust of certificates; on Windows MXC's container shows the Credential Manager empty; on Linux the
keyring sits behind the user's message bus, hidden with the runtime directory. The network is
theirs, and what no rule at a path reaches - a hard link made before the launch, another path bound
to the same files - is reached; inside, no hard link can be made from the machine's files into a
written place: under bubblewrap each place is a mount of its own and a link fails across them,
macOS's sandbox denies every operation it does not allow and no rule of the profile's allows one, and
MXC's container grants the machine read alone, which a hard link needs written - each held by a canary
in its real suite, whose failure would name the link allowed. git's refs and object store, and Git
LFS's, are written as git needs them in each ground's own repository - the main checkout's over a
side-task, a leg in place and a run's commit and push hooks, an agent's repository its own, a linked
worktree's none, its common directory read-only to the work: so a command over the main checkout can
move any branch, tag, stash or remote-tracking
ref, which a person's checkout outside then takes, and, as git's checkout reads an object as it lies
without hashing it again, put other content under a name the store already holds, which a checkout
outside then writes. A command Claude Code runs without its shell prefix runs where Claude Code's
own process runs - in the outer layer on Linux, with the user's rights on macOS and where bubblewrap
starts none inside itself - its missing receipt telling it only once it has run. This tool's verbs that
read a credential slot or write its state or install would fail inside it, so each is refused to
every agent by name, a side-task's as well as a step's (`AgentPolicy.SlotOrStateVerbs`, listed by
`help agent-clients`): `init`, `create-worktree`,
`delete-worktree`, `create-orchestrator`, `delete-orchestrator`, `list-agent-providers`,
`run-orchestrator`, `resume-orchestrator`, `answer-orchestrator`, `amend-orchestrator-request`,
`pause-orchestrator`, `stop-orchestrator`, `ask-orchestrator-status`, `ask-orchestrator`,
`create-agent`, `seed-agent`, `refresh-agent`, `rebase-agent`, `fold-agent`, `delete-agent`, `legs`,
`install-missing-tools`, `update-tool`, `sync`, `build`, `test`, `run`, `check-mutations`,
`stop-run`, `clean`, `host-exec`, `up`, `down` and `workflow-cache`, and the steps nobody types
(`host-agent`, `sync-serve`, `host-hold`, `contain-exec`, `isolate-exec`); those that only read, and
those that write only the tree's own files, stay open (`AgentPolicy.OpenVerbs`).
`validate-workflow` and `export-workflow` are among them: where a person's run of either would fetch
a remote workflow into the workflow cache, an agent's - told as `run-side-task` tells it, by
`DSSHARNESS_UNDER_AGENT`, which its every child carries, or a process of this tool's above it in the
process tree, one whose tree cannot be read taken as no person's - reads the cache alone and writes
nothing to it, and one the cache does not hold is refused saying what made the command an agent's
and that a person fills the cache from a shell of their own, which no process of this tool's started
and which carries no `DSSHARNESS_UNDER_AGENT`, with `dssharness validate-workflow` or
`dssharness export-workflow --from`.

What the agents wrote runs inside the same isolation when this tool runs it. The work of each leg of a
run's `build`, `test`, `run` and `check-mutations` step that this machine runs - every phase it starts:
configure, build and test commands, a runner's steps, a sweep's arms - is held inside it, its own
profile from the leg's ground (`ExecutionGrounds.OfLeg`), built as a launch's is: the machine
read-only; every credential slot of the main checkout, every Claude Code directory and every place the
user keeps credentials in hidden;
the tree the leg works in - the step's tree or its copy under `.legs` - a sweep's workers under
`.mutations`, the build directory of the leg's run of each runner action it runs, its own and a run
check's (`{actionBuild}`, each step's `{stepBuild}` in it; `LegWorkload.WritesIn`), a temporary
directory of its own and the declared tool caches written, and inside them git's code-running files,
the configuration directory - but those build directories, under it - and the tool's state written by
nobody. The
run's records are no place the work writes: every log, the ledger, the run's claim and its stop are
the tool's own, written from what each phase prints. What the tool itself reads out of, or writes
into, a place such work writes - a runner's step's own directory and its kept outputs, a test's evidence kept on failure, a sweep's
sites mutated and put back in its workers, a sync's every read, write, removal and listing in a copy -
goes through no link below that place's root
(`UnfollowedFiles`): each acts relative to a directory held open from that root, every entry on the
way opened as itself and never looked up by its path again, so a link on the way - or one the work
swaps in while the tool acts - is refused, naming it, and a link at the entry's own place is replaced
or removed as itself; a link the work put there never has the tool, outside, write over a file of
the user's or carry what a hidden place holds to where the agents read. A pull (`sync --pull`) alone
follows a link along the path it names, as it is spelt - a build directory pointed at another volume
is ordinary - but never into a place the isolation hides from every command, decided by where what
it opened stands as the system names it; a link below a directory it names is passed over and named. A runner's values and secrets reach its steps as values in their environment, read by this
tool, never as the slot's files, which they cannot read. A set-up's lines are the configuration's and
bring up what outlives the leg, which no isolation outlives, so they run outside it: where the leg's
work is held inside it under `required`, each runs from an empty directory outside every tree
(`outside/` under the isolation's own, `ExecutionIsolation.Outside`), so a program that looks for its
project where it runs or above it - a compose file, a package's scripts, a makefile, a repository -
finds none of the agents', and each is read first as the policy reads an agent's command
(`CommandAnalysis`) - its words, every command it runs, every path a word names, a `source:target`'s
and a search path's parts among them, and every value the leg's environment gives it. One that names
a place in the tree the leg works in, the run's or the main checkout, or a directory holding one, that
names a place by what its shell would expand, that moves to a directory that is no whole path, or that
cannot be read whole skips the leg (`skipped-unavailable`) before any set-up comes up
(`SetUpConfinement`); one that fails says where it ran, where nothing of the tree is found - keep what
a line reads outside the trees and name it by its whole path, or bring what it serves up by hand.
Under `preferred` they run in the tree as the configuration gives them. A leg
another machine runs - an ssh host, a WSL distribution - runs there as that machine's tool runs any
leg, outside this machine's isolation: an ssh host holds none of this machine's slots, state or git's
files, which a sync never carries, for an ssh host is taken to be another machine that reaches none of
this one's files - one that is this machine, or a WSL distribution, a virtual machine or a container
reached over ssh that shares its drives, cannot be told from another by its name or its address, which
ssh's own configuration may point anywhere and a loopback port may forward anywhere, and runs the
agents' work outside the isolation, this machine's files written: name no such host for a run's legs
where the agents' work must be held; a WSL distribution runs on this machine and reaches its drives
under `/mnt`, written, its slots and every tree's git files among them, so a run's step whose legs'
work is held inside the isolation skips such a leg under `required` (`skipped-unavailable`) and runs
it there under `preferred`, its line saying so. A run's `commit` and `push` run git
and the repository's hooks - those git's directory holds, and those `core.hooksPath` points into the
tree for, a husky directory, a pre-commit or lefthook configuration they read - inside it too, over
the main checkout, which they write: its work tree and its `.git`, but git's code-running files, its
pointers, replacements, alternates and submodules' git directories and the tool's state, as a
launch's - under MXC, which makes nothing directly inside `.git`, git's commit
cannot make `.git\index.lock` and fails there. A push to a remote over http or https is handed the
credential git's own helpers give for its address, asked outside (`git credential fill`), as values in
its environment read by a helper its command line names in place of every helper the configuration
names - so it needs no keyring behind the user's message bus, which the isolation hides - and whatever
runs inside the push, its pre-push hook among it, can read them, as in any push; one over ssh signs
through the user's SSH agent alone, for their keys under `~/.ssh` are hidden, its `known_hosts` and
`config` read. Under bubblewrap and macOS's sandbox a push to a
remote over ssh (`ssh://`, `git+ssh://` or `host:path`, as git reads its push address), and a commit
the repository signs with an ssh key (`commit.gpgsign` with `gpg.format` `ssh`), reach the one Unix
socket outside that is the user's SSH agent's (`SSH_AUTH_SOCK`), wherever it stands - inside ssh's
hidden directory among it, named to them at its real spelling, so a link there leads nowhere hidden -
their hooks with them - and no other (`GitSsh`); every other commit and push, and every leg, reaches none, so a
hook reaches the user's SSH agent only where git's own work does; MXC's container is handed none, and as it
hides `~/.ssh` whole a push over ssh inside it finds neither key nor `known_hosts` and fails - a push over
https, whose credential this tool hands in as values, works there. A commit the
repository signs with gpg or x509 reaches no agent of the user's where its socket is hidden - in the
user's runtime directory under bubblewrap, and every one under macOS's sandbox and MXC's container -
and fails there, as does one signed with an ssh key under MXC. One signed with an ssh key whose
`user.signingKey` names a file under `~/.ssh` fails as well, for the file is hidden: name the public
key itself (`key::` and its line) and keep the private key in the agent. Every other git command of this
tool's runs none of the repository's hooks - `core.hooksPath` pointed at the null device (`GitHooks`),
set as every git of the tool's sets its own settings, through `GIT_CONFIG_PARAMETERS` - so a fold, a
refresh, a rebase, a sync or a question of the tool's never runs the agents' work as a side effect of
a checkout, an index written or a ref moved; those that only read (`rev-parse`, `config` and the like)
are left as they are, and answer where the hooks are as git runs them. `agentPolicy.isolation` decides
each as it decides a launch: where this machine has none, `required` fails the step (14) with nothing
run - a leg whose own cannot be had, its tree one git does not answer for, is `skipped-unavailable` -
and `preferred` runs it outside, each leg's line saying so. The step answers `isolation`: the mode,
with the mechanism and its version, what it leaves reached and what its anchors cost, or why there was
none.

The `claude-code` client starts Claude Code once for each launch, a new session every time -
never resumed or continued, keeping no session file (one a launch leaves all the same is removed
once it has ended, however it ended, and said) - streamed both ways: the step's prompt is the first
message on its input, never an argument, and the launch runs in the agent's working directory with
every other root it reaches added. It is given its model and no fallback, the step's effort, turns and budget, and the tools of
the agent's capabilities and no other (`read`: `Read`, `Grep`, `Glob`; `write`: `Write`, `Edit`,
`NotebookEdit`; `run`: `Bash`; `web`: none of Claude Code's, but `mcp__dssharness__web_fetch` and
`mcp__dssharness__web_search` of the engine's tool server beside them), never one that starts
agents, loads skills, asks a person or plans; where its own process runs apart from the isolation it
hands its commands to (above), the engine's tool server serves the tool runtime's file tools in
place of Claude Code's own - `read`: `mcp__dssharness__view_file`, `mcp__dssharness__find_files`,
`mcp__dssharness__search_files`; `write`: `mcp__dssharness__create_file`,
`mcp__dssharness__replace_in_file`, `mcp__dssharness__insert_in_file`,
`mcp__dssharness__create_directory`, `mcp__dssharness__delete_file`,
`mcp__dssharness__delete_directory`, `mcp__dssharness__move_path` - each call asked of the engine and
run by the tool runtime below as on the API clients. A step with the web takes the harness's own
`web_fetch` - and `web_search`, where it has a source to search - from the engine's tool server:
the one MCP server, of type `sdk`, named `dssharness`, that the launch's `--mcp-config` names
beside `--strict-mcp-config` (the step's `claude-mcp.json`), served in the engine's own process
over the launch's control channel; each call of it is asked of the engine first, by its ask rule,
and run by the tool runtime below as on the API clients. A launch whose init does not show that
server connected with its tools taken, or that connects another, never started, and a call of it
the engine did not allow runs nothing and fails the step. Every call its own deny rules do not refuse first is asked of the
engine before it runs and answered from the policy, a denial with its rule's sentence the model reads and a line of the step's
`denials.jsonl`; an answer sent back (below) and a message the engine gives an agent at work are
written to the same launch; a handoff
asked interrupts the turn and asks the agent for its handoff, no tool running in that turn; every
message is held to the model asked for; the launch's environment holds nothing of a Claude Code
session the tool was started in but a name `agentPolicy.keepEnvironment` gives back, none of what
every child of an agent loses (the tool runtime
below), and no credential but the one its `auth` gives; and a key Claude
Code reports invalid is passed over, the next key given a new launch while the step has done no
work. The user's own `settings.json` in the launch's Claude Code directory and the user's
`CLAUDE.md` reach every agent on this client, but no hook of theirs runs: every launch's settings
say `disableAllHooks`, and each launch's start names that settings file and its SHA-256 and says
the hooks are off. A settings file that would give a launch a credential - `ANTHROPIC_API_KEY`,
`ANTHROPIC_AUTH_TOKEN`, `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_BASE_URL` in its `env`, or
`apiKeyHelper` - refuses the run before any launch, naming the file; take it out, or give the
client a directory of its own in the slot: `CLAUDE_CONFIG_DIR`, or the name
`agentClients.claude-code.configDir` gives. The client holds every launch to the protocol itself:
one that does not answer the host's `initialize` request within 30 s, that works before its init
says what it runs as, whose init offers a tool it was not given or names a credential its `auth`
did not give, or that refuses by itself a call the engine allows never started; a call it refuses
by itself that the engine refuses too - by its own deny rules, a file tool's told by its result
alone - is a denial of Claude Code's, counted and recorded once; a call it ran that the engine was
never asked about, or refused, fails the step by the tool's name; each is ended at once. A message
no turn takes within the stall bound stalls the step; a declined request and an account at its
limit move the step to its next entry, a new launch - an account at its limit to the same model's
next key first, a declined request past the model's other keys to the next model - `Refused` and
`RateLimited` (where none is left) with when the limit lifts where Claude Code says. Every move on this client is
a new launch, and a move within the step's work goes on from what the engine stored.
`help agent-clients` says it whole.

The tool runtime is the harness's own tools, built for the API clients to give their agents in
place of a client's own - the `claude-api` and `glm` clients below - and serving
the `claude-code` client the web's through the engine's tool server, and its file tools there
too where Claude Code's own process runs apart from the isolation: `view_file`, `find_files` and `search_files` (read),
`create_file`, `replace_in_file`, `insert_in_file`, `create_directory`, `delete_file`,
`delete_directory` and `move_path` (write), `run_command` (run) and `web_fetch` and `web_search` (web), each under
the capability that gives it; the run's reporter is given those that read and no other. Each
call is decided before it runs and recorded: a tool the step was not given, or an input its schema
does not hold, is refused with how the tool is called; a path is read against the agent's working
directory - refused spelled empty, with a NUL or a leading `~`, and on Windows a share or a
device's prefix, a device's name (`CON`, `NUL`, `COM1` and the rest), a drive's own directory
(`C:x`) or an alternate data stream -
its links followed (one whose links cannot be followed refused as undecided) and held to the
agent's roots: a read inside one of them, a write inside a
writable one and through no link below it (a file is read through the file as it was opened, where
the open file stands decided again before a byte passes - opened never to wait, so a pipe put there
is refused - and a file written, a directory made, and a file or a directory moved or deleted is
acted on relative to its directory held from the writable root it was decided in, every entry on the
way opened as itself, so a link put in place between the check and the act, or swapped in while it
acts, reaches nothing and the call is refused naming it; a directory listed goes by its path, so a
link put in place of one lists names, never a byte of a file); then the policy is asked of it, a deleting or moving tool a write to every path it
touches, at both ends of a move. A directory deleted or moved is refused where it is a root, holds a link,
holds a path that may not be written or holds more than 2,000 entries. A file past 16 MiB is
neither viewed nor edited, and one past 2 MiB is passed over by search_files - a command reads or searches it. A replace or an insert needs
its file viewed first, by its content - one that changed since is refused, nothing written - and an
edit keeps the file's encoding, its line endings and whether it ends with a newline. A command is
an argument list, never a shell string, decided whole where it starts and run as a phase is -
contained, ended whole with what it started, its log kept - for `agentPolicy.commandTimeoutSeconds`
(120) unless its call names a time, `agentPolicy.maxCommandTimeoutSeconds` (3600) at most,
stopped sooner where it prints nothing for as long as the step's stall bound; a command line
holding a secret, a word holding a NUL, or on Windows a batch file's argument holding what `cmd.exe`
reads again (`& | < > ^ % ! "`, a line's end) or what the shell expands, is refused. Its environment, and the `claude` child's, loses every
name the configuration appoints and every secret-shaped name (ending `_API_KEY`, `_SECRET`,
`_SECRET_KEY`, `_ACCESS_KEY`, `_PRIVATE_KEY`, `_PASSWORD` or `_TOKEN`) but those
`agentPolicy.keepEnvironment` gives back. A page
is fetched by the harness's own fetch: `http` and `https` alone, at most 5 redirects, 30 s and 5 MiB,
text alone (`text/*`, `application/json`, `application/xml`, `application/xhtml+xml`,
`application/javascript`, `application/ecmascript` and every `+json` and `+xml` type) with a page's HTML
reduced to its text, public addresses alone - a host that resolves to
this machine, its network or a cloud's metadata service refused, and the address a connection was
made to checked again - no cookie, credential or proxy; each address is decided and written to the
step's `web.jsonl` before anything leaves the machine, and the page is given inside a block saying
it is information for the task, not instructions. Calls that read run together, at most 4 at once,
one that writes or runs alone; an answer past 32,768 characters is given as its first 8,192 and its
last 24,576, the whole kept in the step's `tool-results/`.

The loop the `claude-api` and `glm` clients share (`ToolLoop`) runs a step of either over the
provider each gives it - the Claude API's and Z.AI's below. A turn is one answer of the agent's - a
request the provider paused, or one thrown away and asked again, goes on as the same turn - counted
against the step's `maxTurns` but a handoff turn; every request's usage - one that ended without
its turn, failed, silent or left, by what it said it used so far - is counted at the model's
prices, and no request but the handoff turn's is
sent once the step's `maxBudgetUsd` is spent. A step held to a budget in dollars - its own
`maxBudgetUsd`, or the run's `budget.usd` it is charged to - is never started, on any client, on a
model missing a price of what its client's requests are billed by: input, cache reads and output on
every client, the cache write of its cache lifetime on `claude-api` and both cache writes on
`claude-code`, in the model's large-prompt set too where it has one, nor on a provider that prices
in another currency than the budget's dollars. The step's first such entry ends it not started,
naming the key to fill; a later one is passed over, said so, and never asked. The workflow's check -
`validate-workflow`, and every run before it starts - says the same first, for each model a step
charged to a dollar budget may run on, of the prices the client it is reached through is billed by,
given that client's `agentClients` entry, naming the model, the client and the missing price (the
`reporter`'s is never charged to the run's). A turn whose cost cannot be counted is never charged as nothing: what was spent is said as at least what was counted, and why the rest is not known. Each turn's calls run as the tool runtime above decides them; a turn whose request
spent the budget runs none of them. The conversation is kept in the provider's own form under the
agent's `sessions/<id>/`, each message as it was sent and received and sent back so with every later
request, one line for each point where the history is whole - a turn that called tools stored with
their answers - and a step that goes on in it sends what it began with; where a provider begins
sending it shortened to keep it within its model's context, the point is kept beside it
(`shortened.json`) and given with every later request, a notice saying so once. The first request
asks for a quarter of the model's `maxOutputTokens`. A turn cut off at its length that called no tool, its
request holding it to no schema, is stored and nudged on, 2 times at most; one cut off while it called a
tool - its calls never run - before it wrote anything, or while its request held it to a schema, which
the answer to a nudge would be held to whole, is thrown away and asked again with twice the room, up to
the most; past either the step is `Truncated`. A turn the provider paused is sent again with nothing added, 10 in a row
failing the step. An answer gone on from, cut off or paused, is every piece of it. A failed request
is asked again up to 6 times after a wait that doubles from 2 s to 60 s, or the one the provider
names; a service failing past them, or naming such a wait, fails the step, and one
silent past the stall bound - nothing of it heard, not even the provider's keep-alive - stalls it;
a model the provider does not serve the key never starts the step before the provider begins an
answer with the model asked and fails it once one began. The engine's fallback works inside the one
conversation: a key the provider refuses - said, and passed over for the rest of the run - an
account at its limit (a rate limit past those tries, or naming a wait past 300 s: the model's on
the key it was sent with, or the key's whole where its balance is spent, each named) and a request
the model declined each move the run to its next entry of the same provider and client, the same
request sent again with the model or the key changed and nothing else, the declined
turn thrown away with any part of an answer it had written; at a limit the same model's next key is
tried first and every entry the limit covers is passed over until it lifts, a declined request
passes over the model's other keys, and one the provider marks about what the prompt asks
(`reasoning_extraction`) moves no step. A handoff asked abandons the request under way and cuts the calls
running, each answered `interrupted: the run is pausing; it may have run part way`, then asks the
agent in one more message of the conversation, held to the handoff's bound instead of the step's
time, no tool run in that turn; a side-task's interrupt does the same, each call answered
`interrupted: the turn was interrupted where it stood; it may have run part way`, and ends the run
`Cancelled`, the conversation stored to its last whole turn; a request left so - by a handoff, an
interrupt, the step's time or the handoff's bound - counts what the provider had said it used, and
where it had said nothing yet the run's cost is unknown, never naught; a message the engine gives
an agent at work is given after the results of the calls it is running, and an answer written
while it was is not the step's.

A step's engine may hand its run a whole order - the entry that serves first and each entry the
engine's fallback may move it to, a model with one of its provider's keys, each model's keys before
the next model, the effort the step gives each model with it, and whether each can be used on this
machine - and the runner walks it: an entry that cannot be used here is said and passed over when
its turn comes and never refuses a run, and a declined request, an account at its limit and a failed
run whose provider and client have no key left unrefused move the step to its next entry by the
cause's rule, across clients as within one, each move said by `FallbackStarted` and each entry
passed over by `FallbackPassedOver`. Each client is handed its run's credential its own way. An API
client is handed the keys of its model from the entry it starts on, so a refused key and an account
at its limit move it to the same model's next key inside the client first, and its own walk goes
through the entries of its provider and client up to the first entry of another, which is the
runner's. `claude-code` is handed the same keys and moves to the next only on a key refused before
its launch did any work, with a new launch of the step's prompt; a key refused once it has worked,
and every limit, end its run, and the runner moves the step - a refused key to the next key of its
provider and client at once, with no wait. Where the step gives the next model another
effort, the move goes on at that level and says both: on the Claude API the change is told in the
conversation, a message under `mid-conversation-output-config-2026-07-01`, and the conversation is
still sent top-level the level it began at, its cache kept; on Z.AI every request carries the level
as its `reasoning_effort`, said by a notice; and a move to a model the step gives no level goes on at
the model's own default, its requests naming none from then on (the Claude API's cache spent once for
it), said by a notice - with no credit token, for it is not the same request. Where the next entry is
of another provider or client -
and for every move on `claude-code` - the step begins a new conversation with its prompt on that
client's model, and what every entry did and used is the step's one result, each model that served
it named from the turn it began at, its cost every entry's own summed and unknown where any entry's
is. A limit the run met is remembered until its end: an entry it covers is passed over, said so,
whichever client would serve it, and so is an entry a move reaches that cannot be started. Where the
slot holds none of the keys of the step's first model the step's agent is never started and the step
fails refused (exit 13); a later model's missing key is only said and passed over. A failed agent is
tried again on its model from the entry that served it last, never on a key refused or limited. The
run's pause ends the wait before a retry, and no launch begins once it is asked: the step ends
handed off with no handoff, saying it paused while it waited to be tried again or between two
launches.

A step that goes on with earlier work is given the engine's record of it: the continuation record,
text put together from the step directories of the agent - what it is, the steps so far with each
one's notes and output, the files changed, the last red build or test, the handoff, and the last
iterations of the newest attempt word for word from its stored stream, every run of the attempt
numbered as one (each the agent's message as it wrote it, its thinking as far as the provider
showed it, each call it made with the tool, its input and its result as the stream holds them, and
each message the engine sent the agent after it, in its place) - nothing in it written or shortened
by a model, and whatever is cut named with a copy in the step's own `handed/continuation/`, which the
tool alone writes and the agent reads, never where its commands write. Its bounds
are `agentContext.continuation`'s - `recordChars` (60000), `stepChars` (16000), `paths` (300),
`lastIterations` (3) and `iterationChars` (120000), each at its default where the key says none,
each a positive number, the earlier iterations kept back from the last until the first that does
not fit - and the first user message holds the parts in their order: the run
context, the plan updates, the continuation record and the step's prompt as the task. The record is
sent only where the conversation is a new one - every launch on `claude-code`, and on the API
clients a conversation that cannot be gone on with - for a handed conversation goes on with one
more user message alone; the message as sent is kept in the step's `user.md`, masked.

A step whose conversation has filled its model's context ends `ContextExhausted`, and where its
limit's question is answered `compact` the engine compacts the conversation - a side-task
compacts its own by itself - and no model does: a reduction worked out from the stored stream of
the attempt, every run of it, the same input always the same record - the run context and the task
whole as they were first sent, the last iterations word for word by the
continuation record's rule, every earlier message of the agent its text whole with its thinking
left out, every earlier call one line (the tool, its input cut at `callInputChars`, how it ended,
the path where its input names one), every earlier result left out, and every message the engine
sent since the first whole. The reduced part is at most `agentContext.compaction.reducedChars`
(200000); a call's input is cut at `callInputChars` (400); where the reduced part is more, the
oldest messages of the agent are cut to their first 2000 characters each, oldest first, until it
fits, and the record says how many were. It begins a new conversation, on every client - the
record is the first message of one and the old is kept whole, never edited or sent again - said in
the step's stream as `Compacted` and counted in its result once, as the first launch given it
begins the conversation - an API client's first answer begun, a `claude-code` call run or turn
ended, or the launch ending any way but `NotStarted` - so a run never started (its client missing,
its model unpriced, the client refusing it, a model the plan does not serve) or held at the gate
compacted nothing, and a retry or a move is given the same record and counts no other; a
side-task's compaction is said and counted the same way, its record carried before the person's
next message; an attempt compacted again is reduced
whole again, its earlier records never copied into the next, and a later launch's first message is
read as its task alone, never the record it carried; a
`claude-code` child is started with `DISABLE_COMPACT=1`, so it never compacts by itself and a full
context ends the step `ContextExhausted`.

The requests of one provider and one key go through one lane of the gate: at most
`agentClients.<client>.maxParallel` of them under way at once (3 for claude-code, 4 for claude-api and 2 for glm where
the entry says none; a client that signs in with a login is one lane by its client, and two keys of one provider two
lanes, keyed by a hash of the key). The API clients enter the lane for each request they send, its input counted as
it enters (estimated, its bytes over 4); `claude-code`, whose requests the engine cannot see one by one, is held in the
lane for each launch. Beside it the API clients' optional buckets pace a lane's minute - `requestsPerMinute`,
`inputTokensPerMinute` (a request more than the bucket holds goes in alone once the bucket is empty) and
`outputTokensPerMinute` (an answer's counted once it is had), each waited out until the oldest of the minute is a
minute old - and a 429's cool-down, set by every rate limit and account limit a request or a launch meets from when the
provider says it lifts, holding what its failure covers: a model's rate limit holds that model's requests on the lane,
an account's limit every request of it, a later or longer one holding past a sooner one. The `claude-api` client's own
web search enters the lane too. A wait at the gate is said as it is waited, and the run's pause ends it; a cool-down
past the client's `maxRetryWaitSeconds` is waited by no client - an API client meets it as the limit it is, which moves
the step as a 429 naming that wait does, and a `claude-code` launch it holds is not begun, the step moved to an entry
the cool-down does not hold. Left out, the provider's answers pace
the client; the orchestrator's `maxReasoningAgents` bounds agents beside the gate, and both apply
(`help agent-clients`).

A reason step's prompt says `{skill.<name>}` where a skill's text goes, and the engine puts the
text there before the prompt is sent, on every client alike (`help skills`): the two API clients
have no skill mechanism, and the `claude` child is started with `--setting-sources user`, under
which it loads none of the worktree's. The file is Claude Code's own `SKILL.md`, unchanged - this
tool adds no key and no syntax of its own - and `<name>` is the folder's name alone: letters,
digits, `_` and `-`, a `.` between two words, so no name reaches past the place it is looked for
in - any other refused by the check and the step alike. The first place
that has the folder decides: `.harness-config/skills/<name>/` in the main checkout (the tool's own
place, tracked), `.claude/skills/<name>/` in the main checkout (the repository's Claude Code
skills), and `skills/<name>/` in the user's Claude directory (`CLAUDE_CONFIG_DIR` where it is set,
else `~/.claude`). The repository's come before the user's - Claude Code's own order is the other
way round - and a `SKILL.md` that is there and cannot be used is refused, the next place not tried
in its stead. The places are read in the main checkout, never in an agent's worktree, and each
step reads them as it starts: a run does not freeze its skills. What is put into the prompt is the
body, not the frontmatter, inside one block that names the skill, its place and the folder handed
to the step; the folder is copied whole into what the step's agent is handed, the step's own
`handed/skills/<name>/` - which the tool alone writes, through no link, and the agent reads as a root
of its own, never its work directory, where its commands write - in place of any earlier copy, and
that path is what `${CLAUDE_SKILL_DIR}` becomes. A folder that holds a link, more than 500 files or more than 16 MB
is refused. The step's record names every skill it was given, each once over its attempts - its
`step.json` `skills[]`, and its stream's `sessionStarted` event - with each one's place, its
`SKILL.md`, the folder handed over, and the SHA-256 of the file and of the folder. The frontmatter's keys are each honoured, recorded or refused: `allowed-tools` is
honoured as what the skill needs, each tool it names held to the one table of Claude Code's tools
for each capability and the step's agent's - and it grants nothing; `model`, `effort`, `context`,
`agent`, `background`, `hooks` and `disallowed-tools` are refused, for text put into a prompt runs
in the step's own agent, on its model, under its policy; any other key is refused naming the keys
this tool knows. In the body `${CLAUDE_SKILL_DIR}`, `${CLAUDE_PROJECT_DIR}`,
`${CLAUDE_SESSION_ID}` (the id the step's run begins its first session as), `${CLAUDE_EFFORT}` and `\$` are replaced, a read of an argument, a command
run when the skill is loaded and the reads only a plugin has are refused each naming their line,
and a body is at most 64,000 characters after its substitutions. A skill's text is not scanned for
placeholders again: `{skill.b}` inside skill `a` is plain text. The placeholder stands in a reason
step's `prompt` and `resumePrompt` alone - refused in an agent definition's body, which is sent as
written, in any other template key and in an expression - and each skill once in a prompt. A skill
that is missing or unusable fails loud, twice: when the definition is checked (`validate-workflow`,
and the start of `run-orchestrator` and `resume-orchestrator`; exit 12, nothing started, every
problem of every prompt together, each refusal naming every place looked for it), and when the
step starts, for a file can go or change between - the step is not started, it ends `Failed` with
the same sentence and exit 12, and its `onFailure` decides what follows.

The `claude-api` client works a step through the Claude API by the loop above, every call run by
the tool runtime: each request is posted to its provider's `api` with `/v1/messages` after it,
signed with a key of the provider's (`x-api-key`), under `anthropic-version: 2026-10-21`, and its
answer streamed. It is given the run's entries - its provider, its model and, where the
configuration declares one, its own entry under `agentClients`, every key at its default where it
declares none - and never starts without the first two, or on a provider of a type but `anthropic`. A
request sends the model, the system prompt as one block, the tools - each streaming its input as it
is written - and the messages as the conversation stored them; adaptive thinking, its summary
shown; the step's effort as `output_config.effort`; old tool uses cleared by the server as the
context fills (`clear_tool_uses_20250919`, under the `context-management-2025-06-27` beta), each
clearing said in a notice; and three cache marks of the client's lifetime for
`agentClients.claude-api.cacheTtl` (`5m` or `1h`, `5m` where nothing is said) - on the system text,
covering the tools and it, the same for every step of that agent with the same tools; on the run's
repository map, the first block of the conversation's first user message, covering the tools, the
system text and the map, the same for every such step of the run that reads alike; and the top-level mark, which
moves with the conversation's last block - so a step's second request on reads the map from the
cache, and so does the first request of the run's next step of the same agent - and a budget needs
the model's price of that cache's writes too: `priceCacheWrite`, or `priceCacheWrite1h` for an hour. A
step with a fallback that can go on here sends the `fallback-credit-2026-07-01` beta, and a request
the model declines moves the step to its next model of the same provider and client - the same
request sent again with the model changed and the credit the API gave sent back as its
`fallback_credit_token` (a failure naming it answered by the same request without it), `Refused`
with its category only where none is left; no request carries
`fallbacks` or a `server-side-fallback-*` beta, for the engine's
fallback is the only one a step has. Two keys are `claude-api`'s alone: `betas`, sent beside the
tool's own, each one word and never the API's fallback's, and `cacheTtl`; three are both API
clients': `requestExtras`, an object merged into every request's body - a member it adds sent as
given, an object's members merged into the tool's own - refused where it sets what the tool sends
itself or, here, `fallbacks` or a sampling parameter (`temperature`, `top_p`, `top_k`), which
`anthropic-version 2026-10-21` refuses on every model, and free to add a tool choice or
`thinking.budget_tokens` where the step's model takes one; and `maxRetries` and
`maxRetryWaitSeconds`, the loop's tries and longest wait (6 and 300 where nothing is said). A
spend limit reached - the organization's monthly cap, a 429 whose error names
`enforced_spend_limit_reached`, or one the organization or a workspace set, a 400 that says so - is
the key's limit, every entry on that key passed over until the date the API names; a 401 is a key
refused, said and passed over for the rest of the run; a 402 the key's balance spent, every entry
on that key passed over; a 429 the model's limit on that key, waited out within those tries and the
same model's next key tried past them; a 413, a prompt too long or an answer the model's
context could hold no more of is the context exhausted (`ContextExhausted`); a 403, a 404, or a 400
answering a conversation's first request - a model the key's organization is not given, such as one
that needs a data retention the organization does not keep - is a model the provider does not serve
the key, naming it; an overload, a server's error, a request timed out, a conflict, a network that
failed and a stream cut or ended before its message are asked again; and any other 400 fails the
step, said in the API's own words with the request it names.

The `glm` client works a step through Z.AI by the loop above, every call run by the tool runtime:
each request is posted to its provider's `api` with `/chat/completions` after it, signed with a key
of the provider's (`Authorization: Bearer`), and its answer streamed. It is given the run's entries
as the `claude-api` client is, and never starts without its provider and model, or on a provider of
a type but `zai`. A request sends the model; the system prompt as the first message and every
message as the conversation stored it - the engine's as a user message of text, an answer's calls
each answered by a tool message, in order; the tools as functions, their calls' arguments streamed
(`tool_stream`); the model's thinking on, the step's effort as `reasoning_effort` (`low`, `medium`,
`high`, `xhigh`, `max`); and `max_tokens` - no sampling parameter or tool choice of the tool's own,
which `requestExtras` may add, and no tool of Z.AI's, for the web is the harness's own. A first
message that holds the run's repository map holds it first, ahead of the step's context pack and its
task - the same bytes for every step of the run that reads alike, so the longest prefix the steps of an
agent share begins with it. Z.AI caches what a request repeats by itself
and bills no write, so a budget needs the model's input, cached-input and output prices. Each
answer's reasoning is stored with it and sent back; Z.AI leaves it out of what the model reads in
later turns unless `agentClients.glm.preserveThinking` is true (false where nothing is said), which
keeps it (`thinking.clear_thinking: false`). Z.AI clears nothing of a conversation itself, so once
what the model last read and wrote passes 70% of the model's `contextTokens`, the answers to the
calls of every turn but the last 6 that called tools go out in a short form that says so, from that
request on - the calls and the reasoning as they were, and what is stored whole; past 90% after
that, the context is exhausted (`ContextExhausted`). Three keys are both API clients' -
`requestExtras`, refused where it sets what the tool sends itself, and `maxRetries` and
`maxRetryWaitSeconds`, as the `claude-api` client's above - and one is `glm`'s alone:
`preserveThinking`. A failure is read by Z.AI's code first: a key it does not take (1000, 1001,
1003, 1005) is a key refused, said and passed over for the rest of the run; an empty balance (1113)
is the key's limit, its balance spent, every entry on that key passed over; a usage limit that
names when it resets (1308, 1310, 1316, 1317, 1318, 1319, 1320, 1321) is the key's limit, every
entry on that key passed over until it resets; a rate limit (1302) is the model's limit on that
key, waited out within the tries and the same model's next key tried past them; an
overload, a network error or a call failed on its side (1305, 1234, 1200, 1230) is asked again; a
prompt too long (1261) is the context exhausted (`ContextExhausted`); content it will not answer
(1301) moves the step to its next model, `Refused` where none is left; a model unknown, an API the
key may not call or a plan without the model (1211, 1220, 1311) is a model the provider does not
serve the key; a plan that does not cover the call or has ended (1309, 1313, 1314, 1315) fails the
step. A code that list does not hold is
read by the status: a 401 is a key refused, said and passed over for the rest of the run; a 429 is
the model's limit on that key, waited out within the tries and the same model's next key tried past
them; a 413 is the context exhausted (`ContextExhausted`); a 403 or a 404 is a model the provider
does not serve the key; a
request timed out, a conflict or a server's error is asked again, and any other fails the step. An
answer Z.AI ends as sensitive moves the step to its next model, `Refused` where none is left, one
it ends for its context the context is
exhausted and one it ends for a network error is asked again; a failure Z.AI names is said in its
own words.

Where a step names an output, every client asks the agent for one envelope - `notes`, `output`,
`assumptions`, and `questions` where the step may ask and has rounds left - and the harness's own
validator holds the answer to it, whatever a client was sent. The notes are not empty, and are kept,
masked, as the step's `notes.md` beside its result; the output is read against the step's whole
schema, each problem named by its path under `$.output`, a text an `enum` or a `const` holds in
another case read as the schema spells it; each assumption is `{about, assumed, why}`; each question
is `{question, problem, tried, means, options, recommended, why}`, with at least 2 options, no two
alike, recommending one of them word for word; no text of either is empty. The output is null only
in a handoff, whose notes are the
handoff, or where the answer asks, and the step then ends `Asked` with its questions; an answer with
both is sent back, and so is one that asks where the step may not or has asked as often as it may,
told to write what it would have asked under assumptions. The `claude-code` client holds the answer
to the envelope by `--json-schema`; the `claude-api` client sends it as `output_config.format` on
every request, the handoff's too, each array's `minItems` above 1 and every `maxItems` moved into
the array's description in words, for structured outputs take no other - and where the API refuses
it as more than it compiles, the run's later requests carry no format, and a message of the
conversation says it in words, the system prompt untouched; the `glm` client, whose wire holds no answer to a schema, says it in words in a message
of its own after the step's first, in every conversation the step begins, and asks an answer that read as no JSON again in JSON alone - no tool offered,
`response_format` a JSON object. An answer that does not read is sent back in the same conversation,
2 times at most, and the step is then `InvalidOutput`.

`web_search` is the harness's own search, made by dssharness itself over HTTP: it asks the sources
`agentWebSearch` in `config.json` declares - search APIs that answer JSON, feeds, OpenSearch
descriptions and the harness's own index of a site's pages read through its sitemap - and never a
search engine's results page. The built-in sources (`stackoverflow`, `github-repos`,
`github-issues`, `nuget`, `npm`, `pub-dev`, `learn`, `mdn`, `wikipedia`, `hackernews`,
`readthedocs`) are entries in the same form, which `init` writes and a repository's entry of the
same name changes key by key; a new name adds a source, and `enabled: false` turns one off. None
needs a key: a source's optional `credential` comes from the `.env` slot (sent to the scheme, host
and port the source's own address writes alone) or the machine's `gh` login for its `host`
(github.com unless it says another; sent to that host or its API alone), masked and sent to the
scheme, host and port each search of that source first asks alone, where it is one of those, over
https - a lookup is sent none, and a host a document the source read names (an OpenSearch
template, what `discover` found) never one, its request refused with nothing sent - never in an
address a record holds. A reason step
picks its sources by name or tag with `search` (absent, `agentWebSearch.defaults`); each query is
held to the mask, each address a source asks to the mask and the `Web(...)` rules, each result to
every rule of the web, each source to its `perMinute` and `perDay` on the machine and the step to
`maxPerStep`, a sitemap source's index filled under the configuration's own `Web(...)` rules for
every run; a limit a source answers holds it until the time it gave, told and never retried, and
each source answers in its own part - results, none, a limit, an answer that changed, a failure, a
refusal. `help web-search` lists every kind's keys and every built-in source.

```bash
dssharness validate-workflow                 # check every declared workflow
dssharness validate-workflow implementation  # check one
dssharness list-agent-providers              # each provider, client, tier and key, present or missing
dssharness list-agent-providers --template   # the agent sections, to paste into an existing config
dssharness export-workflow implementation --shipped --to ../mine  # the shipped one, to make your own
dssharness help workflows                    # the file, every step type and every key
dssharness help agent-clients                # providers, tiers, efforts and the key slot
dssharness help skills                       # a step's prompt names one: {skill.<name>}
dssharness help web-search                   # the web search's sources, kinds and keys
```

`export-workflow <workflow> --to <dir>` copies a workflow - its yml and every file it names, and
no other - into `<dir>/<workflow>/`, a folder of its own that must not be there (13 where it is):
a declared one once it holds (12 where it does not, naming each problem), byte for byte, or, with
`--shipped`, the implementation workflow as this tool ships it, which is how a version of your own
starts. A file that cannot be written takes back what was written before it, with every folder the export
made, so no folder is left with part of a workflow in it - or the refusal says why it could not. A
used workflow's files go where it sits under the folder, a hidden folder keeping its dot, and a
workflow that uses one outside its folder, or a remote one, is refused (13) with nothing written,
for the folder alone would name a workflow it has not got - `--from` vendors a remote one.

A step may be a whole workflow: it says `use`, a path to the workflow's yml file in a folder of its
own, relative to the calling workflow's own directory, written with forward slashes and no part that
starts or ends with a space, and held inside the workflows folder once links are followed, as the
run's own workflow is, and `with`, the values
the workflow's inputs are filled with - an expression for a `value` input, a template for a
`text`, a path of the used workflow's directory for a `file`, and a single name for a `step`,
which its steps read as `{input.<name>}` whole in their `onFailure` or a handler's `then`. The
file a step is asked with may be read the same way - one whole input of the kind `file` naming its
`prompt` - so one pipeline is asked with a different prompt by each workflow that uses it, and a
step may say a `when` that reads one input of its workflow alone, its own gate, held off where a
use fills that input so - only where the use's `with` value, or the input's default, closes the
gate as written is the step free of waiting for the agent it works in, and one the use opens is
held to it as any step is. An input the use leaves to its default is read as a `with` value is,
where the use sits; a step input it fills with no name is refused where it is read, and a policy
filled with `recover` is held to what `recover` written out is - a reasoning step is never
recovered, and a recovery never calls itself. The used workflow runs inside the run as a group's
members do, its steps named under the use; its files are read in its own directory, its declared
outputs are the step's own once it ends - one that reads nothing, from a step its item skipped,
absent, and one that cannot be read failing the use - and a failure inside it that is given to a
recovery is taken by the nearest recovery declared around the failing step, however many uses
out, before the run's, its `onFailure` pause or stop as the run's own is. Its `with` values are
read once for each scope its steps run in - the block, each item of a `forEach`, each iteration of
a `repeat`, each raising of a handler - and kept in the use's own record, so a resume takes them
back and never reads them again over what has run since; every item's are read before any item
opens, and a value that cannot be read fails the use, saying which input and for which item, with
nothing of it opened - a step of it that runs all the same, an always step, is refused where it
reads an input, never handed the caller's. A failure policy or a `then` read from a `step` input
is filled in the scope that declares it - a group's `onFailure: '{input.failTo}'` reads the input
of the workflow that declares the group, however deep the failing step - and one that reads
nothing, or fills with no policy, is said in the step's error and its record: the failure goes
where the top level sends what nothing else decides, and a handler whose `then` cannot be read
ends failed saying why. Its steps read only its inputs, its params and its own steps - never the
caller's work item, its iteration before or what raised a handler of the caller, and a pull
request's `base` inside it reads no input; every fact of the caller they need comes through an
input - a recovery's steps read those of the workflow that declares the recovery, wherever the
failure it was taken for happened, and a `{text.<name>}` is the used workflow's own where it
declares the name, else the next one out's, else the run's. Its budget, reporter, `pushCheckpoints`
and `recommendations` are never applied, nor - unless the use says `useParentLimits: false` - its
advisor, amend, answers, limits and `codebaseGraph`, nor its `codebaseGraph` where the run's own
workflow declares one, which `validate-workflow` says beside the fingerprint of what each use
read. With `useParentLimits: false` the workflow's own settings govern its steps key by key: a key
is governed by the nearest such use around a step that declares it, in a recovery as anywhere, and
a key no such use declares stays the run's, for a use cannot widen a run's limits by declaring
nothing. Its `maxReasoningAgents`, `maxParallelProcesses` and
`maxOpenAgents` gate its steps as pools of their own in the place of the run's, counted across
every instance of the use; its `maxInstances` caps the step instances its steps open, across every
instance of the use and in the place of the run's own cap for them; its `maxRetries` and
`backoffSeconds` are what its steps fall back to; its `handoffSeconds` bounds the handoff of its
paused reasoning steps; and its advisor recommends for its steps' questions in the place of the
run's - its files read in its own directory, its prompt handed the inputs the use filled and the
texts its steps see, its bar the workflow's own `texts.recommendations` where it declares one, and
each recommendation credited to it. A question of its steps is answered nearest first: the step's
own answers, the groups around it within the workflow, the workflow's own `answers`, and then the
use step's own, the caller's groups and the run's. The run's one codebase graph has one shape -
the run's own workflow's, else the one its governing uses declare - and every sync, serving and
graphify step of the run is made under it. Each key such a use applies is held to every rule the
run's own key of that name is - its limits, its `maxInstances`, its advisor, its answers, its
graph's shape - for a use cannot declare what a run could not, and two such uses that shape the
graph differently are refused where the run's own workflow declares no shape, for a run has one
graph. Its budget and its reporter are carried and not applied: the run's budget counts its steps'
spend, and the run's reporter words the run as a whole. An amend is refused there, said at
validation, for an amend is a run's own, and `preapproved` is a run's input alone, whatever the
key says. A workflow file that uses itself, directly or through others, is refused with the circle
of the files that close it, steps nest at most ten deep in all - a used workflow's own counted
below the step that uses it - and a resume is refused where what a used workflow says changed
since the run began - the digest of its parsed definition, taken as the run's own definition's is,
so a comment or line endings change nothing - as the run's own definition's would be. The shipped implementation workflow uses the mechanism for itself: its item pipeline -
an item built in an agent of its own, tested, reviewed, folded and checkpointed - is one workflow,
`item-pipeline/item-pipeline.yml`, used by its rounds and by its recovery alike, each use handing
it its prompt, its failure's name and the facts of the run its steps read.

A use may name a workflow beyond the repository: `owner/repo/path/workflow.yml@ref`, as GitHub
Actions spells `uses` - a repository's name may be dotted, as an organisation's `.github` is - or
the same under a GitHub Enterprise host the configuration names, `host/owner/repo/path/workflow.yml@ref`.
The ref is held to git's own rules for a ref's name and resolved at the run's start through the
repository's own refs: its tag, an annotated one peeled to the commit it names, or its branch where
the pin allows one. A full commit SHA is its own commit, and is fetched only once the repository's
default branch, or one of the first 20 tags and branches the forge lists, reaches it: GitHub serves
a commit of any fork under the repository's own name, and one that is not the repository's own is
refused. The commit's tree is walked to the workflow's directory, and the whole is refused with
nothing cached where the directory's listing was cut short, holds a symbolic link or a submodule, or
a path that is rooted, holds `..`, `\`, `:` or a control character, or is another's where case is
folded, or where the file the use names is not in it; every blob is fetched as its bytes and checked
against the hash the tree gives of it, the whole bounded at 2,000 files and 20 MiB. It is kept in
the workflow cache, `workflow-cache/` under this user's own directory for this tool, named the first
time a remote use needs it: each blob as its bytes at `blobs/<first 2 hex>/<remaining 38 hex>`, one
entry per repository, commit and directory, held to its key as it is read, and each ref's newest
resolution with its time; every blob is hashed again as it is read, so a blob changed on disk is
refused and its directory fetched again, and a use is recorded with the look that its entry is
still there, so one a pruning removed as it was read is fetched again. A second run of a pinned SHA
reads the cache with no network, a tag needs one call to resolve at each run's start, and a run
records each use's resolution: a resume uses the recorded commit and never resolves again, so a tag
or branch that moved since changes no run already started, and only a change to what a local file
says refuses it as before. A run keeps every file of the remote workflows it began with beside its
frozen definition, in `remote-workflows/` of its own directory, as the check that let it start
proved, read and hashed them, and its steps read them there as it runs, as the check a resume goes
through reads them there first - the cache, and then the forge, asked only for a use the copy does
not hold whole: neither a pruning of the cache, the network not being there, nor a tag deleted or a
branch pushed over since takes a file from a run that started or refuses it, and each session's
start keeps again what the run's copy does not hold whole. A
remote workflow is trusted by configuration alone: `workflowSources.allow` names the owners and
repositories a use may name - empty by default, so a remote use is refused until the repository
allows its owner - `workflowSources.hosts` names each GitHub Enterprise host with its one
credential, `"credential": {"from": "env", "name": "<VARIABLE>"}`, given to its calls as `GH_TOKEN`
for a `*.ghe.com` host and `GH_ENTERPRISE_TOKEN` for any other, every other token of the machine's
taken away - a host with none, or whose variable is not set, is refused - while github.com is
reached with the machine's own `gh` login, every call naming its host; `pin` holds the refs to a tag
or a full commit SHA (`tag-or-commit`, the default; `commit` refuses tags too; `any` allows a
branch, warned of wherever one is resolved, for a branch moves), and `offline` says what happens
where the network keeps the forge from being reached: `refuse` (the default) or `last-resolved`, the
cache's newest resolution of the ref standing in, held to the pin as the forge's own answer is - a
ref the cache last resolved as a branch is refused where the pin is not `any` - and warned of where
it does. A forge that answered - refusing the credential, or naming a repository or ref that is not
there - is refused with what `gh` said, and nothing stands in for it. A refusal of what the source
is - not allowed, a host or credential not named, a ref the pin refuses or that is not the
repository's own, a file the directory does not hold, a forge that answered and refused - is the
definition's problem, said with the use (`validate-workflow` ends 12, a run is not started); a fetch
that could not run keeps its own code: `gh` missing (14), the forge not reached with nothing
standing in (21 where the call's limit passed, 20 where `gh` said no connection was made), an answer
of the forge's that does not read (20), and the workflow cache that could not be named, written or
read, or whose lock another process held past its window (21). Its files resolve against its cache
entry as a local workflow's do against its directory, a relative use inside it resolves inside the
same entry - one that leaves the entry is refused - and a local use never resolves under the
workflows folder's `remote/`, which is the cache's own reserved place, as no workflow is named
`remote`. A remote workflow's schemas are held to the subset of JSON Schema the engine reads, as a
local one's are. A command that runs under an agent - told by the one rule `run-side-task` is told
by, `DSSHARNESS_UNDER_AGENT` or a process of this tool's above it in the process tree, one whose
tree cannot be read taken as no person's, so a command any process of this tool's starts (a leg's,
a check's or a machine step's as much) is taken for an agent's - never reaches the forge nor writes
the cache: `validate-workflow` and `export-workflow` there read the cache alone, and a use the cache
does not hold whole is refused saying what made the command an agent's - the mark, the process of
this tool's by its name and id, or why the tree could not be read - and that a person fills the
cache from a shell of their own, which no process of this tool's started and which carries no
`DSSHARNESS_UNDER_AGENT`, with `dssharness validate-workflow` or `dssharness export-workflow --from`;
a record or a blob of the cache's that could not be read is the cache's own failure there too (21).

`export-workflow --from <source> --to <dir>` vendors a remote workflow into the repository at its
resolved commit - a workflow named beside it, `--shipped`, or a `--from` that names no remote
workflow, is a usage error (10), and a folder that is there already is refused (13) before anything
is fetched: every file of its directory, byte for byte, and `source.md`, a note of where it came
from and, where that is worth a word, how the commit came about - the cache's last resolution
standing in, or a branch, which moves - a directory holding a `source.md` of its own refused (13) -
so a team can review what is written and use it as `./`. `validate-workflow` names the workflow
cache's directory on the line it ends on wherever it went to resolve a remote use, whether the
workflows hold or not, and `export-workflow --from` on its own, a refusal after the fetch saying
what it fetched is kept there; both name it in `--json`, as `cache`, on every exit.
`workflow-cache list` says each entry the cache holds - where it came from, its commit, its files
and bytes, when it was fetched and last used, and the refs whose newest resolution it is - and
every file of the cache's
own that could not be read. `workflow-cache prune --unused-days N`, the days required and from 0 to
36,500, removes the entries no run used and none was fetched for N days, the resolutions that named
them, and the blobs no entry left holds - every blob an entry it keeps holds kept, and no blob
removed at all while a file of the cache's own could not be read; a file it could not remove or
read is said, and the pruning ends incomplete (21). Both name the cache's directory, in text and in
`--json`. Every record of an entry, a use or a resolution, and every removal a pruning makes, is
made under one lock, held for one record or one batch of blobs at a time - a fetch's blobs are
written before its entry, outside it, and the entry only once every blob it names is there - so a
pruning never takes a blob an entry holds and never holds a fetch off for longer than a moment; a
cache that could not be named, written or read, or whose lock another process held past its window,
ends incomplete (21).

This build checks workflow definitions - the shape of every key, every step, input, param and
output an expression, a template or a prompt names, every path typed by the schema or the built-in
output that holds it, every operator and every key held to what it takes (a `when` a boolean, a
`forEach` a list), every file the definition points at, and every schema a step names held to the
subset of JSON Schema the engine reads, so no run starts over a schema no answer could be held to -
and refuses (12) naming each
problem, without running anything, as a `config.json` that does not hold is refused. A
`config.json` that declares no workflow has nothing to check and is refused (13), saying how to
declare the implementation one. It holds a definition to the configuration too: a `run` step whose
runner, or a runner its run checks name, writes the tree (`writesTree`) names with `in` the
create-agent step whose worktree it works in; the agents a run makes, `a` and a number up to the
workflow's `maxInstances`, fit `worktrees.maxNameLength`; and a commit step gives a value for every
variable `commit.template` requires and none for one it does not declare - with no template, a
`message` and no `variables`. A repeat that says `decideAfter` names a step of each iteration the
loop decides at, instead of at the iteration's end: where that step's newest visit ends done, the
loop's `until`, `blocking` and bound are read there, on the iteration as it then stands, and the
decision is kept in the iteration's own record with that visit - a resume never reads it again
over what has run since, and a later visit of the step in the same iteration is decided anew.
What follows that step starts only where the loop goes on: a loop that asks at its bound there
holds all the rest of the iteration on its question until the answer - a handler its deciding
step's ending raised among it waits on the answer, not started, a group one drawing none of its
steps - and the answer has it go on with the loop or end with the iteration; a loop that ends
there withdraws what of the iteration has not started - a handler its deciding step's ending raised among it, a group
one with all its steps - save its always steps, which run after the iteration's end as after any
iteration's, waits for what is in flight, and leaves what its deciding step found, none of it
acted on; at its bound, work a plan update owes the loop has its iteration added first, as at an
iteration's end; and work an update owes the loop carries it past what it decided there - a loop
that ended at its decision runs again for it, as one that ended at an iteration's end does, and
one that asked at its bound has the question withdrawn for the iteration added and goes on. A
resume makes what followed from a decision where the session that read it ended before it had.
Where that step ends any other way - skipped, failed and passed over, never run - nothing is
decided there, and the iteration's end reads them as for a loop that names no deciding step. Its
`until`, `blocking` and `leftover` read of the iteration only the deciding step and the steps it
waits for, and every other step of the iteration runs before it or after it. The shipped review
loop decides at its `review`, so its last rework is always reviewed and its leftover is what its
last review found.
What only a prompt reads stands where its agent is asked it:
`{recommendations}` in a reason step's prompt and the advisor's - the bar the repository wrote,
read live each time it is given - `{text.<name>}` beside it, a workflow's texts, and
`{question.*}` in the advisor's
alone, `{status.*}` in the reporter's two - `{status.question}` in its `askPrompt` alone -
`{update.*}` in the prompts of the step that plans an update and of the steps of its scope, and
`{piece.*}` in a step of the work-item group; the advisor's and the reporter's prompts read no step
by name, and a pull request's `body`, which the engine fills and no agent reads, reads what a
template key reads and no skill. A question gives only the texts the engine names (`step`, `kind`,
`problem`, `answers`, `record`), and so do the status (`facts`, `question`, `records`), a plan
update (`cause`, `since`, `work`) and a piece (`kind`, `changed`, `keep`, `undo`, `instead`,
`cause`, `before`); the bar is one text, and so is each run fact (`{worktree}`, `{os}` and the
rest), which a template reads and an expression does not. The engine starts and drives a run of a workflow
(`run-orchestrator`, `dssharness help orchestrators`), from the main checkout's HEAD on a branch of its own: a start is
refused where HEAD is detached, where the checkout is on the branch the run's pull request goes to, where a merge is under
way and where changes are staged. Twenty steps are served so far: `orient`, which reads what the repository is and has once
(its branch and the pull request's base branch, the commit the run started on, a remote, legs and how each tests, anchors,
CI, mutation arms, what is dirty, the open agents and who holds the tree); `graphify-codebase`, which syncs the run's one map
of the repository - every file a node, each declaration a language's implementation reads with its lines, its header and
the first sentence the code writes of itself (C# through the compiler's own parser, MSBuild's projects and solutions,
C and C++ through a structural scanner that reads every branch of a conditional, Dart through the same scanner - a library and its parts one scope, its private names its own - and each Dart package's `pubspec.yaml` with the root its `package:` imports resolve through, JavaScript and TypeScript through it too - a module one scope, what it does not export its own, its imports by the forms ESM, CommonJS and TypeScript write, each tried as Node and TypeScript try a path, JSX's components and a test framework's tests - with each `package.json`, `tsconfig.json` and `jsconfig.json` and the roots their bare imports resolve through, PHP through it too - its code between its tags, each namespace a scope, every name written out by its `use` declarations, the files it includes, PHPUnit's and Pest's tests - with each `composer.json`, Markdown's headings; every other file by its name and size), and the edges between them, each saying how sure it is - under the run's
`graph/`, reading again only the files whose content, reader, reader's version, size limit or languages changed and those it could not read, and starting a run's first
graph from the newest earlier run's - or, with `in`, the view of that agent's worktree, held against the main checkout's
graph as an overlay of what the worktree holds otherwise, so no file is read whose extract either made; the engine syncs
on its own too, under the claim its step holds - a reason step's view where it starts, the main checkout's by every path
the run's folds changed that it has not taken yet and every path a commit changed since its last sync, a worktree's after a refresh or a rebase moved it, and a view dropped
once its agent is no longer live - and a sync of its own that fails is said, owed until one goes through, and never
fails the step (`dssharness help codebase-graph`; the workflow's `codebaseGraph` shapes it);
`validate-plan`, the machine's check of a plan's
work items; `create-agent`, which makes the agent the engine named for it (`a<N>`, never twice) for the model its
first reason step runs on, both recorded before the agent is made so a step run again asks for the same one;
`reason`, which works the step's agent - the one its `in` names or the work it goes on with (`resume`), or, where it
names neither, the engine's own read-only agent, with no worktree, made before the step runs and deleted once no step
can go on with its work: the step's model resolved with its agent definition below its own and its order built as
entries of a model and one of its provider's keys, each entry's credential read from the slot; its prompt file read
and filled where the step starts, each skill it names looked up again and the folder handed over in the agent's work
directory; the work it goes on with rendered as its continuation record, and its going on by the client's kind - the
stored conversation handed where the client goes on with one, a new launch rendered where it does not; and its answer,
held to the schema its `outputs` names, the step's output - a schema in the subset the engine reads, which
`validate-workflow` and a run's start refuse otherwise (exit 12), each problem naming the file, the step and the place
in the schema that says it, and which the step's start holds it to again before any client is asked; and the leg
steps `build`, `test`, `run` and `check-mutations`, which run their legs as the commands do, in the worktree of the agent
they name with `in` or in the main checkout, each leg's verdict read on its own and its red legs answered alone - a leg
that failed with the phase that did not pass and its last lines, a sweep's arm with its arm - a leg in a copy letting the
tree go once its copy is made, and keep the ledger, and in an agent's worktree each red leg's failing log, in the step's
directory, the work of each leg this machine runs held inside the isolation of the agents' work and answered as
`isolation` (none here and `agentPolicy.isolation: required` fails the step, 14, nothing run); the checks `anchor-balance` and `anchor-citations`, each held to the rows or files a step names; `ci-read`,
the legs `check-ci-legs` reads; and `ci-wait`, which waits for every check the forge runs on a commit - the one the run
last pushed where it names none - ends at the first red one with its job's failing step and whole log, and asks where
none appeared, some are still running, or one ended neither green nor red; and the fold family - `fold`, `delete-agent`,
`refresh` and `rebase` - which make their verb's dry run first, read what it stood, typed, and ask what it lacks - a
consent, a path outside a fold's `within`, conflicting merges - with what it is about, a fold and a deletion applying
only what it named, write each path a fold means to write to the run's ledger of its folds (`folds.json`) before the
verb and, before the fold lets go of the main tree's lock it wrote under, what it left - so a side-task's write made once
it let go is never taken for the fold's, whatever comes after: the verb's answer, a throw, a crash, a stop. The one limit
is a fold that dies inside its own hold, part way through its write: the next attempt reads what it left from the tree,
under that lock, and marks it `readFromTree`, for a side-task's write made between the death and that read cannot be
told from the fold's (an intent an earlier release left unsettled, which does not say `recordsWrite` - its verb recorded
nothing of its write, so it may have written all of it - is read so too) - and, of `others`, work on each other open
agent of the run in turn; and `commit`, which commits
the folds upstream of it and no other work - each path they changed that no commit of the run took, held to what
its fold left there (a path edited or restored since, a path the index marks assume-unchanged or skip-worktree, the
main checkout off the branch the run started on, or a fold upstream whose intent nobody settled, fails it, and nothing
is staged), on an empty index, by explicit path, with the configuration's `commit.template` filled or the step's own
message, the commit held to that set over the commit the branch named before it (one a hook made take more, or less,
or stand on anything but that commit alone, fails it, 20, left for a person) - answers the
paths git ignores, which it never commits, and with nothing owed is done and runs no git - its commit and the
repository's commit hooks run inside the isolation of the agents' work, answered as `isolation` (none here and `required`
fails it, 14, nothing staged); any template reads what the run checked (`{run.ran}`, `{run.notRun}`, `{run.red}`), and a commit's message what
checked what it carries (`{carried.ran}`, `{carried.notRun}`, `{carried.red}`); `push`, which pushes the commit the
run's commit steps recorded last, else the one the run started on - never what the branch names by then, which a hook
or a build working in the main checkout could have moved - to the main checkout's branch on the remote the run's branch
goes to (or the step's), a fast-forward never forced, and answers whether the branch moved, its push and the
repository's push hooks inside the isolation of the agents' work, answered as `isolation` (a detached HEAD, the main
checkout off the branch the run started on, a run that made no commit and started on none, or no remote it can name
refuses it, 13; no isolation here under `required` fails it, 14; the remote refusing fails it, 20; push steps that name
two remotes are refused); and
`pull-request`, which opens the run's branch's pull request against its base - the step's, else the remote's default -
on the repository the remote's address names, its body the workflow's file filled where the step starts, and answers an
open one as it is (`created` false) so a step run again opens no second, and never one from a fork's branch of the same
name; the run never merges it, and its end leaves each pull request, and each folded path git ignores, for a person. A check that finds red
fails its step under `failOn: red`, every check's default but `ci-read`'s. Every text a step hands on is filled where it
starts, and one that reads nothing fails the step (12) - a reason step's prompt alone is filled further: a placeholder in
it naming a step the run left no output is filled with a sentence saying so and why, so the agent knows what it does not
have. Every call of `gh` and every git command is started so it
cannot ask, and each command a step runs on the forge or to publish - a read of CI, a commit's, a push, a pull
request's - is ended after its step's `timeoutMinutes` where the step says one, else its own (5 minutes for `gh` and
a push, 15 minutes for a commit's), and is not done (21). A step of any other type fails, saying no runner serves its type;
the rest are the next thing the tool grows. A step that works in or on an agent
waits for the create-agent step that makes it, and is not run where that step did not make it. The topic carries its
worked examples besides - each printed as a whole workflow that holds as it is printed: a repair raised by a failed
build that goes back to what failed, a check raised whenever a builder ends, issues found and fixed one by one under
`maxOpenAgents: 1`, and a review loop that reworks what it finds until it finds nothing. Run
`dssharness help workflows` for the whole of it.

## Anchors

An anchor is a named piece of deferred work, kept as a row in a markdown registry so it
cannot be forgotten. Two registries hold every anchor: pending, for open, gated and
disclosed anchors, and done, the archive of closed ones. Their paths are set in
`config.json` under `anchors`, and `init` creates each one that is missing.

```bash
dssharness write-anchor D-AUTH-TOKEN-REFRESH --priority P1 --trigger "tokens expire mid-request"
dssharness set-anchor D-AUTH-TOKEN-REFRESH --status closed   # moves it to the done registry
dssharness read-anchor D-AUTH-TOKEN-REFRESH
dssharness read-anchors --open --band P0 P1
dssharness check-anchor-balance --base main
dssharness check-anchor-arms
```

The Status cell (`🟠 OPEN`, `⏳ GATED`, `🔵 DISCLOSED`, `✅ CLOSED`) is the only verdict that
decides whether a row is closed. Closing an anchor moves its row to the done registry, and
`check-anchor-balance` fails a change that creates more anchors than its work closes. A row is
one line, so a value's line breaks are stored as spaces: a value that would be stored with an
id or a path cut in two is refused, and so is one that newly cites an id no row holds. A row
names a mutation arm with a backticked id, and `check-anchor-arms` holds a done row's named
arms to the mutation record - each must be unmoved with a recorded passed on the legs its S
row names, a pass cited beside a run id must be spelled as history (`anchors.passWords`,
`anchors.historyForms`), and a backticked id no A row holds any more is reported where git
history shows it was declared once. Run
`dssharness help anchors` for the rules.

## Building from source

```bash
dotnet build DssHarness.slnx
dotnet test --project tests/dss-harness-test/dss-harness-test.csproj
```

Everything a build produces — binaries, intermediates and packages — lands under
`build/`, which git ignores. Deleting it is a complete clean.

The end-to-end tests build real CMake projects, so they need `cmake`, `ninja` and a C and C++
compiler on the `PATH` (`gcc` and `g++` on Windows, `cc` and `c++` elsewhere), and on Windows
one of them needs Visual Studio's C++ build tools. A machine lacking any of these skips those
tests, naming what it lacks; set `DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS=1`, as the continuous
integration runners do, to have them fail instead.

## Documentation

- [Architecture](docs/architecture.md) — layering, paths, hosts and legs, parallel execution, contamination guarantees
- [Releasing](docs/releasing.md) — channels, trusted publishing, the release pipelines

## License

DssHarness is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE)
and [NOTICE](NOTICE). The package carries other software under the MIT License, each named with
its copyright in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).
