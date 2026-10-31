using System.Globalization;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// The effort levels a side-task is asked for on its command line (value 418, as a step's <c>effort</c> is given): one
/// level for every model (<c>--effort high</c>), or one entry per model (<c>--effort glm-5.3=high</c>), with
/// <c>all=&lt;level&gt;</c> for the rest. The one reader of the form; whether a model takes the level it is given is
/// checked where the model is resolved.
/// </summary>
public static class SideTaskEffort
{
    /// <summary>The entry that stands for every model not named, as a step's effort map's does.</summary>
    public const string Every = "all";

    /// <summary>
    /// Reads the effort the person asked for: one level, or a map of model codes to levels with <c>all</c> for the rest.
    /// </summary>
    /// <param name="given">The <c>--effort</c> arguments, each one.</param>
    /// <returns>
    /// The one level where one was given, the map where entries were, and why neither reads where they do not: a level
    /// that says nothing, a map entry without <c>=</c>, a model code or a level that says nothing, one given twice, a
    /// bare level beside entries, and <c>all</c> given twice.
    /// </returns>
    public static (string? Level, IReadOnlyDictionary<string, string>? ByModel, string? Problem) Parse(IReadOnlyList<string> given)
    {
        ArgumentNullException.ThrowIfNull(given);

        string? level = null;
        Dictionary<string, string>? byModel = null;

        foreach (var one in given)
        {
            var at = one.Split('=', 2);

            if (at.Length == 1)
            {
                if (byModel is not null)
                {
                    return (null, null, $"'--effort {one}' is a bare level beside entries by model: give one level for every model, or one entry per model with all=<level> for the rest");
                }

                if (level is not null)
                {
                    return (null, null, $"'--effort {one}' gives a second level after '{level}': one level, or entries by model");
                }

                if (at[0].Trim().Length == 0)
                {
                    return (null, null, "'--effort ' gives a level that says nothing");
                }

                level = at[0].Trim();
                continue;
            }

            if (level is not null)
            {
                return (null, null, $"'--effort {one}' is an entry by model beside the level '{level}': give one level for every model, or one entry per model with all=<level> for the rest");
            }

            byModel ??= new Dictionary<string, string>(StringComparer.Ordinal);

            var code = at[0].Trim();
            var value = at[1].Trim();

            if (code.Length == 0 || value.Length == 0)
            {
                return (null, null, $"'--effort {one}' is no entry by model: code=level, each saying something - glm-5.3=high, all=medium");
            }

            if (byModel.ContainsKey(code))
            {
                return (null, null, $"'--effort {one}' gives '{code}' a second level; each model is given one");
            }

            byModel[code] = value;
        }

        return (level, byModel, null);
    }
}
