using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.Processes;
using DssHarness.Core.Waiting;
using DssHarness.Core.Web;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The tools that reach past the files: a command run, contained, as a phase runs; a page fetched by the harness's own
/// fetch; and a query searched by the harness's own search - each address decided and recorded before anything leaves
/// the machine.
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
    /// run as a phase is - contained, its log kept whole beside the stream, its silence bounded by the step's stall bound
    /// and its time by the one its call names, else by <c>agentPolicy.commandTimeoutSeconds</c> where it is given, else by
    /// none of its own - with the environment every child of an agent has, then followed as every write is; the answer how
    /// it ended and its output whole, or, where its output is longer than a page, the first page of its log, which says how
    /// <c>read_result</c> gives the rest. A program that started ran, however it exited - grep, rg and git grep exit 1
    /// finding nothing - and one refused, or never started, did not.
    /// </summary>
    private async Task<Answer> RunCommandAsync(string callId, JsonObject input, CancellationToken cancellationToken)
    {
        var program = Text(input, "program")!;
        IReadOnlyList<string> arguments = input["args"] is JsonArray given ? [.. given.Select(argument => argument!.GetValue<string>())] : [];
        var commands = _run.Commands;
        var asked = Number(input, "timeout_seconds");
        var command = string.Join(' ', [program, .. arguments]);

        if (asked is < 1 or > AgentCommandSettings.MostTimeoutSeconds)
        {
            return Answer.NotRun($"timeout_seconds is {asked}, and is 1 to {AgentCommandSettings.MostTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}, what a command's timer holds; leave it out to run the command until it ends");
        }

        long? seconds = asked ?? commands.CommandTimeoutSeconds;

        if (program.Length == 0)
        {
            return Answer.NotRun("program is empty: name the program to run");
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
            return Answer.NotRun($"there is no directory at '{Said(directory)}' to start in");
        }

        // What the command is written as its input is text the policy does not read: a program that reads its script from
        // its input is refused where it is given one.
        var stdin = Text(input, "stdin");

        if (_policy.Run([program, .. arguments], directory, inputGiven: stdin is not null).Refusal() is { } denied)
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
            StandardInput = stdin is not null ? ChildInput.Of(stdin) : null,
            Timeout = seconds is { } bound ? TimeSpan.FromSeconds(bound) : null,
            StallSeconds = _run.Limits.StallSeconds ?? 0,
            Mask = _run.Mask,
            Contained = true,
            Isolation = _run.Isolation?.Commands,
            SessionRecord = _host.Sessions.For(_run.StepDirectory),
        };

        PhaseResult result;

        try
        {
            result = await _host.Phases.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            return Answer.NotRun($"'{program}' could not be started: {ex.Message.TrimEnd('.')}");
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

        // A command that ran may have written anything in the tree, however it ended: it is followed as every write is,
        // the run's graph synced by what the tree holds now.
        await FollowedAsync(AgentGraphWrite.Command(AgentTools.RunCommand)).ConfigureAwait(false);

        var ended = result.Stopped switch
        {
            PhaseStop.WallClock => $"stopped: it ran past its {seconds} s",
            PhaseStop.Silence => $"stopped: it printed nothing for {result.StallSeconds} s",
            _ => $"exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}",
        };

        var after = $"{ended}, after {result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
        string said;

        try
        {
            said = Whole(result.Output) is { } output
                ? (output.Length == 0 ? $"{after}; it printed nothing" : $"{after}:\n{output}")
                : $"{after}; its output is longer than a page, and its log is given a page at a time, the log's own lines first:\n{await _results.FirstPageAsync(callId, cancellationToken).ConfigureAwait(false)}";
        }
        catch (PhaseOutputUnreadException ex)
        {
            said = $"{after}:\n[its output could not be read back: {ex.Message.TrimEnd('.')}]";
        }
        catch (InvalidDataException ex)
        {
            said = $"{after}:\n[its output, longer than a page, does not read as text, and is in '{log}': {ex.Message.TrimEnd('.')}]";
        }

        return Answer.Kept(result.Stopped == PhaseStop.None && result.ExitCode == 0, said, log);
    }

    /// <summary>
    /// A command's output whole, its lines read back from its log while they fit one page of a call's answer
    /// (<see cref="KeptResults.OnePage(StringBuilder)"/>); null where they pass it - the reading stops there - and the
    /// answer gives its log a page at a time.
    /// </summary>
    private static string? Whole(PhaseOutput output)
    {
        var held = new StringBuilder();
        var first = true;

        foreach (var line in output.Lines())
        {
            if (!first)
            {
                held.Append('\n');
            }

            first = false;
            held.Append(line);

            if (!KeptResults.OnePage(held))
            {
                return null;
            }
        }

        return held.ToString();
    }

    /// <summary>
    /// <c>web_fetch</c>: a page fetched by the harness's own fetch, each address - the first and each redirect - refused
    /// where it holds a secret, asked of the policy and recorded before it is asked, and what each was answered recorded;
    /// the text given inside a block that says where it is from and what it is. The text is written into the call's answer
    /// as it is read, a line at a time and each line masked (<see cref="ResultLines"/>), so the text of a page of any size is
    /// never held whole: one that passes a page is kept whole in the call's file, put inside its block where it lies, and
    /// given a page at a time.
    /// </summary>
    /// <param name="callId">The call's id, which names its answer's file.</param>
    /// <param name="input">The call's input.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    private async Task<Answer> FetchAsync(string callId, JsonObject input, CancellationToken cancellationToken)
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
            hop => _records.WebAnsweredAsync(AgentTools.WebFetch, "fetch", hop.Address, hop.Status, hop.Type, hop.Bytes, hop.Failure))
        {
            // An answer's silence ends at the step's stall bound, as a command's does; one that goes on coming is read whole,
            // its wait told where the running operation says everything else.
            Stall = _run.Limits.StallSeconds is { } stall and > 0 ? TimeSpan.FromSeconds(stall) : null,
            Tell = Waits.Told,
        };

        // The block's head names the address the redirects ended at and the status it answered, known once the text is
        // read: so the text is written first, and its block about it once the fetch has ended.
        await using var lines = _records.Lines(callId);
        using var text = lines.Writer(cancellationToken);
        var fetched = await _host.Fetch.FetchAsync(url, WebFetchAsk.Page with { Into = text }, gate, cancellationToken).ConfigureAwait(false);

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

        await text.EndAsync().ConfigureAwait(false);
        await lines.EndAsync().ConfigureAwait(false);

        // A page cannot end its own block, or open one that seems another's: its own tags are written as text. The address
        // is masked behind its escapes before the block writes it as an attribute, past which a secret no longer reads as
        // itself.
        var address = _run.Mask.RedactUnescaped(fetched.Address);

        if (lines.Text is { } whole)
        {
            return Answer.Done(WebBlocks.Page(address, fetched.Status, whole));
        }

        await lines.RewriteAsync((page, into) => WebBlocks.Page(into, address, fetched.Status, inside =>
        {
            var chunk = new char[PieceOfAPage];

            while (page.Read(chunk, 0, chunk.Length) is var read and > 0)
            {
                inside.Write(chunk, 0, read);
            }
        })).ConfigureAwait(false);

        return Answer.Kept(ok: true, await _results.FirstPageAsync(callId, cancellationToken).ConfigureAwait(false), lines.File!);
    }

    /// <summary>How many characters of a page kept in its call's file are put inside its block at a time.</summary>
    private const int PieceOfAPage = 81920;

    /// <summary>
    /// <c>web_search</c>: the query asked of the step's sources - those it names, else every one - by the harness's own
    /// search, every rule of which is <see cref="Web.Search.HarnessWebSearch"/>'s; each source's part in turn, and a
    /// search none of whose sources answered failed, each part's cause said. A side-task's search asks one more beside
    /// them: the provider's own, its <c>native</c> part, where the run carries one - asked through the client's loop, as
    /// the run stands when the agent asks.
    /// </summary>
    private async Task<Answer> WebSearchAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var step = _webSearch ?? throw new InvalidOperationException("the runtime offers web_search to a step with no source");
        var search = _host.Search!;
        var query = Text(input, "query")!;
        IReadOnlyList<string>? names = input["sources"] is JsonArray given ? [.. given.Select(name => name!.GetValue<string>())] : null;
        var page = Number(input, "page") ?? 1;

        if (page is < 1 or > int.MaxValue)
        {
            return Answer.Failed($"page is {page.ToString(CultureInfo.InvariantCulture)}, and is 1 to {int.MaxValue.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        var searched = await search.SearchAsync(step, query, names, (int)page, cancellationToken).ConfigureAwait(false);

        if (searched.Refusal is { } refusal)
        {
            return searched.RefusedBy is { } rule
                ? Answer.Refused(query, new AgentPolicyAnswer.Denied(rule, AgentPolicySource.Builtin, refusal))
                : Answer.Failed(refusal);
        }

        if (_native is { } native)
        {
            searched = searched with { Parts = [.. searched.Parts, await NativeAsync(native, step, query, cancellationToken).ConfigureAwait(false)] };
        }

        return searched.Answered ? Answer.Done(searched.Text) : Answer.Failed($"no source answered the search:\n\n{searched.Text}");
    }

    /// <summary>
    /// The provider's own search, asked as one more source of the same search through the client's loop - which asks it
    /// with the entry serving the run, through the gate, and counts what it used - its part recorded as every source's
    /// is; a part that failed never hides the configured sources' answers. The run stopped is no failed part: the stop
    /// goes on as every stop of the run does. The ask is one request of the step's search, refused unasked where the
    /// step's are spent, and each search the provider ran of it counts as a request too: the provider decides them
    /// itself, so they are known only once the ask says how many it ran, and spent then - past the bound where the
    /// provider ran them, for they were made.
    /// </summary>
    /// <param name="native">What asks the provider's own search, as the run stands.</param>
    /// <param name="step">The step's search, whose requests it counts among.</param>
    /// <param name="query">The query, as the agent wrote it.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    private async Task<Web.Search.WebSearchPart> NativeAsync(AgentNativeAsk native, Web.Search.WebSearchStep step, string query, CancellationToken cancellationToken)
    {
        Tally.Asked();

        if (!step.Take())
        {
            var unasked = Web.Search.WebSearchPart.NativeRefused(query, Web.Search.HarnessWebSearch.Spent(step));

            await _records.WebSearchedAsync(unasked).ConfigureAwait(false);

            return unasked;
        }

        var asked = await native(query, cancellationToken).ConfigureAwait(false);

        // Every search the ask ran is a request of the step's search: the ask's own request already counted, the rest are
        // counted here, as the ask says how many it ran.
        step.Made(asked.Searches);

        await _records.WebSearchedAsync(asked.Part).ConfigureAwait(false);

        return asked.Part;
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
