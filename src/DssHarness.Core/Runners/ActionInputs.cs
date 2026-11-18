using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runners;

/// <summary>
/// An action's inputs given their values, for the steps one run runs on one machine, with every input that could not be
/// given one it takes.
/// </summary>
public sealed record ResolvedInputs
{
    /// <summary>No input, as a runner of phases of its own, which runs no action, has none.</summary>
    public static ResolvedInputs None { get; } = new()
    {
        Action = new Dictionary<string, string>(StringComparer.Ordinal),
        Steps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
    };

    /// <summary>The value of each input the action declares that has one, by name.</summary>
    public required IReadOnlyDictionary<string, string> Action { get; init; }

    /// <summary>Each step's own inputs that have a value, by step name; a step that declares none is absent.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Steps { get; init; }

    /// <summary>Each required input with no value, as a refusal names it: with its step, where it is a step's own.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>Each value its input's declaration refuses, as a refusal says it: the input, where the value came from, and why.</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];
}

/// <summary>
/// The one rule an action's inputs are given their values by - what <c>run --input</c> gave, else what the runner's
/// <c>.env</c> directory supplies under that name, else the input's own default - and held to what each declares: a
/// value when it is <c>required</c>, its <c>pattern</c>, and the items its <c>separator</c> parts.
/// </summary>
/// <remarks>
/// <para>
/// One owner, asked wherever the values are known, and always before a leg is admitted: by a run before any of its legs
/// is placed, synced, locked or admitted (<c>RunnerLegs</c>), from this machine's values for each leg it runs itself, and
/// again, with every other check its runner makes before its build, right before such a leg waits for its slot; by a
/// WSL distribution, from its own values, when the machine it runs on asks it before taking the leg's slot; by an ssh
/// host as its own run of the leg begins, before it admits the leg - so a run of a heavy runner is not refused only after
/// it waited for a slot of its machine; and by each leg as its runner starts (<see cref="RunnerRunService"/>), against the
/// values its own machine holds - the last line of defence, which no caller gets to assume another made. Resolved in two
/// places they drift, which is what shipped once: the load-time check knew the declared names while the expansion looked
/// somewhere else entirely, so a step naming a declared input passed the check, reached the program as the literal text
/// '{name}', and the leg reported passed.
/// </para>
/// <para>
/// Secrets are deliberately not a source. A value spliced into a command line reaches the process table, where anything
/// on the machine can read it; a secret reaches a step through the environment, which is what <c>.secrets</c> is for.
/// </para>
/// </remarks>
public static class ActionInputs
{
    /// <summary>
    /// The value of every input <paramref name="file"/> declares - the action's and each of its steps' own - with each
    /// required one that has none, and each value its declaration refuses.
    /// </summary>
    /// <param name="file">The action, holding only the steps one run runs on one machine.</param>
    /// <param name="given">What <c>run --input</c> gave, every name one the run's steps read.</param>
    /// <param name="values">
    /// What the runner's <c>.env</c> directory holds on the machine the steps run on, or <see langword="null"/> where they
    /// run on another machine, whose values are its own and not read here: then only what was given is resolved and held
    /// to its declaration - it is the value every machine uses - and an input given nothing is that machine's to resolve,
    /// a required one with none its to refuse.
    /// </param>
    public static ResolvedInputs Resolve(
        ActionFile file,
        IReadOnlyDictionary<string, string> given,
        IReadOnlyDictionary<string, string>? values)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(given);

        var missing = new List<string>();
        var refused = new List<string>();
        var action = Resolve(file.Inputs, given, values, missing, refused, step: null);

        // Each step's own, by the same rule - only the steps this run runs are in the file by now, so a step left out
        // never refuses a run over an input nothing gave it.
        var steps = file.Steps
            .Where(step => step.Inputs.Count > 0)
            .ToDictionary(
                step => step.Name,
                step => Resolve(step.Inputs, given, values, missing, refused, step.Name),
                StringComparer.Ordinal);

