using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;
using Xunit;

namespace DssHarness.Tests;

public sealed class AgentKeyStoreTests
{
    private static AppointedKey ZaiKey { get; } = new("ZAI_API_KEY", "provider 'zai'", "the Z.AI API key");

    [Fact]
    public void Present_SaysWhichNamesTheSlotDeclares_NeverAValue()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ANTHROPIC_API_KEY=one\nZAI_API_KEY=two\n");
        temp.WriteFile(".harness-config/workflows/.env/mine.env", "EMPTY=\n");
        var (files, store) = Store();

        var present = store.Present(Layout(temp.Path));

        Assert.Equal(
            [
                Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env"),
                Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "mine.env"),
            ],
            present.Files);

        // A name written blank holds none, as it does where the value is read, so a listing never calls it present.
        Assert.Equal(["ANTHROPIC_API_KEY", "ZAI_API_KEY"], present.Names);
        Assert.True(present.Holds("zai_api_key"));
        Assert.False(present.Holds("EMPTY"));
        Assert.Equal(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env"), present.Origins["ZAI_API_KEY"]);
        Assert.Empty(present.Problems);
    }

    [Fact]
    public void Present_OfADirectoryThatIsNotThere_HoldsNothingAndRefusesNothing()
    {
        using var temp = new TempDirectory();
        var (_, store) = Store();

        var present = store.Present(Layout(temp.Path));

        Assert.Empty(present.Files);
        Assert.Empty(present.Names);
        Assert.Empty(present.Problems);
    }

    [Fact]
    public void Present_RefusesANameDeclaredTwice_InItsOwnWords()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\n");
        temp.WriteFile(".harness-config/workflows/.env/work.env", "ZAI_API_KEY=two\n");
        var (_, store) = Store();

        var present = store.Present(Layout(temp.Path));

        var keys = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env");
        var work = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "work.env");

        Assert.Equal([$"'ZAI_API_KEY' is declared in '{keys}' and in '{work}': a name is declared once"], present.Problems);
    }

    [Fact]
    public void Value_AnswersTheValueAFileDeclares()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one-two\n");
        var (_, store) = Store();

        var answer = store.Value(Layout(temp.Path), [ZaiKey], "ZAI_API_KEY");

        Assert.Null(answer.Problem);
        Assert.Equal("one-two", answer.Value);
    }

    [Fact]
    public void Value_OfANameTheSlotDoesNotHold_IsRefusedByItsSentence()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_APIKEY=one\n");
        var (_, store) = Store();

        var answer = store.Value(Layout(temp.Path), [ZaiKey], "ZAI_API_KEY");

        Assert.Null(answer.Value);
        Assert.Equal(
            "no file under '.harness-config/workflows/.env/' declares 'ZAI_API_KEY', the name provider 'zai' appoints "
            + "for its key (its apiKeyNames); add ZAI_API_KEY= to a file there, holding the Z.AI API key "
            + "(it is read from this slot and nowhere else). Declared there and appointed by nothing: ZAI_APIKEY",
            answer.Problem);
    }

    [Fact]
    public void Value_OfANameWrittenBlank_HoldsNone()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=\n");
        var (_, store) = Store();

        var answer = store.Value(Layout(temp.Path), [ZaiKey], "ZAI_API_KEY");

        Assert.Null(answer.Value);
        Assert.NotNull(answer.Problem);
    }

    [Fact]
    public void Value_OfAFileOthersCanChange_IsRefused()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\n");
        var file = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env");
        var (_, store) = Store(writableByOthers: true);

        var answer = store.Value(Layout(temp.Path), [ZaiKey], "ZAI_API_KEY");

        Assert.Null(answer.Value);
        Assert.Equal($"other users can change '{file}', and whoever can change it can point a run at another key; make it private with: chmod 600 '{file}'", answer.Problem);
    }

    [Fact]
    public void Value_OfAFileOthersCanRead_IsRefused()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\n");
        var file = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env");
        var (_, store) = Store(isPrivate: false);

        var answer = store.Value(Layout(temp.Path), [ZaiKey], "ZAI_API_KEY");

        Assert.Null(answer.Value);
        Assert.Equal($"other users can read '{file}', and it holds ZAI_API_KEY; make it private with: chmod 600 '{file}'", answer.Problem);
    }

    /// <summary>
    /// Who may read a file is asked only where it holds a secret, by the one item rule: a setting - the program the
    /// claude-code client starts - in a file other users can read is answered, for it is no credential, while the same
    /// file holding a key beside it is refused naming the key.
    /// </summary>
    [Fact]
    public void Value_OfASettingInAFileOthersCanRead_IsAnswered_UnlessAKeyIsBesideIt()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "CLAUDE_EXECUTABLE=/opt/claude/bin/claude\n");
        var file = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env");
        var (_, store) = Store(isPrivate: false);
        var program = new AppointedKey("CLAUDE_EXECUTABLE", "client 'claude-code'", "the program the claude-code client starts", Secret: false);

        var setting = store.Value(Layout(temp.Path), [ZaiKey, program], "CLAUDE_EXECUTABLE");

        Assert.Null(setting.Problem);
        Assert.Equal("/opt/claude/bin/claude", setting.Value);

        temp.WriteFile(".harness-config/workflows/.env/keys.env", "CLAUDE_EXECUTABLE=/opt/claude/bin/claude\nZAI_API_KEY=one\n");

        var beside = store.Value(Layout(temp.Path), [ZaiKey, program], "CLAUDE_EXECUTABLE");

        Assert.Null(beside.Value);
        Assert.Equal($"other users can read '{file}', and it holds ZAI_API_KEY; make it private with: chmod 600 '{file}'", beside.Problem);
    }

    /// <summary>On Windows the refusal of a file others can read says the command that makes it private there.</summary>
    [Fact]
    public void Value_OfAFileOthersCanRead_OnWindows_SaysTheIcaclsCommand()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\n");
        var file = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env");
        var (_, store) = Store(isPrivate: false, platform: PlatformId.Windows);

        var answer = store.Value(Layout(temp.Path), [ZaiKey], "ZAI_API_KEY");

        Assert.Null(answer.Value);
        Assert.Equal($"other users can read '{file}', and it holds ZAI_API_KEY; make it private with: icacls \"{file}\" /inheritance:r /grant:r \"%USERNAME%:F\"", answer.Problem);
    }

    /// <summary>
    /// A line of the slot whose part before '=' is no variable's name - a key pasted alone, cut at a '=' of its own - is
    /// reported by its file and line and never by its text, which may be part of a key: it is no name the slot holds, so
    /// no listing, no document and no refusal could carry it.
    /// </summary>
    [Fact]
    public void Present_OfALineWhoseNameIsNoVariablesName_ReportsItByItsLineAlone()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_API_KEY=one\nc2VjcmV0a2V5MTIz==\nsk-ant pasted=x\n");
        var file = Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "keys.env");
        var (_, store) = Store();

        var present = store.Present(Layout(temp.Path));

        Assert.Equal(["ZAI_API_KEY"], present.Names);
        Assert.Equal(
            [
                $"'{file}' line 2: what is before '=' is no variable's name ([A-Z][A-Z0-9_]*), and it is not repeated here, for it may be part of a key.",
                $"'{file}' line 3: what is before '=' is no variable's name ([A-Z][A-Z0-9_]*), and it is not repeated here, for it may be part of a key.",
            ],
            present.Problems);
    }

    /// <summary>A file where the slot's directory should be is refused by a sentence, never read as an empty slot.</summary>
    [Fact]
    public void Present_OfAFileWhereTheSlotsDirectoryShouldBe_IsRefused()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env", "ZAI_API_KEY=one\n");
        var slot = Path.Combine(temp.Path, ".harness-config", "workflows", ".env");
        var (_, store) = Store();

        var present = store.Present(Layout(temp.Path));

        Assert.Empty(present.Names);
        Assert.Empty(present.Files);
        Assert.Equal(
            [$"'{slot}' is a file, where a directory of dotenv files is read: nothing of it was read. Make it a directory and move the file into it, where every file is read and merged."],
            present.Problems);
    }

    [Fact]
    public void Missing_NamesEveryKeyOfTheProviderAndTheNamesNothingAppoints()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/workflows/.env/keys.env", "ZAI_APIKEY=one\n");
        var (_, store) = Store();

        var missing = AgentKeyStore.Missing(
            store.Present(Layout(temp.Path)),
            [ZaiKey, new("ZAI_BACKUP_KEY", "provider 'zai'", "the Z.AI API key")],
            ["ZAI_API_KEY", "ZAI_BACKUP_KEY"],
            ZaiKey);

        Assert.Equal(
            "no file under '.harness-config/workflows/.env/' declares 'ZAI_API_KEY' or 'ZAI_BACKUP_KEY', the names provider 'zai' "
            + "appoints for its keys (its apiKeyNames); add ZAI_API_KEY= or ZAI_BACKUP_KEY= to a file there, holding the Z.AI API key "
            + "(it is read from this slot and nowhere else). Declared there and appointed by nothing: ZAI_APIKEY",
            missing);
    }

    private static HarnessLayout Layout(string repository)
        => new(repository, repository);

    /// <summary>The store over the real file system, with every answer a privacy rule needs fixed at its making.</summary>
    private static (IFileSystem Files, AgentKeyStore Store) Store(bool isPrivate = true, bool writableByOthers = false, PlatformId platform = PlatformId.Linux)
    {
        var permissions = new FixedPermissions(isPrivate, writableByOthers);
        var files = new PassThroughFileSystem(new PhysicalFileSystem(permissions));

        return (files, new AgentKeyStore(files, permissions, HostDoubles.Platform(platform)));
    }

    /// <summary>Every answer a test of the store's privacy rules needs, fixed at its making.</summary>
    private sealed class FixedPermissions(bool isPrivate, bool writableByOthers) : IFilePermissions
    {
        public void ProtectSecret(string path)
        {
        }

        public bool IsExecutable(string path) => false;

        public void SetExecutable(string path, bool executable)
        {
        }

        public void CopyMode(string from, string to)
        {
        }

        public bool IsPrivate(string path) => isPrivate;

        public bool IsWritableByOthers(string path) => writableByOthers;
    }
}
