using NSubstitute;
using DssHarness.Core.FileSystem;
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

        Assert.Contains(
            $"other users can read '.harness-config/{(kind == "ssh" ? "sshItems" : "wslDistros")}/{Item}/.env', "
                + "and it holds this host's SUDO_PASSWORD; make it private with: chmod 600",
            problem,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A key the item declares optional may be written blank: it means none, and no refusal comes of
    /// it - a host whose superuser needs no password is not made to tighten a file that already
    /// holds no credential worth reading.
    /// </summary>
    [Fact]
    public void AnOptionalKeyWrittenBlank_MeansNone_IsNoRefusal_AndHandsOnNoCredential()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(repository);
        fixture.WriteSshItem(Item, "ADDRESS=host.invalid\nUSER=harness\nSUDO_PASSWORD=\n");

        var read = fixture.Store.ReadSshItem(fixture.Layout, Item);

        Assert.Equal(string.Empty, read.Problem);
        Assert.NotNull(read.Item);
        Assert.Null(read.Item.Superuser);
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
