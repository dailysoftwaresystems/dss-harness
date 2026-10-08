using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

public sealed class ConfigStoreTests
{
    /// <summary>An emulator declaration every leg test below can refer to.</summary>
    private const string QemuArm64 = """
        { "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": ["qemu-aarch64"], "witness": { "command": ["/opt/arm64/uname", "-m"], "pattern": "^aarch64$" } }
        """;

    [Fact]
    public void Load_Throws_WhenTheFileIsMissing()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ConfigException>(() => CreateStore().Load(temp.Combine("config.json")));

        Assert.Contains("init", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_Throws_WhenJsonIsMalformed()
    {
        var exception = LoadInvalid("{ this is not json");

        Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommas()
    {
        var config = LoadValid("""
            {
              // config.json is hand edited, so this must parse.
              "version": 1,
              "defaults": { "buildCores": 8, },
            }
            """);

        Assert.Equal(1, config.Version);
        Assert.Equal(8, config.Defaults.BuildCores);
    }

    [Fact]
    public void Load_IsCaseInsensitiveOnPropertyNames()
    {
        var config = LoadValid("""{ "Version": 1, "Defaults": { "TestCores": 3 } }""");

        Assert.Equal(3, config.Defaults.TestCores);
    }

    [Fact]
    public void SaveThenLoad_PreservesEveryConfiguredSection()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        var original = new HarnessConfig
        {
            Defaults = new HarnessDefaults { BuildCores = 12, TestCores = 4, MaxParallelLegs = 2 },
            Toolchains = { ["clang"] = new ToolchainConfig { Env = { ["CXX"] = "clang++" } } },
            Sanitizers = { ["asan"] = new VariantOverlay { Env = { ["CFLAGS"] = "-fsanitize=address" } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { BuildCores = 8 },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "~/src/repo" } },
                Ssh = { ["vps"] = new SshHostConfig { RepositoryPath = "/home/dev/repo", BuildCores = 32, TestCores = 16 } },
            },
            SshItems = ["vps"],
            WslDistros = ["Ubuntu"],
            Emulators =
            {
                ["qemu-arm64"] = new EmulatorConfig
                {
                    HostOs = "linux",
                    HostProcessor = "x86_64",
                    Processor = "arm64",
                    Launcher = ["qemu-aarch64", "-L", "/usr/aarch64-linux-gnu"],
                    Requires = ["qemu-aarch64", "/usr/aarch64-linux-gnu"],
                    Witness = new EmulatorWitness { Command = ["/opt/arm64/uname", "-m"], Pattern = "^aarch64$" },
                },
            },
            Legs =
            {
                ["vps-debug"] = new LegConfig { Os = "linux", Processor = "x86_64", Ssh = "vps", Config = "debug", Toolchain = "clang", Sanitizer = "asan" },
                ["arm64-debug"] = new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Wsl = "Ubuntu", Config = "debug" },
            },
            LegSets = { ["gate"] = ["vps-debug", "arm64-debug"] },
            Exec = { ["fmt"] = new ExecConfig { Command = "clang-format", Args = ["-i"] } },
            Worktrees = new WorktreeSettings { MaxNameLength = 16, PathLimit = 1024 },
        };

        store.Save(path, original);
        var loaded = store.Load(path);

