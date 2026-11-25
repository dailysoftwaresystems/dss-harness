using System.Globalization;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What a number of the report measures, which decides the form its value is said in (<see cref="FlowRunReport.SaidIn"/>).</summary>
public enum FlowUnitKind
{
    /// <summary>A time in seconds: the report's every duration but its budget's minutes.</summary>
    Seconds,

    /// <summary>A time in minutes: the budget's reasoning.</summary>
    Minutes,

    /// <summary>United States dollars, the currency every cost of the report is in.</summary>
    Dollars,

    /// <summary>A share in percent.</summary>
    Percent,

    /// <summary>A count of tokens.</summary>
    Tokens,

    /// <summary>A count of something else, said with what it counts after it: <c>2 tries</c>.</summary>
    Counted,

    /// <summary>A number that is no amount - an exit code, an attempt's place, a line - said after its label: <c>exit 20</c>.</summary>
    Labelled,
}

/// <summary>
/// The unit a number of the report is in: what it measures (<see cref="FlowUnitKind"/>) and, for a count or a labelled
/// number, the words it is said with - so no value of the report is ever said as a bare number a word beside it could
/// claim (<see cref="FlowRunReport.SaidIn"/>).
/// </summary>
/// <param name="Kind">What it measures.</param>
/// <param name="One">What one of a count is called (<c>try</c>), or a labelled number's label (<c>exit</c>); null where a labelled number is said after the path of the member that holds it.</param>
/// <param name="Many">What more than one of a count are called (<c>tries</c>); null for any other unit.</param>
public sealed record FlowUnit(FlowUnitKind Kind, string? One = null, string? Many = null)
{
    /// <summary>A time in seconds.</summary>
    public static FlowUnit Seconds { get; } = new(FlowUnitKind.Seconds);

    /// <summary>A time in minutes.</summary>
    public static FlowUnit Minutes { get; } = new(FlowUnitKind.Minutes);

    /// <summary>A cost in the report's dollars.</summary>
    public static FlowUnit Dollars { get; } = new(FlowUnitKind.Dollars);

    /// <summary>A share in percent.</summary>
    public static FlowUnit Percent { get; } = new(FlowUnitKind.Percent);

    /// <summary>A count of tokens.</summary>
    public static FlowUnit Tokens { get; } = new(FlowUnitKind.Tokens);

    /// <summary>A number said after the path of the member that holds it, as the report's text says a member of an object: <c>firstError line 3</c>.</summary>
    public static FlowUnit ByItsPath { get; } = new(FlowUnitKind.Labelled);

    /// <summary>A count of what <paramref name="one"/> and <paramref name="many"/> call it: <c>1 try</c>, <c>2 tries</c>.</summary>
    /// <param name="one">What one of it is called.</param>
    /// <param name="many">What more than one are called.</param>
    public static FlowUnit Of(string one, string many) => new(FlowUnitKind.Counted, one, many);

    /// <summary>A number that is no amount, said after <paramref name="label"/>: <c>exit 20</c>.</summary>
    /// <param name="label">What it is.</param>
    public static FlowUnit Labelled(string label) => new(FlowUnitKind.Labelled, label);
}

