using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;

namespace DssHarness.Core.Application.Legs;

/// <summary>
/// Proves each selected leg's tests can fail: the use case the <c>check-mutations</c> command runs, and a step of an
/// orchestrator run will call - the option rules of a sweep and a listing, and what a dispatched leg's host was given,
/// decided once, here, before any configuration is read.
/// </summary>
public interface IMutationLegs : IUseCase
{
    /// <summary>Sweeps every selected leg and answers what the run reached.</summary>
    /// <param name="request">What was asked.</param>
    /// <param name="run">The run, begun before anything could refuse it.</param>
    /// <param name="cancellationToken">Stops the sweep.</param>
    /// <returns>What the run reached, in the terms its caller answers in.</returns>
    Task<LegRunResult> RunAsync(MutationLegsRequest request, RunId run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists, without sweeping, each selected arm's last verdict on each selected leg: asks no host, takes no lock,
    /// begins no run and writes nothing.
    /// </summary>
    /// <param name="request">What was asked.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>
    /// The listing, rendered as the command always answers it, and typed: each leg's arms, the registry, the record's
    /// directory and the records that could not be read (<see cref="ArmListing"/>).
    /// </returns>
    Task<UseCaseResult<ArmListing>> ListAsync(MutationLegsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What a sweep or a listing was asked: the arms, and the three shapes a sweep can take.</summary>
public sealed record MutationLegsRequest : LegCommandRequest
{
    /// <summary>Only these arms of the registry - with a self-test, of the fixture's - by id; <see langword="null"/> for every arm.</summary>
    public IReadOnlyList<string>? ArmNames { get; init; }

    /// <summary>Sweep the fixture this tool carries instead of the registry's arms.</summary>
    public bool SelfTest { get; init; }

    /// <summary>Sweep nothing: say each selected arm's last verdict and whether what it was judged on has moved.</summary>
    public bool List { get; init; }

    /// <summary>Drive on each leg only the arms with no recorded passed that still holds.</summary>
    public bool Pending { get; init; }

    /// <summary>
    /// What the record of verdicts holds of one leg's arms, as bytes read as their base64 text: given to a host sweeping
    /// one leg of a run by the machine that asked, and typed by nobody.
    /// </summary>
    public string? Held { get; init; }
}

/// <summary>
/// Proves each selected leg's tests can fail: in worker copies of its tree, mutate each arm the registry declares, build
/// it, witness every object depending on the site rebuilt, run its test binary whole, and judge what reddened against
/// what the arm declares.
/// </summary>
public sealed class MutationLegs(MutationService sweeps) : IMutationLegs
{
    private readonly MutationService _sweeps = sweeps;

    /// <inheritdoc/>
    public async Task<LegRunResult> RunAsync(MutationLegsRequest request, RunId run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);

        if (Refused(request) is { } refusal)
        {
            throw new HarnessException(HarnessExit.UsageError, refusal);
        }

        return await _sweeps.ExecuteAsync(Request(request), run, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UseCaseResult<ArmListing>> ListAsync(MutationLegsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Refused(request) is { } refusal)
        {
            throw new HarnessException(HarnessExit.UsageError, refusal);
        }

        return UseCaseResult<ArmListing>.From(await _sweeps.ListAsync(Request(request), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// What <paramref name="request"/> was given as <c>--held</c>, decoded, or <see langword="null"/> where it was given
    /// none: bytes the machine that dispatched this sweep carried, read here as their base64 text.
    /// </summary>
    private static RecordedArms? Held(MutationLegsRequest request)
        => request.Held is { } carried ? RecordedArms.OfCarried(Convert.FromBase64String(carried)) : null;

    private static MutationRequest Request(MutationLegsRequest request)
        => new(
            request.Directory,
            request.LegNames,
            request.ArmNames,
            request.ForceLock,
            request.Json,
            request.UseStaged,
            request.Here,
            request.SelfTest,
            request.List,
            request.Pending,
            Held(request))
        {
            Watch = request.Watch,
        };

    /// <summary>
    /// Why what <paramref name="request"/> holds cannot be given together, or <see langword="null"/> where it can: a
    /// self-test under <c>--pending</c>, which nothing of was ever recorded; <c>--held</c> where no machine dispatched
    /// this sweep, since what the record holds is this machine's to read where it was typed; and <c>--list</c> with each
    /// option only a sweep has a use for. Each said in one sentence.
    /// </summary>
    private static string? Refused(MutationLegsRequest request)
    {
        if (!request.List)
        {
            if (request.Pending && request.SelfTest)
            {
                return "A self-test is never recorded, so nothing of it is pending: leave --pending out.";
            }

            return request.Held is not null && request.Here is null
                ? "--held is given by the machine that dispatches a leg's sweep to the host that sweeps it, so it needs --here: leave it out."
                : null;
        }

        (string Option, bool Given, string Wants)[] sweepsOnly =
        [
            ("--pending", request.Pending, "has no arm to sweep"),
            ("--self-test", request.SelfTest, "has no fixture to sweep"),
            ("--force-lock", request.ForceLock, "has no lock to take"),
            ("--use-staged", request.UseStaged, "has nothing staged to sweep"),
            ("--held", request.Held is not null, "is given to a host sweeping one leg of a run"),
            (RemoteLegRunner.HereOption, request.Here is not null, "has no leg to sweep for another machine"),
        ];

        return sweepsOnly.FirstOrDefault(option => option.Given) is { Option: { } given } named
            ? $"--list sweeps nothing, so {given} {named.Wants}: leave one of them out."
            : null;
    }
}
