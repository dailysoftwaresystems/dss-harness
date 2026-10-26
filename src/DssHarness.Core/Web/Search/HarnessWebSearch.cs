using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Web.Index;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// The harness's own web search: every rule of a search that is no kind's. A query is held to the mask; each source a
/// step may search is asked in its own part - what its lookups found read once for the run, its request sent through the
/// harness's fetch, each address decided by the step's <c>Web(...)</c> rules and recorded before it is asked, the
/// source's pace on this machine taken first and a pause its own answer asks for kept - or answered from what the run
/// asked before, under the step's own rules; each request made counted against the step's <c>maxPerStep</c>; a source's
/// key, where the step was given one, sent by the fetch to the scheme, host and port its configuration writes alone - never
/// to a host a document it fetched names - and never in an address any record holds; the results held to the
/// step's web rules, those they refuse left out and counted. One source's failure never hides another's results, and a
/// search none of whose sources answered is no answer.
/// </summary>
/// <remarks>Nothing is retried on its own: a limit is told, and the source left alone until the time it gave.</remarks>
public sealed class HarnessWebSearch(WebSearchKinds kinds, HarnessFetch fetch, WebIndexes indexes, WebSearchPace pace, IFileSystem fileSystem, TimeProvider clock) : IDisposable
{
    /// <summary>The rule a query that holds a secret is refused by: the one an address or a command that holds one is (<see cref="Agents.Runtime.AgentToolRuntime.MaskRule"/>).</summary>
    public const string MaskRule = Agents.Runtime.AgentToolRuntime.MaskRule;

    /// <summary>The rule a request past the step's bound is refused by.</summary>
    public const string BudgetRule = "agentWebSearch.maxPerStep";

    private readonly WebSearchKinds _kinds = kinds;
    private readonly HarnessFetch _fetch = fetch;
    private readonly WebIndexes _indexes = indexes;
    private readonly WebSearchPace _pace = pace;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly TimeProvider _clock = clock;
    private readonly ConcurrentDictionary<string, Lazy<WebSearchRun>> _runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Searches <paramref name="query"/> in <paramref name="step"/>'s sources - those <paramref name="names"/> names, where
    /// it names any - each in its own part, in the step's order.
    /// </summary>
    /// <param name="step">The step searching: its sources, bounds, rules and records.</param>
    /// <param name="query">The query, as the agent wrote it.</param>
    /// <param name="names">The sources the agent named, among the step's; every one of the step's where none.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    public async Task<WebSearched> SearchAsync(WebSearchStep step, string query, IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query))
        {
            return WebSearched.Refused("the query says nothing; a search asks its sources for words");
        }

        // Held to the mask as it was written - behind its escapes too, as an address is - before any source reads it: a
        // secret of this run's never leaves in a query.
        if (step.Mask.HoldsUnescaped(query))
        {
            return WebSearched.Refused("the query holds a secret of this run's, which no request of the web carries", MaskRule);
        }

        if (names is { Count: 0 })
        {
            return WebSearched.Refused($"sources is given empty; name some of this step's - {string.Join(", ", step.Sources.Select(source => source.Name))} - or leave it out for every one");
        }

        if ((names ?? []).Where(name => step.Sources.All(source => source.Name != name)).ToList() is [_, ..] unknown)
        {
            return WebSearched.Refused($"sources names {string.Join(", ", unknown.Select(name => $"'{name}'"))}, which {(unknown.Count == 1 ? "is" : "are")} none of this step's: {string.Join(", ", step.Sources.Select(source => source.Name))}");
        }

        var asked = names is null ? step.Sources : [.. step.Sources.Where(source => names.Contains(source.Name, StringComparer.Ordinal))];
        var run = _runs.GetOrAdd(step.RunDirectory, directory => new(() => new WebSearchRun(_fileSystem, _clock, directory), LazyThreadSafetyMode.PublicationOnly)).Value;

        // Each source in its own part, asked together: a slow source or a long pace holds none of the others back - and a
        // defect of one never hides another's, every one raised where more than one failed.
        var asking = Task.WhenAll(asked.Select(source => GuardedPartAsync(step, run, source, query, cancellationToken)));
        WebSearchPart[] parts;

        try
        {
            parts = await asking.ConfigureAwait(false);
        }
        catch when (asking.Exception is { InnerExceptions.Count: > 1 } every)
        {
            throw every.Flatten();
        }

        foreach (var part in parts)
        {
            await step.Records.SearchedAsync(part).ConfigureAwait(false);
        }

