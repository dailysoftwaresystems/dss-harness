using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.Processes;
using DssHarness.Core.Web;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The tools that reach past the files: a command run, contained, as a phase runs; and a page fetched by the harness's own
/// fetch, each address decided and recorded before anything leaves the machine.
/// </summary>
public sealed partial class AgentToolRuntime
{
    /// <summary>The rule a command or an address that holds a secret is refused by.</summary>
    public const string MaskRule = "the mask";

    /// <summary>What an address that holds a secret of the run's is refused with, whichever tool of the web would ask it.</summary>
    public static AgentPolicyAnswer.Denied SecretAddress { get; } = new(MaskRule, AgentPolicySource.Builtin, "the address holds a secret of this run's, which no request of the web carries");

    /// <summary>The leg a command an agent runs is logged under, as a phase is.</summary>
    public const string CommandLeg = "agent";

    /// <summary>
    /// <c>run_command</c>: a program and its arguments, never a shell string, decided whole by the policy where it starts,
    /// run as a phase is - contained, its log kept whole beside the stream, its silence and its time bounded - with the
    /// environment every child of an agent has; the answer how it ended and its output, cut as every answer is.
    /// </summary>
    private async Task<Answer> RunCommandAsync(string callId, JsonObject input, CancellationToken cancellationToken)
    {
        var program = Text(input, "program")!;
        IReadOnlyList<string> arguments = input["args"] is JsonArray given ? [.. given.Select(argument => argument!.GetValue<string>())] : [];
        var commands = _run.Commands;
        var seconds = Number(input, "timeout_seconds") ?? commands.TimeoutSeconds;
        var command = string.Join(' ', [program, .. arguments]);

        if (seconds < 1 || seconds > commands.MaxTimeoutSeconds)
        {
            return Answer.Failed($"timeout_seconds is {seconds}, and is 1 to {commands.MaxTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}");
        }

        if (program.Length == 0)
        {
            return Answer.Failed("program is empty: name the program to run");
        }

        // The command line is written to its log, and nothing secret is: one the mask would change is refused before it runs.
        if (_run.Mask.Holds(command))
        {
            return Answer.Refused(_run.Mask.Redact(command), new AgentPolicyAnswer.Denied(
                MaskRule,
                AgentPolicySource.Builtin,
                "the command holds a secret of this run's, which a command's line never carries: give it through what the program reads instead"));
        }

        var start = Text(input, "cwd") ?? ".";
        var place = ReadPlace(start);

        if (place.Denied is { } refused)
        {
            return Answer.Refused(start, refused);
        }

        var directory = place.Full!;

        if (!_host.FileSystem.DirectoryExists(directory))
        {
            return Answer.Failed($"there is no directory at '{Said(directory)}' to start in");
        }

        if (_policy.Run([program, .. arguments], directory) is AgentPolicyAnswer.Denied denied)
        {
            return Answer.Refused(command, denied);
        }

        var log = AgentCallRecords.ResultFile(_run.StepDirectory, callId);
        var request = new PhaseRequest
        {
            Leg = CommandLeg,
            Phase = AgentTools.RunCommand,
            // A relative path to a program is read where the command starts, as the policy read it, never in this process's own directory.
            FileName = ProcessRunner.Anchored(program, directory),
            Arguments = arguments,
            LogFile = log,
            WorkingDirectory = directory,
            Environment = AgentChildEnvironment.Of(_host.Environment().Keys, _run.Withheld, commands.KeepEnvironment),
            StandardInput = Text(input, "stdin") is { } stdin ? ChildInput.Of(stdin) : null,
            Timeout = TimeSpan.FromSeconds(seconds),
            StallSeconds = _run.Limits.StallSeconds ?? 0,
            Mask = _run.Mask,
            Contained = true,
            SessionRecord = _host.Sessions.For(_run.StepDirectory),
        };

        PhaseResult result;

        try
        {
            result = await _host.Phases.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            return Answer.Failed($"'{program}' could not be started: {ex.Message.TrimEnd('.')}");
        }
        catch (ProcessStoppedException ex) when (ex.Uncontained is { } unreached)
        {
            // Stopped by the run's own stop: what its container could not reach may still run, which the run says
            // however it ended, so it is kept before the stop goes on.
            Tally.LeftRunning($"{command}: {unreached}");
            throw;
        }

        if (result.Uncontained is { } left)
        {
            Tally.LeftRunning($"{command}: {left}");
        }

        var ended = result.Stopped switch
        {
            PhaseStop.WallClock => $"stopped: it ran past its {seconds} s",
            PhaseStop.Silence => $"stopped: it printed nothing for {result.StallSeconds} s",
            _ => $"exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}",
        };

        string output;

        try
        {
            output = AgentCallRecords.Cut(result.Output.Lines(), log);
        }
        catch (PhaseOutputUnreadException ex)
        {
            output = $"[its output could not be read back: {ex.Message.TrimEnd('.')}]";
        }

        return Answer.Kept(
            result.Stopped == PhaseStop.None && result.ExitCode == 0,
            output.Length == 0 ? $"{ended}, after {result.Duration.TotalSeconds:0.0} s; it printed nothing" : $"{ended}, after {result.Duration.TotalSeconds:0.0} s:\n{output}",
            log);
    }

