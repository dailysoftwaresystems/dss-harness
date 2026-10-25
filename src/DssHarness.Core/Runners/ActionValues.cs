using DssHarness.Core.Secrets;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runners;

/// <summary>
/// The values an action reads: the plain ones, and the secret ones behind a door that has to be
/// opened deliberately.
/// </summary>
/// <remarks>
/// <para>
/// A secret's value is never a property. It is reachable only through <see cref="RevealSecrets"/>,
/// whose one legitimate caller is the code handing a child process its environment. Everything else
/// that formats a value — a log line, a rendered argument list, the message of a failure — goes
/// through <see cref="Redact(string?)"/>. A secret leaks by being convenient, not by being
/// attacked: the leak that costs a credential is a failed command echoing what it was asked to run.
/// </para>
/// <para>
/// <see cref="ToString"/> is overridden for the same reason. A record's generated one prints every
/// member, so this is a class, and its text says how many values there are and never what they are.
/// </para>
/// <para>
/// It is the mask a step's lines are kept through, too: its <see cref="Cut(string)"/> says where a line
/// too long to keep whole can be cut without parting a secret, by the same values it masks.
/// </para>
/// </remarks>
public sealed class ActionValues : ILineMask
{
    /// <summary>What a redacted secret is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>
    /// The plain values, by name, for a step's run line to name. Secrets are deliberately absent:
    /// a value spliced into a command line reaches the process table, where anything on the machine
    /// can read it, and the environment is how a secret is handed over instead.
    /// </summary>
    /// <remarks>
    /// Ordinal, unlike the environment these same values are handed over in. A name in a run line is
    /// matched exactly because the text around it belongs to a program: an action declaring an input
    /// called <c>home</c> must not have a step's <c>${HOME}</c> replaced with its value.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Supplied =>
        _supplied ??= new Dictionary<string, string>(_values, StringComparer.Ordinal);

    private Dictionary<string, string>? _supplied;

    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, string> _secrets;
    private readonly IReadOnlyList<string> _secretTexts;

    /// <summary>
    /// Builds the values an action reads.
    /// </summary>
    /// <param name="values">Plain values, by name.</param>
    /// <param name="secrets">Secret values, by name. Never exposed as a property.</param>
    public ActionValues(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(secrets);

        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        _secrets = new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase);

        // Longest first, so a secret that contains a shorter one does not leave its tail behind
        // after the shorter one has been masked. An empty value is not redactable: replacing it
        // would mask the whole text and say nothing about what was hidden.
        _secretTexts =
        [
            .. _secrets.Values
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length)
                .ThenBy(value => value, StringComparer.Ordinal),
        ];
    }

    /// <summary>No values at all, for a repository that declares none.</summary>
    public static ActionValues Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The plain values, by name. Safe to print.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The names of the secret values. The names are safe to print; the values are not.</summary>
    public IReadOnlyCollection<string> SecretNames => _secrets.Keys;

    /// <summary>Whether <paramref name="name"/> came from the secrets directory.</summary>
    /// <param name="name">The value's name.</param>
    public bool IsSecret(string name) => _secrets.ContainsKey(name);

    /// <summary>
    /// <paramref name="text"/> with every secret value replaced by <see cref="Mask"/>.
    /// </summary>
    /// <remarks>
    /// By value rather than by name, because what reaches a log is the value: a command line
    /// already has its placeholders substituted by the time anyone wants to print it.
    /// </remarks>
    /// <param name="text">Any text about to be shown, logged or thrown.</param>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var redacted = text;

        foreach (var secret in _secretTexts)
        {
            redacted = redacted.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return redacted;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A step that prints a line longer than a line is kept whole in - megabytes of a document on one line - has it kept in
    /// pieces, each masked alone, and a secret cut in two would reach the log, and the terminal of whoever watches with
    /// --verbose, as two halves neither of which is the secret. So the cut is made before the last characters of
    /// <paramref name="text"/> that could still be the start of a secret - as many as the longest secret, less one - and
    /// then moved back to the start of every secret <paramref name="text"/> holds that would still span it, until none does.
    /// Zero only where a secret is as long as the text itself, or secrets overlap all the way back to its start: a line is
    /// then cut where it must be, and only a secret that long can be parted.
    /// </remarks>
    public int Cut(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_secretTexts.Count == 0)
        {
            return text.Length;
        }

        // Longest first, as the secrets are ordered for masking.
        var cut = text.Length - (_secretTexts[0].Length - 1);

        for (var moved = true; moved && cut > 0;)
        {
            moved = false;

            foreach (var secret in _secretTexts)
            {
                // Only a secret beginning less than its own length before the cut can span it.
                var spanning = text.IndexOf(secret, Math.Max(0, cut - secret.Length + 1), StringComparison.Ordinal);

                if (spanning >= 0 && spanning < cut)
                {
                    cut = spanning;
                    moved = true;
                }
            }
        }

        return Math.Max(cut, 0);
    }

    /// <summary>Every element of <paramref name="arguments"/>, redacted.</summary>
    /// <remarks>
    /// Named apart from <see cref="Redact(string?)"/> rather than overloading it. The two overloads
    /// would be ambiguous for a null literal, and the call this exists for — rendering a command
    /// line for a log — is the one that must never be the call that failed to compile and got
    /// replaced by a plain join.
    /// </remarks>
    /// <param name="arguments">An argument list about to be shown or logged.</param>
    public IReadOnlyList<string> RedactAll(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return [.. arguments.Select(Redact)];
    }

    /// <summary>Whether any element of <paramref name="arguments"/> holds a secret value.</summary>
    /// <remarks>
    /// The check every built argument list is held to before the command it carries is started: a
    /// value given with an input, or held in a values directory, reaches the list all the same once
    /// its name is filled in, and the log header, the process table and any error quoting the
    /// command would all carry it. Handing a secret over is the environment's job.
    /// </remarks>
    /// <param name="arguments">The argument list a command is about to be started with.</param>
    public bool HoldsASecret(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return _secrets.Count > 0 && arguments.Any(argument => this.Holds(argument));
    }

    /// <summary>
    /// The secret values themselves.
    /// </summary>
    /// <remarks>
    /// The only deliberate way to the raw values, named so that it is conspicuous at the call site
    /// and in a review. The one legitimate caller is the code that hands a child process its
    /// environment, where the value has to be the real one. Anything that formats text calls
    /// <see cref="Redact(string?)"/> instead.
    /// </remarks>
    public IReadOnlyDictionary<string, string> RevealSecrets()
        => new Dictionary<string, string>(_secrets, StringComparer.OrdinalIgnoreCase);

    /// <summary>How many values there are, and never what they are.</summary>
    public override string ToString()
        => $"{ReportText.Counted(_values.Count, "value")}, {ReportText.Counted(_secrets.Count, "secret")}";
}

