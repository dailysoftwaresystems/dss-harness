namespace RepoHarness.Core.Mutations;

/// <summary>
/// Where a leg's sweep keeps its records, within the leg's directory in the run's: each arm's, each test binary's control's,
/// and each worker's whole build's - named once, for the sweep that writes them and the help that points at them.
/// </summary>
public static class MutationRecords
{
    /// <summary>Where a leg's arms keep their records, under its directory in the run's, one directory to an arm.</summary>
    public const string ArmsDirectory = "arms";

    /// <summary>Where a leg's pristine controls keep theirs, one directory to a test binary.</summary>
    public const string ControlsDirectory = "controls";

    /// <summary>Where a leg's workers keep the logs of their whole builds, one directory to a worker.</summary>
    public const string WorkersDirectory = "workers";

    /// <summary>Where a BUILD-RED arm's paired control keeps its build's logs, within the arm's records.</summary>
    public const string PairedControlDirectory = "control";

    /// <summary>The record each arm leaves among its records: its line, as the ledger carries it.</summary>
    public const string ArmRecordFileName = "arm.json";
}
