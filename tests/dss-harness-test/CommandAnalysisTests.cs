using DssHarness.Core.Agents.Policy;

namespace DssHarness.Tests;

/// <summary>
/// The flattening of a command, held to what the plan's rules to mutate by hand name: each way a
/// command hides its program - a separator, a substitution, a wrapper, a script, git's own options -
/// is read through, and what the reader cannot open stays as words.
/// </summary>
public sealed class CommandAnalysisTests
{
    [Fact]
    public void ASeparatorSplitsAStringIntoCommands()
    {
        Assert.Equal([["git", "push"], ["git", "tag"]], CommandAnalysis.Commands("git push; git tag"));
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("a && b"));
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("a || b"));
        Assert.Equal([["grep", "x"], ["wc", "-l"]], CommandAnalysis.Commands("grep x | wc -l"));
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("a & b"));
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("a\nb"));
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("a\r\nb"));
    }

    [Fact]
    public void QuotesKeepOneWordOne()
    {
        Assert.Equal([["echo", "a b"]], CommandAnalysis.Commands("echo \"a b\""));
        Assert.Equal([["echo", "a && b"]], CommandAnalysis.Commands("echo 'a && b'"));
        Assert.Equal([["echo", "a\"b"]], CommandAnalysis.Commands("echo \"a\\\"b\""));
        Assert.Equal([["echo", "a b"]], CommandAnalysis.Commands("echo a\\ b"));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands("\\sudo x"));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands("su\\do x"));
        Assert.Equal([["echo", "C:\\Users\\dev"]], CommandAnalysis.Commands("echo \"C:\\Users\\dev\""));
    }

    [Fact]
    public void ASeparatorInsideQuotesIsAnArgument_EvenAfterASubstitution()
    {
        Assert.Equal([["echo", "a;b"]], CommandAnalysis.Commands("echo \"a;b\""));
        Assert.Equal([["true"], ["echo", "rm x; sudo y"]], CommandAnalysis.Commands("echo \"$(true)\" \"rm x; sudo y\""));
        Assert.Equal([["true"], ["echo", "; sudo y"]], CommandAnalysis.Commands("echo \"$(true); sudo y\""));
    }

    [Fact]
    public void TheBodyOfACommandSubstitutionIsACommandOfItsOwn()
    {
        Assert.Equal([["sudo", "b"], ["echo"]], CommandAnalysis.Commands("echo \"$(sudo b)\""));
        Assert.Equal([["sudo", "b"], ["echo"]], CommandAnalysis.Commands("echo `sudo b`"));
        Assert.Equal([["git", "push"], ["true"], ["echo"]], CommandAnalysis.Commands("echo \"$(git push)\" `true`"));
    }

    [Fact]
    public void ASubshellAndALoopAndABranchBodyAreCommandsOfTheirOwn()
    {
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("(a && b)"));
        Assert.Equal([["x"], ["a"], ["sudo", "x"]], CommandAnalysis.Commands("for x in a; do sudo x; done"));
        Assert.Equal([["[", "-f", "x", "]"], ["rm", "x"]], CommandAnalysis.Commands("if [ -f x ]; then rm x; fi"));
        Assert.Equal([["true"], ["sudo", "x"], ["break"]], CommandAnalysis.Commands("while true; do sudo x; break; done"));
    }

    [Fact]
    public void WrappersAreStrippedWithTheirOwnOptions()
    {
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("env A=1 git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("env -u B A=1 git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("nohup git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("time -p git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("timeout 5 git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("timeout --foreground 5m git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("nice -n 5 git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("nice -5 git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("xargs -I{} git push"));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands("command -v git push"));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands("exec sudo x"));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands("eval \"sudo x\""));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands("nohup env A=1 timeout 5 sudo x"));
        Assert.Empty(CommandAnalysis.Commands("nice"));
    }

    [Fact]
    public void EnvSplitStringIsReadAsTheCommandLineItIs()
    {
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "-S", "sudo x"]));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "-Ssudo x"]));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "A=1", "--split-string=sudo x"]));
    }

    [Fact]
    public void AShellScriptTheCommandNamesIsGoneInto()
    {
        Assert.Equal([["a"], ["sudo", "b"]], CommandAnalysis.Commands("sh -c 'a && sudo b'"));
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("bash -lc \"a; b\""));
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("zsh -c 'a\nb'"));
    }

    [Fact]
    public void CmdAndPowerShellAreGoneInto_AnEncodedCommandDecoded()
    {
        Assert.Equal([["a"], ["b"]], CommandAnalysis.Commands("cmd /c \"a & b\""));
        Assert.Equal([["rm", "x"], ["sudo", "y"]], CommandAnalysis.Commands("powershell -Command \"rm x; sudo y\""));
        Assert.Equal([["sudo", "y"]], CommandAnalysis.Commands("pwsh -c \"sudo y\""));

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("sudo x"));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands($"pwsh -EncodedCommand {encoded}"));
    }

    [Fact]
    public void GoingIntoStopsAtFourLevels()
    {
        var script = "sudo x";

        for (var level = 0; level < 4; level++)
        {
            script = $"sh -c \"{script.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands(script));

        var deeper = $"sh -c \"{script.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

        // The fifth going-in is not made: the fourth level's script stays its words, opened no further.
        Assert.Equal([["sh", "-c", "sudo x"]], CommandAnalysis.Commands(deeper));
    }

    [Fact]
    public void TheInsideOfAScriptFileIsNotRead()
    {
        Assert.Equal([["bash", "script.sh"]], CommandAnalysis.Commands("bash script.sh"));
        Assert.Equal([["pwsh", "-File", "run.ps1"]], CommandAnalysis.Commands("pwsh -File run.ps1"));
    }

    [Fact]
    public void AnArgumentListIsFlattenedAsTheCommandItNames()
    {
        Assert.Equal([["git", "-C", "x", "push"]], CommandAnalysis.Commands((IReadOnlyList<string>)["git", "-C", "x", "push"]));
        Assert.Equal([["sudo", "b"]], CommandAnalysis.Commands((IReadOnlyList<string>)["bash", "-c", "sudo b"]));
        Assert.Equal([["git", "push"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "A=1", "git", "push"]));
    }

    [Fact]
    public void ARedirectsFileIsAWord_ItsOperatorIsNot()
    {
        Assert.Equal([["echo", "x", "out.txt"]], CommandAnalysis.Commands("echo x >out.txt"));
        Assert.Equal([["echo", "x", "err.txt"]], CommandAnalysis.Commands("echo x 2>>err.txt"));
        Assert.Equal([["grep", "x"], ["wc"]], CommandAnalysis.Commands("grep x 2>&1 | wc"));
    }

    [Fact]
    public void ForRules_DropsGitsOwnOptionsBeforeItsSubcommand()
    {
        Assert.Equal(["git", "push"], CommandAnalysis.ForRules(["git", "-C", "x", "push"], StringComparison.Ordinal));
        Assert.Equal(["git", "push"], CommandAnalysis.ForRules(["git", "-Cfoo", "push"], StringComparison.Ordinal));
        Assert.Equal(["git", "push"], CommandAnalysis.ForRules(["git", "--git-dir=y", "push"], StringComparison.Ordinal));
        Assert.Equal(["git", "push"], CommandAnalysis.ForRules(["git", "--no-pager", "push"], StringComparison.Ordinal));
        Assert.Equal(["git", "push", "origin"], CommandAnalysis.ForRules(["git", "-p", "push", "origin"], StringComparison.Ordinal));
        Assert.Equal(["git", "push"], CommandAnalysis.ForRules(["git", "push"], StringComparison.Ordinal));
        Assert.Equal(["dotnet", "test"], CommandAnalysis.ForRules(["dotnet", "test"], StringComparison.Ordinal));
    }

    [Fact]
    public void ForRules_OnceItsOptionsAreDropped_TheCommandKeepsItsArguments()
    {
        Assert.Equal(["git", "push", "origin", "main"], CommandAnalysis.ForRules(["git", "-C", "..", "push", "origin", "main"], StringComparison.Ordinal));
        Assert.Equal(["git", "commit", "-m", "x"], CommandAnalysis.ForRules(["git", "-c", "user.email=a@b", "commit", "-m", "x"], StringComparison.Ordinal));
    }

    [Fact]
    public void ForRules_AGitCThatNamesAnAliasOrAProgramGitRuns_IsTheGitConfigItIs()
    {
        Assert.Equal(["git", "config", "x"], CommandAnalysis.ForRules(["git", "-c", "alias.x=commit", "x"], StringComparison.Ordinal));
        Assert.Equal(["git", "config", "x"], CommandAnalysis.ForRules(["git", "-calias.x=commit", "x"], StringComparison.Ordinal));
        Assert.Equal(["git", "config", "push"], CommandAnalysis.ForRules(["git", "-c", "core.sshCommand=ssh -i ~/.ssh/other", "push"], StringComparison.Ordinal));
        Assert.Equal(["git", "config", "status"], CommandAnalysis.ForRules(["git", "-c", "core.hooksPath=.githooks", "status"], StringComparison.Ordinal));
    }

    [Fact]
    public void ForRules_LeavesANonGitCommandAlone()
    {
        Assert.Equal(["echo", "git", "-C", "x", "push"], CommandAnalysis.ForRules(["echo", "git", "-C", "x", "push"], StringComparison.Ordinal));
    }

    [Fact]
    public void AProgramIsReadByItsName_ItsDirectoriesAndExeEndFoldedAway()
    {
        Assert.Equal("sudo", CommandAnalysis.Name("/usr/bin/sudo"));
        Assert.Equal("SUDO", CommandAnalysis.Name("SUDO.EXE"));
        Assert.Equal("git", CommandAnalysis.Name("git"));
        Assert.Equal("bash", CommandAnalysis.Name("/usr/bin/bash.exe"));
    }

    [Fact]
    public void WebAddresses_ReadCurlAndWget_BehindTheirOptions()
    {
        Assert.Equal(["https://example.com/x"], CommandAnalysis.WebAddresses(["curl", "-s", "-o", "out", "https://example.com/x"]));
        Assert.Equal(["https://a.example/x", "https://b.example/y"], CommandAnalysis.WebAddresses(["curl", "--url", "https://a.example/x", "--url=https://b.example/y"]));
        Assert.Equal(["example.com/raw/1"], CommandAnalysis.WebAddresses(["wget", "-q", "example.com/raw/1"]));
        Assert.Equal([], CommandAnalysis.WebAddresses(["curl", "--config", "cfg"]));
    }

    [Fact]
    public void WebAddresses_ReadTheFetchersOfPowerShellAndHttp()
    {
        Assert.Equal(["https://example.com/api"], CommandAnalysis.WebAddresses(["Invoke-WebRequest", "-Uri", "https://example.com/api"]));
        Assert.Equal(["https://example.com/api"], CommandAnalysis.WebAddresses(["iwr", "-Uri:https://example.com/api"]));
        Assert.Equal(["https://example.com/api"], CommandAnalysis.WebAddresses(["irm", "https://example.com/api"]));
        Assert.Equal(["example.org/x"], CommandAnalysis.WebAddresses(["http", "example.org/x", "a==b"]));
        Assert.Equal([], CommandAnalysis.WebAddresses(["Invoke-WebRequest", "-Method", "GET"]));
    }

    [Fact]
    public void WebAddresses_ReadGhApiAsApiGithubcoms_UnlessAHostnameNamesAnother()
    {
        Assert.Equal(["api.github.com"], CommandAnalysis.WebAddresses(["gh", "api", "repos/owner/repo"]));
        Assert.Equal(["api.github.com"], CommandAnalysis.WebAddresses(["gh", "api", "/user"]));
        Assert.Equal(["ghe.example.com"], CommandAnalysis.WebAddresses(["gh", "api", "--hostname", "ghe.example.com", "/user"]));
        Assert.Equal([], CommandAnalysis.WebAddresses(["gh", "pr", "view", "41"]));
    }

    [Fact]
    public void WebAddresses_ReadAProgramTheListDoesNotNameAsNoRequest()
    {
        Assert.Equal([], CommandAnalysis.WebAddresses(["git", "clone", "https://example.com/x.git"]));
        Assert.Equal([], CommandAnalysis.WebAddresses(["dotnet", "restore"]));
        Assert.Equal([], CommandAnalysis.WebAddresses(["curl.tar.gz"]));
    }
}