/// <summary>
/// Reads the values an action reads from the runner's two value directories.
/// </summary>
/// <remarks>
/// <c>.harness-config/runner/.env</c> and <c>.harness-config/runner/.secrets</c> are each a
/// directory of dotenv files, read by the one rule for such a directory (<see cref="DotEnvDirectories"/>):
/// every file merged, a name defined twice refused. The two are read as one set of names, since a
/// name in both would leave <see cref="ActionValues.Redact(string?)"/> masking one value while the
/// run used the other.
/// </remarks>
public sealed class ActionValuesReader(IFileSystem fileSystem, IHarnessOutput output)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Reads and merges both directories.
    /// </summary>
    /// <param name="envDirectory">The plain value directory. Absent means no plain values.</param>
    /// <param name="secretsDirectory">The secret value directory. Absent means no secrets.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="HarnessException">
    /// A file is malformed, or a name is defined more than once. Every problem is reported together.
    /// </exception>
    public Task<ActionValues> ReadAsync(
        string envDirectory,
        string secretsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);

        cancellationToken.ThrowIfCancellationRequested();

        var directories = new DotEnvDirectories(_fileSystem);
        var values = directories.Read(envDirectory, cancellationToken);
        var secrets = directories.Read(secretsDirectory, cancellationToken);
        var origins = directories.Origins;
        var problems = new List<string>(directories.Problems);

        // A plain value is one a run line can name, so one named like a name this tool fills in is
        // refused, as an input so named is: a run line naming it got this tool's value, or this one
        // on a leg with none there, while the environment held this one.
        foreach (var name in values.Keys.Where(name => LegPathNames.All.Contains(name, StringComparer.Ordinal)))
        {
            problems.Add($"'{origins[name]}' defines '{name}', a name this tool already fills in, so a run line "
                + $"naming '{{{name}}}' would get this tool's value, or this one on a leg with none, while the "
                + "environment held this one. Give it another name.");
        }

        if (problems.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"the runner value directories have {ReportText.Counted(problems.Count, "problem")}:"
                + $"{Environment.NewLine}{detail}");
        }

        // Names only. The count of secrets is worth seeing; a secret is not.
        _output.Detail(
            "run",
            $"read {ReportText.Counted(values.Count, "value")} and {ReportText.Counted(secrets.Count, "secret")} for this action");

        return Task.FromResult(new ActionValues(values, secrets));
    }
}
