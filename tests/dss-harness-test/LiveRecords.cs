using System.Globalization;

namespace DssHarness.Tests;

/// <summary>
/// Where a person-run live smoke leaves its records for the person to read: the directory <see cref="RecordsVariable"/>
/// names, a directory of each run's under it named by the time it was copied. Nothing of it is ever committed.
/// </summary>
internal static class LiveRecords
{
    /// <summary>The variable naming the directory a smoke's records are copied to; nothing is copied where it is not set.</summary>
    public const string RecordsVariable = "DSSHARNESS_TESTS_LIVE_RECORDS";

    /// <summary>Copies every file under each source into the run's directory, under the name given with it - into the run's directory itself where that is empty.</summary>
    /// <param name="sources">Each directory to copy, and where under the run's directory it goes.</param>
    public static void Copy(params (string From, string Under)[] sources)
    {
        if (Environment.GetEnvironmentVariable(RecordsVariable) is not { Length: > 0 } records)
        {
            return;
        }

        var into = Path.Combine(records, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        foreach (var (from, under) in sources)
        {
            foreach (var file in Directory.Exists(from) ? Directory.GetFiles(from, "*", SearchOption.AllDirectories) : [])
            {
                var target = Path.Combine(into, under, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }
}