/// <summary>The units of the report's numbers, beside the builder that writes them.</summary>
public static partial class FlowRunReport
{
    /// <summary>
    /// The unit of every number the sections an analysis may cite hold, keyed by its member's path: its section, a list's
    /// rows marked <c>[]</c> and the members inside joined by '.' - <c>theRun.wallSeconds</c>, <c>steps[].usd</c>,
    /// <c>totals.dollarsBy.groups[].usd</c>. Every count names what it counts and every number that is no amount its label,
    /// so a value is never said bare. An entry of a member that holds an object covers every number inside it but where an
    /// entry of its own says otherwise: a failed attempt's exception and first error, whose numbers are each said after
    /// their own path (<see cref="FlowUnit.ByItsPath"/>). An entry of a text member is the unit of the one number it holds
    /// whole, as the report writes it: a step's <c>ran</c>, in seconds, a hotspot's touches and a class's reopens. The one
    /// owner of a cited number's unit an analysis's impact is written in (<see cref="FlowAnalysisEvidence"/>), held to
    /// every number the builder writes by the report's tests.
    /// </summary>
    public static IReadOnlyDictionary<string, FlowUnit> Units { get; } = new Dictionary<string, FlowUnit>(StringComparer.Ordinal)
    {
        ["theRun.sessions"] = FlowUnit.Of("session", "sessions"),
        ["theRun.wallSeconds"] = FlowUnit.Seconds,
        ["theRun.activeSeconds"] = FlowUnit.Seconds,
        ["theRun.pausedSeconds"] = FlowUnit.Seconds,
        ["theRun.waitingForAPersonSeconds"] = FlowUnit.Seconds,
        ["theRun.budget.usd"] = FlowUnit.Dollars,
        ["theRun.budget.tokens"] = FlowUnit.Tokens,
        ["theRun.budget.minutes"] = FlowUnit.Minutes,
        ["totals.tokens"] = FlowUnit.Tokens,
        ["totals.tokens.searches"] = FlowUnit.Of("search", "searches"),
        ["totals.cacheSharePercent"] = FlowUnit.Percent,
        ["totals.dollarsBy.groups[].usd"] = FlowUnit.Dollars,
        ["totals.dollarsBy.groups[].tokens"] = FlowUnit.Tokens,
        ["totals.usd"] = FlowUnit.Dollars,
        ["totals.agents"] = FlowUnit.Of("agent", "agents"),
        ["totals.stepsRun"] = FlowUnit.Of("step run", "steps run"),
        ["totals.stepsSkipped"] = FlowUnit.Of("step skipped", "steps skipped"),
        ["totals.stepsFailed"] = FlowUnit.Of("step failed", "steps failed"),
        ["totals.retries"] = FlowUnit.Of("retry", "retries"),
        ["totals.fallbacks"] = FlowUnit.Of("fallback", "fallbacks"),
        ["totals.graph.steps"] = FlowUnit.Of("step given a graph", "steps given a graph"),
        ["totals.graph.packTokens"] = FlowUnit.Tokens,
        ["totals.graph.calls"] = FlowUnit.Of("graph call", "graph calls"),
        ["totals.graph.servedTokens"] = FlowUnit.Tokens,
        ["totals.graph.bodies"] = FlowUnit.Of("body served", "bodies served"),
        ["totals.graph.textHits"] = FlowUnit.Of("text hit", "text hits"),
        ["totals.graph.edits.made"] = FlowUnit.Of("graph edit made", "graph edits made"),
        ["totals.graph.edits.refused"] = FlowUnit.Of("graph edit refused", "graph edits refused"),
        ["totals.graph.wholeFileReads.count"] = FlowUnit.Of("whole-file read", "whole-file reads"),
        ["totals.graph.wholeFileReads.tokens"] = FlowUnit.Tokens,
        ["totals.graph.deniedReads"] = FlowUnit.Of("denied read", "denied reads"),
        ["totals.graph.escapes.count"] = FlowUnit.Of("graph escape", "graph escapes"),
        ["totals.graph.escapes.tokens"] = FlowUnit.Tokens,
        ["totals.graph.escapes.lowConfidence"] = FlowUnit.Of("low-confidence escape", "low-confidence escapes"),
        ["totals.graph.escapes.stated"] = FlowUnit.Of("stated escape", "stated escapes"),
        ["steps[].tries"] = FlowUnit.Of("try", "tries"),
        ["steps[].ran"] = FlowUnit.Seconds,
        ["steps[].tokens"] = FlowUnit.Tokens,
        ["steps[].usd"] = FlowUnit.Dollars,
        ["steps[].exit"] = FlowUnit.Labelled("exit"),
        ["steps[].graph.mapTokens"] = FlowUnit.Tokens,
        ["steps[].graph.pack.seeds"] = FlowUnit.Of("pack seed", "pack seeds"),
        ["steps[].graph.pack.nodes"] = FlowUnit.Of("pack node", "pack nodes"),
        ["steps[].graph.pack.tokens"] = FlowUnit.Tokens,
        ["steps[].graph.pack.used"] = FlowUnit.Of("pack node used", "pack nodes used"),
        ["steps[].graph.changesTokens"] = FlowUnit.Tokens,
        ["steps[].graph.calls"] = FlowUnit.Of("graph call", "graph calls"),
        ["steps[].graph.servedTokens"] = FlowUnit.Tokens,
        ["steps[].graph.bodies"] = FlowUnit.Of("body served", "bodies served"),
        ["steps[].graph.textHits"] = FlowUnit.Of("text hit", "text hits"),
        ["steps[].graph.edits.made"] = FlowUnit.Of("graph edit made", "graph edits made"),
        ["steps[].graph.edits.refused"] = FlowUnit.Of("graph edit refused", "graph edits refused"),
        ["steps[].graph.wholeFileReads.count"] = FlowUnit.Of("whole-file read", "whole-file reads"),
        ["steps[].graph.wholeFileReads.tokens"] = FlowUnit.Tokens,
        ["steps[].graph.deniedReads"] = FlowUnit.Of("denied read", "denied reads"),
        ["steps[].graph.escapes.count"] = FlowUnit.Of("graph escape", "graph escapes"),
        ["steps[].graph.escapes.tokens"] = FlowUnit.Tokens,
        ["steps[].graph.escapes.lowConfidence"] = FlowUnit.Of("low-confidence escape", "low-confidence escapes"),
        ["steps[].graph.escapes.stated"] = FlowUnit.Of("stated escape", "stated escapes"),
        ["criticalPath[].seconds"] = FlowUnit.Seconds,
        ["errors[].attempt"] = FlowUnit.Labelled("attempt"),
        ["errors[].exit"] = FlowUnit.Labelled("exit"),
        ["errors[].exception"] = FlowUnit.ByItsPath,
        ["errors[].firstError"] = FlowUnit.ByItsPath,
        ["people[].waitedSeconds"] = FlowUnit.Seconds,
        ["convergence.items[].reviewRounds"] = FlowUnit.Of("review round", "review rounds"),
        ["convergence.items[].homeAudits"] = FlowUnit.Of("home audit", "home audits"),
        ["convergence.items[].classes[].sitesConsolidated"] = FlowUnit.Of("site consolidated", "sites consolidated"),
        ["convergence.items[].classes[].sitesDeleted"] = FlowUnit.Of("site deleted", "sites deleted"),
        ["convergence.items[].classes[].cellsPinned"] = FlowUnit.Of("cell pinned", "cells pinned"),
        ["convergence.items[].classes[].neighboursMeasured"] = FlowUnit.Of("neighbour measured", "neighbours measured"),
        ["convergence.items[].classes[].neighboursUnmeasured"] = FlowUnit.Of("neighbour unmeasured", "neighbours unmeasured"),
        ["convergence.items[].classes[].sendBacks"] = FlowUnit.Of("send-back", "send-backs"),
        ["convergence.classesReopened[].reopens"] = FlowUnit.Of("reopen", "reopens"),
        ["convergence.hotspots[].touches"] = FlowUnit.Of("touch", "touches"),
    };