    /// <summary>
    /// <c>web_fetch</c>: a page fetched by the harness's own fetch, each address - the first and each redirect - refused
    /// where it holds a secret, asked of the policy and recorded before it is asked, and what each was answered recorded;
    /// the text given inside a block that says where it is from and what it is.
    /// </summary>
    private async Task<Answer> FetchAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var url = Text(input, "url")!;
        var secret = SecretAddress;

        // Held to the mask as it was asked, before the fetch reads it: a reading of an address folds its host's case and
        // escapes its characters, past which a secret no longer reads as itself.
        if (_run.Mask.HoldsUnescaped(url))
        {
            Tally.Asked();
            await _records.WebDecidedAsync(AgentTools.WebFetch, "fetch", url, secret).ConfigureAwait(false);

            return Answer.Refused(url, secret);
        }

        var gate = new WebFetchGate(
            async address =>
            {
                Tally.Asked();

                var answer = _run.Mask.HoldsUnescaped(address) ? secret : _policy.Web(address);

                await _records.WebDecidedAsync(AgentTools.WebFetch, "fetch", address, answer).ConfigureAwait(false);

                return answer;
            },
            hop => _records.WebAnsweredAsync(AgentTools.WebFetch, "fetch", hop.Address, hop.Status, hop.Type, hop.Bytes, hop.Failure));

        var fetched = await _host.Fetch.FetchAsync(url, gate, cancellationToken).ConfigureAwait(false);

        if (fetched.Denial is { } denied)
        {
            return Answer.Refused(fetched.Address, denied);
        }

        if (fetched.Failure is { } failure)
        {
            // Said behind its escapes too: an address never decided - no web one's, or one past the redirects followed - may
            // carry a secret echoed in another's parameter.
            return Answer.Failed(_run.Mask.RedactUnescaped($"'{fetched.Address}' was not read: {failure}"));
        }

        // A page cannot end its own block, or open one that seems another's: its own tags are written as text.
        return Answer.Done(WebBlocks.Page(fetched.Address, fetched.Status, fetched.Text!));
    }

    /// <summary>
    /// <c>web_search</c>: the query asked of the step's sources - those it names, else every one - by the harness's own
    /// search, every rule of which is <see cref="Web.Search.HarnessWebSearch"/>'s; each source's part in turn, and a
    /// search none of whose sources answered failed, each part's cause said.
    /// </summary>
    private async Task<Answer> WebSearchAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var step = _webSearch ?? throw new InvalidOperationException("the runtime offers web_search to a step with no source");
        var search = _host.Search!;
        var query = Text(input, "query")!;
        IReadOnlyList<string>? names = input["sources"] is JsonArray given ? [.. given.Select(name => name!.GetValue<string>())] : null;

        var searched = await search.SearchAsync(step, query, names, cancellationToken).ConfigureAwait(false);

        if (searched.Refusal is { } refusal)
        {
            return searched.RefusedBy is { } rule
                ? Answer.Refused(query, new AgentPolicyAnswer.Denied(rule, AgentPolicySource.Builtin, refusal))
                : Answer.Failed(refusal);
        }

        return searched.Answered ? Answer.Done(searched.Text) : Answer.Failed($"no source answered the search:\n\n{searched.Text}");
    }

    /// <summary>What records the step's searches: its own records, and its tally of the requests it made.</summary>
    private sealed class SearchRecords(AgentToolRuntime runtime) : Web.Search.IWebSearchRecords
    {
        public Task DecidedAsync(string source, string address, AgentPolicyAnswer answer)
        {
            runtime.Tally.Asked();
            return runtime._records.WebDecidedAsync(AgentTools.WebSearch, source, address, answer);
        }

        public Task AnsweredAsync(string source, WebHop hop)
            => runtime._records.WebAnsweredAsync(AgentTools.WebSearch, source, hop.Address, hop.Status, hop.Type, hop.Bytes, hop.Failure);

        public Task SearchedAsync(Web.Search.WebSearchPart part) => runtime._records.WebSearchedAsync(part);
    }
}