        return new ResolvedInputs { Action = action, Steps = steps, Missing = missing, Refused = refused };
    }

    /// <summary>Refuses a run in which an input is <see cref="ResolvedInputs.Missing"/> or <see cref="ResolvedInputs.Refused"/>.</summary>
    /// <param name="path">The action file, as the refusal names it.</param>
    /// <param name="valuesDirectory">Where the runner's plain values were read, as the refusal names it.</param>
    /// <param name="missing">Each required input with no value.</param>
    /// <param name="refused">Each value its input refuses.</param>
    /// <param name="redact">Masks any secret a value quoted in the refusal holds.</param>
    /// <exception cref="HarnessException">
    /// An input is missing or refused (<see cref="HarnessExit.UsageError"/>, as every value a run is given that the
    /// action cannot take is): every such input named in one refusal.
    /// </exception>
    public static void Require(
        string path,
        string valuesDirectory,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> refused,
        Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(redact);

        if (missing.Count == 0 && refused.Count == 0)
        {
            return;
        }

        var said = new List<string>();

        if (missing.Count > 0)
        {
            said.Add($"'{path}' requires input(s) {string.Join(", ", missing)}, and none was given with "
                + $"{CommandLineInputs.Option}, found in {valuesDirectory} or declared as a default, so its "
                + "steps would run with nothing where a value belongs.");
        }

        if (refused.Count > 0)
        {
            said.Add($"'{path}' declares what its inputs take, and this run's are not that: "
                + $"{redact(string.Join("; ", refused))}.");
        }

        throw new HarnessException(HarnessExit.UsageError, $"{string.Join(" ", said)} Nothing was run.");
    }

    /// <summary>
    /// Refuses a step that names, in a run line or its working directory, an input the file declares - the action's, or
    /// the step's own - to which this run gave no value, saying how it can have one.
    /// </summary>
    /// <param name="file">The steps one leg runs.</param>
    /// <param name="fillable">The names each step's lines can have filled in (<see cref="SuppliedTo"/>).</param>
    /// <param name="values">What the runner value directories hold, for what the refusal says of them.</param>
    /// <param name="valuesDirectory">Where the runner's plain values were read, as the refusal names it.</param>
    /// <param name="namedOnCommandLine">Whether the command line names the runner, and so reaches it with <c>--input</c>.</param>
    /// <exception cref="HarnessException">
    /// A step names a declared input this run gave no value (<see cref="HarnessExit.ConfigInvalid"/>: the file names, where
    /// it runs a program, an input it lets go without a value - neither required nor given a default).
    /// </exception>
    /// <remarks>
    /// Asked apart from the refusal of a name nothing can fill in, and before it: that check is given only the names that
    /// have a value, so such an input was refused as a name nothing here can fill in, and the names that refusal listed
    /// left it out, which read as an input nobody declared. Asked before the tool policy as well: a program written as such
    /// an input is judged there as the braces it was written as, and refused as a program nobody declared. Asked where the
    /// values are known, as the inputs are given them: by a run before any leg it runs is placed, by a WSL distribution
    /// before this machine admits its leg, by an ssh host before it admits its own, and by each leg's runner as it starts.
    /// </remarks>
    public static void RefuseUnvalued(
        ActionFile file,
        Func<ActionStep, IEnumerable<string>> fillable,
        ActionValues values,
        string valuesDirectory,
        bool namedOnCommandLine)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fillable);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var step in file.Steps)
        {
            var unvalued = Unvalued(file, step, fillable(step));

            foreach (var (text, setting) in step.Written)
            {
                if (LegPathNames.NamesIn(text).FirstOrDefault(name => unvalued.Contains(name)) is { } name)
                {
                    var owner = step.Inputs.Any(input => input.Name == name) ? "the step's own input" : "an input of the action";

                    throw new HarnessException(
                        HarnessExit.ConfigInvalid,
                        $"{setting} names '{{{name}}}', {owner}, which has no value: {HowToGive(name, values, valuesDirectory, namedOnCommandLine)}");
                }
            }
        }
    }

    /// <summary>
    /// The inputs <paramref name="step"/> may name - the action's, then its own - that this run gave no value: those
    /// <paramref name="fillable"/> leaves out.
    /// </summary>
    /// <param name="file">The steps one leg runs.</param>
    /// <param name="step">The step.</param>
    /// <param name="fillable">The names the step's lines can have filled in.</param>
    public static IReadOnlyList<string> Unvalued(ActionFile file, ActionStep step, IEnumerable<string> fillable)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(step);

        return [.. file.Inputs.Concat(step.Inputs).Select(input => input.Name).Except(fillable, StringComparer.Ordinal)];
    }

    /// <summary>
    /// What <paramref name="step"/>'s lines can have filled in: the runner's plain values, with the action's inputs and the
    /// step's own over them, as <paramref name="resolved"/> gave them their values.
    /// </summary>
    /// <param name="values">What the runner value directories hold.</param>
    /// <param name="resolved">The inputs, given their values by <see cref="Resolve(ActionFile, IReadOnlyDictionary{string, string}, IReadOnlyDictionary{string, string})"/>.</param>
    /// <param name="step">The step.</param>
    public static IReadOnlyDictionary<string, string> SuppliedTo(ActionValues values, ResolvedInputs resolved, ActionStep step)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(step);

        return Supplied(values, ForStep(resolved.Action, resolved.Steps, step.Name));
    }

    /// <summary>
    /// The values a step's placeholders are filled from: the runner's own, and the inputs it can name over them. One map,
    /// because the check that refuses an unfillable name is given this same set: fed from two places they disagree, and a
    /// name the check accepted reached the program as literal text.
    /// </summary>
    /// <param name="values">What the runner value directories hold.</param>
    /// <param name="inputs">The inputs the step can name, already resolved.</param>
    public static IReadOnlyDictionary<string, string> Supplied(ActionValues values, IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            return values.Supplied;
        }

        var namable = new Dictionary<string, string>(values.Supplied, StringComparer.Ordinal);

        foreach (var (name, value) in inputs)
        {
            namable[name] = value;
        }

        return namable;
    }

    /// <summary>
    /// What the step <paramref name="stepName"/> can name: <paramref name="action"/>, with the step's own inputs over them;
    /// the action's alone for a step that declares none. The parser refuses a step input named like one of the action's,
    /// so neither ever hides the other.
    /// </summary>
    /// <param name="action">The action's inputs, resolved.</param>
    /// <param name="steps">Each step's own inputs, resolved, by step name.</param>
    /// <param name="stepName">The step.</param>
    public static IReadOnlyDictionary<string, string> ForStep(
        IReadOnlyDictionary<string, string> action,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> steps,
        string stepName)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(steps);

        if (!steps.TryGetValue(stepName, out var own) || own.Count == 0)
        {
            return action;
        }

        var merged = new Dictionary<string, string>(action, StringComparer.Ordinal);

        foreach (var (name, value) in own)
        {
            merged[name] = value;
        }

        return merged;
    }

    /// <summary>
    /// How the input <paramref name="name"/> can have a value on this run, as far as this run can say.
    /// </summary>
    /// <remarks>
    /// Said from what the run read rather than as one sentence for every run, since that sentence was wrong in each of
    /// these: a secret was offered the command line, where it would leak, and the plain values, which refuse a name the
    /// secrets hold; a runner a run check starts was offered <c>--input</c>, which reaches only the runner the command line
    /// names; a value written under another spelling was asked for where it already was; and "the runner value
    /// directories" said neither which of the two, nor on which machine.
    /// </remarks>
    private static string HowToGive(string name, ActionValues values, string directory, bool namedOnCommandLine)
    {
        if (values.SecretNames.FirstOrDefault(secret => string.Equals(secret, name, StringComparison.OrdinalIgnoreCase)) is { } secret)
        {
            return $"'{secret}' is one of the runner's secrets, and a secret never fills a run line, where the "
                + $"process table shows it to anything on the machine. Its step reads it from its environment, as {secret}.";
        }

        var ways = namedOnCommandLine
            ? $"give it one with {CommandLineInputs.Option} {name}=<value> or in {directory}, or declare a default for it."
            : $"give it one in {directory}, or declare a default for it. A run check started this runner, "
                + $"and {CommandLineInputs.Option} reaches only the runner the command line names.";

        // Matched exactly, as a run line's names are, so a value written under another spelling is there and not read.
        return values.Values.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) is { } spelled
            ? $"{ways} That directory holds '{spelled}', which is not '{name}' as written."
            : ways;
    }

    /// <summary>
    /// The value of every input in <paramref name="declared"/> that has one here, each value its declaration refuses added
    /// to <paramref name="refused"/>, and each required one with none to <paramref name="missing"/> - named with its step,
    /// where it is one of a step's.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Resolve(
        IEnumerable<ActionInput> declared,
        IReadOnlyDictionary<string, string> given,
        IReadOnlyDictionary<string, string>? values,
        List<string> missing,
        List<string> refused,
        string? step)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in declared)
        {
            var (value, from) = given.TryGetValue(input.Name, out var typed) ? (typed, $"given with {CommandLineInputs.Option}")
                : values is null ? (null, null)
                : values.TryGetValue(input.Name, out var supplied) ? (supplied, "from the runner's .env")
                : input.Default is { } fallback ? (fallback, "its default")
                : (null, null);

            if (value is null)
            {
                // Another machine's values are its own: an input given nothing is that machine's to resolve.
                if (input.Required && values is not null)
                {
                    missing.Add(step is null ? input.Name : $"{input.Name} (step '{step}')");
                }

                continue;
            }

            resolved[input.Name] = value;

            if (input.Refusal(value) is { } refusal)
            {
                refused.Add($"input '{input.Name}'{(step is null ? string.Empty : $" of step '{step}'")}, {from}, {refusal}");
            }
        }

        return resolved;
    }
}