        return new WebSearched(query, parts, null);
    }

    /// <summary>Closes every run's record.</summary>
    public void Dispose()
    {
        foreach (var run in _runs.Values.Where(run => run.IsValueCreated))
        {
            run.Value.Dispose();
        }
    }

    /// <summary>
    /// <paramref name="source"/>'s part, whatever stops it: what this machine could not do for it - read or write the pace's
    /// record or the run's, reach a file - is that part's failure, said as this machine's and never the source's, and never
    /// hides another source's answer. What the source was asked and answered before it, the step's record holds.
    /// </summary>
    private async Task<WebSearchPart> GuardedPartAsync(WebSearchStep step, WebSearchRun run, WebSearchSource source, string query, CancellationToken cancellationToken)
    {
        try
        {
            return await PartAsync(step, run, source, query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (OnThisMachine(ex))
        {
            return PartOf(step, source, Kind(source), query, WebSearchAnswer.Failed($"its search failed on this machine, not at the source: {ex.Message}"));
        }
    }

    /// <summary>Whether <paramref name="failure"/> is this machine's - a record, a pace, a file - rather than a defect.</summary>
    private static bool OnThisMachine(Exception failure) => failure is Results.HarnessException or IOException or UnauthorizedAccessException;

    private async Task<WebSearchPart> PartAsync(WebSearchStep step, WebSearchRun run, WebSearchSource source, string query, CancellationToken cancellationToken)
    {
        var kind = Kind(source);
        var key = step.Keys.GetValueOrDefault(source.Name);
        WebSearchPart Part(WebSearchAnswer answer, bool cached = false) => PartOf(step, source, kind, query, answer, cached);

        // What the fetch sends as the source's key, where the step was given one - to the scheme, host and port the source's
        // configuration writes alone, never to one a document it fetched names: an answer made with it is no answer to a step
        // without it - nor one made without it to a step that sends it - so the run keeps each apart.
        var sent = key?.Value is { } value && source.Credential is { } credential ? credential.Fetched(value, kind.KeyOrigin(source)) : null;

        // What the run's lookups found is used under this step's own rules: each address they asked decided again, so no
        // answer made from a document this step's rules refuse is given it - whichever step of the run asked first.
        var known = run.Found(source, kind.Version);

        if (known is { Addresses: var looked } && Refusal(step, looked) is { } lookupRefused)
        {
            return Part(lookupRefused.Of("its lookup"), cached: true);
        }

        // What the run asked before is answered again under this step's own rules: each address the source asked for it -
        // its redirects among them - held to its Web(...) rules, as a request would be, and each result to every rule of its web.
        if (run.Searched(source, kind.Version, query, step.ResultsPerSource, keyed: sent is not null) is { } stored)
        {
            return Part(Refusal(step, stored.Addresses!) ?? stored.Answer(), cached: true);
        }

        if (_pace.Held(source.Name) is { } held)
        {
            return Part(WebSearchAnswer.Limited(held.Until, $"{held.Why}; it was not asked"));
        }

        var found = known?.Found;

        if (found is null)
        {
            (found, var lookedUp) = await LookupAsync(step, source, kind, cancellationToken).ConfigureAwait(false);

            // What was found is kept for the run, with each address its lookups asked; why nothing was - a failure, a refusal
            // under this step's rules, a limit - is this query's answer alone, the next one asking again.
            if (found.Failure is null)
            {
                run.Keep(source, kind.Version, found with { Until = null }, lookedUp, step.Mask);
            }
        }

        if (found.Failure is { } failure)
        {
            return Part(failure);
        }

        var request = kind.Request(source, found, query, step.ResultsPerSource);
        var (response, refusal, addresses, unrecorded) = request switch
        {
            WebSearchRequest.Http http => await AskAsync(step, source, http, sent, cancellationToken).ConfigureAwait(false),
            WebSearchRequest.Indexed indexed => Indexed(step, source, indexed),
            _ => throw new InvalidOperationException($"The kind '{kind.Kind}' asked for a request this search does not send: {request.GetType().Name}."),
        };

        WebSearchAnswer answer;

        try
        {
            answer = refusal ?? kind.Read(source, found, response!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && response is WebSearchResponse.Http http)
        {
            // A kind's defect still leaves its source held for the pause its answer's headers asked for.
            if (Beside(ex, unrecorded, () => Pause(step, source, WebRateLimit.Read(http.Status, http.Headers, http.At)?.Until, null, "it")) is { } both)
            {
                throw both;
            }

            throw;
        }

        // A pause is the source's own to ask, in what it answered: never one this search made of a refusal or of a turn its
        // pace held - which the pace already holds - nor an index's, which its fill keeps.
        if (response is WebSearchResponse.Http && answer.Until is { } until)
        {
            Pause(step, source, until, answer, "it");
        }

        // What of the answer could not be recorded is raised once the pause it asks for - in its headers, or in its body,
        // which its kind alone reads - holds its source: what failed is this machine's, and nothing of the answer is kept.
        if (unrecorded is not null)
        {
            ExceptionDispatchInfo.Throw(unrecorded);
        }

        // The kind's answer is kept as any step may be told it (Masked), so a later step of the run is given it under its own
        // rules; an index's once it is no longer being filled.
        if (answer.Outcome is WebSearchOutcome.Ok or WebSearchOutcome.NoResults && response is not WebSearchResponse.Indexed { Answer.Filling: true })
        {
            run.Keep(source, kind.Version, query, step.ResultsPerSource, keyed: sent is not null, addresses, Masked(step, answer), step.Mask);
        }

        return Part(answer);
    }

    /// <summary>
    /// <paramref name="defect"/>, raised with what failed beside it, never in its place: holding its source for a pause its
    /// answer asked for, done first, and recording that answer (<paramref name="unrecorded"/>) - so a defect is never told as
    /// this machine's failure, nor hides one. <see langword="null"/> where nothing did, the defect raised alone.
    /// </summary>
    private static AggregateException? Beside(Exception defect, Exception? unrecorded, Action holding)
    {
        Exception? unheld = null;

        try
        {
            holding();
        }
        catch (Exception ex)
        {
            unheld = ex;
        }

        return unheld is null && unrecorded is null ? null : new AggregateException([defect, .. new[] { unheld, unrecorded }.OfType<Exception>()]);
    }

    /// <summary>The kind of <paramref name="source"/>, which the configuration's check holds to one this build has.</summary>
    private IWebSearchKind Kind(WebSearchSource source)
        => _kinds.Find(source.Kind) ?? throw new InvalidOperationException($"The source '{source.Name}' names the kind '{source.Kind}', which this build does not hold; the configuration's check refuses it.");

    /// <summary><paramref name="source"/>'s part of the search: <paramref name="answer"/> as <paramref name="step"/> is given it.</summary>
    private static WebSearchPart PartOf(WebSearchStep step, WebSearchSource source, IWebSearchKind kind, string query, WebSearchAnswer answer, bool cached = false)
    {
        var (given, withheld) = Given(step, answer);
        return new(source.Name, kind.Kind, kind.Version, query, given, withheld, cached, step.Keys.GetValueOrDefault(source.Name)?.Missing);
    }

    /// <summary>
    /// What <paramref name="step"/> is given of <paramref name="answer"/>: the answer as it may be told it (<see
    /// cref="Masked"/>), its results its web rules let it read, at most its <c>resultsPerSource</c>, and how many they left
    /// out - an agent is never handed an address it could not then read.
    /// </summary>
    private static (WebSearchAnswer Given, int Withheld) Given(WebSearchStep step, WebSearchAnswer answer)
    {
        var told = Masked(step, answer);

        if (told.Outcome != WebSearchOutcome.Ok)
        {
            return (told, 0);
        }

        var kept = told.Results.Where(result => step.Rules.Result(result.Link) is AgentPolicyAnswer.Allowed).ToList();

        return (WebSearchAnswer.Read([.. kept.Take(step.ResultsPerSource)]) with { Total = told.Total, Held = told.Held, LeftOut = told.LeftOut }, told.Results.Count - kept.Count);
    }

    /// <summary>
    /// <paramref name="answer"/> as <paramref name="step"/> may be told it, and as the run keeps it: each text it took from
    /// its source as <see cref="Told"/> says, and each result whose link holds a secret of the run's, behind its escapes too,
    /// left out.
    /// </summary>
    private static WebSearchAnswer Masked(WebSearchStep step, WebSearchAnswer answer)
        => answer.Saying(text => Told(step.Mask, text), link => !step.Mask.HoldsUnescaped(link));

    /// <summary>
    /// <paramref name="text"/> - what a kind read of an answer - as a step is told it: masked, behind its escapes too, and
    /// where its reader cut it short (<see cref="Output.TextCut.Ellipsis"/>), cut again before any secret its end could be
    /// the start of (<see cref="LineMasks.RedactHead"/>) - so no half of a key a reader decoded, an HTML reference or a JSON
    /// escape, is told, nor of one behind escapes the cut parted.
    /// </summary>
    private static string Told(ILineMask mask, string text)
        => text.EndsWith(Output.TextCut.Ellipsis) ? mask.RedactHead(text[..^1]) + Output.TextCut.Ellipsis : mask.RedactUnescaped(text);

    /// <summary>
    /// Why <paramref name="step"/>'s rules refuse <paramref name="addresses"/> - each one the run asked before, decided again
    /// for this step - by the first they refuse; <see langword="null"/> where they refuse none.
    /// </summary>
    private static WebSearchAnswer? Refusal(WebSearchStep step, IEnumerable<string> addresses)
        => addresses.Select(address => (Address: address, Answer: Decided(step, address))).FirstOrDefault(decided => decided.Answer is AgentPolicyAnswer.Denied) is { Answer: AgentPolicyAnswer.Denied denied, Address: var refused }
            ? Refused(refused, denied)
            : null;

    /// <summary>A source refused <paramref name="address"/> by <paramref name="denied"/>, told as any answer is (<see cref="Masked"/>).</summary>
    private static WebSearchAnswer Refused(string address, AgentPolicyAnswer.Denied denied)
        => WebSearchAnswer.Refused($"'{address}' was refused by {denied.Rule}: {denied.Reason}");

    /// <summary>
    /// How <paramref name="step"/> decides an address a source asks: one that holds a secret of the run's - behind its
    /// escapes too, as a redirect carrying another address holds it - is refused as a page an agent fetches is, then the
    /// step's rules.
    /// </summary>
    private static AgentPolicyAnswer Decided(WebSearchStep step, string address)
        => step.Mask.HoldsUnescaped(address) ? Agents.Runtime.AgentToolRuntime.SecretAddress : step.Rules.Source(address);

    /// <summary>
    /// What <paramref name="source"/>'s lookups find: each asked as any of its requests is - but with no key, for a lookup's
    /// document is no request of the source's own - then read by its kind; with each address they asked, which a later
    /// step's rules decide again.
    /// </summary>
    private async Task<(WebSearchFound Found, IReadOnlyList<string> Addresses)> LookupAsync(WebSearchStep step, WebSearchSource source, IWebSearchKind kind, CancellationToken cancellationToken)
    {
        var answers = new List<WebSearchResponse.Http>();
        var addresses = new List<string>();
        var lookups = kind.Lookups(source);
        Exception? unrecorded = null;

        foreach (var lookup in lookups)
        {
            (var response, var refusal, var allowed, unrecorded) = await AskAsync(step, source, lookup, sent: null, cancellationToken).ConfigureAwait(false);
            addresses.AddRange(allowed);

            // Why nothing was asked - a refusal, a turn its pace held, an address not read - is no answer of the source's,
            // and asks for no pause past one its unread status asked, which holds it already.
            if (refusal is not null)
            {
                if (unrecorded is not null)
                {
                    ExceptionDispatchInfo.Throw(unrecorded);
                }

                return (WebSearchFound.FailedWith(refusal.Of($"its lookup at '{lookup.Address}'")), addresses);
            }

            var answered = (WebSearchResponse.Http)response!;
            answers.Add(answered);

            // An answer that could not be recorded ends the lookups, nothing more sent: the pause it asks for holds the
            // source first, below, then what failed is raised.
            if (unrecorded is not null)
            {
                break;
            }

            if (answers.Count < lookups.Count)
            {
                // A lookup its status fails ends them: what follows could not be used, so nothing more is sent.
                if (WebSearchStatus.Before(answered, out _) is not null)
                {
                    break;
                }

                // An answer that asks the source to be left alone holds the lookups after it as it holds the request: the
                // pause is kept before the next is asked, which is then told rather than sent.
                Pause(step, source, WebRateLimit.Read(answered.Status, answered.Headers, answered.At)?.Until, null, "its lookup");
            }
        }

        // What the source answered holds it until the latest time any answer gave, found or not, as any of its answers
        // would - a document its kind could not use ends the kind's reading before the answers after it, whose pauses hold
        // all the same, as they do beside a defect of the kind's: the request after it is not sent before then, and is
        // told so.
        var asked = answers.Aggregate((DateTimeOffset?)null, (later, answered) => WebRateLimit.Later(later, WebRateLimit.Read(answered.Status, answered.Headers, answered.At)?.Until));
        WebSearchFound found;

        try
        {
            found = answers.Count == 0 || unrecorded is not null ? WebSearchFound.None : kind.Found(source, answers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Beside(ex, null, () => Pause(step, source, asked, null, "its lookup")) is { } both)
            {
                throw both;
            }

            throw;
        }

        Pause(step, source, WebRateLimit.Later(found.Until, asked), found.Failure, "its lookup");

        if (unrecorded is not null)
        {
            ExceptionDispatchInfo.Throw(unrecorded);
        }

        return (found, addresses);
    }

    /// <summary>
    /// Sends <paramref name="request"/> through the harness's fetch - its key where the step was given one, which the fetch
    /// adds to what it sends to the scheme, host and port first asked alone, where it is one the key may go to, each address
    /// decided, counted and recorded, the source's turn taken first - and answers what came back, or why nothing did, with
    /// each address it allowed.
    /// </summary>
    /// <remarks>
    /// What of its answers could not be recorded is given back rather than raised, so the caller holds the source for the
    /// pause the answer asks for before it raises it.
    /// </remarks>
    private async Task<(WebSearchResponse? Response, WebSearchAnswer? Refusal, IReadOnlyList<string> Addresses, Exception? Unrecorded)> AskAsync(WebSearchStep step, WebSearchSource source, WebSearchRequest.Http request, WebFetchKey? sent, CancellationToken cancellationToken)
    {
        // A step whose requests are spent sends nothing more: the source is told, with no address decided for it.
        if (step.Spent >= step.MaxPerStep)
        {
            return (null, WebSearchAnswer.Refused(Spent(step)), [], null);
        }

        var allowed = new List<string>();
        Exception? unrecorded = null;

        WebSearchHold? hold = null;
        (string Address, AgentPolicyAnswer Answer)? turning = null;
        AgentPolicyAnswer? refused = null;
        var gate = new WebFetchGate(
            async asked =>
            {
                // An address the step's rules refuse is no request, and is not counted; one they allow is, while any is left.
                var answer = Decided(step, asked);

                if (answer is AgentPolicyAnswer.Allowed && !step.Take())
                {
                    answer = new AgentPolicyAnswer.Denied(BudgetRule, AgentPolicySource.Config, Spent(step));
                }

                // Whatever the rules do not allow is refused as the denial it reads as, which the fetch is handed in its place.
                if (answer.Refusal() is { } denied)
                {
                    refused = denied;
                    await step.Records.DecidedAsync(source.Name, asked, denied).ConfigureAwait(false);

                    return denied;
                }

                allowed.Add(asked);
                turning = (asked, answer);

                return answer;
            },
            async hop =>
            {
                // An answer that could not be recorded is raised once it is read, so the pause it asks for holds its source
                // first: what failed is this machine's, and every run on it keeps to the source's word all the same.
                try
                {
                    await step.Records.AnsweredAsync(source.Name, hop).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    unrecorded ??= ex;
                }
            })
        {
            Pace = async token =>
            {
                try
                {
                    hold = await _pace.TurnAsync(source, token).ConfigureAwait(false);

                    // A turn taken is recorded as it was decided, before it is asked.
                    if (hold is null && turning is var (address, answer))
                    {
                        await step.Records.DecidedAsync(source.Name, address, answer).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // A turn that could not be taken or recorded sends nothing: what the decision counted is given back.
                    step.GiveBack();
                    throw;
                }

                // A turn refused is no request: what the decision counted is given back, and nothing is recorded of it.
                if (hold is not null)
                {
                    step.GiveBack();
                }

                return hold?.Until;
            },
        };

        var ask = new WebFetchAsk { Method = request.Method, Body = request.Body, Headers = request.Headers, Key = sent, AnyType = true, Html = WebFetchHtml.Markup };
        WebFetched fetched;

        try
        {
            fetched = await _fetch.FetchAsync(request.Address, ask, gate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (unrecorded is not null && ex is not OperationCanceledException && !OnThisMachine(ex))
        {
            // A defect never hides what failed recording an answer: both are raised, the defect first.
            throw new AggregateException(ex, unrecorded);
        }

        if (fetched.Denial is { } denied)
        {
            // A refusal of the fetch's own - by the key's rules, before any of the step's decided the address - is recorded
            // as the step's are: nothing was asked.
            if (!ReferenceEquals(denied, refused))
            {
                await step.Records.DecidedAsync(source.Name, fetched.Address, denied).ConfigureAwait(false);
            }

            return (null, Refused(fetched.Address, denied), allowed, unrecorded);
        }

        if (fetched.Until is { } until)
        {
            return (null, WebSearchAnswer.Limited(until, $"{hold?.Why ?? $"its turn is spent until {WebRateLimit.Said(until)}"}; it was not asked"), allowed, unrecorded);
        }

        // Masked as written before any reader cuts it, its form kept - a JSON answer's JSON: a secret it echoes is never
        // parted, and one a reader decodes - a JSON escape - is masked as the step is told it (Given).
        var body = fetched.Text is { } text ? step.Mask.Redact(text) : null;

        // Not read where nothing came back - or where what came back still holds a secret behind its escapes, its key
        // echoed as a page writes it, '&#x2B;' for its '+': a reader cuts where it may, and no cut of it may part one.
        var unread = fetched.Status is null || body is null ? fetched.Failure
            : step.Mask.HoldsUnescaped(body) ? $"it answered {fetched.Status.Value.ToString(CultureInfo.InvariantCulture)}, and its answer holds a secret of this run's behind its escapes"
            : null;

        if (fetched.Status is not { } status || unread is not null)
        {
            var said = $"'{fetched.Address}' was not read: {unread}";
            var failed = WebSearchAnswer.Failed(said);
            DateTimeOffset? paused = null;
            WebSearchAnswer? before = null;

            // A limit its status and headers say is told as one, its body unread, and a pause they ask for holds the source
            // all the same - as its status and headers alone answer them (WebSearchStatus).
            if (fetched.Status is { } answered)
            {
                before = WebSearchStatus.Before(new WebSearchResponse.Http(fetched.Address, answered, fetched.Type, fetched.Headers, string.Empty, _clock.GetUtcNow()), out paused);

                if (before is { Outcome: WebSearchOutcome.RateLimited, Until: { } limited, Why: { } limit })
                {
                    failed = WebSearchAnswer.Limited(limited, $"{limit}; {said}");
                    paused = limited;
                }
            }

            // Held in the limit's own words: an answer that was not read is never the reason a later query is told.
            if (paused is { } held)
            {
                Pause(step, source, held, before, "it");
                failed = failed with { Until = held };
            }

            return (null, failed, allowed, unrecorded);
        }

        return (new WebSearchResponse.Http(fetched.Address, status, fetched.Type, fetched.Headers, body!, _clock.GetUtcNow()), null, allowed, unrecorded);
    }

    /// <summary>
    /// Searches the harness's index of <paramref name="source"/>'s site, kept with the repository's, where the step's
    /// <c>Web(...)</c> rules let it ask the site: its fill, where it begins one, held to the run's rules (the policy's
    /// <c>WebFill</c>), never this step's alone, for the index outlives it - paced as the source is, recorded by the fill
    /// in its own record, and counted against no step's <c>maxPerStep</c>.
    /// </summary>
    private (WebSearchResponse? Response, WebSearchAnswer? Refusal, IReadOnlyList<string> Addresses, Exception? Unrecorded) Indexed(WebSearchStep step, WebSearchSource source, WebSearchRequest.Indexed request)
    {
        var site = request.Settings.Site;

        if (Decided(step, site) is AgentPolicyAnswer.Denied denied)
        {
            return (null, Refused(site, denied), [site], null);
        }

        var gate = new WebFetchGate(address => Task.FromResult(step.Rules.Fill(address)), _ => Task.CompletedTask)
        {
            Pace = async token => (await _pace.TurnAsync(source, token).ConfigureAwait(false))?.Until,
        };

        var directory = OrchestratorLayout.WebIndexDirectory(step.Repository, source.Name);

        return (new WebSearchResponse.Indexed(_indexes.Search(directory, request.Settings, request.Query, request.Limit, gate)), null, [site], null);
    }

    /// <summary>
    /// Holds <paramref name="source"/> on this machine until <paramref name="until"/>, a pause its own answer asked for,
    /// where it asked one: in a limit's own words where <paramref name="answer"/> is one, as <paramref name="step"/> may be
    /// told them - every run on the machine reads them - else as <paramref name="asker"/> asking; an answer that could not
    /// be read is never the reason a later query is told.
    /// </summary>
    private void Pause(WebSearchStep step, WebSearchSource source, DateTimeOffset? until, WebSearchAnswer? answer, string asker)
    {
        if (until is { } held)
        {
            _pace.Pause(source.Name, held, answer is { Outcome: WebSearchOutcome.RateLimited, Why: { } why } ? Told(step.Mask, why) : $"{asker} asked to be left alone until {WebRateLimit.Said(held)}");
        }
    }

    private static string Spent(WebSearchStep step)
        => $"this step's {step.MaxPerStep.ToString("N0", CultureInfo.InvariantCulture)} requests of the web search's sources are spent (agentWebSearch.maxPerStep)";
}

/// <summary>The rules a step's search is held to: its policy's, for an address a source asks, a page its index's fill reads, and a result it gives.</summary>
/// <param name="Source">Decides an address a source asks for the step: no web, and the <c>Web(...)</c> rules, never <c>webDomains</c>.</param>
/// <param name="Fill">Decides a page an index's fill reads: no web, and the run's <c>Web(...)</c> rules, never an agent's own.</param>
/// <param name="Result">Decides a result's address: whether the step could read it.</param>
public sealed record WebSearchRules(Func<string, AgentPolicyAnswer> Source, Func<string, AgentPolicyAnswer> Fill, Func<string, AgentPolicyAnswer> Result);

/// <summary>What records a step's search: each address decided and answered, and each source's part.</summary>
public interface IWebSearchRecords
{
    /// <summary>
    /// Records an address <paramref name="source"/> asks, as it was decided, before it is asked: a refused one at once, an
    /// allowed one once its turn is taken - a turn its pace refuses is no request, and records none.
    /// </summary>
    Task DecidedAsync(string source, string address, AgentPolicyAnswer answer);

    /// <summary>Records what an allowed address of <paramref name="source"/>'s was answered.</summary>
    Task AnsweredAsync(string source, WebHop hop);

    /// <summary>Records one source's part of a search.</summary>
    Task SearchedAsync(WebSearchPart part);
}

/// <summary>A source's key as a step was given it: what is sent, or why there is none - which the step's record says, and the source is asked without.</summary>
/// <param name="Value">The key; the step's mask holds every form it is sent in (<see cref="WebSearchCredential.Forms"/>).</param>
/// <param name="Missing">Why there is none; <see langword="null"/> where there is one.</param>
public sealed record WebSearchKey(string? Value, string? Missing)
{
    /// <summary>The key by whether it is there alone, so no text made of it holds the value.</summary>
    public override string ToString() => Value is null ? $"no key: {Missing}" : "a key";
}

/// <summary>One step's web search: its sources, bounds, rules and records, and the requests it has made.</summary>
public sealed class WebSearchStep
{
    private int _spent;

    /// <summary>The search of a step.</summary>
    /// <param name="sources">The step's sources, in its order, those turned on.</param>
    /// <param name="resultsPerSource">The most results a source gives one search.</param>
    /// <param name="maxPerStep">The most requests of sources the step makes.</param>
    /// <param name="runDirectory">The run's directory, whose record its searches share.</param>
    /// <param name="repository">The repository, whose index of a sitemap source's site it searches.</param>
    /// <param name="mask">The step's mask: it holds every form each key in <paramref name="keys"/> is sent in (<see cref="WebSearchCredential.Forms"/>).</param>
    /// <param name="keys">Each source's key, by its name, where the step was given one.</param>
    /// <param name="rules">The step's rules.</param>
    /// <param name="records">Its records.</param>
    /// <exception cref="ArgumentException">The mask does not hold a form of a key: a record would write it.</exception>
    public WebSearchStep(IReadOnlyList<WebSearchSource> sources, int resultsPerSource, int maxPerStep, string runDirectory, HarnessLayout repository, ILineMask mask, IReadOnlyDictionary<string, WebSearchKey> keys, WebSearchRules rules, IWebSearchRecords records)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfLessThan(resultsPerSource, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerStep, 1);

        if (keys.FirstOrDefault(pair => pair.Value.Value is { } value
            && (sources.FirstOrDefault(source => source.Name == pair.Key)?.Credential?.Forms(value) ?? [value]).Any(form => !mask.Holds(form))) is { Key: { } unmasked })
        {
            throw new ArgumentException($"The key of the source '{unmasked}', in a form it is sent in, is not one the step's mask holds, and its records would write it.", nameof(mask));
        }

        Sources = sources;
        ResultsPerSource = resultsPerSource;
        MaxPerStep = maxPerStep;
        RunDirectory = runDirectory;
        Repository = repository;
        Mask = mask;
        Keys = keys;
        Rules = rules;
        Records = records;
    }

    /// <summary>The step's sources, in its order.</summary>
    public IReadOnlyList<WebSearchSource> Sources { get; }

    /// <summary>The most results a source gives one search.</summary>
    public int ResultsPerSource { get; }

    /// <summary>The most requests of sources the step makes.</summary>
    public int MaxPerStep { get; }

    /// <summary>The run's directory.</summary>
    public string RunDirectory { get; }

    /// <summary>The repository.</summary>
    public HarnessLayout Repository { get; }

    /// <summary>The step's mask.</summary>
    public ILineMask Mask { get; }

    /// <summary>Each source's key, by its name.</summary>
    public IReadOnlyDictionary<string, WebSearchKey> Keys { get; }

    /// <summary>The step's rules.</summary>
    public WebSearchRules Rules { get; }

    /// <summary>Its records.</summary>
    public IWebSearchRecords Records { get; }

    /// <summary>How many requests of sources the step's searches have made.</summary>
    public int Spent => Volatile.Read(ref _spent);

    /// <summary>Counts one request against the bound, never past it: whether it may be made.</summary>
    internal bool Take()
    {
        while (true)
        {
            var spent = Volatile.Read(ref _spent);

            if (spent >= MaxPerStep)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _spent, spent + 1, spent) == spent)
            {
                return true;
            }
        }
    }

    /// <summary>Gives back what <see cref="Take"/> counted for a request that was never made: a turn the source's pace refused.</summary>
    internal void GiveBack() => Interlocked.Decrement(ref _spent);
}

/// <summary>One source's part of a search: what it answered, and how.</summary>
/// <param name="Source">The source's name.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Version">Its kind's version.</param>
/// <param name="Query">The query.</param>
/// <param name="Answer">What it answered, as the agent is given it.</param>
/// <param name="Withheld">How many of its results the step's web rules left out.</param>
/// <param name="Cached">Whether it was answered from what the run asked before, with no request.</param>
/// <param name="KeyMissing">Why it was asked without its key, where it names one and the step had none.</param>
public sealed record WebSearchPart(string Source, string Kind, int Version, string Query, WebSearchAnswer Answer, int Withheld, bool Cached, string? KeyMissing);

/// <summary>What a search came to: each source's part, in the step's order, or why it asked none.</summary>
/// <param name="Query">The query.</param>
/// <param name="Parts">Each source's part.</param>
/// <param name="Refusal">Why no source was asked; <see langword="null"/> where they were.</param>
public sealed record WebSearched(string Query, IReadOnlyList<WebSearchPart> Parts, string? Refusal)
{
    /// <summary>The rule that refused the search, where a rule did - the mask - rather than its input; <see langword="null"/> otherwise.</summary>
    public string? RefusedBy { get; init; }

    /// <summary>Whether any source answered - results, or none: a search none of whose sources answered is no answer.</summary>
    public bool Answered => Parts.Any(part => part.Answer.Outcome is WebSearchOutcome.Ok or WebSearchOutcome.NoResults);

    /// <summary>
    /// The answer as the agent is given it: each source's part in turn, inside a block that says it is from the web - its
    /// results, or what it came to, which may quote what the source said.
    /// </summary>
    public string Text
        => Refusal ?? string.Join("\n\n", Parts.Select(Said));

    /// <summary>A search that asked no source, for <paramref name="why"/> - refused by <paramref name="rule"/>, where a rule refused it.</summary>
    internal static WebSearched Refused(string why, string? rule = null) => new(string.Empty, [], why) { RefusedBy = rule };

    private static string Said(WebSearchPart part)
    {
        var answer = part.Answer;

        if (answer.Outcome == WebSearchOutcome.Ok)
        {
            var text = new StringBuilder();

            for (var at = 0; at < answer.Results.Count; at++)
            {
                var result = answer.Results[at];
                text.Append(CultureInfo.InvariantCulture, $"{at + 1}. {result.Title}\n   {result.Link}\n");

                if (result.Snippet.Length > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $"   {result.Snippet}\n");
                }
            }

            if (Notes(part) is { } notes)
            {
                text.Append(notes).Append('\n');
            }

            return WebBlocks.Search(part.Query, part.Source, text.ToString().TrimEnd('\n'));
        }

        return WebBlocks.Search(part.Query, part.Source, answer.Outcome switch
        {
            WebSearchOutcome.NoResults => $"no results for '{part.Query}' from {part.Source}{(Notes(part) is { } notes ? $" {notes}" : string.Empty)}",
            WebSearchOutcome.Changed => $"{part.Source}'s answer could not be read (it changed); nothing was read from it: {answer.Why}",
            WebSearchOutcome.Refused => $"{part.Source} was refused: {answer.Why}",
            _ => $"{part.Source}: {answer.Why}",
        });
    }

    private static string? Notes(WebSearchPart part)
    {
        var answer = part.Answer;
        var notes = new List<string>();

        if (answer.Total is { } total)
        {
            notes.Add($"{total.ToString("N0", CultureInfo.InvariantCulture)} in all");
        }

        if (part.Withheld > 0)
        {
            notes.Add($"{part.Withheld.ToString(CultureInfo.InvariantCulture)} left out by this step's web rules");
        }

        if (answer.LeftOut > 0)
        {
            notes.Add($"{answer.LeftOut.ToString(CultureInfo.InvariantCulture)} that named no title or address, or one holding a secret, left out");
        }

        if (answer.Held is { } held)
        {
            notes.Add(held);
        }

        return notes.Count == 0 ? null : $"({string.Join("; ", notes)})";
    }
}