    /// <summary>
    /// The unit of the member at <paramref name="path"/> (<see cref="Units"/>): its own entry's, else that of the nearest
    /// member around it that has one; null where none does - a member the report measures nothing in.
    /// </summary>
    /// <param name="path">The member's path, a list's rows marked <c>[]</c>: <c>steps[].graph.pack.tokens</c>.</param>
    public static FlowUnit? UnitOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        for (var key = path; key.Length > 0; key = key.LastIndexOf('.') is var dot and >= 0 ? key[..dot] : string.Empty)
        {
            if (Units.TryGetValue(key, out var unit))
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="value"/>, a number of the report or a text that holds one whole, said in <paramref name="unit"/> as
    /// the report's own words say a value of that unit, by the same forms, and always with its unit: a time in seconds with
    /// one decimal (<c>412.3 s</c>), the budget's minutes with one (<c>90.0 minutes</c>), dollars with four and the
    /// currency (<c>0.0200 USD</c>), a share with one and its sign (<c>28.0%</c>), tokens as a count of them (<c>180000
    /// tokens</c>), any other count with what it counts (<c>2 tries</c>, <c>1 session</c>) and a number that is no amount
    /// after its label (<c>exit 20</c>) or, where its unit names none, after <paramref name="member"/>
    /// (<c>firstError line 3</c>) - the one way an analysis's impact says a figure it cites (<see cref="FlowAnalysisEvidence"/>).
    /// </summary>
    /// <param name="unit">The cited member's unit (<see cref="UnitOf"/>).</param>
    /// <param name="value">The cited value, as the report holds it.</param>
    /// <param name="member">The cited member's path below its row, as the citation writes it (<c>firstError.line</c>).</param>
    public static string SaidIn(FlowUnit unit, JsonValue value, string member)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(member);

        var number = double.Parse(value.TryGetValue<string>(out var text) ? text : value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

        return unit.Kind switch
        {
            FlowUnitKind.Seconds => SecondsSaid(number),
            FlowUnitKind.Minutes => MinutesOf(number),
            FlowUnitKind.Dollars => UsdOf(number),
            FlowUnitKind.Percent => PercentOf(number),
            FlowUnitKind.Tokens => TokensOf(value),
            FlowUnitKind.Counted => $"{Said(value)} {(number == 1 ? unit.One : unit.Many)}",
            _ => $"{unit.One ?? member.Replace('.', ' ')} {Said(value)}",
        };
    }
}
