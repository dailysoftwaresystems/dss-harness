using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents;

/// <summary>
/// Where a run records what each key spent, for the next step's choice of one (<c>balanceKeyUsage</c>, part 03's "A
/// provider's keys, and balancing them"): <c>key-spend.jsonl</c> in the repository's records across runs, one line for
/// a run's every entry - the time, the provider, the key's name (never its value), the model, the cost. A key's week
/// is read for the balancing, and the lines older than seven days are dropped when the file is next written; what the
/// same key spent in another repository, on another machine or in another tool is not seen.
/// </summary>
/// <param name="fileSystem">The file system the record is kept on.</param>
/// <param name="store">Runs the record's reads and writes in the repository's section across runs, so two engines never lose one a line.</param>
/// <param name="clock">Tells the time each line is written and the week it is read for.</param>
public sealed class AgentKeySpend(IFileSystem fileSystem, OrchestrationStore store, TimeProvider clock)
{
    /// <summary>The record's name, in the repository's orchestrators directory.</summary>
    public const string FileName = "key-spend.jsonl";

    /// <summary>How far back a key's spend is counted: the user's "this week (it's weekly)".</summary>
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The record's path, in the repository's orchestrators directory beside the section that guards it.</summary>
    /// <param name="layout">The repository.</param>
    public static string FileOf(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.OrchestratorsDirectory, FileName);
    }

    /// <summary>
    /// What each of the keys spent in the last seven days, counted back from now: by provider and the key's name,
    /// whatever tier, model, step, run or side-task spent it. Read in the repository's section across runs, so a line
    /// another engine is writing is never half-read.
    /// </summary>
    /// <param name="layout">The repository whose record is read.</param>
    public IReadOnlyDictionary<(string Provider, string Key), decimal> Spent(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var now = _clock.GetUtcNow();

        return _store.AcrossRuns(layout, () => Read(_fileSystem, FileOf(layout), now));
    }

    /// <summary>
    /// The recorder a run hands its step's request: one line for the run's every entry, written whole in the
    /// repository's section across runs with the lines older than the week dropped from the file first.
    /// </summary>
    /// <param name="layout">The repository whose record is written.</param>
    public IAgentSpendSink RecordingIn(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new Recorder(this, layout);
    }

    /// <summary>
    /// The file read: each line within the week of <paramref name="now"/> summed by provider and key, a line that does
    /// not read skipped - the record is evidence, and one a person or a tool broke stops nothing.
    /// </summary>
    private static Dictionary<(string Provider, string Key), decimal> Read(IFileSystem fileSystem, string path, DateTimeOffset now)
    {
        var spent = new Dictionary<(string Provider, string Key), decimal>();

        if (!fileSystem.FileExists(path))
        {
            return spent;
        }

        foreach (var line in Lines(fileSystem.ReadAllText(path)))
        {
            if (LineOf(line) is not { } record || now - record.At > Week)
            {
                continue;
            }

            var by = (record.Provider, record.Key);
            spent[by] = spent.GetValueOrDefault(by) + record.Cost;
        }

        return spent;
    }

    /// <summary>The file's lines, each once, an empty or torn tail left out.</summary>
    private static string[] Lines(string text)
        => text.Length == 0 ? [] : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>One line of the record, read; null where it does not read.</summary>
    private static (DateTimeOffset At, string Provider, string Key, string Model, decimal Cost)? LineOf(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not { } read)
            {
                return null;
            }

            var at = read["time"]?.GetValue<string>();
            var provider = read["provider"]?.GetValue<string>();
            var key = read["key"]?.GetValue<string>();
            var model = read["model"]?.GetValue<string>();
            var cost = read["cost"];

            if (at is null || provider is null || key is null || model is null || cost is not JsonValue value || !value.TryGetValue<decimal>(out var counted))
            {
                return null;
            }

            return (DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), provider, key, model, counted);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// One run's recorder: each line appended in the section across runs, with the lines older than the week dropped
    /// from the file first - the record never grows past what the balancing reads of it.
    /// </summary>
    private sealed class Recorder(AgentKeySpend spend, HarnessLayout layout) : IAgentSpendSink
    {
        private readonly AgentKeySpend _spend = spend;
        private readonly HarnessLayout _layout = layout;

        public void Record(string provider, string? key, string model, decimal cost)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            ArgumentOutOfRangeException.ThrowIfNegative(cost);

            if (key is not { Length: > 0 })
            {
                // A client that signs in with the machine's login reads no key, and nothing of the login is counted.
                return;
            }

            var now = _spend._clock.GetUtcNow();
            var path = FileOf(_layout);

            _ = _spend._store.AcrossRuns(_layout, () =>
            {
                var kept = new StringBuilder();

                if (_spend._fileSystem.FileExists(path))
                {
                    foreach (var line in Lines(_spend._fileSystem.ReadAllText(path)))
                    {
                        if (LineOf(line) is not { } record || now - record.At <= Week)
                        {
                            kept.AppendLine(line);
                        }
                    }
                }

                kept.AppendLine(new JsonObject
                {
                    ["time"] = now.ToString("O", CultureInfo.InvariantCulture),
                    ["provider"] = provider,
                    ["key"] = key,
                    ["model"] = model,
                    ["cost"] = cost,
                }.ToJsonString());

                _spend._fileSystem.WriteAllTextAtomic(path, kept.ToString());
                return 0;
            });
        }
    }
}

/// <summary>Where a run's step records what each key spent: one line for the run's every entry, by the key's name and never its value.</summary>
public interface IAgentSpendSink
{
    /// <summary>Records one entry's cost, spent by the key's name on the model.</summary>
    /// <param name="provider">The provider the key is of.</param>
    /// <param name="key">The key's name; null for the machine's login, which nothing is counted of.</param>
    /// <param name="model">The model the entry ran on.</param>
    /// <param name="cost">What it cost - what the client reported, where it does, else what the engine counted.</param>
    void Record(string provider, string? key, string model, decimal cost);
}
