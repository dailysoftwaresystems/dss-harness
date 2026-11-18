using NSubstitute;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// What one host's own directory under .harness-config declares, and what stops it from being used.
/// Every value here is fictitious: the layout exists precisely so that no address, user, key or
/// credential is ever written into a file git tracks, and a test fixture is such a file.
/// </summary>
public sealed class HostSecretsStoreTests
{
    private const string Item = "build-box";

    [Fact]
    public void AnSshItem_DeclaresWhereTheMachineIs_AndWhatProvesEachEnd()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\nPORT=2222\n");

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Equal(string.Empty, read.Problem);
        var item = Assert.IsType<SshItem>(read.Item);
        Assert.Equal("host.invalid", item.Address);
        Assert.Equal("harness", item.User);
        Assert.Equal(2222, item.Port);
        PathAssert.Same(repository.Combine(".harness-config", "sshItems", Item, ".key"), item.KeyFile);
        PathAssert.Same(repository.Combine(".harness-config", "sshItems", Item, "known_hosts"), item.KnownHostsFile);
        Assert.Null(item.Superuser);
    }

    [Fact]
    public void AnSshItem_WithoutAPort_ConnectsOnTwentyTwo()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "# only what is needed\nADDRESS = host.invalid \nUSER=\"harness\"\n");

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.NotNull(read.Item);
        Assert.Equal(SshItem.DefaultPort, read.Item.Port);

        // Surrounding whitespace and one pair of quotes are removed, so a value written either way means
        // the same thing.
        Assert.Equal("host.invalid", read.Item.Address);
        Assert.Equal("harness", read.Item.User);
    }

    [Fact]
    public void AnItemDirectoryThatIsNotThere_IsNamed_WithWhatItHasToHold()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Equal(
            $"'.harness-config/sshItems/{Item}' does not exist; create it, with a '.env' declaring ADDRESS and USER",
            read.Problem);
    }

    /// <summary>
    /// In a copy the harness synced to a host, connection data is absent by design: it is gitignored and never
    /// transferred, so the host holds no key of its own. The store says only that it is absent - never to
    /// create it there, which would have the host reach a machine over ssh to run work it was dispatched.
    /// Why it is absent is the connector's to say, as it says it for every kind of host a copy cannot reach.
    /// </summary>
    [Theory]
    [InlineData("ssh")]
    [InlineData("wsl")]
    public void AnItemDirectoryMissingFromAHostsOwnCopy_IsSaidAsAbsent_NeverAsSomethingToCreate(string kind)
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        repository.WriteFile(Path.Combine(".harness-config", HarnessLayout.SyncedCopyMarkerName), """{"Adopted":false,"Completed":true}""");

        var problem = kind == "ssh"
            ? fixture.Store.ReadSshItem(fixture.Layout, Item).Problem
            : fixture.Store.ReadWslItem(fixture.Layout, Item).Problem;

        var directory = kind == "ssh" ? "sshItems" : "wslDistros";
        Assert.Equal($"'.harness-config/{directory}/{Item}' does not exist", problem);
    }

    [Fact]
    public void AKeyTheEnvDoesNotDeclare_IsNamed_WithTheFileItBelongsIn()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\n");

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains($"'.harness-config/sshItems/{Item}/.env' declares no 'USER'", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankValue_IsNamed_RatherThanUsed()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=\nUSER=harness\n");

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains($"'ADDRESS' in '.harness-config/sshItems/{Item}/.env' is blank", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void APortThatIsNotANumber_IsRefused_BeforeSshIsGivenIt()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\nPORT=twenty-two\n");

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains("is 'twenty-two', which is not a port number", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingKeyFile_IsNamed()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\n", key: null);

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains($"'.harness-config/sshItems/{Item}/.key' does not exist", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyOtherUsersCanRead_IsRefused_WithTheCommandThatFixesIt()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\n");
        fixture.Permissions.IsPrivate(Arg.Any<string>()).Returns(false);

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains("so ssh ignores it", read.Problem, StringComparison.Ordinal);
        Assert.Contains("chmod 600", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnvOtherUsersCanChange_IsRefused_BecauseItDecidesWhichMachineIsReached()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\n");
        fixture.Permissions.IsWritableByOthers(Arg.Any<string>()).Returns(true);

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains("can point this host at another machine", read.Problem, StringComparison.Ordinal);
        Assert.Contains("chmod 600", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownHostsOtherUsersCanChange_IsRefused_BecauseAnotherMachineCouldAnswerForTheHost()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\n");
        fixture.Permissions
            .IsWritableByOthers(Arg.Is<string>(path => path.EndsWith("known_hosts", StringComparison.Ordinal)))
            .Returns(true);

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains("another machine answer as this host", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionsThatCannotBeRead_StopThisHostAlone()
    {
        // A problem of this host alone: it must not abort the measurement of every other host.
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\n");
        fixture.Permissions
            .IsWritableByOthers(Arg.Any<string>())
            .Returns(_ => throw new UnauthorizedAccessException("Access to the path is denied."));

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Contains("could not be read: Access to the path is denied.", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AWslItem_DeclaresTheDistribution_AndTheCredentialAnInstallNeeds()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        repository.WriteFile(
            Path.Combine(".harness-config", "wslDistros", "wsl-a", ".env"),
            "DISTRO=Example-Linux\nSUDO_PASSWORD=not-a-real-password\n");

        var read = fixture.Store.ReadWslItem(fixture.Layout, "wsl-a");

        Assert.NotNull(read.Item);
        Assert.Equal("Example-Linux", read.Item.Distribution);
        Assert.NotNull(read.Item.Superuser);
        Assert.Equal("not-a-real-password", read.Item.Superuser.Reveal());
    }

    [Fact]
    public void ACredential_IsNeverWhatAnythingPrints()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        repository.WriteFile(
            Path.Combine(".harness-config", "wslDistros", "wsl-a", ".env"),
            "DISTRO=Example-Linux\nSUDO_PASSWORD=not-a-real-password\n");

        var read = fixture.Store.ReadWslItem(fixture.Layout, "wsl-a");

        // A record prints its members, and an interpolated message and a debugger both call ToString,
        // so the wrapper is what keeps a credential out of every one of them.
        Assert.DoesNotContain("not-a-real-password", read.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-password", $"{read.Item!.Superuser}", StringComparison.Ordinal);
    }

    [Fact]
    public void AWslItemWithoutItsDistribution_IsNamed()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        repository.WriteFile(Path.Combine(".harness-config", "wslDistros", "wsl-a", ".env"), "SUDO_PASSWORD=x\n");

        var read = fixture.Store.ReadWslItem(fixture.Layout, "wsl-a");

        Assert.Null(read.Item);
        Assert.Contains("declares no 'DISTRO'", read.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A=1\r\nB = two \r\n", "1", "two")]
    [InlineData("#A=skipped\nA=1\nB='two'\nnot a pair\n", "1", "two")]
    public void ADotEnvFile_ReadsNamesAndValues_AndSkipsWhatIsNeither(string text, string first, string second)
    {
        var values = DotEnvFile.Read(text);

        // A file written on Windows ends each line with a carriage return, which is part of no value:
        // a port read as "22\r" is not a number and an address read that way resolves nowhere.
        Assert.Equal(first, values["A"]);
        Assert.Equal(second, values["B"]);
        Assert.False(values.ContainsKey("not a pair"));
    }

    /// <summary>
    /// An environment file holding a key the item declares secret is refused where other users can
    /// read it, naming the key: the rule reads the declared kind of key, not a name spelled inside
    /// it, so a second reader with a secret of another name is held to it by declaring the key. The
    /// readable check is scoped to the <c>.env</c> on the ssh side, whose key file of its own would
    /// otherwise be the refusal this measures.
    /// </summary>
    [Theory]
    [InlineData("ssh")]
    [InlineData("wsl")]
    public void AnEnvHoldingAKeyDeclaredSecret_OtherUsersCanRead_IsRefused_NamingTheKey(string kind)
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);

        if (kind == "ssh")
        {
            fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\nSUDO_PASSWORD=hunter2\n");
            fixture.Permissions
                .IsPrivate(Arg.Is<string>(path => path.EndsWith(".env", StringComparison.Ordinal)))
                .Returns(false);
        }
        else
        {
            repository.WriteFile(
                Path.Combine(".harness-config", "wslDistros", Item, ".env"),
                "DISTRO=Ubuntu\nSUDO_PASSWORD=hunter2\n");
            fixture.Permissions.IsPrivate(Arg.Any<string>()).Returns(false);
        }

        var problem = kind == "ssh"
            ? fixture.Store.ReadSshItem(fixture.Layout, Item).Problem
            : fixture.Store.ReadWslItem(fixture.Layout, Item).Problem;
        var slot = kind == "ssh" ? "sshItems" : "wslDistros";

        Assert.Equal(
            $"other users can read '.harness-config/{slot}/{Item}/.env', and it holds this host's SUDO_PASSWORD; "
                + $"make it private with: chmod 600 '{repository.Combine(".harness-config", slot, Item, ".env")}'",
            problem);
    }

    /// <summary>
    /// A key the item declares optional may be written blank: it means none, and no refusal comes of
    /// it - not even where other users can read the file, which then holds no credential worth
    /// reading: a host whose superuser needs no password is not made to tighten a file for nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOptionalKeyWrittenBlank_MeansNone_IsNoRefusal_AndHandsOnNoCredential(bool readableByOthers)
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\nSUDO_PASSWORD=\n");

        if (readableByOthers)
        {
            fixture.Permissions
                .IsPrivate(Arg.Is<string>(path => path.EndsWith(".env", StringComparison.Ordinal)))
                .Returns(false);
        }

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Equal(string.Empty, read.Problem);
        Assert.NotNull(read.Item);
        Assert.Null(read.Item.Superuser);
    }

    /// <summary>
    /// An item may set where its host keeps this machine's copies, REPOSITORY_PATH, over config.json's repositoryPath: read
    /// as written, for an ssh host and a WSL distribution alike, and told with the distribution a WSL item names - never
    /// its credential - by the one reading every reader of a host's copies asks.
    /// </summary>
    [Theory]
    [InlineData("ssh")]
    [InlineData("wsl")]
    public void AnItemsRepositoryPath_IsReadAsWritten_AndToldWithItsDistribution(string kind)
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        var host = kind == "ssh" ? HostId.Ssh(Item) : HostId.Wsl(Item);

        if (kind == "ssh")
        {
            fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\nREPOSITORY_PATH=~/mine/app\n");
        }
        else
        {
            repository.WriteFile(
                Path.Combine(".harness-config", "wslDistros", Item, ".env"),
                "DISTRO=Example-Linux\nSUDO_PASSWORD=not-a-real-password\nREPOSITORY_PATH=~/mine/app\n");
        }

        var facts = fixture.Store.ReadFacts(fixture.Layout, host);

        Assert.Equal(
            new ItemFacts
            {
                File = $".harness-config/{(kind == "ssh" ? "sshItems" : "wslDistros")}/{Item}/.env",
                Present = true,
                RepositoryPath = "~/mine/app",
                Distribution = kind == "ssh" ? null : "Example-Linux",
            },
            facts);
        Assert.Equal("~/mine/app", kind == "ssh"
            ? fixture.Store.ReadSshItem(fixture.Layout, Item).Item!.RepositoryPath
            : fixture.Store.ReadWslItem(fixture.Layout, Item).Item!.RepositoryPath);
    }

    /// <summary>
    /// An item's REPOSITORY_PATH is held to the rule config.json's repositoryPath is, by the one owner of it, and refused
    /// naming the file to fix - a WSL distribution's without a drive or a share, which names nothing inside it.
    /// </summary>
    [Theory]
    [InlineData("ssh", "src/app", "must be absolute, or start with ~/ for the home directory")]
    [InlineData("wsl", "C:/src/app", "must be absolute, or start with ~/ for the home directory inside the distribution")]
    [InlineData("ssh", "~/src/..", "has a '.' or '..' segment; name the directory directly")]
    [InlineData("wsl", "~/", "is a home or root directory; name a directory of its own for the copy")]
    public void AnItemsRepositoryPathBreakingTheRule_IsRefused_NamingTheFile(string kind, string path, string problem)
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        var slot = kind == "ssh" ? "sshItems" : "wslDistros";

        if (kind == "ssh")
        {
            fixture.WriteSshItem(Item, $"ADDRESS=host.invalid\nUSER=harness\nREPOSITORY_PATH={path}\n");
        }
        else
        {
            repository.WriteFile(Path.Combine(".harness-config", slot, Item, ".env"), $"DISTRO=Example-Linux\nREPOSITORY_PATH={path}\n");
        }

        var expected = $"REPOSITORY_PATH '{path}' in '.harness-config/{slot}/{Item}/.env' {problem}";

        Assert.Equal(expected, kind == "ssh" ? fixture.Store.ReadSshItem(fixture.Layout, Item).Problem : fixture.Store.ReadWslItem(fixture.Layout, Item).Problem);
        Assert.Equal(
            new ItemFacts { File = $".harness-config/{slot}/{Item}/.env", Present = true, Problem = expected },
            fixture.Store.ReadFacts(fixture.Layout, kind == "ssh" ? HostId.Ssh(Item) : HostId.Wsl(Item)));
    }

    /// <summary>
    /// A key the item is not read for is refused, naming the keys it is, never passed over: a misspelt REPOSITORY_PATH
    /// would leave the copies where they were while whoever wrote it believed them moved.
    /// </summary>
    [Fact]
    public void AKeyTheItemIsNotReadFor_IsRefused_NamingTheKeysItIs()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\nREPOSTORY_PATH=~/mine/app\n");

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Null(read.Item);
        Assert.Equal(
            $"'.harness-config/sshItems/{Item}/.env' declares 'REPOSTORY_PATH', which is no key it is read for; "
                + "the keys are ADDRESS, USER, PORT, SUDO_PASSWORD, REPOSITORY_PATH",
            read.Problem);
    }

    /// <summary>An item this machine does not hold declares nothing, and is no problem: its host's copies are where config.json says.</summary>
    [Fact]
    public void AnItemThisMachineDoesNotHold_DeclaresNothing()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);

        Assert.Equal(new ItemFacts { File = $".harness-config/wslDistros/{Item}/.env" }, fixture.Store.ReadFacts(fixture.Layout, HostId.Wsl(Item)));
    }

    private sealed class Fixture
    {
        private readonly TempDirectory _repository;

        public Fixture(TempDirectory repository)
        {
            _repository = repository;

            Permissions = Substitute.For<IFilePermissions>();
            Permissions.IsPrivate(Arg.Any<string>()).Returns(true);
            Permissions.IsWritableByOthers(Arg.Any<string>()).Returns(false);

            Layout = new HarnessLayout(repository.Path, repository.Path);
            Store = new HostSecretsStore(
                new PhysicalFileSystem(FilePermissionsFactory.Create()),
                Permissions,
                HostDoubles.Platform(PlatformId.Linux));
        }

        public IFilePermissions Permissions { get; }

        public HarnessLayout Layout { get; }

        public IHostSecretsStore Store { get; }

        /// <summary>Writes one ssh item's directory: its settings, its key, and the host keys it accepts.</summary>
        public void WriteSshItem(string name, string environment, string? key = "not a real key")
        {
            _repository.WriteFile(Path.Combine(".harness-config", "sshItems", name, ".env"), environment);
            _repository.WriteFile(Path.Combine(".harness-config", "sshItems", name, "known_hosts"), "host.invalid ssh-ed25519 AAAA\n");

            if (key is not null)
            {
                _repository.WriteFile(Path.Combine(".harness-config", "sshItems", name, ".key"), key);
            }
        }
    }
}
