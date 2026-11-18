using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;

namespace DssHarness.Tests;

/// <summary>
/// A machine's own settings file (<see cref="MachineSettingsFile"/>): read as strictly as config.json, its admission winning
/// field by field over what every tree's config.json declares for the machine, its count every leg's in line whatever count
/// a tree committed before gave the legs ahead, and the rule in force said by <c>legs</c> with where each field came from.
/// </summary>
public sealed class MachineSettingsTests
{
    private const string Refusal = "Until it can be, no heavy leg is admitted onto this machine by it, nor by any tree's config.json in its "
        + "place; put it right, or remove it to admit by config.json alone.";

    /// <summary>A settings file that is not there declares nothing, and a survey says where it would be.</summary>
    [Fact]
    public void ASettingsFileThatIsNotThere_DeclaresNothing_AndIsAnsweredAsWhereItWouldBe()
    {
        var file = HostDoubles.MachineSettings();

        Assert.Null(file.Read().Admission);
        Assert.Equal(new MachineSettingsAnswer(file.Location), file.Answer(path => path));
        Assert.EndsWith("settings-tests.json", file.Location, StringComparison.Ordinal);
    }

    /// <summary>The file is read in the keys config.json's admission takes, the new one among them, and nothing else is added.</summary>
    [Fact]
    public void ASettingsFile_IsReadInTheKeysConfigJsonsAdmissionTakes()
    {
        var file = Written("""{ "admission": { "heavyLegs": 3, "minFreeGiB": 4.5, "reclaimIdleBuilds": true } } // the machine's own""");

        var admission = file.Read().Admission!;

        Assert.Equal(
            (3, (double?)null, (List<int>?)null, (int?)null, (double?)null, 4.5, true),
            (admission.HeavyLegs, admission.MaxMemoryPercent, admission.SettleSeconds, admission.PollSeconds, admission.MaxWaitMinutes, admission.MinFreeGiB, admission.ReclaimIdleBuilds));
    }