        Assert.Equal(12, loaded.Defaults.BuildCores);
        Assert.Equal(4, loaded.Defaults.TestCores);
        Assert.Equal(2, loaded.Defaults.MaxParallelLegs);
        Assert.Equal("clang++", loaded.Toolchains["clang"].Env["CXX"]);
        Assert.Equal("-fsanitize=address", loaded.Sanitizers["asan"].Env["CFLAGS"]);
        Assert.Equal("Debug", loaded.BuildConfigs["debug"].CmakeBuildType);
        Assert.Equal(8, loaded.Hosts.Local.BuildCores);
        Assert.Equal("~/src/repo", loaded.Hosts.Wsl["Ubuntu"].RepositoryPath);
        Assert.Equal("/home/dev/repo", loaded.Hosts.Ssh["vps"].RepositoryPath);
        Assert.Equal(32, loaded.Hosts.Ssh["vps"].BuildCores);
        Assert.Equal(16, loaded.Hosts.Ssh["vps"].TestCores);
        Assert.Equal(["qemu-aarch64", "-L", "/usr/aarch64-linux-gnu"], loaded.Emulators["qemu-arm64"].Launcher);
        Assert.Equal(["qemu-aarch64", "/usr/aarch64-linux-gnu"], loaded.Emulators["qemu-arm64"].Requires);
        Assert.Equal(["test"], loaded.Emulators["qemu-arm64"].Phases);
        Assert.Equal("^aarch64$", loaded.Emulators["qemu-arm64"].Witness.Pattern);
        Assert.Equal("vps", loaded.Legs["vps-debug"].Ssh);
        Assert.Equal("asan", loaded.Legs["vps-debug"].Sanitizer);
        Assert.Equal("qemu-arm64", loaded.Legs["arm64-debug"].Emulator);
        Assert.Equal("Ubuntu", loaded.Legs["arm64-debug"].Wsl);
        Assert.Equal(["vps-debug", "arm64-debug"], loaded.LegSets["gate"]);
        Assert.Equal(["-i"], loaded.Exec["fmt"].Args);
        Assert.Equal(16, loaded.Worktrees.MaxNameLength);
        Assert.Equal(1024, loaded.Worktrees.PathLimit);
    }

    [Fact]
    public void Serialize_DoesNotEscapePlusSigns()
    {
        // The default HTML-safe encoder writes each plus sign as a + escape: valid
        // JSON, and unreadable to whoever maintains the file.
        var config = new HarnessConfig
        {
            Toolchains = { ["gcc"] = new ToolchainConfig { Env = { ["CXX"] = "g++" } } },
        };

        var json = CreateStore().Serialize(config);

        Assert.Contains("g++", json, StringComparison.Ordinal);
        Assert.DoesNotContain("u002B", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_OmitsEmptyCollections()
    {
        // A generated file full of "env": {} buries the settings that matter.
        var json = CreateStore().Serialize(new HarnessConfig());

        Assert.DoesNotContain("\"env\": {}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"projects\": []", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WritesLineFeeds_OnEveryPlatform()
    {
        // config.json is tracked; its bytes must not depend on which machine ran init.
        var json = CreateStore().Serialize(DefaultConfigFactory.Create([], PlatformNames.Linux, PlatformNames.X64));

        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedConfig_LooksUpKeysCaseInsensitively()
    {
        // Config keys are names a person typed. Looking up "Local-Debug" against a
        // config declaring "local-debug" must not report the leg as unknown, and the
        // in-memory config and the loaded one must not disagree about that.
        var config = LoadValid($$"""
            {
              "toolchains": { "msvc": { "generator": "Ninja", "env": { "CC": "cl" } } },
              "buildConfigs": { "debug": { "cmakeBuildType": "Debug" } },
              "sshItems": ["vps"], "wslDistros": ["Ubuntu"],
              "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "/home/dev/repo" } }, "ssh": { "vps": { "repositoryPath": "/srv/repo" } } },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "local-debug": { "os": "linux", "processor": "x86_64", "config": "debug" } },
              "legSets": { "gate": ["local-debug"] },
              "exec": { "fmt": { "command": "clang-format" } }
            }
            """);

        Assert.True(config.Toolchains.ContainsKey("MSVC"), "toolchains");
        Assert.True(config.BuildConfigs.ContainsKey("Debug"), "buildConfigs");
        Assert.True(config.Hosts.Wsl.ContainsKey("ubuntu"), "hosts.wsl");
        Assert.True(config.Hosts.Ssh.ContainsKey("VPS"), "hosts.ssh");
        Assert.True(config.Emulators.ContainsKey("QEMU-ARM64"), "emulators");
        Assert.True(config.Legs.ContainsKey("Local-Debug"), "legs");
        Assert.True(config.LegSets.ContainsKey("Gate"), "legSets");
        Assert.True(config.Exec.ContainsKey("FMT"), "exec");
    }

    [Fact]
    public void LoadedConfig_ReplacesCollectionDefaults_RatherThanAppendingToThem()
    {
        // A toolchain declaring one platform must have exactly that platform. If
        // deserialization appended to the default instead of replacing it, a
        // Windows-only toolchain would silently become an everywhere toolchain.
        var config = LoadValid("""{ "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" } } } }""");

        Assert.Equal(["windows"], config.Toolchains["msvc"].Platforms);
    }

    [Fact]
    public void Load_RejectsAnUnknownKey_RatherThanSilentlyIgnoringIt()
    {
        // A misspelled key that is dropped reverts a setting to its default while the
        // file plainly appears to set it - the most frequent failure a config-driven
        // tool has, and the hardest to see.
        var exception = LoadInvalid("""{ "worktrees": { "pathBudgetReserv": 400 } }""");

        Assert.Contains("pathBudgetReserv", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "targets": { "root": { "transport": "local" } } }""", "targets")]
    [InlineData("""{ "defaults": { "legSet": "gate" } }""", "legSet")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "test": { "all": { "runner": "ctest", "successPattern": "ok" }, "testAgainstSshIfAvailable": [] } } ] }""", "testAgainstSshIfAvailable")]
    public void Load_RejectsSettingsThatNoLongerExist(string json, string setting)
    {
        // Silently ignored, a file written for the old shape would lose its legs' hosts and its
        // default selection without a word.
        var exception = LoadInvalid(json);

        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAKeyDeclaredTwice_InDifferentCase()
    {
        // Looked up ignoring case, the two would be one entry, silently discarding one.
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "legs": {
                "local": { "os": "linux", "processor": "x86_64", "config": "debug" },
                "LOCAL": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NamesAMissingRequiredSetting()
    {
        var exception = LoadInvalid("""{ "hosts": { "ssh": { "vps": { "buildCores": 2 } } } }""");

        Assert.Contains("repositoryPath", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsNull_ForASettingThatCannotBeNull()
    {
        // Loaded, this would crash whatever first read the setting, far from the file.
        var exception = LoadInvalid("""{ "sync": { "neverTransfer": null } }""");

        Assert.Contains("neverTransfer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RejectsANullListItem_AndSaysWhichLine()
    {
        var exception = LoadInvalid("""
            {
              "legSets": {
                "gate": [null]
              }
            }
            """);

        Assert.Contains("cannot contain null", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsANullMapValue()
    {
        var exception = LoadInvalid("""{ "toolchains": { "msvc": null } }""");

        Assert.Contains("'msvc' is null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsANegativePathBudget_WhichWouldDisableTheGuard()
    {
        var exception = LoadInvalid("""{ "worktrees": { "pathBudgetReserve": -10000 } }""");

        Assert.Contains("disables the path budget", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// How check-ci-legs reads a workflow is held when the file is read: a pattern that does not compile, or
    /// lacks the group it is read by, a step named by nothing, and a workflow pattern naming no leg, which
    /// would find the same budget for every one.
    /// </summary>
    [Theory]
    [InlineData("""{ "ci": { "legJobPattern": "unit (" } }""", "ci.legJobPattern is not a valid regular expression")]
    [InlineData("""{ "ci": { "legJobPattern": "^unit \\((?<name>[^)]+)\\)" } }""", "ci.legJobPattern has no named group 'leg' to capture the leg's name")]
    [InlineData("""{ "ci": { "buildStep": " " } }""", "ci.buildStep is given empty, or as spaces alone")]
    [InlineData("""{ "ci": { "testStep": "" } }""", "ci.testStep is given empty, or as spaces alone")]
    [InlineData("""{ "ci": { "workflowBudgetPattern": "minutes: (?<budget>[0-9]+)" } }""", "ci.workflowBudgetPattern has no {leg}")]
    [InlineData("""{ "ci": { "workflowBudgetPattern": "{leg}: (?<minutes>[0-9]+)" } }""", "ci.workflowBudgetPattern has no named group 'budget'")]
    [InlineData("""{ "ci": { "workflowBudgetPattern": "{leg}: (?<budget>[0-9]+" } }""", "ci.workflowBudgetPattern is not a valid regular expression")]
    [InlineData("""{ "ci": { "legBudgetMinutes": -1 } }""", "ci.legBudgetMinutes cannot be negative, found -1")]
    public void Load_RejectsCiConventionsThatCannotBeRead(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsCiConventions_ThatCanBeRead()
    {
        var config = LoadValid("""{ "ci": { "legJobPattern": "^unit \\((?<leg>[^,)]+)", "buildStep": "Build", "testStep": "Test", "workflowBudgetPattern": "leg: {leg}, minutes: (?<budget>[0-9]+)" } }""");

        Assert.Equal("^unit \\((?<leg>[^,)]+)", config.Ci.LegJobPattern);
        Assert.Equal("leg: {leg}, minutes: (?<budget>[0-9]+)", config.Ci.WorkflowBudgetPattern);
    }

    /// <summary>
    /// A host's hold between commands is some seconds or none, and holds the host with its keepAwake: a hold
    /// on a host that declares none would hold it with nothing.
    /// </summary>
    [Theory]
    [InlineData(-5, "[\"caffeinate\", \"-w\", \"{pid}\"]", "hosts.ssh 'mac' holdAwakeSeconds cannot be negative, found -5")]
    [InlineData(600, "null", "hosts.ssh 'mac' holdAwakeSeconds holds the host awake with its keepAwake, and it declares none")]
    public void Load_RejectsAHoldThatCannotHoldAnything(int seconds, string keepAwake, string expected)
    {
        var exception = LoadInvalid(
            "{ \"sshItems\": [\"mac\"], \"hosts\": { \"ssh\": { \"mac\": { \"repositoryPath\": \"/Users/me/repo\", \"keepAwake\": "
            + keepAwake + ", \"holdAwakeSeconds\": " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " } } } }");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A host's wake window is some seconds or none: a negative one is no window.</summary>
    [Fact]
    public void Load_RejectsANegativeWakeWindow()
    {
        var exception = LoadInvalid(
            "{ \"sshItems\": [\"mac\"], \"hosts\": { \"ssh\": { \"mac\": { \"repositoryPath\": \"/Users/me/repo\", \"wakeWaitSeconds\": -1 } } } }");

        Assert.Contains("hosts.ssh 'mac' wakeWaitSeconds cannot be negative, found -1", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A leg's buildSpaceGiB is a positive number of GiB: none, or less, is no need anybody measured.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    public void Load_RejectsABuildSpaceThatIsNoRoom(string room)
    {
        var exception = LoadInvalid(
            "{ \"buildConfigs\": { \"debug\": {} }, \"legs\": { \"native\": { \"os\": \"linux\", \"processor\": \"x86_64\", \"config\": \"debug\", \"buildSpaceGiB\": "
            + room + " } } }");

        Assert.Contains($"leg 'native' buildSpaceGiB must be a positive number of GiB, found {room}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAPathLimitBelowOne()
    {
        var exception = LoadInvalid("""{ "worktrees": { "pathLimit": 0 } }""");

        Assert.Contains("worktrees.pathLimit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsALegNamingSomethingUndeclared_ReportingEveryProblemAtOnce()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": { "cmakeBuildType": "Debug" } },
              "legs": { "bad": { "os": "linux", "processor": "x86_64", "ssh": "typo", "config": "nope", "sanitizer": "tsan" } }
            }
            """);

        // Fixing one problem only to be shown the next is a poor way to correct a file.
        Assert.Contains("ssh host 'typo'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("config 'nope'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sanitizer 'tsan'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsALegWorktreeThatIsNotAValidName()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "legs": { "in-worktree": { "os": "linux", "processor": "x86_64", "config": "debug", "worktree": "Bad_Name" } }
            }
            """);

        Assert.Contains("leg 'in-worktree' worktree", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "os": "linx", "processor": "x86_64", "config": "debug" }""", "leg 'x' os is 'linx'")]
    [InlineData("""{ "os": "linux", "processor": "amd64", "config": "debug" }""", "leg 'x' processor is 'amd64'")]
    [InlineData("""{ "os": "linux", "processor": "x86_64", "config": "debug", "wsl": "Ubuntu", "ssh": "vps" }""", "names both a wsl and an ssh host")]
    [InlineData("""{ "os": "windows", "processor": "x86_64", "config": "debug", "wsl": "Ubuntu" }""", "names wsl host 'Ubuntu', which runs linux")]
    [InlineData("""{ "os": "linux", "processor": "x86_64", "config": "debug", "wsl": "Debian" }""", "wsl host 'Debian', which is not declared under hosts.wsl")]
    [InlineData("""{ "os": "linux", "processor": "arm64", "config": "debug", "emulator": "missing" }""", "emulator 'missing', which is not declared")]
    [InlineData("""{ "os": "linux", "processor": "riscv64", "config": "debug", "emulator": "qemu-arm64" }""", "but emulator 'qemu-arm64' runs arm64 programs")]
    [InlineData("""{ "os": "macos", "processor": "arm64", "config": "debug", "emulator": "qemu-arm64" }""", "but emulator 'qemu-arm64' runs on linux hosts")]
    public void Load_RejectsALegThatCanNeverBePlaced(string leg, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "buildConfigs": { "debug": {} },
              "sshItems": ["vps"], "wslDistros": ["Ubuntu"],
              "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "/home/dev/repo" } }, "ssh": { "vps": { "repositoryPath": "/srv/repo" } } },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "x": {{leg}} }
            }
            """);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsALegThatNamesNoHost_BecauseWhereItRunsIsMeasured()
    {
        var config = LoadValid($$"""
            {
              "buildConfigs": { "debug": {} },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "arm64": { "os": "linux", "processor": "arm64", "emulator": "qemu-arm64", "config": "debug" } }
            }
            """);

        var leg = config.Legs["arm64"];
        Assert.Null(leg.Wsl);
        Assert.Null(leg.Ssh);
    }

    [Fact]
    public void Load_RejectsALegSetNamedLikeALeg()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "legs": { "gate": { "os": "linux", "processor": "x86_64", "config": "debug" } },
              "legSets": { "gate": ["gate"] }
            }
            """);

        Assert.Contains("legSet 'gate' has the same name as a leg", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "legs": { "a,b": { "os": "linux", "processor": "x86_64", "config": "debug" } } }""", "leg 'a,b' cannot be selected with --legs")]
    [InlineData("""{ "legs": { "a b": { "os": "linux", "processor": "x86_64", "config": "debug" } } }""", "leg 'a b' cannot be selected with --legs")]
    [InlineData("""{ "legs": { "gate": { "os": "linux", "processor": "x86_64", "config": "debug" } }, "legSets": { "x,y": ["gate"] } }""", "legSet 'x,y' cannot be selected with --legs")]
    [InlineData("""{ "legs": { "gate": { "os": "linux", "processor": "x86_64", "config": "debug" } }, "legSets": { "set": ["missing"] } }""", "legSet 'set' names leg 'missing', which is not declared")]
    public void Load_RejectsALegOrLegSet_ThatLegsCouldNeverSelect(string json, string expected)
    {
        // --legs splits its value at commas and the command line at spaces, so such a name is unreachable.
        var exception = LoadInvalid(json.Replace("{ \"legs\"", "{ \"buildConfigs\": { \"debug\": {} }, \"legs\"", StringComparison.Ordinal));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "repo" } } } }""", "repositoryPath 'repo' must be absolute")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "~" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "~/" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "C:\\" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "C:\\src\\repo" } } } }""", "start with ~/ for the home directory inside the distribution")]
    [InlineData("""{ "hosts": { "ssh": { "-oProxyCommand=x": { "repositoryPath": "/r" } } } }""", "is not a usable name")]
    [InlineData("""{ "hosts": { "wsl": { "my distro": { "repositoryPath": "/r" } } } }""", "is not a usable name")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "~/src/.." } } } }""", "has a '.' or '..' segment")]
    [InlineData("""{ "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "/home/dev/./repo" } } } }""", "has a '.' or '..' segment")]
    public void Load_RejectsAHostThatCannotBeUsed(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/home/dev/repo")]
    [InlineData("~/src/repo")]
    [InlineData("C:\\\\src\\\\repo")]
    [InlineData("D:/src/repo")]
    public void Load_AcceptsAnSshRepositoryPath_ThatNamesItsDirectory(string path)
    {
        var config = LoadValid($$"""{ "sshItems": ["vps"], "hosts": { "ssh": { "vps": { "repositoryPath": "{{path}}" } } } }""");

        Assert.True(config.Hosts.Ssh.ContainsKey("vps"));
    }

    [Theory]
    [InlineData("""{ "hostOs": "beos", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": ["w"], "pattern": "x" } }""", "emulator 'e' hostOs is 'beos'")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "x86_64", "witness": { "command": ["w"], "pattern": "x" } }""", "which needs no emulator")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": [], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher is empty")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "phases": ["deploy"], "witness": { "command": ["w"], "pattern": "x" } }""", "phases names 'deploy'")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "phases": [], "witness": { "command": ["w"], "pattern": "x" } }""", "phases is given empty, which is never read as the test phase alone: list test, build or both")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": [], "pattern": "x" } }""", "witness has an empty command")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": ["w"], "pattern": "(" } }""", "witness.pattern is not a valid regular expression")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "tool": "qemu", "witness": { "command": ["w"], "pattern": "x" } }""", "names tool 'qemu', which is not declared")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "requires": [" "], "witness": { "command": ["w"], "pattern": "x" } }""", "requires contains a blank entry")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": ["./qemu-aarch64"], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher './qemu-aarch64' must be a program name, looked up on the PATH, or an absolute path")]
    [InlineData("""{ "hostOs": "windows", "hostProcessor": "arm64", "processor": "x86_64", "launcher": ["tools\\prism.exe"], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher 'tools\\prism.exe' must be a program name")]
    [InlineData("""{ "hostOs": "windows", "hostProcessor": "arm64", "processor": "x86_64", "launcher": ["C:prism.exe"], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher 'C:prism.exe' must be a program name")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": ["bin/uname"], "pattern": "x" } }""", "witness 'bin/uname' must be a program name")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "requires": ["~/sysroot"], "witness": { "command": ["w"], "pattern": "x" } }""", "requires '~/sysroot' must be a program name")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": ["qemu-aarch64"], "witness": { "command": ["uname", "-m"], "pattern": "x" } }""", "witness 'uname' must be an absolute path: behind a launcher it is found by the launcher")]
    public void Load_RejectsAnEmulatorThatCannotWork(string emulator, string expected)
    {
        var exception = LoadInvalid($$"""{ "emulators": { "e": {{emulator}} } }""");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qemu-aarch64")]
    [InlineData("/usr/bin/qemu-aarch64")]
    [InlineData("C:/Tools/qemu-aarch64.exe")]
    [InlineData("C:\\\\Tools\\\\qemu-aarch64.exe")]
    public void Load_AcceptsAnEmulatorProgram_NamedOrGivenByAnAbsolutePath(string program)
    {
        var config = LoadValid($$"""
            { "emulators": { "e": {
                "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64",
                "launcher": ["{{program}}"], "requires": ["{{program}}"],
                "witness": { "command": ["/opt/arm64/uname"], "pattern": "x" } } } }
            """);

        Assert.True(config.Emulators.ContainsKey("e"));
    }

    [Fact]
    public void Load_AcceptsAWitnessNamedWithoutAPath_WhenNoLauncherStandsBeforeIt()
    {
        // With no launcher the witness is started like any program, so it is looked up on the PATH.
        var config = LoadValid("""
            { "emulators": { "prism": {
                "hostOs": "windows", "hostProcessor": "arm64", "processor": "x86_64",
                "witness": { "command": ["x64-witness"], "pattern": "x" } } } }
            """);

        Assert.Null(config.Emulators["prism"].Launcher);
    }

    [Theory]
    [InlineData("""{ "commit": { "template": "{area}: {summary}", "variables": { "area": {} } } }""", "commit.template uses {summary}, which is not declared")]
    [InlineData("""{ "commit": { "template": "{area}: fix", "variables": { "area": {}, "scope": {} } } }""", "commit.variables 'scope' is never used")]
    [InlineData("""{ "commit": { "variables": { "area": {} } } }""", "there is no commit.template to use them")]
    [InlineData("""{ "commit": { "template": "{area}", "variables": { "area": { "required": true, "default": "core" } } } }""", "is required and has a default")]
    [InlineData("""{ "commit": { "template": "{1area}", "variables": { "1area": {} } } }""", "commit.variables '1area' must be letters")]
    [InlineData("""{ "commit": { "template": " " } }""", "commit.template is blank")]
    public void Load_RejectsACommitPolicyThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsACommitTemplate_WhoseEveryPlaceholderIsDeclared()
    {
        var config = LoadValid("""
            { "commit": { "template": "{area}: {summary}", "variables": { "area": { "required": true }, "summary": { "default": "update" } } } }
            """);

        Assert.Equal(2, config.Commit.Variables.Count);
    }

    [Theory]
    [InlineData("""{ "sync": { "exclude": ["../outside"] } }""", "sync.exclude entry '../outside'")]
    [InlineData("""{ "sync": { "neverTransfer": ["/etc"] } }""", "sync.neverTransfer entry '/etc'")]
    [InlineData("""{ "sync": { "exclude": [""] } }""", "sync.exclude entry ''")]
    public void Load_RejectsASyncPathOutsideTheTree(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worktrees root or a sync path spelled with a '.' segment or a doubled separator inside it is
    /// compared as written - by sync's lists, and by init's ignore rule for the root - and so would
    /// withhold and ignore nothing it names: refused, with the one spelling to write.
    /// </summary>
    [Theory]
    [InlineData("""{ "worktrees": { "root": ".harness-config/./worktrees" } }""", "worktrees.root names '.harness-config/./worktrees'", ".harness-config/worktrees")]
    [InlineData("""{ "sync": { "neverTransfer": ["build//x"] } }""", "sync.neverTransfer names 'build//x'", "build/x")]
    [InlineData("""{ "sync": { "exclude": ["docs/./old"] } }""", "sync.exclude names 'docs/./old'", "docs/old")]
    public void Load_RejectsAPathSpelledTwoWays_NamingTheOneSpelling(string json, string named, string spelling)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(named, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"write '{spelling}'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worktrees root that is the tree itself is refused as that, and only as that: it is no path
    /// spelled two ways, and a line saying to write '' would send the reader nowhere.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void Load_RejectsAWorktreesRootThatIsTheTreeItself_AsThatAlone(string root)
    {
        var exception = LoadInvalid($$"""{ "worktrees": { "root": "{{root}}" } }""");

        Assert.Contains("worktrees.root cannot be the repository root itself", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("doubled separator", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worktrees root that is git's own directory or the orchestrators directory, or is inside either, is refused:
    /// worktrees made there would mix with git's records or an orchestrator's.
    /// </summary>
    [Theory]
    [InlineData(".orchestrators")]
    [InlineData(".orchestrators/trees")]
    [InlineData(".Orchestrators")]
    [InlineData(".git")]
    [InlineData(".git/worktrees")]
    public void Load_RejectsAWorktreesRootThatIsOrIsInsideGitOrTheOrchestratorsDirectory(string root)
    {
        var exception = LoadInvalid($$"""{ "worktrees": { "root": "{{root}}" } }""");

        Assert.Contains($"worktrees.root names '{root}', which is or is inside '", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAnUnsupportedVersion()
    {
        var exception = LoadInvalid("""{ "version": 99 }""");

        Assert.Contains("version 99", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "defaults": { "buildCores": 0 } }""", "defaults.buildCores")]
    [InlineData("""{ "defaults": { "testCores": -1 } }""", "defaults.testCores")]
    [InlineData("""{ "defaults": { "maxParallelLegs": 0 } }""", "defaults.maxParallelLegs")]
    [InlineData("""{ "hosts": { "local": { "buildCores": 0 } } }""", "hosts.local buildCores")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/r", "testCores": 0 } } } }""", "hosts.ssh 'vps' testCores")]
    public void Load_RejectsACoreCountBelowOne(string json, string setting)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsATestSectionThatDeclaresNoInvocation()
    {
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": {} } ] }
            """);

        Assert.Contains("declares no invocation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsASuccessPatternThatIsNotARegularExpression()
    {
        // Found at load this is a clear message; found mid-run, a crash in an unrelated phase.
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": { "all": { "runner": "ctest", "successPattern": "(" } } } ] }
            """);

        Assert.Contains("not a valid regular expression", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreUsable_WithoutAnyConfiguration()
    {
        var config = new HarnessConfig();

        Assert.Equal(6, HarnessDefaults.DefaultCores);
        Assert.Equal(HarnessDefaults.DefaultCores, config.Defaults.BuildCores);
        Assert.Equal(HarnessDefaults.DefaultCores, config.Defaults.TestCores);
        Assert.Null(config.Defaults.MaxParallelLegs);
        Assert.Equal(WorktreeSettings.DefaultMaxNameLength, config.Worktrees.MaxNameLength);
        Assert.True(config.Commit.SignOff);
        Assert.Empty(HarnessConfigValidator.Validate(config));
    }

    [Fact]
    public void TheSyncFloor_CannotBeRemovedByConfiguration()
    {
        // A floor held as a default value would be replaced by whatever list the file
        // declares, including an empty one, and sync would then carry .git and ssh secrets.
        var emptied = LoadValid("""{ "sync": { "neverTransfer": [] } }""");
        var replaced = LoadValid("""{ "sync": { "neverTransfer": ["out"] } }""");

        Assert.Equal(SyncConfig.NeverTransferFloor, emptied.Sync.EffectiveNeverTransfer);
        Assert.Equal([.. SyncConfig.NeverTransferFloor, "out"], replaced.Sync.EffectiveNeverTransfer);
        Assert.Contains(".git", SyncConfig.NeverTransferFloor);

        // The harness's own state is withheld by code rather than by that list, so emptying the list
        // reaches it no more than it reaches .git: a host's credentials are neither sent nor deleted.
        var exclusions = new RepoHarness.Core.Sync.SyncExclusions(emptied.Sync, emptied.Worktrees.Root);

        foreach (var local in new[] { ".git/config", ".harness-config/sshItems/vps/.env", ".harness-config/runner/.secrets/ci.env" })
        {
            Assert.True(exclusions.IsWithheldFromTransfer(local), $"'{local}' should never be sent");
            Assert.True(exclusions.IsProtectedFromDeletion(local), $"'{local}' should never be deleted");
        }
    }

    [Fact]
    public void SaveThenLoad_PreservesTheLegIntegritySettings()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        var original = new HarnessConfig
        {
            Defaults = new HarnessDefaults
            {
                DurationWarningFactor = 2.5,
                ClockStepToleranceMilliseconds = 500,
                ProcessSampleSeconds = 2,
            },
            Contention = new ContentionConfig { BuildTools = ["ninja"], SharedResourceTools = ["compiler-daemon"] },
            SshItems = ["mac"],
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["mac"] = new SshHostConfig
                    {
                        RepositoryPath = "/Users/dev/repo",
                        KeepAwake = ["caffeinate", "-dimsu", "-w", "{pid}"],
                        ConnectTimeoutSeconds = 10,
                        KeepAliveSeconds = 15,
                    },
                },
            },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "main",
                    Type = "cmake",
                    BuildOutputs = ["bin/tool"],
                    Test = new TestConfig
                    {
                        Inputs = ["config/**"],
                        All = new TestInvocation
                        {
                            Runner = "ctest",
                            CoresArgs = ["-j", "{cores}"],
                            CoresEnv = ["CTEST_PARALLEL_LEVEL"],
                            SuccessPattern = "tests passed",
                            CountPattern = @"out of (?<total>\d+)",
                        },
                    },
                },
            },
        };

        store.Save(path, original);
        var loaded = store.Load(path);

        Assert.Equal(2.5, loaded.Defaults.DurationWarningFactor);
        Assert.Equal(500, loaded.Defaults.ClockStepToleranceMilliseconds);
        Assert.Equal(2, loaded.Defaults.ProcessSampleSeconds);
        Assert.Equal(["ninja"], loaded.Contention.BuildTools);
        Assert.Equal(["compiler-daemon"], loaded.Contention.SharedResourceTools);
        Assert.Equal(["caffeinate", "-dimsu", "-w", "{pid}"], loaded.Hosts.Ssh["mac"].KeepAwake);
        Assert.Equal(10, loaded.Hosts.Ssh["mac"].ConnectTimeoutSeconds);
        Assert.Equal(15, loaded.Hosts.Ssh["mac"].KeepAliveSeconds);

        var project = Assert.Single(loaded.Projects);
        Assert.Equal(["bin/tool"], project.BuildOutputs);
        Assert.Equal(["config/**"], project.Test?.Inputs);
        Assert.Equal(["-j", "{cores}"], project.Test?.All?.CoresArgs);
        Assert.Equal(["CTEST_PARALLEL_LEVEL"], project.Test?.All?.CoresEnv);
        Assert.Equal(@"out of (?<total>\d+)", project.Test?.All?.CountPattern);
    }

    [Fact]
    public void LegIntegrityDefaults_ApplyWithoutConfiguration()
    {
        var config = new HarnessConfig();
        var host = new SshHostConfig { RepositoryPath = "/srv/repo" };

        Assert.Equal(3.0, config.Defaults.DurationWarningFactor);
        Assert.Equal(2000, config.Defaults.ClockStepToleranceMilliseconds);
        Assert.Equal(5, config.Defaults.ProcessSampleSeconds);
        Assert.Contains("ctest", config.Contention.BuildTools);
        Assert.Contains("ninja", config.Contention.BuildTools);
        Assert.Empty(config.Contention.SharedResourceTools);
        Assert.Equal(25, host.ConnectTimeoutSeconds);
        Assert.Equal(30, host.KeepAliveSeconds);
        Assert.Null(host.KeepAwake);
    }

    [Theory]
    [InlineData("""{ "defaults": { "durationWarningFactor": 0.5 } }""", "defaults.durationWarningFactor")]
    [InlineData("""{ "defaults": { "clockStepToleranceMilliseconds": -1 } }""", "defaults.clockStepToleranceMilliseconds")]
    [InlineData("""{ "defaults": { "processSampleSeconds": 0 } }""", "defaults.processSampleSeconds")]
    [InlineData("""{ "contention": { "buildTools": ["ninja", " "] } }""", "contention.buildTools contains a blank name")]
    [InlineData("""{ "hosts": { "local": { "keepAwake": [] } } }""", "keepAwake has an empty command")]
    [InlineData("""{ "hosts": { "local": { "keepAwake": ["caffeinate", "-w", "{leg}"] } } }""", "keepAwake names '{leg}', which nothing fills in: it can hold only {pid}")]
    [InlineData("""{ "hosts": { "local": { "keepAwake": ["./awake.sh"] } } }""", "keepAwake './awake.sh' must be a program name")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/r", "connectTimeoutSeconds": 0 } } } }""", "connectTimeoutSeconds")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/r", "keepAliveSeconds": 0 } } } }""", "keepAliveSeconds")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "buildOutputs": ["../other/bin"] } ] }""", "buildOutputs entry '../other/bin'")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "buildOutputs": ["/abs/bin"] } ] }""", "buildOutputs entry '/abs/bin'")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "buildOutputs": ["C:/bin"] } ] }""", "buildOutputs entry 'C:/bin'")]
    public void Load_RejectsALegIntegritySettingThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RequiresASuccessPattern_OnEveryPlatformATestRunsOn()
    {
        // A zero exit code alone has been measured, three separate times, to mean nothing ran.
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": { "all": { "runner": "ctest" } } } ] }
            """);

        Assert.Contains("no successPattern for windows, linux, macos", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsPlatformSections_ThatSupplyWhatAllLeavesOut()
    {
        // Platform sections merge over 'all' field by field, so the requirement is checked on
        // the merged result rather than on each section alone.
        var config = LoadValid("""
            {
              "projects": [
                {
                  "name": "main",
                  "type": "cmake",
                  "test": {
                    "all": { "runner": "ctest" },
                    "windows": { "successPattern": "tests passed" },
                    "linux": { "successPattern": "tests passed" },
                    "macos": { "successPattern": "tests passed" }
                  }
                }
              ]
            }
            """);

        Assert.Equal("ctest", Assert.Single(config.Projects).Test?.All?.Runner);
    }

    [Fact]
    public void Load_RequiresARunner_ForAPlatformSectionThatStandsAlone()
    {
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": { "linux": { "successPattern": "ok" } } } ] }
            """);

        Assert.Contains("names no runner for linux", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "runner": "ctest", "successPattern": "" }""", "successPattern is empty")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "countPattern": "out of (\\d+)" }""", "no named group 'total'")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "countPattern": "(" }""", "countPattern is not a valid regular expression")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "coresArgs": ["-j", "8"] }""", "never uses {cores}")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "coresEnv": [""] }""", "coresEnv contains a blank variable name")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "testSet": " " }""", "testSet is blank; leave it out for the project's shared test set")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state", " "] }""", "remoteExcludes cannot reach the runner on windows, linux, macos: An exclusion was given empty, or as spaces alone")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "remoteExcludes": ["git-state"] }""", "remoteExcludes cannot reach the runner on windows, linux, macos: An exclusion was given, but the test settings declare no excludeArg")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-LE", "remoteExcludes": ["git-state"] }""", "with no excludeJoin it would take them apart, never leaving out what each names; declare \"excludeJoin\": \"|\"")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["--rerun-failed"], "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state"] }""", "run ctest with --rerun-failed")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["-U", "ON"], "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state"] }""", "run ctest with --union")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["-U", "ON", "-R", "unit"], "excludeArg": "-E", "excludeJoin": "|", "remoteExcludes": ["git_state"] }""", "run ctest with --union and -R")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["-L", "git-state"], "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state"] }""", "remoteExcludes cannot reach the runner on windows, linux, macos: -L 'git-state' in the test settings' args chooses only tests carrying a label that matches it, and the exclusion 'git-state', given with -LE, leaves every one of them out, so ctest would choose no test")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "filterArg": "-E" }""", "test on windows, linux, macos: filterArg '-E' leaves tests out to ctest, so --filter would run every test but those it names; declare an option that chooses them, such as '-R'")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "labelArg": "--label-exclude" }""", "test on windows, linux, macos: labelArg '--label-exclude' leaves tests out to ctest, so --label would run every test but those carrying it; declare '-L'")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "labelArg": "-R" }""", "test on windows, linux, macos: labelArg '-R' chooses tests by name to ctest, so --label would read a label as a name; declare '-L'")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-L" }""", "test on windows, linux, macos: excludeArg '-L' chooses tests to ctest, so --exclude would run only the tests it names; declare an option that leaves them out, such as '-LE'")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-LE", "excludeJoin": ";" }""", "test on windows, linux, macos: excludeJoin ';' joins nothing to ctest, which reads what it joins as one pattern holding ';', so joined exclusions would leave out no test; declare \"excludeJoin\": \"|\"")]
    public void Load_RejectsATestInvocationThatCannotWork(string invocation, string expected)
    {
        var exception = LoadInvalid(
            $$"""{ "projects": [ { "name": "main", "type": "cmake", "test": { "all": {{invocation}} } } ] }""");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// How --filter, --label and --exclude reach ctest is judged on each operating system's merged invocation, each
    /// problem said once with the systems it holds for; another runner's options are its own.
    /// </summary>
    [Fact]
    public void Load_RejectsAnOptionCtestWouldReadOtherwise_OnTheSystemsItHoldsFor()
    {
        var exception = LoadInvalid("""
            {
              "projects": [
                {
                  "name": "main",
                  "type": "cmake",
                  "test": {
                    "all": { "runner": "ctest", "successPattern": "ok", "filterArg": "-R", "excludeArg": "-LE", "excludeJoin": "|" },
                    "windows": { "filterArg": "-E" },
                    "linux": { "excludeJoin": "," },
                    "macos": { "filterArg": "-E" }
                  }
                },
                {
                  "name": "app",
                  "type": "cmake",
                  "test": { "all": { "runner": "dart", "successPattern": "ok", "filterArg": "-E", "excludeJoin": ";" } }
                }
              ]
            }
            """);

        Assert.Contains("test on windows, macos: filterArg '-E' leaves tests out to ctest", exception.Message, StringComparison.Ordinal);
        Assert.Contains("test on linux: excludeJoin ','", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("linux, macos: filterArg", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("excludeJoin ';'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// remoteExcludes that can reach the runner as declared load: joined for ctest, by -E beside a union in
    /// the args, and beside a test preset, whose files are read as the leg starts, not here.
    /// </summary>
    [Theory]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state", "gpu"] }""")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["-U", "ON"], "excludeArg": "-E", "excludeJoin": "|", "remoteExcludes": ["git_state"] }""")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["--preset", "ci"], "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state"] }""")]
    [InlineData("""{ "runner": "dart", "successPattern": "ok", "excludeArg": "--exclude-tags", "remoteExcludes": ["git-state", "gpu"] }""")]
    public void Load_AcceptsRemoteExcludes_ThatCanReachTheRunnerAsDeclared(string invocation)
    {
        var config = LoadValid(
            $$"""{ "projects": [ { "name": "main", "type": "cmake", "test": { "all": {{invocation}} } } ] }""");

        Assert.NotEmpty(Assert.Single(config.Projects).Test!.All!.RemoteExcludes!);
    }

    [Fact]
    public void Load_RejectsATestInputOutsideTheTree()
    {
        var exception = LoadInvalid("""
            {
              "projects": [
                {
                  "name": "main",
                  "type": "cmake",
                  "test": { "inputs": ["../shared/**"], "all": { "runner": "ctest", "successPattern": "ok" } }
                }
              ]
            }
            """);

        Assert.Contains("test.inputs entry '../shared/**'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorSettings_DefaultToTheConventionalRegistries()
    {
        var anchors = new HarnessConfig().Anchors;

        Assert.Equal(".plans/_deferred-anchor-registry.md", anchors.PendingAnchorsPath);
        Assert.Equal(".plans/_deferred-anchor-registry-done.md", anchors.DoneAnchorsPath);
        Assert.Equal("D", anchors.IdPrefix);
        Assert.Equal(3, anchors.MinimumIdSegments);
    }

    [Fact]
    public void SeededConfiguration_NamesTheAnchorRegistries()
    {
        var json = CreateStore().Serialize(DefaultConfigFactory.Create([], PlatformNames.Linux, PlatformNames.X64));

        Assert.Contains("\"pendingAnchorsPath\": \".plans/_deferred-anchor-registry.md\"", json, StringComparison.Ordinal);
        Assert.Contains("\"doneAnchorsPath\": \".plans/_deferred-anchor-registry-done.md\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "../outside.md" } }""", "anchors.pendingAnchorsPath entry '../outside.md'")]
    [InlineData("""{ "anchors": { "doneAnchorsPath": "/abs/done.md" } }""", "anchors.doneAnchorsPath entry '/abs/done.md'")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "notes/pending.txt" } }""", "must be a markdown (.md) file")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "a.md", "doneAnchorsPath": "./A.md" } }""", "name the same file")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "plans/work.md", "doneAnchorsPath": "plans/./work.md" } }""", "name the same file")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "plans/work.md", "doneAnchorsPath": "plans//work.md" } }""", "name the same file")]
    [InlineData("""{ "anchors": { "idPrefix": "D-" } }""", "anchors.idPrefix")]
    [InlineData("""{ "anchors": { "minimumIdSegments": 0 } }""", "anchors.minimumIdSegments")]
    public void Load_RejectsAnchorSettingsThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A configuration naming no mutations takes the defaults - two workers, a mutated run bound at ten times the
    /// unmutated one, no registry - and one that names them is read as written.
    /// </summary>
    [Fact]
    public void MutationSettings_DefaultToTwoWorkers_AndATenfoldBound()
    {
        var defaults = new HarnessConfig().Mutations;
        var named = LoadValid("""
            {
              "mutations": {
                "registry": "tests/mutations/arms.registry",
                "textDirectory": ".harness-config/runner/actions/sweep/texts",
                "workers": 3,
                "reportArgs": ["--reporters=junit", "--out={report}"],
                "runTimeFactor": 4.5
              }
            }
            """).Mutations;

        Assert.Equal((null, null, MutationSettings.DefaultWorkers, null, MutationSettings.DefaultRunTimeFactor), (defaults.Registry, defaults.TextDirectory, defaults.Workers, defaults.ReportArgs, defaults.RunTimeFactor));
        Assert.Equal((2, 10.0), (MutationSettings.DefaultWorkers, MutationSettings.DefaultRunTimeFactor));
        Assert.Equal(("tests/mutations/arms.registry", ".harness-config/runner/actions/sweep/texts", 3, 4.5), (named.Registry, named.TextDirectory, named.Workers, named.RunTimeFactor));
        Assert.Equal(["--reporters=junit", "--out={report}"], named.ReportArgs);
    }

    /// <summary>
    /// What check-mutations could not use is refused when the file is read: a registry or text directory outside the
    /// tree, spelt two ways, inside the harness's own directory, which sync never carries to another host but for its
    /// runner actions, or where the configuration's own sync settings or its worktrees root keep a sync from carrying it -
    /// a worker is a copy a sync makes, and a host sweeps its own; fewer than one worker; report arguments naming
    /// anything in braces but {report}, or never naming it; and a bound on a mutated run at or below the unmutated run's
    /// own duration.
    /// </summary>
    [Theory]
    [InlineData("""{ "mutations": { "registry": "../arms.registry" } }""", "mutations.registry entry '../arms.registry' must be a relative path inside the tree")]
    [InlineData("""{ "mutations": { "textDirectory": "/texts" } }""", "mutations.textDirectory entry '/texts' must be a relative path inside the tree")]
    [InlineData("""{ "mutations": { "registry": "tests/./arms.registry" } }""", "mutations.registry names 'tests/./arms.registry', which holds a '.' segment")]
    [InlineData("""{ "mutations": { "registry": ".harness-config/arms.registry" } }""", "mutations.registry names '.harness-config/arms.registry', inside the harness's own directory, which sync never carries")]
    [InlineData("""{ "mutations": { "textDirectory": ".harness-config/runner/actions/sweep/build" } }""", "mutations.textDirectory names '.harness-config/runner/actions/sweep/build', inside the harness's own directory")]
    [InlineData(
        """{ "mutations": { "registry": "tests/arms.registry" }, "sync": { "neverTransfer": ["tests/"] } }""",
        "mutations.registry names 'tests/arms.registry', which a sync withholds from every copy of the tree - sync.neverTransfer, sync.exclude or worktrees.root covers it - so no worker, and no host sweeping a leg, would hold it: keep it where a sync carries it")]
    [InlineData(
        """{ "mutations": { "textDirectory": "tests/texts" }, "sync": { "exclude": ["tests/texts"] } }""",
        "mutations.textDirectory names 'tests/texts', which a sync withholds from every copy of the tree - sync.neverTransfer, sync.exclude or worktrees.root covers it")]
    [InlineData(
        """{ "mutations": { "registry": "kept/trees/arms.registry" }, "worktrees": { "root": "kept/trees" } }""",
        "mutations.registry names 'kept/trees/arms.registry', which a sync withholds from every copy of the tree - sync.neverTransfer, sync.exclude or worktrees.root covers it")]
    [InlineData("""{ "mutations": { "workers": 0 } }""", "mutations.workers must be at least 1, found 0")]
    [InlineData("""{ "mutations": { "reportArgs": ["--out={reprot}"] } }""", "mutations.reportArgs names '{reprot}', which nothing fills in: it can hold only {report}.")]
    [InlineData("""{ "mutations": { "reportArgs": ["--gtest_output=xml"] } }""", "mutations.reportArgs never names {report}, so a test binary would write its report where no arm reads it")]
    [InlineData("""{ "mutations": { "runTimeFactor": 1 } }""", "mutations.runTimeFactor must be above 1, found 1")]
    [InlineData("""{ "mutations": { "runTimeFactor": 0.5 } }""", "mutations.runTimeFactor must be above 1, found 0.5")]
    public void Load_RejectsMutationSettingsThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry under a runner action's directory is carried by sync like the action, and so is accepted, as are report
    /// arguments given empty, which name none, as left out does.
    /// </summary>
    [Fact]
    public void Load_AcceptsARegistryUnderAnActionsDirectory_AndNoReportArgument()
    {
        // Beside what a sync withholds, and under none of it.
        var config = LoadValid("""
            {
              "mutations": { "registry": ".harness-config/runner/actions/sweep/arms.registry", "textDirectory": "tests/texts", "reportArgs": [] },
              "sync": { "neverTransfer": ["tests/texts-old"], "exclude": ["tests/text"] }
            }
            """);

        Assert.Equal(".harness-config/runner/actions/sweep/arms.registry", config.Mutations.Registry);
        Assert.True(config.Mutations.ReportArgs is null or { Count: 0 }, "report arguments given empty were read as some");
    }

    /// <summary>
    /// Every way a runner's action can be spelled wrong, refused when the file is read rather than
    /// when a runner is finally invoked. The rule needs no file system, so it belongs here: a
    /// configuration <c>legs</c> calls valid is one <c>run</c> can act on, and the promise that
    /// every problem is listed at once covers this one too.
    /// </summary>
    [Theory]
    [InlineData("corpus.yml", "corpus/corpus.yml")]
    [InlineData("corpus.yaml", "corpus/corpus.yml")]
    [InlineData("../outside.yml", "without '.' or '..'")]
    [InlineData("a/../../outside.yml", "without '.' or '..'")]
    [InlineData("./corpus/corpus.yml", "without '.' or '..'")]
    [InlineData("/etc/passwd.yml", "absolute path")]
    [InlineData("C:/windows/evil.yml", "absolute path")]
    [InlineData("corpus/nested/corpus.yml", "carries its directory's name")]
    [InlineData("corpus/steps.yml", "carries its directory's name")]
    [InlineData("corpus/corpus.txt", "does not end in")]
    public void Load_RejectsARunnerActionThatIsNotOneDirectoryPerAction(string action, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "predefinedRunners": { "corpus": { "action": {{System.Text.Json.JsonSerializer.Serialize(action)}} } }
            }
            """);

        Assert.Contains("predefined runner 'corpus' action", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a runner's own steps cannot say, refused when the file is read: steps on a runner of phases,
    /// which has no step to name; none at all, which would run nothing; and a step named blank or twice.
    /// Which steps its action declares is asked when that file is read.
    /// </summary>
    [Theory]
    [InlineData("""{ "phases": [ { "name": "go", "command": ["dotnet", "--info"] } ], "steps": ["go"] }""", "names steps, which only an action file has")]
    [InlineData("""{ "action": "corpus/corpus.yml", "steps": [] }""", "steps is given empty, which is never read as every step that is not manual: name the steps it runs")]
    [InlineData("""{ "action": "corpus/corpus.yml", "steps": [" "] }""", "names a blank step under steps")]
    [InlineData("""{ "action": "corpus/corpus.yml", "steps": ["bench", "bench"] }""", "names step 'bench' more than once under steps")]
    public void Load_RejectsARunnersStepsThatCannotBeRun(string runner, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "predefinedRunners": { "corpus": {{runner}} }
            }
            """);

        Assert.Contains($"predefined runner 'corpus' {expected}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A runner naming steps of its action loads, and keeps them in the order written.</summary>
    [Fact]
    public void Load_ReadsARunnersOwnSteps()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, """
            {
              "predefinedRunners": { "bench": { "action": "corpus/corpus.yml", "steps": ["bench", "profile"] } }
            }
            """);

        var config = new JsonConfigStore(new PhysicalFileSystem(FilePermissionsFactory.Create())).Load(path);

        Assert.Equal(["bench", "profile"], config.PredefinedRunners["bench"].Steps);
    }

    /// <summary>
    /// Directories above an action's own group actions and are the author's to arrange. A corpus
    /// large enough to be a harness of its own is unreadable as a flat pile of names carrying their
    /// grouping as a prefix, and what identifies an action is unchanged: the file carries the name
    /// of the directory that directly contains it.
    /// </summary>
    [Theory]
    [InlineData("corpus/corpus.yml")]
    [InlineData("corpus/corpus.yaml")]
    [InlineData("real-examples/sqlite/sqlite.yml")]
    [InlineData("real-examples/c/probe-nest/probe-nest.yml")]
    public void Load_AcceptsARunnerActionInItsOwnDirectory(string action)
    {
        var config = LoadValid($$"""
            {
              "predefinedRunners": { "corpus": { "action": {{System.Text.Json.JsonSerializer.Serialize(action)}} } }
            }
            """);

        Assert.Equal(action, config.PredefinedRunners["corpus"].Action);
    }

    /// <summary>
    /// A ceiling below the per-machine cap makes the per-machine number a claim nothing can honour,
    /// so the file would say one thing and the run show another.
    /// </summary>
    [Fact]
    public void Load_RejectsAFleetCeilingBelowThePerMachineCap()
    {
        var exception = LoadInvalid("""
            { "defaults": { "maxParallelLegs": 4, "maxParallelLegsTotal": 2 } }
            """);

        Assert.Contains("maxParallelLegsTotal", exception.Message, StringComparison.Ordinal);
        Assert.Contains("maxParallelLegs", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "defaults": { "maxParallelLegsTotal": 0 } }""", "defaults.maxParallelLegsTotal")]
    [InlineData("""{ "testTimingRegex": ["(unclosed"] }""", "testTimingRegex[0]")]
    public void Load_RejectsParallelismAndTimingSettingsThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsAFleetCeilingAtOrAboveThePerMachineCap()
    {
        var config = LoadValid("""
            { "defaults": { "maxParallelLegs": 2, "maxParallelLegsTotal": 6 } }
            """);

        Assert.Equal(2, config.Defaults.MaxParallelLegs);
        Assert.Equal(6, config.Defaults.MaxParallelLegsTotal);
    }

    /// <summary>
    /// A keyed entry that covers no platform some leg builds on would leave that leg with one
    /// fewer witness than the file appears to give it, which is the failure buildOutputs exists to
    /// prevent. Refused when the file is read, naming the leg: every declared leg and its operating
    /// system are known then.
    /// </summary>
    [Fact]
    public void Load_RejectsABuildOutputThatNoLegsPlatformIsCoveredBy()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "projects": [
                { "name": "app", "type": "cmake", "buildOutputs": [ { "windows": "bin/app.exe" } ] }
              ],
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug" },
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Contains("names no path for 'linux'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("leg 'nix'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsAKeyedBuildOutputThatCoversEveryLeg()
    {
        var config = LoadValid("""
            {
              "buildConfigs": { "debug": {} },
              "projects": [
                { "name": "app", "type": "cmake",
                  "buildOutputs": [ "compile_commands.json", { "windows": "bin/app.exe", "all": "bin/app" } ] }
              ],
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug" },
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        var outputs = Assert.Single(config.Projects).BuildOutputs;

        Assert.Equal(2, outputs.Count);
        Assert.Equal("compile_commands.json", outputs[0].For("linux"));
        Assert.Equal("bin/app.exe", outputs[1].For("windows"));
        Assert.Equal("bin/app", outputs[1].For("linux"));
    }

    [Theory]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "freebsd": "bin/a" } ] } ] }""", "expected one of")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "all": "/etc/passwd" } ] } ] }""", "relative path")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "all": "../escape" } ] } ] }""", "relative path")]
    public void Load_RejectsABuildOutputThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare string is what every configuration written before platform keying used, and it still
    /// means the same thing, written back the same way.
    /// </summary>
    [Fact]
    public void ABuildOutput_WrittenAsAString_RoundTripsAsAString()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        store.Save(path, new HarnessConfig
        {
            Projects = { new ProjectConfig { Name = "a", Type = "cmake", BuildOutputs = ["bin/a"] } },
        });

        Assert.Contains("\"bin/a\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal([(BuildOutput)"bin/a"], Assert.Single(store.Load(path).Projects).BuildOutputs);
    }

    /// <summary>
    /// A tool needed only on one platform stops being reported missing on the others. Without this
    /// a repository declaring both MSVC and a POSIX compiler could never have every leg provisioned.
    /// </summary>
    [Fact]
    public void ATool_MayNameThePlatformsItIsNeededOn()
    {
        var config = LoadValid("""
            {
              "tools": [
                { "name": "cl", "platforms": ["windows"] },
                { "name": "cmake" }
              ]
            }
            """);

        Assert.Equal(["windows"], config.Tools[0].Platforms);
        Assert.Null(config.Tools[1].Platforms);
    }

    /// <summary>
    /// A list whose absence means every one of what it names, or a set the tool chooses, is refused given empty, by one
    /// rule in one wording, naming the key and both remedies. Read as the key left out, a runner's <c>"legs": []</c>
    /// synced the tree to every host and ran on all eight legs, as <c>--legs</c> given no name would have.
    /// </summary>
    [Theory]
    [InlineData("""{ "predefinedRunners": { "probe": { "legs": [], "action": "probe/probe.yml" } } }""", "predefined runner 'probe' legs", "every declared leg")]
    [InlineData("""{ "tools": [ { "name": "cl", "platforms": [] } ] }""", "tool 'cl' platforms", "every platform")]
    [InlineData("""{ "tools": [ { "name": "cl", "toolchains": [] } ] }""", "tool 'cl' toolchains", "every toolchain")]
    [InlineData("""{ "tools": [ { "name": "cl", "legs": [] } ] }""", "tool 'cl' legs", "every leg")]
    [InlineData("""{ "tools": [ { "name": "cl", "processors": [] } ] }""", "tool 'cl' processors", "every processor")]
    [InlineData("""{ "tools": [ { "name": "cl", "emulators": [] } ] }""", "tool 'cl' emulators", "every leg, emulated or not")]
    [InlineData("""{ "toolchains": { "gcc": { "platforms": [], "env": { "CC": "gcc" } } } }""", "toolchain 'gcc' platforms", "every platform")]
    [InlineData("""{ "ci": { "workflows": [] } }""", "ci.workflows", "every workflow directly in .github/workflows")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "targets": [] } ] }""", "project 'main' targets", "the project's own default target")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "rebuildableFormats": [] } ] }""", "project 'main' rebuildableFormats", "the set its type 'cmake' uses")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "test": { "inputs": [], "all": { "runner": "ctest", "successPattern": "passed" } } } ] }""", "project 'main' test.inputs", "every file git tracks")]
    [InlineData("""{ "toolSearchDirectories": { "linux": [] } }""", "toolSearchDirectories.linux", "the directories this build already knows to look in")]
    [InlineData("""{ "predefinedRunners": { "probe": { "steps": [], "action": "probe/probe.yml" } } }""", "predefined runner 'probe' steps", "every step that is not manual")]
    public void Load_RefusesAListGivenEmpty_WhereLeavingItOutMeansEvery(string json, string setting, string leftOut)
    {
        var exception = LoadInvalid(json);

        Assert.Contains($"{setting} is given empty, which is never read as {leftOut}:", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"or leave the key out for {leftOut}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused alone: only the empty lists are named, not what follows from them - an empty scope covering no leg, a
    /// toolchain given no platform lacking one - so the one line to fix is the one line said.
    /// </summary>
    [Fact]
    public void AListGivenEmpty_IsTheOneProblemSaid_NotItsConsequences()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": { "platforms": [], "env": { "CC": "gcc" } } },
              "legs": { "nix": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "gcc" } },
              "tools": [ { "name": "cl", "platforms": [], "legs": ["nix"] } ]
            }
            """);

        Assert.Contains("has 2 problem(s)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("toolchain 'gcc' platforms is given empty", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tool 'cl' platforms is given empty", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A key left out still means every one of what it names: nothing written before this changes.</summary>
    [Fact]
    public void AListLeftOut_StillMeansEvery()
    {
        var config = LoadValid("""
            {
              "predefinedRunners": { "probe": { "action": "probe/probe.yml" } },
              "tools": [ { "name": "cmake" } ],
              "projects": [ { "name": "main", "type": "cmake" } ]
            }
            """);

        Assert.Null(config.PredefinedRunners["probe"].Legs);
        Assert.Null(config.Tools[0].Legs);
        Assert.Null(config.Ci.Workflows);
        Assert.Null(config.Projects[0].Targets);
        Assert.Null(config.Projects[0].RebuildableFormats);
    }

    /// <summary>
    /// A test section's <c>configs</c> was written by init and checked against buildConfigs, and nothing read
    /// it: a file naming release there tested only the config each leg names. Refused as the key nothing reads it is, saying what
    /// decides the config a leg tests.
    /// </summary>
    [Theory]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "test": { "configs": ["debug"] } } ] }""", "project 'main' test configs")]
    [InlineData("""{ "legs": { "nix": { "test": { "configs": [] } } } }""", "leg 'nix' test configs")]
    public void Load_RefusesATestSectionsConfigs_AsAKeyNothingReads(string json, string owner)
    {
        var exception = LoadInvalid(json);

        Assert.Contains($"{owner} is not read, and never was", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Each leg builds and tests the one build config its 'config' names", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAToolPlatformThatIsNotOne()
    {
        var exception = LoadInvalid("""{ "tools": [ { "name": "cl", "platforms": ["windoze"] } ] }""");

        Assert.Contains("windoze", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A toolchain's platforms used to be validated for spelling and then read by nothing, so a leg
    /// naming a compiler that does not exist on its own operating system was attempted anyway and
    /// failed much later as a missing program.
    /// </summary>
    [Fact]
    public void Load_RejectsALegNamingAToolchainThatDoesNotExistOnItsPlatform()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" } } },
              "legs": {
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "msvc" }
              }
            }
            """);

        Assert.Contains("leg 'nix'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist on 'linux'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "platforms": ["linux"], "env": { "CC": "gcc" } }""")]
    [InlineData("""{ "platforms": ["all"], "env": { "CC": "gcc" } }""")]
    [InlineData("""{ "env": { "CC": "gcc" } }""")]
    public void Load_AcceptsALegWhoseToolchainExistsOnItsPlatform(string toolchain)
    {
        var config = LoadValid($$"""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": {{toolchain}} },
              "legs": {
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "gcc" }
              }
            }
            """);

        Assert.Equal("gcc", config.Legs["nix"].Toolchain);
    }

    /// <summary>
    /// The same contradiction reached through a project's default rather than a leg's own name.
    /// A key naming one platform says which platform it is for; <c>all</c> does not, so it is
    /// checked against the operating systems the legs building that project declare.
    /// </summary>
    [Theory]
    [InlineData("windows", "defaultToolchain['windows']")]
    [InlineData("all", "defaultToolchain['all']")]
    public void Load_RejectsADefaultToolchainThatDoesNotExistOnThePlatformItServes(string key, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "projects": [ { "name": "app", "type": "cmake", "defaultToolchain": { "{{key}}": "gcc" } } ],
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist on 'windows'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>all</c> default serving only legs it can serve is fine: the key covers every platform,
    /// but only the ones some leg actually builds on have to be satisfiable.
    /// </summary>
    [Fact]
    public void Load_AcceptsAnAllDefaultToolchain_WhenEveryLegBuildingItIsOnAPlatformItExistsOn()
    {
        var config = LoadValid("""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "projects": [ { "name": "app", "type": "cmake", "defaultToolchain": { "all": "gcc" } } ],
              "legs": {
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Equal("gcc", Assert.Single(config.Projects).DefaultToolchain["all"]);
    }

    /// <summary>
    /// Every way a buildOutputs entry can be malformed is refused where the file is read, so the
    /// reader is told which line is wrong rather than handed a degenerate entry that witnesses
    /// nothing later.
    /// </summary>
    [Theory]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ 3 ] } ] }""", "a path, or a mapping")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ ["x"] ] } ] }""", "a path, or a mapping")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "windows": 3 } ] } ] }""", "is not a path")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "windows": { "x": "y" } } ] } ] }""", "is not a path")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ {} ] } ] }""", "names no path at all")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ "" ] } ] }""", "empty path")]
    public void Load_RefusesAMalformedBuildOutput(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two spellings of one platform are one key on the entry, so the second would silently replace
    /// the first and a leg would be witnessed by a file nobody meant to name.
    /// </summary>
    [Fact]
    public void Load_RefusesABuildOutputThatNamesOnePlatformTwice()
    {
        var exception = LoadInvalid("""
            {
              "projects": [
                { "name": "a", "type": "cmake",
                  "buildOutputs": [ { "windows": "bin/a.exe", "WINDOWS": "bin/b.exe" } ] }
              ]
            }
            """);

        Assert.Contains("names platform 'WINDOWS' twice", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A keyed entry comes back keyed. Written back as a bare string it would lose every platform
    /// but one; written back as a map, an entry spelled as a string would turn every save into a
    /// diff against a file nobody edited.
    /// </summary>
    [Fact]
    public void ABuildOutput_RoundTripsInTheShapeItWasWritten()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        store.Save(path, new HarnessConfig
        {
            Projects =
            {
                new ProjectConfig
                {
                    Name = "a",
                    Type = "cmake",
                    BuildOutputs =
                    [
                        "compile_commands.json",
                        BuildOutput.Keyed([
                            new KeyValuePair<string, string>("windows", "bin/a.exe"),
                            new KeyValuePair<string, string>("all", "bin/a"),
                        ]),
                    ],
                },
            },
        });

        var text = File.ReadAllText(path);

        // The plain entry stays plain, and the keyed one keeps every platform it named.
        Assert.Contains("\"compile_commands.json\"", text, StringComparison.Ordinal);
        Assert.Contains("\"windows\"", text, StringComparison.Ordinal);

        var outputs = Assert.Single(store.Load(path).Projects).BuildOutputs;

        Assert.Equal("compile_commands.json", outputs[0].Plain);
        Assert.Null(outputs[1].Plain);
        Assert.Equal("bin/a.exe", outputs[1].For("windows"));
        Assert.Equal("bin/a", outputs[1].For("linux"));
    }

    /// <summary>
    /// A list naming only platforms no leg runs on removes the tool from every host, which reads
    /// exactly like never having declared it — and the spelling that causes it is one mistyped word.
    /// </summary>
    [Fact]
    public void Load_RejectsAToolNeededOnlyWhereNoLegRuns()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "tools": [ { "name": "clang", "platforms": ["macos"] } ],
              "legs": { "nix": { "os": "linux", "processor": "x86_64", "config": "debug" } }
            }
            """);

        Assert.Contains("tool 'clang'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("never be checked anywhere", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool may be scoped narrower than an operating system: to toolchains, legs or leg sets,
    /// processors and emulators, each read as written.
    /// </summary>
    [Fact]
    public void ATool_MayNameTheToolchainsLegsProcessorsAndEmulatorsItIsNeededFor()
    {
        var tool = Assert.Single(LoadValid($$"""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "arm": { "os": "linux", "processor": "arm64", "config": "debug", "toolchain": "gcc", "emulator": "qemu-arm64" } },
              "legSets": { "gate": ["arm"] },
              "tools": [ { "name": "qemu-aarch64", "toolchains": ["gcc"], "legs": ["gate"], "processors": ["arm64"], "emulators": ["qemu-arm64"] } ]
            }
            """).Tools);

        Assert.Equal(["gcc"], tool.Toolchains);
        Assert.Equal(["gate"], tool.Legs);
        Assert.Equal(["arm64"], tool.Processors);
        Assert.Equal(["qemu-arm64"], tool.Emulators);
    }

    /// <summary>
    /// A scope naming nothing declared is refused naming it: read as no scope, the tool would be
    /// needed everywhere, and read as an empty one, nowhere.
    /// </summary>
    [Theory]
    [InlineData("\"toolchains\": [\"msvcc\"]", "tool 'cl' names toolchain 'msvcc', which is not declared under toolchains")]
    [InlineData("\"legs\": [\"wn\"]", "tool 'cl' names leg 'wn', which is neither a leg nor a leg set")]
    [InlineData("\"emulators\": [\"qemu\"]", "tool 'cl' names emulator 'qemu', which is not declared under emulators")]
    [InlineData("\"processors\": [\"aarch64\"]", "tool 'cl' names processor 'aarch64', which is not a processor; expected one of")]
    public void Load_RejectsAToolScopeThatNamesNothingDeclared(string scope, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "buildConfigs": { "debug": {} },
              "legs": { "win": { "os": "windows", "processor": "x86_64", "config": "debug" } },
              "tools": [ { "name": "cl", {{scope}} } ]
            }
            """);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every scope must hold at once, so two that no one leg satisfies together cover nothing: the
    /// tool would never be checked anywhere, which reads exactly like never having declared it.
    /// </summary>
    [Fact]
    public void Load_RejectsAToolWhoseScopesTogetherCoverNoLeg()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" } }, "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug", "toolchain": "msvc" },
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "gcc" }
              },
              "tools": [ { "name": "cl", "platforms": ["linux"], "toolchains": ["msvc"] } ]
            }
            """);

        Assert.Contains(
            "tool 'cl' is needed only by legs of platforms linux and toolchains msvc, and no declared leg is one, so it would never be checked anywhere",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A toolchain CMake builds with names the compiler it builds with - as a CMake project's default,
    /// or as a CMake leg's own. One naming none left CMake to take whatever it found first, which is how
    /// a leg named msvc built with MinGW's gcc on every run.
    /// </summary>
    [Theory]
    [InlineData("""{ "projects": [{ "name": "app", "type": "cmake", "path": ".", "defaultToolchain": { "linux": "gcc" } }] }""")]
    [InlineData("""{ "projects": [{ "name": "app", "type": "cmake", "path": "." }], "buildConfigs": { "debug": {} }, "legs": { "lin": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "gcc" } } }""")]
    public void Load_RejectsAToolchainCMakeBuildsWith_ThatNamesNoCompiler(string uses)
    {
        var exception = LoadInvalid(
            uses[..^1] + """, "toolchains": { "gcc": { "platforms": ["linux"], "generator": "Ninja", "env": { "CFLAGS": "-O2" } } } }""");

        Assert.Contains(
            "toolchain 'gcc', which CMake builds with, names no compiler: declare CC or CXX under its env, CMAKE_C_COMPILER, "
            + "CMAKE_CXX_COMPILER or CMAKE_TOOLCHAIN_FILE under its cacheVars, or the compilerId CMake must configure it with",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Any of these names a compiler for CMake: either language, in its environment or in the cache
    /// variable CMake is given for it - the one CMake itself reads first - a toolchain file, which
    /// names it for CMake, or the compilerId the build is held to.
    /// </summary>
    [Theory]
    [InlineData("\"env\": { \"CC\": \"gcc\" }")]
    [InlineData("\"env\": { \"CXX\": \"g++\" }")]
    [InlineData("\"cacheVars\": { \"CMAKE_C_COMPILER\": \"gcc\" }")]
    [InlineData("\"cacheVars\": { \"CMAKE_CXX_COMPILER\": \"g++\" }")]
    [InlineData("\"cacheVars\": { \"CMAKE_TOOLCHAIN_FILE\": \"cmake/arm-gcc.cmake\" }")]
    [InlineData("\"compilerId\": { \"C\": \"GNU\" }")]
    public void AToolchainNamingOneCompiler_AnyWay_IsAccepted(string names)
    {
        var config = LoadValid(
            $$"""{ "projects": [{ "name": "app", "type": "cmake", "path": ".", "defaultToolchain": { "linux": "gcc" } }], "toolchains": { "gcc": { "platforms": ["linux"], {{names}} } } }""");

        Assert.True(config.Toolchains.ContainsKey("gcc"));
    }

    /// <summary>
    /// A toolchain only a .NET or Dart project builds with names no compiler: their build systems
    /// resolve their own, and its cacheVars are that build's properties. Refused for it, a
    /// configuration that built was refused as a whole.
    /// </summary>
    [Fact]
    public void AToolchainOnlyANonCMakeProjectBuildsWith_NeedNameNoCompiler()
    {
        var config = LoadValid(
            """{ "projects": [{ "name": "app", "type": "dotnet", "path": "App.slnx", "defaultToolchain": { "all": "net" } }], "toolchains": { "net": { "platforms": ["all"], "cacheVars": { "TargetFramework": "net9.0" } } } }""");

        Assert.Equal("net9.0", config.Toolchains["net"].CacheVars["TargetFramework"]);
    }

    /// <summary>A toolchain may declare the compiler CMake must configure it with, by language.</summary>
    [Fact]
    public void AToolchain_MayDeclareTheCompilerCMakeMustConfigureItWith()
    {
        var config = LoadValid("""{ "toolchains": { "msvc": { "env": { "CC": "cl" }, "compilerId": { "C": "MSVC", "CXX": "MSVC" } } } }""");

        Assert.Equal("MSVC", config.Toolchains["msvc"].CompilerId["cxx"]);
    }

    /// <summary>
    /// A language CMake has no name like, or a language given no id, is refused: read as declared,
    /// it would never be answered, and every build would be unwitnessed over a typo.
    /// </summary>
    [Theory]
    [InlineData("\"C++\": \"MSVC\"", "toolchain 'msvc' compilerId names language 'C++', which is not a CMake language name such as C or CXX")]
    [InlineData("\"C\": \" \"", "toolchain 'msvc' compilerId gives 'C' no compiler id")]
    public void Load_RejectsACompilerIdThatNamesNoLanguageOrNoId(string entry, string expected)
    {
        var exception = LoadInvalid($$"""{ "toolchains": { "msvc": { "env": { "CC": "cl" }, "compilerId": { {{entry}} } } } }""");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A toolchain may name the developer environment its legs start in, declared once under
    /// developerEnvironments; Visual Studio's asks for the C++ build tools unless it names another
    /// component.
    /// </summary>
    [Fact]
    public void AToolchain_MayNameTheDeveloperEnvironmentItsLegsStartIn()
    {
        var config = LoadValid(
            """{ "developerEnvironments": { "vs": { "kind": "visualStudio" } }, "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""");

        Assert.Equal("vs", config.Toolchains["msvc"].DeveloperEnvironment);
        Assert.Equal(DeveloperEnvironmentKinds.VisualStudio, config.DeveloperEnvironments["VS"].Kind);
        Assert.Equal(DeveloperEnvironmentConfig.DefaultVisualStudioComponent, config.DeveloperEnvironments["vs"].RequiresComponent);
    }

    /// <summary>
    /// A developer environment this build cannot set up is refused when the file is read, and so is a
    /// toolchain naming one that is not declared, or Visual Studio's on a platform it never exists on:
    /// a leg there would be turned away on every run for want of it.
    /// </summary>
    [Theory]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "xcode" } } }""",
        "developer environment 'vs' has kind 'xcode'; this build sets up visualStudio")]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "visualStudio", "requiresComponent": " " } } }""",
        "developer environment 'vs' requiresComponent is blank; leave it out for the C++ build tools")]
    [InlineData(
        """{ "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""",
        "toolchain 'msvc' names developer environment 'vs', which is not declared under developerEnvironments")]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "visualStudio" } }, "toolchains": { "msvc": { "platforms": ["windows", "linux"], "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""",
        "toolchain 'msvc' names developer environment 'vs', which Visual Studio sets up on windows alone, and declares platforms windows, linux; declare \"platforms\": [\"windows\"] for it")]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "visualStudio" } }, "toolchains": { "msvc": { "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""",
        "toolchain 'msvc' names developer environment 'vs', which Visual Studio sets up on windows alone, and declares platforms all; declare \"platforms\": [\"windows\"] for it")]
    public void Load_RejectsADeveloperEnvironmentNoLegCouldStartIn(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    private static JsonConfigStore CreateStore() => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));

    /// <summary>
    /// Admission is read where it is declared - under defaults, hosts.local and each ssh host - in the words the file
    /// spells it, and a machine's section replaces the defaults' one field at a time: what neither says is the built-in
    /// value, and a machine where neither declares one admits every leg at once.
    /// </summary>
    [Fact]
    public void Admission_IsReadWhereDeclared_AndAMachinesSectionReplacesTheDefaultsFieldByField()
    {
        var config = LoadValid("""
            {
              "defaults": { "admission": { "heavyLegs": 3, "maxMemoryPercent": 80, "settleSeconds": [5, 10] } },
              "hosts": {
                "local": { "admission": { "heavyLegs": 1, "pollSeconds": 10, "maxWaitMinutes": 0.5 } },
                "ssh": { "vps": { "repositoryPath": "/r" } }
              },
              "sshItems": ["vps"]
            }
            """);

        var local = AdmissionSettings.RuleFor(config.Hosts.Local.Admission, config.Defaults.Admission);
        var vps = AdmissionSettings.RuleFor(config.Hosts.Ssh["vps"].Admission, config.Defaults.Admission);

        Assert.Equal(new AdmissionRule(1, 80, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)), local);
        Assert.Equal(new AdmissionRule(3, 80, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(60)), vps);
        Assert.Null(AdmissionSettings.RuleFor(null, null));
        Assert.Equal(
            new AdmissionRule(2, 76, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(60)),
            AdmissionSettings.RuleFor(new AdmissionSettings(), null));
    }

    /// <summary>A section's every field is one a machine can admit heavy legs by, or the file is refused naming it.</summary>
    [Theory]
    [InlineData("""{ "defaults": { "admission": { "heavyLegs": 0 } } }""", "defaults.admission heavyLegs must be at least 1, found 0")]
    [InlineData("""{ "defaults": { "admission": { "maxMemoryPercent": 0 } } }""", "defaults.admission maxMemoryPercent must be above 0 and at most 100, found 0")]
    [InlineData("""{ "hosts": { "local": { "admission": { "maxMemoryPercent": 101 } } } }""", "hosts.local admission maxMemoryPercent must be above 0 and at most 100, found 101")]
    [InlineData("""{ "defaults": { "admission": { "settleSeconds": [90, 15] } } }""", "defaults.admission settleSeconds is [least, most], two whole numbers of seconds from 0 to 3600 with the least first, found [90, 15]")]
    [InlineData("""{ "defaults": { "admission": { "settleSeconds": [15] } } }""", "defaults.admission settleSeconds is [least, most]")]
    [InlineData("""{ "defaults": { "admission": { "settleSeconds": [0, 2147483647] } } }""", "found [0, 2147483647]")]
    [InlineData("""{ "defaults": { "admission": { "pollSeconds": 0 } } }""", "defaults.admission pollSeconds must be from 1 to 3600, found 0")]
    [InlineData("""{ "defaults": { "admission": { "pollSeconds": 5000000 } } }""", "defaults.admission pollSeconds must be from 1 to 3600, found 5000000")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/r", "admission": { "maxWaitMinutes": 0 } } } }, "sshItems": ["vps"] }""", "hosts.ssh 'vps' admission maxWaitMinutes must be above 0 and at most 10080, found 0")]
    [InlineData("""{ "defaults": { "admission": { "maxWaitMinutes": 1e11 } } }""", "defaults.admission maxWaitMinutes must be above 0 and at most 10080, found 100000000000")]
    [InlineData("""{ "defaults": { "admission": { "maxWaitMinutes": 10080.5 } } }""", "defaults.admission maxWaitMinutes must be above 0 and at most 10080, found 10080.5")]
    [InlineData("""{ "defaults": { "admission": { "maxMemoryPercent": 100.5 } } }""", "defaults.admission maxMemoryPercent must be above 0 and at most 100, found 100.5")]
    [InlineData("""{ "defaults": { "admission": { "pollSeconds": 3601 } } }""", "defaults.admission pollSeconds must be from 1 to 3600, found 3601")]
    [InlineData("""{ "defaults": { "admission": { "settleSeconds": [-1, 5] } } }""", "found [-1, 5]")]
    [InlineData("""{ "defaults": { "admission": { "heavyLegs": -2 } } }""", "defaults.admission heavyLegs must be at least 1, found -2")]
    public void Admission_ThatNoMachineCouldAdmitBy_IsRefused(string json, string expected)
        => Assert.Contains(expected, LoadInvalid(json).Message, StringComparison.Ordinal);

    /// <summary>Every bound admits the value at its edge: the least and the most a section may say load, and are the rule.</summary>
    [Fact]
    public void Admission_AtEveryBound_Loads()
    {
        var most = LoadValid("""{ "defaults": { "admission": { "heavyLegs": 1, "maxMemoryPercent": 100, "settleSeconds": [0, 3600], "pollSeconds": 3600, "maxWaitMinutes": 10080 } } }""");
        var least = LoadValid("""{ "defaults": { "admission": { "maxMemoryPercent": 0.5, "settleSeconds": [5, 5], "pollSeconds": 1, "maxWaitMinutes": 0.1 } } }""");

        Assert.Equal(
            new AdmissionRule(1, 100, TimeSpan.Zero, TimeSpan.FromSeconds(3600), TimeSpan.FromSeconds(3600), TimeSpan.FromMinutes(10080)),
            AdmissionSettings.RuleFor(null, most.Defaults.Admission));
        Assert.Equal(
            new AdmissionRule(2, 0.5, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(0.1)),
            AdmissionSettings.RuleFor(null, least.Defaults.Admission));
    }

    /// <summary>
    /// A rule built from sections no file was read into is held to the bounds a file is: refused, naming what is out of
    /// them, rather than waited by - a count of none would hold every leg back, and a settle whose least passes its most
    /// would fail the leg picking it.
    /// </summary>
    [Fact]
    public void AnAdmissionRuleBuiltInCode_IsHeldToTheBoundsAFileIs()
    {
        var refusal = Assert.Throws<ConfigException>(() => AdmissionSettings.RuleFor(
            new AdmissionSettings { HeavyLegs = 0, SettleSeconds = [20, 10] },
            null));

        Assert.Contains("admission heavyLegs must be at least 1, found 0", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("admission settleSeconds is [least, most]", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A WSL distribution runs on this machine, whose slots and memory its legs share, so a section of its own would be
    /// a second rule for them: refused, pointing at hosts.local and defaults.
    /// </summary>
    [Fact]
    public void Admission_UnderAWslDistribution_IsRefused()
    {
        var exception = LoadInvalid("""
            {
              "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "/home/dev/repo", "admission": { "heavyLegs": 1 } } } },
              "wslDistros": ["Ubuntu"]
            }
            """);

        Assert.Contains("hosts.wsl 'Ubuntu' admission: a WSL distribution runs on this machine", exception.Message, StringComparison.Ordinal);
        Assert.Contains("declare admission under hosts.local or defaults", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner that requires the build is heavy, since its build is: one saying heavy is false is refused rather than
    /// believed, offering either fix, since either works.
    /// </summary>
    [Fact]
    public void ARunnerSayingItIsNotHeavy_WhileItRequiresTheBuild_IsRefused()
    {
        var exception = LoadInvalid("""
            { "predefinedRunners": { "bench": { "action": "bench/bench.yml", "requireBuild": true, "heavy": false } } }
            """);

        Assert.Contains(
            "predefined runner 'bench' says heavy is false, and builds its legs first, which is heavy - the runner requires the "
            + "build: leave heavy out, or drop requireBuild",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Build and test legs are heavy; a runner's are where it builds or says it is heavy; a copy's, and a runner's that
    /// only reads the tree, are light.
    /// </summary>
    [Fact]
    public void WhatIsHeavy_IsWhatBuildsOrTests_OrARunnerSaysIs()
    {
        Assert.True(Core.Legs.LegWorkload.BuildOnly.Heavy);
        Assert.True(Core.Legs.LegWorkload.BuildAndTest.Heavy);
        Assert.True(new Core.Legs.LegWorkload(Build: false, Test: true, []).Heavy);
        Assert.False(Core.Legs.LegWorkload.Copy.Heavy);

        Assert.True(Core.Legs.LegWorkload.ForRunner(new RunnerConfig { Action = "a/a.yml", RequireBuild = true }, null).Heavy);
        Assert.True(Core.Legs.LegWorkload.ForRunner(new RunnerConfig { Action = "a/a.yml", Heavy = true }, null).Heavy);
        Assert.False(Core.Legs.LegWorkload.ForRunner(new RunnerConfig { Action = "a/a.yml" }, null).Heavy);
        Assert.True(Core.Legs.LegWorkload.ForRunner(new RunnerConfig { Action = "a/a.yml", Heavy = true }, null).On("linux").Heavy);
    }

    /// <summary>
    /// A runner its expected exceptions' run checks name runs within its legs, so one of them heavy - requiring the build,
    /// or saying so - makes the legs heavy, whatever the runner carrying the checks says of its own work.
    /// </summary>
    [Fact]
    public void ARunnerWhoseRunChecksNameAHeavyRunner_IsHeavy()
    {
        var light = new RunnerConfig { Action = "a/a.yml" };

        Assert.True(Core.Legs.LegWorkload.ForRunner(light, null, [("b", new RunnerConfig { Action = "b/b.yml", Heavy = true }, null, false)]).Heavy);
        Assert.True(Core.Legs.LegWorkload.ForRunner(light, null, [("b", new RunnerConfig { Action = "b/b.yml", RequireBuild = true }, null, false)]).Heavy);
        Assert.False(Core.Legs.LegWorkload.ForRunner(light, null, [("b", new RunnerConfig { Action = "b/b.yml" }, null, false)]).Heavy);
        Assert.False(Core.Legs.LegWorkload.ForRunner(light, null, []).Heavy);

        // One whose steps could not be read counts heavy: nothing says it is not.
        Assert.True(Core.Legs.LegWorkload.ForRunner(light, null, [("b", new RunnerConfig { Action = "b/b.yml" }, null, true)]).Heavy);
    }

    /// <summary>
    /// A step declared heavy makes heavy every leg of a run that runs it - named with --manual-step, through a runner that
    /// says nothing of its weight or says it is light - and a run that leaves it out stays as light as its runner. One
    /// limited by runOn makes heavy the legs of those systems alone. A run check's runner whose steps include one is heavy
    /// the same way. Declared only on runners, a light runner sharing an action with a heavy manual step started it with
    /// no slot at all.
    /// </summary>
    [Fact]
    public void AHeavyStep_MakesHeavyARunThatRunsIt_WhicheverRunnerStartsIt()
    {
        var action = ActionKit.Parse(
            Path.Combine("actions", "sqlite", "sqlite.yml"),
            """
            steps:
              - name: self-test
                run: python3 self_test.py
              - name: recompile
                manual: true
                heavy: true
                successPattern: '^built'
                run: cmake --build build
              - name: profile
                manual: true
                heavy: true
                runOn: [linux]
                successPattern: '^profiled'
                run: perf record ./app
            """);

        Assert.True(action.Steps[1].Heavy);
        Assert.False(action.Steps[0].Heavy);

        Core.Legs.LegWorkload Run(RunnerConfig runner, params string[] manual)
            => Core.Legs.LegWorkload.ForRunner(runner, Core.Runners.StepSelection.For(runner, manual).Apply("sqlite", action).File);

        foreach (var runner in new[] { new RunnerConfig { Action = "sqlite" }, new RunnerConfig { Action = "sqlite", Heavy = false } })
        {
            Assert.False(Run(runner).On("linux").Heavy);
            Assert.True(Run(runner, "recompile").On("windows").Heavy);
            Assert.True(Run(runner, "profile").On("linux").Heavy);
            Assert.False(Run(runner, "profile").On("windows").Heavy);
        }

        var checks = new RunnerConfig { Action = "sqlite", Steps = ["recompile"] };
        var light = new RunnerConfig { Action = "a/a.yml" };

        Assert.True(Core.Legs.LegWorkload.ForRunner(light, null, [("rebuild", checks, Core.Runners.StepSelection.For(checks, []).Apply("rebuild", action).File, false)]).Heavy);
    }

    /// <summary>
    /// A step whose run line - its program included - or working directory names what the build makes, {product} or
    /// {buildDir}, builds every leg of a run that runs it first: by default, named with --manual-step, or needed by a step
    /// named, through a runner that declares no requireBuild or says it is light; and a run that leaves it out builds
    /// nothing. One limited by runOn builds, and so makes heavy, the legs of those systems alone. A brace written
    /// doubled, and another expander's ${...}, name nothing. A runner's own phase naming either - in its command, or as
    /// its working directory - builds as a step does. Unbuilt, such a step read whatever the last build left.
    /// </summary>
    [Fact]
    public void AStepNamingWhatTheBuildMakes_BuildsTheLegsOfARunThatRunsIt_WhicheverRunnerStartsIt()
    {
        var action = ActionKit.Parse(
            Path.Combine("actions", "sqlite", "sqlite.yml"),
            """
            steps:
              - name: self-test
                run: python3 self_test.py --app={{product}} ${buildDir}
              - name: recompile
                manual: true
                successPattern: '^built'
                run: python3 recompile.py --app="{product}" --out={buildDir}/recompile
              - name: report
                manual: true
                needs: [recompile]
                successPattern: '^reported'
                run: python3 report.py
              - name: deps
                manual: true
                successPattern: '^deps'
                workingDirectory: '{buildDir}'
                run: python3 deps.py
              - name: bench
                manual: true
                successPattern: '^benched'
                run: '{buildDir}/bin/app --bench'
              - name: profile
                manual: true
                runOn: [linux]
                successPattern: '^profiled'
                run: perf record {buildDir}/bin/app
            """);

        Assert.Equal([false, true, false, true, true, true], action.Steps.Select(step => step.NeedsBuild));
        Assert.Equal(["product", "buildDir"], action.Steps[1].NamesOfTheBuild);

        Core.Legs.LegWorkload Run(RunnerConfig runner, params string[] manual)
            => Core.Legs.LegWorkload.ForRunner(runner, Core.Runners.StepSelection.For(runner, manual).Apply("sqlite", action).File);

        foreach (var runner in new[] { new RunnerConfig { Action = "sqlite" }, new RunnerConfig { Action = "sqlite", Heavy = false } })
        {
            Assert.False(Run(runner).On("linux").Build);
            Assert.False(Run(runner).On("linux").Heavy);
            Assert.True(Run(runner, "recompile").On("windows").Build);
            Assert.True(Run(runner, "recompile").On("windows").Heavy);
            Assert.True(Run(runner, "report").On("windows").Build);
            Assert.True(Run(runner, "deps").On("windows").Build);
            Assert.True(Run(runner, "bench").On("windows").Build);
            Assert.True(Run(runner, "profile").On("Linux").Build);
            Assert.True(Run(runner, "profile").On("linux").Heavy);
            Assert.False(Run(runner, "profile").On("windows").Build);
            Assert.False(Run(runner, "profile").On("windows").Heavy);
        }

        // Which legs a step limited by runOn builds is known once each leg's system is: asked before, it builds none yet.
        Assert.False(Run(new RunnerConfig { Action = "sqlite" }, "profile").Build);

        Assert.Equal("step 'recompile' names {product} and {buildDir}", Assert.Single(Run(new RunnerConfig { Action = "sqlite" }, "recompile").BuiltBy).Reason);

        // A step every run runs builds every leg of a run that names none.
        var corpus = ActionKit.Parse(
            Path.Combine("actions", "corpus", "corpus.yml"),
            """
            steps:
              - name: corpus
                run: python3 corpus.py --app="{product}"
            """);

        Assert.True(Core.Legs.LegWorkload.ForRunner(new RunnerConfig { Action = "corpus" }, Core.Runners.StepSelection.Default.Apply("corpus", corpus).File).Build);

        var phases = new RunnerConfig { Phases = [new RunnerPhase { Name = "deps", Command = ["python3", "deps.py", "{buildDir}"] }] };
        var inTheBuild = new RunnerConfig { Phases = [new RunnerPhase { Name = "ctest", Command = ["ctest"], WorkingDirectory = "{buildDir}" }] };
        var reads = new RunnerConfig { Phases = [new RunnerPhase { Name = "lint", Command = ["python3", "lint.py"], WorkingDirectory = "{treeDir}" }] };

        Assert.True(Core.Legs.LegWorkload.ForRunner(phases, null).Build);
        Assert.True(Core.Legs.LegWorkload.ForRunner(inTheBuild, null).Build);
        Assert.False(Core.Legs.LegWorkload.ForRunner(reads, null).Build);
    }

    /// <summary>
    /// A run check runs on its leg as the runner carrying it left it, and is never built itself, so a check whose runner
    /// needs the build - requiring it, or by a step or phase naming what the build makes, on every system or on those its
    /// runOn names - builds the carrier's legs first, and says whose need it is. One whose steps could not be read is
    /// heavy, and builds nothing, whatever its runner requires: the check that runs it is refused, so a build made for it
    /// would be made for nothing.
    /// </summary>
    [Fact]
    public void ARunnerWhoseRunCheckNeedsTheBuild_BuildsItsLegsFirst()
    {
        var action = ActionKit.Parse(
            Path.Combine("actions", "sqlite", "sqlite.yml"),
            """
            steps:
              - name: self-test
                run: python3 self_test.py
              - name: recompile
                manual: true
                successPattern: '^built'
                run: python3 recompile.py --app="{product}"
              - name: profile
                manual: true
                runOn: [linux]
                successPattern: '^profiled'
                run: perf record {product}
            """);

        var light = new RunnerConfig { Action = "a/a.yml" };

        Core.Legs.LegWorkload Carrying(params string[] steps)
        {
            var check = new RunnerConfig { Action = "sqlite", Steps = steps.Length == 0 ? null : [.. steps] };

            return Core.Legs.LegWorkload.ForRunner(
                light,
                null,
                [("confirm", check, Core.Runners.StepSelection.For(check, []).Apply("confirm", action).File, false)]);
        }

        Assert.True(Carrying("recompile").Build);
        Assert.Equal(
            "step 'recompile' of runner 'confirm', which a run check names, names {product}",
            Assert.Single(Carrying("recompile").BuiltBy).Reason);
        Assert.False(Carrying().Build);
        Assert.False(Carrying("profile").Build);
        Assert.True(Carrying("profile").On("linux").Build);
        Assert.False(Carrying("profile").On("windows").Build);

        var requiring = Core.Legs.LegWorkload.ForRunner(light, null, [("confirm", new RunnerConfig { Action = "b/b.yml", RequireBuild = true }, null, false)]);

        Assert.True(requiring.Build);
        Assert.Equal("runner 'confirm', which a run check names, requires the build", Assert.Single(requiring.BuiltBy).Reason);

        var phased = new RunnerConfig { Phases = [new RunnerPhase { Name = "deps", Command = ["python3", "deps.py"], WorkingDirectory = "{buildDir}" }] };

        Assert.Equal(
            "phase 'deps' of runner 'confirm', which a run check names, names {buildDir}",
            Assert.Single(Core.Legs.LegWorkload.ForRunner(light, null, [("confirm", phased, null, false)]).BuiltBy).Reason);

        var unread = Core.Legs.LegWorkload.ForRunner(light, null, [("confirm", new RunnerConfig { Action = "b/b.yml", RequireBuild = true }, null, true)]);

        Assert.True(unread.Heavy);
        Assert.False(unread.Build);
        Assert.Empty(unread.BuiltBy);
        Assert.False(unread.On("linux").Build);
    }

    /// <summary>
    /// A leg a run would build that cannot be built - it names no project, or no toolchain for its system - is refused
    /// before any host is measured, naming every such leg, why, and what builds it; so is a step of the run's own runner
    /// naming {product} on a leg whose project declares no one file for its system; one refusal names both kinds at once.
    /// A leg the run does not build is asked nothing, and a product a run check's step names is that check's to refuse.
    /// For a runner requiring the build, the first was found only where the leg's build began: it ended the whole run
    /// once its hosts were measured, without saying what built the leg; the second was refused only after the build it
    /// had cost.
    /// </summary>
    [Fact]
    public void ALegARunWouldBuild_ThatCannotBeBuilt_OrHasNoProduct_IsRefusedBeforeAnythingStarts()
    {
        var config = new HarnessConfig
        {
            Toolchains = { ["sdk"] = new ToolchainConfig { Platforms = ["windows", "linux", "macos"] } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects =
            {
                new ProjectConfig { Name = "app", Type = "dotnet", Path = "app.csproj", BuildOutputs = [BuildOutput.Keyed([new("windows", "app.exe"), new("linux", "app")])] },
                new ProjectConfig { Name = "tool", Type = "dotnet", Path = "tool.csproj" },
            },
            Legs =
            {
                ["win"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "sdk", Project = "app" },
                ["mac"] = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug", Toolchain = "sdk", Project = "app" },
                ["lone"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" },
                ["bare"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Project = "app" },
            },
        };

        var action = ActionKit.Parse(
            Path.Combine("actions", "probe", "probe.yml"),
            """
            steps:
              - name: version
                run: python3 version.py
              - name: measure
                manual: true
                successPattern: '^measured'
                run: python3 measure.py --app="{product}"
              - name: winbench
                manual: true
                runOn: [windows]
                successPattern: '^benched'
                run: python3 bench.py --out={buildDir}
            """);

        var light = new RunnerConfig { Action = "probe" };
        var requiring = new RunnerConfig { Action = "probe", RequireBuild = true };

        Core.Legs.LegWorkload Run(RunnerConfig runner, params string[] manual)
            => Core.Legs.LegWorkload.ForRunner(runner, Core.Runners.StepSelection.For(runner, manual).Apply("probe", action).File);

        List<Core.Legs.SelectedLeg> Legs(params string[] names) => [.. names.Select(name => new Core.Legs.SelectedLeg(name, config.Legs[name]))];

        Core.Results.HarnessException Refusal(Core.Legs.LegWorkload workload, params string[] names)
            => Assert.Throws<Core.Results.HarnessException>(() => workload.RequireBuildable(config, "probe", Legs(names)));

        // A run that builds nothing is asked nothing, nor one building a leg it can build whose product it can name, nor
        // one whose step limited by runOn builds no leg of the system asked about.
        Run(light).RequireBuildable(config, "probe", Legs("lone", "bare", "mac"));
        Run(light, "measure").RequireBuildable(config, "probe", Legs("win"));
        Run(light, "winbench").RequireBuildable(config, "probe", Legs("lone", "bare"));

        var unbuildable = Refusal(Run(light, "measure"), "lone", "bare", "win");

        Assert.Equal(Core.Results.HarnessExit.ConfigInvalid, unbuildable.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                [
                    "A run of runner 'probe' cannot run every leg it reaches, so nothing was run.",
                    "It builds 2 legs first that cannot be built: leave them out of the run - with --legs, or from the runner's own "
                        + "legs - or give each a project and a toolchain to build:",
                    "  - Leg 'lone' cannot be built: it names no project, and neither defaults.project nor a single declared "
                        + "project supplies one. The run builds it first because step 'measure' names {product}.",
                    "  - Leg 'bare' cannot be built: it names no toolchain, and project 'app' declares no default toolchain for "
                        + "linux. The run builds it first because step 'measure' names {product}.",
                ]),
            unbuildable.Message);

        Assert.Contains(
            "The run builds it first because the runner requires the build.",
            Refusal(Run(requiring), "lone").Message,
            StringComparison.Ordinal);

        var productless = Refusal(Run(requiring, "measure"), "mac", "win");

        Assert.Equal(Core.Results.HarnessExit.ConfigInvalid, productless.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                [
                    "A run of runner 'probe' cannot run every leg it reaches, so nothing was run.",
                    "It runs a step or phase naming {product} where no one product fills it in: leave the leg out, or declare one "
                        + "build output for its system:",
                    "  - Leg 'mac': step 'measure' names {product}, and project 'app' declares no buildOutputs for macos.",
                ]),
            productless.Message);

        // Both kinds in one refusal, so the reader has every leg to deal with at once rather than one kind per run.
        var both = Refusal(Run(requiring, "measure"), "lone", "mac");

        Assert.Contains("It builds a leg first that cannot be built: leave it out of the run", both.Message, StringComparison.Ordinal);
        Assert.Contains("  - Leg 'lone' cannot be built: it names no project", both.Message, StringComparison.Ordinal);
        Assert.Contains("  - Leg 'mac': step 'measure' names {product}, and project 'app'", both.Message, StringComparison.Ordinal);

        // A phase of the run's own runner naming {product} is refused as a step is, and called a phase.
        var phased = new RunnerConfig { Phases = [new RunnerPhase { Name = "measure", Command = ["{product}"] }] };

        Assert.Contains(
            "  - Leg 'mac': phase 'measure' names {product}, and project 'app' declares no buildOutputs for macos.",
            Refusal(Core.Legs.LegWorkload.ForRunner(phased, null), "mac").Message,
            StringComparison.Ordinal);

        // A workload that builds every leg as such names nothing that builds it, and says only why it cannot be built.
        Assert.EndsWith(
            "  - Leg 'lone' cannot be built: it names no project, and neither defaults.project nor a single declared project "
                + "supplies one.",
            Refusal(Core.Legs.LegWorkload.BuildOnly, "lone").Message,
            StringComparison.Ordinal);

        // A product a run check's step names is the check's to refuse, when it runs; a leg the check builds and that
        // cannot be built is refused here, naming the check.
        var check = new RunnerConfig { Action = "probe", Steps = ["measure"] };
        var carrying = Core.Legs.LegWorkload.ForRunner(
            light,
            Core.Runners.StepSelection.For(light, []).Apply("probe", action).File,
            [("confirm", check, Core.Runners.StepSelection.For(check, []).Apply("confirm", action).File, false)]);

        carrying.RequireBuildable(config, "probe", Legs("mac"));

        Assert.Contains(
            "The run builds it first because step 'measure' of runner 'confirm', which a run check names, names {product}.",
            Refusal(carrying, "lone").Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A step or a runner's phase naming one of the leg's compilers - as its program, in an argument, or as its working
    /// directory - reads what the build identified, so every leg of a run that runs it is built first, as for
    /// {product}. Where no build of the leg's project identifies a compiler - another tool than CMake builds it - the
    /// run is refused before any host is measured, naming the leg, the step and why: refused after its build, the leg
    /// would have cost that build to say what the configuration already said. A run check's step naming one is the
    /// check's to refuse, when it runs.
    /// </summary>
    [Fact]
    public void AStepNamingTheLegsCompiler_BuildsItsLegFirst_AndIsRefusedWhereNoBuildIdentifiesOne()
    {
        var config = new HarnessConfig
        {
            Toolchains = { ["sdk"] = new ToolchainConfig { Platforms = ["windows", "linux"] } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects =
            {
                new ProjectConfig { Name = "engine", Type = "cmake" },
                new ProjectConfig { Name = "app", Type = "dotnet", Path = "app.csproj" },
            },
            Legs =
            {
                ["native"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "sdk", Project = "engine" },
                ["managed"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "sdk", Project = "app" },
                ["win"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "sdk", Project = "app" },
            },
        };

        var action = ActionKit.Parse(
            Path.Combine("actions", "census", "census.yml"),
            """
            steps:
              - name: version
                run: python3 version.py
              - name: census
                manual: true
                successPattern: '^counted'
                run: python3 census.py --cc "{compiler_C}" --cxx "{compiler_CXX}" --into {buildDir}
              - name: probe
                manual: true
                runOn: [linux]
                successPattern: 'Free Software'
                run: '{compiler_CXX} --version'
              - name: literal
                manual: true
                successPattern: '^said'
                run: python3 say.py {{compiler_C}} ${compiler_CXX}
            """);

        Assert.Equal([false, true, true, false], action.Steps.Select(step => step.NeedsBuild));
        Assert.Equal(["compiler_C", "compiler_CXX", "buildDir"], action.Steps[1].NamesOfTheBuild);
        Assert.Equal(["compiler_CXX"], action.Steps[2].NamesOfTheBuild);

        var light = new RunnerConfig { Action = "census", Heavy = false };

        Core.Legs.LegWorkload Run(params string[] manual)
            => Core.Legs.LegWorkload.ForRunner(light, Core.Runners.StepSelection.For(light, manual).Apply("census", action).File);

        List<Core.Legs.SelectedLeg> Legs(params string[] names) => [.. names.Select(name => new Core.Legs.SelectedLeg(name, config.Legs[name]))];

        Assert.False(Run().Build);
        Assert.False(Run("literal").Build);
        Assert.True(Run("census").Build);
        Assert.True(Run("census").Heavy);
        Assert.True(Run("probe").On("linux").Build);
        Assert.False(Run("probe").On("windows").Build);
        Assert.Equal("step 'census' names {compiler_C} and {compiler_CXX} and {buildDir}", Assert.Single(Run("census").BuiltBy).Reason);

        var phased = new RunnerConfig { Phases = [new RunnerPhase { Name = "probe", Command = ["{compiler_C}", "-dumpversion"] }] };
        var inside = new RunnerConfig { Phases = [new RunnerPhase { Name = "probe", Command = ["python3", "probe.py"], WorkingDirectory = "{compiler_CXX}" }] };

        Assert.True(Core.Legs.LegWorkload.ForRunner(phased, null).Build);
        Assert.Equal("phase 'probe' names {compiler_CXX}", Assert.Single(Core.Legs.LegWorkload.ForRunner(inside, null).BuiltBy).Reason);

        // A leg CMake builds is asked nothing more: which compiler its build identifies is the build's to say.
        Run("census").RequireBuildable(config, "census", Legs("native"));
        Run("probe").RequireBuildable(config, "census", Legs("native", "win"));
        Run().RequireBuildable(config, "census", Legs("managed"));
        Run("literal").RequireBuildable(config, "census", Legs("managed"));

        var refusal = Assert.Throws<Core.Results.HarnessException>(() => Run("census", "probe").RequireBuildable(config, "census", Legs("native", "managed", "win")));

        Assert.Equal(Core.Results.HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                [
                    "A run of runner 'census' cannot run every leg it reaches, so nothing was run.",
                    "It runs a step or phase naming a compiler where no build of the leg identifies one: leave the leg out, or take the "
                        + "name out:",
                    "  - Leg 'managed': step 'census' names {compiler_C} and {compiler_CXX}, and project 'app' is built by dotnet, which "
                        + "identifies no compiler: only a build CMake configures records one.",
                    "  - Leg 'managed': step 'probe' names {compiler_CXX}, and project 'app' is built by dotnet, which identifies no "
                        + "compiler: only a build CMake configures records one.",
                    "  - Leg 'win': step 'census' names {compiler_C} and {compiler_CXX}, and project 'app' is built by dotnet, which "
                        + "identifies no compiler: only a build CMake configures records one.",
                ]),
            refusal.Message);

        // A phase of the run's own runner is refused as a step is, and called a phase.
        Assert.Contains(
            "  - Leg 'managed': phase 'probe' names {compiler_C}, and project 'app' is built by dotnet",
            Assert.Throws<Core.Results.HarnessException>(() => Core.Legs.LegWorkload.ForRunner(phased, null).RequireBuildable(config, "census", Legs("managed"))).Message,
            StringComparison.Ordinal);

        // A run check's step naming one is the check's to refuse, when it runs.
        var check = new RunnerConfig { Action = "census", Steps = ["census"] };
        var carrying = Core.Legs.LegWorkload.ForRunner(
            light,
            Core.Runners.StepSelection.For(light, []).Apply("census", action).File,
            [("confirm", check, Core.Runners.StepSelection.For(check, []).Apply("confirm", action).File, false)]);

        Assert.True(carrying.Build);
        carrying.RequireBuildable(config, "census", Legs("managed"));
    }

    /// <summary>
    /// What a run's refusal gives for a leg is what builds that leg, on its own system - a step limited by runOn to
    /// another names nothing there - and a leg is refused for its product only where a step names {product}: a step
    /// naming the build directory alone reads no one file, so a project declaring several asks nothing of it, while one
    /// naming the product is refused, naming the files there are to choose from.
    /// </summary>
    [Fact]
    public void ARunsRefusal_NamesWhatBuildsTheLegOnItsSystem_AndAProductOnlyWhereOneIsNamed()
    {
        var config = new HarnessConfig
        {
            Toolchains = { ["sdk"] = new ToolchainConfig { Platforms = ["windows", "linux", "macos"] } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "many", Type = "dotnet", Path = "many.csproj", BuildOutputs = [BuildOutput.Everywhere("a"), BuildOutput.Everywhere("b")] } },
            Legs =
            {
                ["lone"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Project = "many" },
                ["twice"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Toolchain = "sdk", Project = "many" },
            },
        };

        var action = ActionKit.Parse(
            Path.Combine("actions", "probe", "probe.yml"),
            """
            steps:
              - name: version
                run: python3 version.py
              - name: measure
                manual: true
                successPattern: '^measured'
                run: python3 measure.py --app="{product}"
              - name: winbench
                manual: true
                runOn: [windows]
                successPattern: '^benched'
                run: python3 bench.py --out={buildDir}
              - name: linbench
                manual: true
                runOn: [linux]
                successPattern: '^benched'
                run: python3 bench.py --out={buildDir}
            """);

        var runner = new RunnerConfig { Action = "probe" };

        Core.Legs.LegWorkload Run(params string[] manual)
            => Core.Legs.LegWorkload.ForRunner(runner, Core.Runners.StepSelection.For(runner, manual).Apply("probe", action).File);

        List<Core.Legs.SelectedLeg> Legs(params string[] names) => [.. names.Select(name => new Core.Legs.SelectedLeg(name, config.Legs[name]))];

        // 'lone' names no toolchain, nor does its project for linux, so it cannot be built; winbench builds no linux leg.
        var lone = Assert.Throws<Core.Results.HarnessException>(() => Run("measure", "winbench").RequireBuildable(config, "probe", Legs("lone")));

        Assert.Contains("The run builds it first because step 'measure' names {product}.", lone.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("winbench", lone.Message, StringComparison.Ordinal);

        Run("linbench").RequireBuildable(config, "probe", Legs("twice"));

        var several = Assert.Throws<Core.Results.HarnessException>(() => Run("measure", "linbench").RequireBuildable(config, "probe", Legs("twice")));

        Assert.Contains(
            "  - Leg 'twice': step 'measure' names {product}, and project 'many' declares 2 buildOutputs for linux (a, b)",
            several.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("linbench", several.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner's own phase naming what the build makes builds its legs, and a build is heavy: one saying heavy is false
    /// beside it is refused, as one beside requireBuild is, rather than believed. Every phase that builds is named, and
    /// requireBuild beside them, and dropping requireBuild is not offered where a phase would still build: named one at a
    /// time, the fix offered for the first led to a second refusal.
    /// </summary>
    [Fact]
    public void ARunnerSayingItIsNotHeavy_WhileAPhaseOfItBuilds_IsRefused()
    {
        var exception = LoadInvalid("""
            { "predefinedRunners": { "deps": { "phases": [ { "name": "deps", "command": ["python3", "deps.py", "{buildDir}"] } ], "heavy": false } } }
            """);

        Assert.Contains(
            "predefined runner 'deps' says heavy is false, and builds its legs first, which is heavy - phase 'deps' names "
            + "{buildDir}: leave heavy out",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("drop requireBuild", exception.Message, StringComparison.Ordinal);

        var both = LoadInvalid("""
            {
              "predefinedRunners": {
                "deps": {
                  "requireBuild": true,
                  "heavy": false,
                  "phases": [
                    { "name": "deps", "command": ["python3", "deps.py"], "workingDirectory": "{buildDir}" },
                    { "name": "lint", "command": ["python3", "lint.py"] },
                    { "name": "measure", "command": ["{product}", "--{{literal}}"] }
                  ]
                }
              }
            }
            """);

        Assert.Contains(
            "predefined runner 'deps' says heavy is false, and builds its legs first, which is heavy - the runner requires the "
            + "build; phase 'deps' names {buildDir}; phase 'measure' names {product}: leave heavy out",
            both.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("drop requireBuild", both.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner's phases are told apart by name - each writes the log its name names, a resumed run skips the ones its
    /// name says were done, a refusal names one by it - so a blank name, and one given twice in any case, are refused when
    /// the file is read, rather than once a leg has been built for the run.
    /// </summary>
    [Fact]
    public void ARunnersPhase_WithABlankName_OrANameGivenTwice_IsRefused()
    {
        var exception = LoadInvalid("""
            {
              "predefinedRunners": {
                "blank": { "phases": [ { "name": " ", "command": ["python3", "a.py"] } ] },
                "twice": {
                  "phases": [
                    { "name": "measure", "command": ["python3", "a.py"] },
                    { "name": "Measure", "command": ["python3", "b.py"] },
                    { "name": "report", "command": ["python3", "c.py"] }
                  ]
                }
              }
            }
            """);

        Assert.Contains("predefined runner 'blank' has a phase with a blank name", exception.Message, StringComparison.Ordinal);
        Assert.Contains("predefined runner 'twice' names phase 'measure' more than once", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("phase 'report' more than once", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner's cleanDirectories are deleted, with all they hold, before its first step starts - after the build a run
    /// makes first - so one that is, holds or is inside what the run stands on is refused when the file is read: where the
    /// builds are kept, which a run building first would delete straight after making, the harness's own directory, and
    /// what a tree never moves; and so is the whole tree, one outside it and one misspelled. Compared ignoring case. A
    /// directory of the run's own is taken.
    /// </summary>
    [Fact]
    public void CleanDirectories_ThatHoldWhatTheRunStandsOn_AreRefused()
    {
        var exception = LoadInvalid("""
            {
              "worktrees": { "root": ".worktrees" },
              "predefinedRunners": {
                "corpus": {
                  "phases": [ { "name": "corpus", "command": ["python3", "corpus.py"] } ],
                  "cleanDirectories": [
                    "out/run", "build", "Build/x64-gcc-debug", "./build", ".git", ".harness-config/runner",
                    ".worktrees", ".orchestrators/agent", ".", "../elsewhere", "/abs/scratch", "out//run", "builds", "src/build"
                  ]
                }
              }
            }
            """);

        const string Setting = "predefined runner 'corpus' cleanDirectories";

        foreach (var (declared, held) in new[]
        {
            ("build", "build"), ("Build/x64-gcc-debug", "build"), ("./build", "build"), (".git", ".git"),
            (".harness-config/runner", ".harness-config"), (".worktrees", ".worktrees"), (".orchestrators/agent", ".orchestrators"),
        })
        {
            Assert.Contains($"{Setting} names '{declared}', which is, holds or is inside '{held}', ", exception.Message, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"{Setting} names 'build', which is, holds or is inside 'build', where every leg's build is kept: a build stays "
            + "incremental, and a run that builds first would delete what it had just built",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains($"{Setting} names '.', which is the leg's whole tree", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"{Setting} entry '../elsewhere' must be a relative path inside the tree, without '..'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"{Setting} entry '/abs/scratch' must be a relative path inside the tree, without '..'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"{Setting} names 'out//run', which holds a '.' segment or a doubled separator", exception.Message, StringComparison.Ordinal);

        foreach (var taken in new[] { "'out/run'", "'builds'", "'src/build'" })
        {
            Assert.DoesNotContain($"{Setting} names {taken}", exception.Message, StringComparison.Ordinal);
        }

        // The worktrees are made inside the harness's own directory unless the file says otherwise: one entry, one problem.
        var nested = LoadInvalid("""
            { "predefinedRunners": { "corpus": { "phases": [ { "name": "corpus", "command": ["python3", "corpus.py"] } ], "cleanDirectories": [".harness-config/worktrees/a"] } } }
            """);

        Assert.Single(nested.Message.Split('\n'), line => line.Contains("cleanDirectories names", StringComparison.Ordinal));

        // One holding what the run stands on is refused as one inside it is: deleted, it takes that with it.
        var holding = LoadInvalid("""
            {
              "worktrees": { "root": "scratch/worktrees" },
              "predefinedRunners": { "corpus": { "phases": [ { "name": "corpus", "command": ["python3", "corpus.py"] } ], "cleanDirectories": ["scratch"] } }
            }
            """);

        Assert.Contains(
            "predefined runner 'corpus' cleanDirectories names 'scratch', which is, holds or is inside 'scratch/worktrees', which a "
            + "tree never moves",
            holding.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner of phases takes "heavy" on itself: a phase declaring it is refused, as every key only a step takes is, and
    /// the refusal says where the runner's weight is declared.
    /// </summary>
    [Fact]
    public void HeavyOnAPhase_IsRefused_SayingItIsTheRunnersToSay()
    {
        var exception = LoadInvalid("""
            { "predefinedRunners": { "guard": { "phases": [ { "name": "check", "command": ["python3", "check.py"], "heavy": true } ] } } }
            """);

        Assert.Contains("predefined runner 'guard' phase 'check' declares 'heavy', which only a step of an action file takes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Whether a runner's legs are heavy is said on the runner itself, as \"heavy\": true beside its phases.", exception.Message, StringComparison.Ordinal);
    }

    private static HarnessConfig LoadValid(string json)
    {
        using var temp = new TempDirectory();
        return CreateStore().Load(temp.WriteFile("config.json", json));
    }

    /// <summary>
    /// A key this tool retired is refused like any unknown one, saying what took its place: told only
    /// that compilerCacheDirectory is unknown, a reader deletes the line and loses the per-host store
    /// it was there for.
    /// </summary>
    [Theory]
    [InlineData("""{ "hosts": { "local": { "compilerCacheDirectory": "/cache" } } }""", "hosts.local compilerCacheDirectory")]
    [InlineData("""{ "hosts": { "ssh": { "mac.mini": { "repositoryPath": "/r", "CompilerCacheDirectory": "/cache" } } } }""", "hosts.ssh 'mac.mini' compilerCacheDirectory")]
    [InlineData("""{ "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "~/r", "compilerCacheDirectory": "/cache" } } } }""", "hosts.wsl 'Ubuntu' compilerCacheDirectory")]
    public void ARetiredKey_IsRefused_SayingWhatTookItsPlace(string json, string where)
    {
        var exception = LoadInvalid(json);

        Assert.Contains($"{where} is no longer read", exception.Message, StringComparison.Ordinal);
        Assert.Contains("\"CCACHE_DIR\"", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An unknown key that was never read is refused as unknown, with nothing said to take its place.</summary>
    [Fact]
    public void AnUnknownKey_IsNotMistakenForARetiredOne()
    {
        var exception = LoadInvalid("""{ "hosts": { "local": { "compilerCache": "/cache" } } }""");

        Assert.Contains("compilerCache", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer read", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key only a step of an action file takes, written on a runner's phase, is refused naming the
    /// runner, the phase and the key, pointing to an action file's step, where it is read, and saying
    /// what a phase does take; for outputs and persist, for the reason <see cref="RunnerPhase.Outputs"/> gives.
    /// </summary>
    [Theory]
    [InlineData("""{ "name": "go", "command": ["tool"], "outputs": ["result.txt"] }""", "phase 'go' declares 'outputs', which")]
    [InlineData("""{ "name": "go", "command": ["tool"], "Persist": true }""", "phase 'go' declares 'persist', which")]
    [InlineData("""{ "name": "go", "command": ["tool"], "outputs": ["result.txt"], "persist": true }""", "phase 'go' declares 'outputs', 'persist', which")]
    [InlineData("""{ "command": ["tool"], "runOn": ["linux"] }""", "phase #1 declares 'runOn', which")]
    public void APhase_DeclaringAKeyOnlyAStepTakes_IsRefused_PointingToAnActionFile(string phase, string expected)
    {
        var exception = LoadInvalid($$"""
            { "predefinedRunners": { "bench": { "phases": [ {{phase}} ] } } }
            """);

        Assert.Contains($"predefined runner 'bench' {expected} only a step of an action file takes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Declare the work as a step of an action file", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"A phase takes '{string.Join("', '", KeyDescription.Names(ConfigKeys.Of<RunnerPhase>()))}'.",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>A phase takes stepName; what it does is pinned where a run reports it.</summary>
    [Fact]
    public void APhase_TakesStepName()
    {
        var config = LoadValid("""
            { "predefinedRunners": { "bench": { "phases": [ { "name": "go", "command": ["tool"], "stepName": "measure" } ] } } }
            """);

        Assert.Equal("measure", Assert.Single(config.PredefinedRunners["bench"].Phases).StepName);
    }

    /// <summary>
    /// A step's key on a phase is refused naming it however the file is laid out: behind a runner of an
    /// action and a phase that declares none, in any case, among comments and trailing commas, and on a
    /// phase named only by its place.
    /// </summary>
    [Theory]
    [InlineData("""{ "predefinedRunners": { "corpus": { "action": "corpus/corpus.yml" }, "bench": { "phases": [ { "name": "warm", "command": ["tool"] }, { "name": "go", "command": ["tool"], "outputs": ["r.txt"] } ] } } }""", "phase 'go' declares 'outputs'")]
    [InlineData("""{ "PredefinedRunners": { "bench": { "Phases": [ { "name": "go", "command": ["tool"], "outputs": ["r.txt"] } ] } } }""", "phase 'go' declares 'outputs'")]
    [InlineData("{ // hand edited\n \"predefinedRunners\": { \"bench\": { \"phases\": [ { \"name\": \"go\", \"command\": [\"tool\"], \"outputs\": [\"r.txt\"], }, ], }, }, }", "phase 'go' declares 'outputs'")]
    [InlineData("""{ "predefinedRunners": { "bench": { "phases": [ 5, { "command": ["tool"], "runOn": ["linux"] } ] } } }""", "phase #2 declares 'runOn'")]
    public void AStepsKeyOnAPhase_IsRefused_HoweverTheFileIsLaidOut(string json, string expected)
        => Assert.Contains($"predefined runner 'bench' {expected}", LoadInvalid(json).Message, StringComparison.Ordinal);

    /// <summary>
    /// A key written twice is refused naming it, in any case and whatever it holds: read, the file kept
    /// the last copy's value and dropped the first's without a word.
    /// </summary>
    [Theory]
    [InlineData("""{ "sync": { "maxDeleteFraction": 0.5, "maxDeleteFraction": 0.1 } }""", "'maxDeleteFraction'")]
    [InlineData("""{ "sync": { "maxDeleteFraction": 0.5, "MaxDeleteFraction": 0.1 } }""", "'maxDeleteFraction'")]
    [InlineData("""{ "predefinedRunners": { "a": { "phases": [ { "name": "go", "command": ["tool"] } ] } }, "PredefinedRunners": { "b": { "phases": [ { "name": "go", "command": ["tool"] } ] } } }""", "'predefinedRunners'")]
    [InlineData("""{ "predefinedRunners": { "a": { "legs": ["x"], "legs": ["y"], "phases": [ { "name": "go", "command": ["tool"] } ] } } }""", "'legs'")]
    public void AKeyWrittenTwice_IsRefused_NamingIt(string json, string key)
    {
        var exception = LoadInvalid(json);

        Assert.Contains("Duplicate property", exception.Message, StringComparison.Ordinal);
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step's key in the second copy of a section written twice is refused too, where the check that
    /// points to an action file reads only the first: the reader refuses it, as any key nothing reads.
    /// </summary>
    [Theory]
    [InlineData("""{ "predefinedRunners": {}, "PredefinedRunners": { "bench": { "phases": [ { "name": "go", "command": ["tool"], "outputs": ["r.txt"] } ] } } }""")]
    [InlineData("""{ "predefinedRunners": { "bench": { "phases": [], "Phases": [ { "name": "go", "command": ["tool"], "outputs": ["r.txt"] } ] } } }""")]
    public void AStepsKeyInASecondCopyOfASection_IsRefused(string json)
        => Assert.Contains("'outputs'", LoadInvalid(json).Message, StringComparison.Ordinal);

    /// <summary>
    /// A key naming a member the reader never reads is refused like any other key nothing reads:
    /// sync's computed effectiveNeverTransfer, and a phase's outputs read on its own, without the check
    /// that says where it belongs.
    /// </summary>
    [Fact]
    public void AKeyNamingAMemberTheReaderIgnores_IsRefusedLikeAnyUnknownKey()
    {
        Assert.Contains("effectiveNeverTransfer", LoadInvalid("""{ "sync": { "effectiveNeverTransfer": ["x"] } }""").Message, StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<RunnerPhase>(
            """{ "name": "go", "command": ["tool"], "outputs": ["r.txt"] }""",
            JsonConfigOptions.Default));
    }

    /// <summary>A runner section of the wrong shape is refused as the file, never reported as a defect in the tool.</summary>
    [Theory]
    [InlineData("""{ "predefinedRunners": [] }""")]
    [InlineData("""{ "predefinedRunners": { "bench": null } }""")]
    [InlineData("""{ "predefinedRunners": { "bench": { "phases": {} } } }""")]
    public void AMisshapenRunnerSection_IsRefusedAsTheFile(string json) => LoadInvalid(json);

    /// <summary>An expected exception that names no messages is refused as the file is read, saying which key.</summary>
    [Fact]
    public void AnExpectedExceptionNamingNoMessages_IsRefused()
    {
        var exception = LoadInvalid("""
            { "predefinedRunners": { "bench": {
                "phases": [ { "name": "go", "command": ["tool"] } ],
                "expectedExceptions": [ { "exceptionType": "IOException", "message": "m", "earnedOn": "2026-01-01", "earnedAt": "a leg", "mechanism": "a lock", "anchor": "A-1" } ] } } }
            """);

        Assert.Contains("messages", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key or a value spelling half a character - an escape of a lone surrogate - is refused as the
    /// file, naming the line and what it said, wherever it stands: no text can hold it, and reading one
    /// stopped the command as a defect in this tool.
    /// </summary>
    [Theory]
    [InlineData("{ \"predefinedRunners\": { \"b\\ud800\": { \"phases\": [ { \"name\": \"go\", \"command\": [\"tool\"] } ] } } }", 1, "b\\ud800")]
    [InlineData("{\n  \"sync\": { \"never\\udc00\": [\"x\"] }\n}", 2, "never\\udc00")]
    [InlineData("{\n  \"predefinedRunners\": {\n    \"bench\": { \"description\": \"half \\ud83d\", \"phases\": [ { \"name\": \"go\", \"command\": [\"tool\"] } ] } } }", 3, "half \\ud83d")]
    [InlineData("{ \"predefinedRunners\": { \"bench\": { \"legs\": [\"\\udfff\"], \"phases\": [ { \"name\": \"go\", \"command\": [\"tool\"] } ] } } }", 1, "\\udfff")]
    [InlineData("{ \"\\ud800\": 1 }", 1, "\\ud800")]
    [InlineData("{ \"hosts\": { \"ssh\": { \"m\\ud800\": { \"repositoryPath\": \"~/r\" } } } }", 1, "m\\ud800")]
    public void AStringSpellingHalfACharacter_IsRefusedAsTheFile_NamingItsLine(string json, int line, string spelt)
    {
        var exception = LoadInvalid(json);

        Assert.Contains($"line {line}: '{spelt}' spells half a character", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A whole character, spelt as the pair of escapes it takes, is read as that character.</summary>
    [Fact]
    public void ACharacterSpeltAsItsPairOfEscapes_IsRead()
    {
        var config = LoadValid("{ \"predefinedRunners\": { \"bench\": { \"description\": \"\\ud83d\\ude00\", \"phases\": [ { \"name\": \"go\", \"command\": [\"tool\"] } ] } } }");

        Assert.Equal("\U0001F600", config.PredefinedRunners["bench"].Description);
    }

    private static ConfigException LoadInvalid(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", json);

        return Assert.Throws<ConfigException>(() => CreateStore().Load(path));
    }
}