    /// <summary>
    /// What config.json would refuse, the settings file refuses, naming it and what is wrong, never read past: a key it does
    /// not know - at the top or within admission - a key written twice, a value out of the bounds config.json holds, and no
    /// object at all. A machine whose own count could not be read is never admitted by a tree's in its place.
    /// </summary>
    [Theory]
    [InlineData("""{ "admision": { "heavyLegs": 3 } }""", "'admision' could not be mapped")]
    [InlineData("""{ "admission": { "heavyLeg": 3 } }""", "'heavyLeg' could not be mapped")]
    [InlineData("""{ "admission": { "heavyLegs": 3, "heavyLegs": 4 } }""", "Duplicate")]
    [InlineData("""{ "admission": { "heavyLegs": 0 } }""", "could not be read: admission heavyLegs must be at least 1, found 0. ")]
    [InlineData("""{ "admission": { "minFreeGiB": -1, "pollSeconds": 0 } }""", "could not be read: admission pollSeconds must be from 1 to 3600, found 0; admission minFreeGiB must be from 0 to 1024, found -1. ")]
    [InlineData("null", "could not be read: it holds null where an object is expected. ")]
    public void ASettingsFile_ThatConfigJsonWouldRefuse_IsRefused_NamingIt(string json, string problem)
    {
        var file = Written(json);

        var refused = Assert.Throws<ConfigException>(() => file.Read());

        Assert.StartsWith($"This machine's settings file '{file.Location}' could not be read: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(problem, refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(Refusal, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A settings file that is there and cannot be read is refused, naming it and why, never taken for one that declares
    /// nothing; a survey says the same, and declares nothing of it.
    /// </summary>
    [Fact]
    public void ASettingsFileThatCannotBeRead_IsRefused_NamingIt_AndASurveySaysWhy()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("settings-tests.json", """{ "admission": { "heavyLegs": 3 } }""");
        var file = new MachineSettingsFile(new UnreadableFile(new HarnessFactory().FileSystem, path), path);

        var refused = Assert.Throws<ConfigException>(() => file.Read());
        var answer = file.Answer(shown => shown.Replace(temp.Path, "~", StringComparison.Ordinal));

        Assert.Equal($"This machine's settings file '{path}' could not be read: the disk is unwell. {Refusal}", refused.Message);
        Assert.Equal(
            new MachineSettingsAnswer(path.Replace(temp.Path, "~", StringComparison.Ordinal), null, refused.Message.Replace(temp.Path, "~", StringComparison.Ordinal)),
            answer);
    }

    /// <summary>
    /// Each field of a machine's rule is the settings file's where it declares it, else the machine's section's in
    /// config.json, else the defaults', else the built-in value - and the rule says where each came from: a worktree's
    /// config.json, committed before the machine's count was raised, says nothing the machine's own file says otherwise.
    /// </summary>
    [Fact]
    public void TheSettingsFile_WinsFieldByField_OverTheMachinesSection_WhichWinsOverTheDefaults()
    {
        var inForce = AdmissionSettings.InForce(
            AdmissionSection.SettingsFile(new AdmissionSettings { HeavyLegs = 3, ReclaimIdleBuilds = true }, "/u/settings.json"),
            AdmissionSection.Host(new AdmissionSettings { HeavyLegs = 2, MinFreeGiB = 4 }, HostId.Local),
            AdmissionSection.Defaults(new AdmissionSettings { HeavyLegs = 1, MinFreeGiB = 8, MaxMemoryPercent = 80 }))!;

        Assert.Equal(
            new AdmissionRule(3, 80, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(60), 4L << 30)
            {
                ReclaimIdleBuilds = true,
            },
            inForce.Rule);
        Assert.Equal(
            [
                new AdmissionField("heavyLegs", "3", "the machine's settings file '/u/settings.json'"),
                new AdmissionField("maxMemoryPercent", "80", "config.json's defaults.admission"),
                new AdmissionField("settleSeconds", "[15, 90]", AdmissionSettings.BuiltIn),
                new AdmissionField("pollSeconds", "30", AdmissionSettings.BuiltIn),
                new AdmissionField("maxWaitMinutes", "60", AdmissionSettings.BuiltIn),
                new AdmissionField("minFreeGiB", "4", "config.json's hosts.local admission"),
                new AdmissionField("reclaimIdleBuilds", "true", "the machine's settings file '/u/settings.json'"),
            ],
            inForce.Fields);
        Assert.Equal(
            "heavyLegs 3, reclaimIdleBuilds true from the machine's settings file '/u/settings.json'; maxMemoryPercent 80 from config.json's "
            + "defaults.admission; settleSeconds [15, 90], pollSeconds 30, maxWaitMinutes 60 built in; minFreeGiB 4 from config.json's hosts.local admission",
            inForce.Describe());
    }

    /// <summary>
    /// A machine whose settings file declares admission admits by it though no tree's config.json declares any, and one
    /// where nothing declares it admits every leg at once: no rule. An ssh host's section is named as its own.
    /// </summary>
    [Fact]
    public void ASettingsFileAlone_IsARule_AndNothingDeclaredIsNone()
    {
        var alone = AdmissionSettings.InForce(
            AdmissionSection.SettingsFile(new AdmissionSettings { HeavyLegs = 4 }, "/u/settings.json"),
            AdmissionSection.Host(null, HostId.Ssh("vps")),
            AdmissionSection.Defaults(null))!;

        Assert.Equal(4, alone.Rule.HeavyLegs);
        Assert.False(alone.Rule.ReclaimIdleBuilds);
        Assert.Null(AdmissionSettings.InForce(AdmissionSection.SettingsFile(null, "/u/settings.json"), AdmissionSection.Host(null, HostId.Local), AdmissionSection.Defaults(null)));
        Assert.Equal("config.json's hosts.ssh 'vps' admission", AdmissionSection.Host(null, HostId.Ssh("vps")).From);
        Assert.Equal("config.json's hosts.local admission", AdmissionSection.Host(null, HostId.Wsl("Ubuntu")).From);
    }

    /// <summary>
    /// A machine whose settings file declares its count takes every leg in line by it, whatever count the legs ahead were
    /// asked under: two legs of a worktree committed when the count was 2 hold no third leg back on a machine that declares
    /// 3. Each entry still carries its own command's count, which a build before this one weighs.
    /// </summary>
    [Fact]
    public async Task AMachinesOwnCount_IsEveryLegsInLine_WhateverCountTheLegsAheadCarry()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "stale-first", slots: 2), AdmissionKit.Holder(harness, "stale-second", slots: 2));

        var slots = new HeavyLegSlots(harness.FileSystem, harness.Output, harness.Identity, record, () => 3);

        using var admitted = await AdmissionKit.Admission(slots, new ScriptedGauge(10), new ManualClock(), settle: TimeSpan.Zero)
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 3, maxWaitMinutes: 1), []), TestContext.Current.CancellationToken);

        Assert.True(admitted.Fact.Admitted);
        Assert.Equal(0, admitted.Fact.WaitedSeconds);
        Assert.Equal([2, 2, 3], AdmissionKit.Read(record).Select(entry => entry.Slots));
        Assert.Equal([true, true, true], slots.Line().Select(inLine => inLine.HoldsSlot));
    }

    /// <summary>
    /// A leg waiting behind legs that fill the machine's own count says it waits for one of the machine's slots by that
    /// count, not by the fewest any entry ahead carries, and a dry run's reading of the line weighs it the same way.
    /// </summary>
    [Fact]
    public async Task ALegWaitingBehindAMachinesOwnCount_SaysThatCount()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var record = temp.Combine("admission.json");
        var said = new List<string>();

        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "first", slots: 1), AdmissionKit.Holder(harness, "second", slots: 1), AdmissionKit.Holder(harness, "third", slots: 1));

        var slots = new HeavyLegSlots(harness.FileSystem, harness.Output, harness.Identity, record, () => 3);

        using var refused = await AdmissionKit.Admission(slots, new ScriptedGauge(10), new ManualClock())
            .AdmitAsync(AdmissionKit.Request(AdmissionKit.Rule(heavyLegs: 3, maxWaitMinutes: 1), said), TestContext.Current.CancellationToken);

        Assert.False(refused.Fact.Admitted);
        Assert.StartsWith("waits for one of this machine's 3 heavy-leg slot(s), 3 leg(s) ahead; held by '/src/first'", said[0], StringComparison.Ordinal);
        Assert.Equal([true, true, true], slots.Line().Select(inLine => inLine.HoldsSlot));
    }

    /// <summary>
    /// The settings file of a WSL distribution's machine travels with its leg, as what the host reads its floor by: carried
    /// beside the command, never on its line, and read back as it was sent.
    /// </summary>
    [Fact]
    public void AWslLegsRequest_CarriesItsMachinesAdmission_AndTheHostReadsItBack()
    {
        var request = new HostAgentRequest
        {
            Kind = HostAgentRequestKind.Run,
            Nonce = "n",
            MachineAdmission = new AdmissionSettings { MinFreeGiB = 6, ReclaimIdleBuilds = true },
        };

        var read = JsonSerializer.Deserialize<HostAgentRequest>(JsonSerializer.Serialize(request, HostAgentProtocol.JsonOptions), HostAgentProtocol.JsonOptions)!;
        var carried = Dispatch.Of(read).MachineAdmission!;

        Assert.Equal((6.0, true, (int?)null), (carried.MinFreeGiB, carried.ReclaimIdleBuilds, carried.HeavyLegs));
        Assert.Null(Dispatch.Of(new HostAgentRequest { Kind = HostAgentRequestKind.Run, Nonce = "n" }).MachineAdmission);
    }

    /// <summary>
    /// <c>legs</c> says, for each host it reaches, the rule in force and where each field came from - and, where a host's
    /// settings file could not be read, why - in text and in --json; a host with no rule in force says nothing of it.
    /// </summary>
    [Fact]
    public void Legs_SaysForEachHostTheRuleInForce_AndWhereEachFieldCameFrom()
    {
        var inForce = AdmissionSettings.InForce(
            AdmissionSection.SettingsFile(new AdmissionSettings { HeavyLegs = 3 }, "/u/settings.json"),
            AdmissionSection.Host(new AdmissionSettings { MinFreeGiB = 4 }, HostId.Local),
            AdmissionSection.Defaults(null))!;
        var unread = $"This machine's settings file '~/settings.json' could not be read: the disk is unwell. {Refusal}";
        var report = new LegsReport(
            [],
            [
                new HostReport { Host = HostId.Local, MachineSettings = new MachineSettingsAnswer("/u/settings.json", new AdmissionSettings { HeavyLegs = 3 }), Admission = inForce },
                new HostReport { Host = HostId.Ssh("vps"), MachineSettings = new MachineSettingsAnswer("~/settings.json", Problem: unread), AdmissionUnread = unread },
                new HostReport { Host = HostId.Ssh("pi"), MachineSettings = new MachineSettingsAnswer("~/settings.json") },
            ],
            Named: false);

        var text = LegsReports.Render(report, json: false);

        Assert.Equal(
            [
                "local: admission: heavyLegs 3 from the machine's settings file '/u/settings.json'; maxMemoryPercent 76, settleSeconds [15, 90], "
                    + "pollSeconds 30, maxWaitMinutes 60, reclaimIdleBuilds false built in; minFreeGiB 4 from config.json's hosts.local admission",
                $"ssh vps: admission: {unread}",
            ],
            text.Details);

        using var document = JsonDocument.Parse(Assert.Single(LegsReports.Render(report, json: true).Data));
        var hosts = document.RootElement.GetProperty("hosts").EnumerateArray().ToList();
        var fields = hosts[0].GetProperty("admission").EnumerateArray().Select(field => (field.GetProperty("key").GetString(), field.GetProperty("value").GetString(), field.GetProperty("from").GetString())).ToList();

        Assert.Equal("/u/settings.json", hosts[0].GetProperty("settingsFile").GetString());
        Assert.Equal(("heavyLegs", "3", "the machine's settings file '/u/settings.json'"), fields[0]);
        Assert.Equal(("minFreeGiB", "4", "config.json's hosts.local admission"), fields[5]);
        Assert.Equal(7, fields.Count);
        Assert.False(hosts[0].TryGetProperty("admissionUnread", out _));
        Assert.Equal(unread, hosts[1].GetProperty("admissionUnread").GetString());
        Assert.False(hosts[1].TryGetProperty("admission", out _));
        Assert.False(hosts[2].TryGetProperty("admission", out _));
        Assert.Equal("~/settings.json", hosts[2].GetProperty("settingsFile").GetString());
    }

    /// <summary>A settings file holding <paramref name="json"/>, in a scratch place of its own.</summary>
    private static MachineSettingsFile Written(string json)
    {
        var file = HostDoubles.MachineSettings();

        Directory.CreateDirectory(Path.GetDirectoryName(file.Location)!);
        File.WriteAllText(file.Location!, json);
        return file;
    }

    /// <summary>The real file system, save that the file at <paramref name="path"/> is there and cannot be read.</summary>
    private sealed class UnreadableFile(IFileSystem inner, string path) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path1)
            => string.Equals(path1, path, StringComparison.Ordinal) ? throw new IOException("the disk is unwell.") : base.OpenRead(path1);
    }
}
