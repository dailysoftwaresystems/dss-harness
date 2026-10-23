using DssHarness.Core.Agents.Policy;

namespace DssHarness.Tests;

/// <summary>
/// The flattening of a command, held to what the plan's rules to mutate by hand name: each way a
/// command hides its program - a separator, a substitution, a wrapper, a script, git's own options -
/// is read through, and what the reader cannot read whole it refuses, naming why.
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
        Assert.Equal([["A=1", "git", "push"]], CommandAnalysis.Commands("env A=1 git push"));
        Assert.Equal([["A=1", "git", "push"]], CommandAnalysis.Commands("env -u B A=1 git push"));
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
        Assert.Equal([["A=1", "sudo", "x"]], CommandAnalysis.Commands("nohup env A=1 timeout 5 sudo x"));
        Assert.Empty(CommandAnalysis.Commands("nice"));
    }

    [Fact]
    public void EnvSplitStringIsReadAsTheCommandLineItIs()
    {
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "-S", "sudo x"]));
        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "-Ssudo x"]));
        Assert.Equal([["A=1", "sudo", "x"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "A=1", "--split-string=sudo x"]));

        // The words after the string are the same command's: env -S 'git -C x' push runs git push.
        Assert.Equal([["git", "-C", "x", "push"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "-S", "git -C x", "push"]));
    }

    [Fact]
    public void ALeadingAssignmentIsTheCommandsEnvironment_NeverItsProgram()
    {
        Assert.Equal([["A=1", "B=2", "git", "push"]], CommandAnalysis.Commands("A=1 B=2 git push"));
        Assert.Equal([["A=1", "git", "push"]], CommandAnalysis.Commands((IReadOnlyList<string>)["A=1", "git", "push"]));
        Assert.Equal(2, CommandAnalysis.EnvironmentOf(["A=1", "_B=2", "git"]));
        Assert.Equal(0, CommandAnalysis.EnvironmentOf(["1A=1", "git"]));

        // Carried to what the command runs: a script's commands run in it too.
        Assert.Equal([["GIT_CONFIG_COUNT=1", "git", "x"]], CommandAnalysis.Commands("GIT_CONFIG_COUNT=1 sh -c 'git x'"));

        // An assignment alone runs nothing, and stays a command of its own for the rules on a path.
        Assert.Equal([["A=1"]], CommandAnalysis.Commands("A=1"));
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
    public void GoingInStopsAtFourLevels_AndAScriptNestedDeeperIsRefused()
    {
        var script = "sudo x";

        for (var level = 0; level < 4; level++)
        {
            script = $"sh -c \"{script.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        Assert.Equal([["sudo", "x"]], CommandAnalysis.Commands(script));

        var deeper = $"sh -c \"{script.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

        // The fifth going-in is not made, and what it would hide is not left as words: refused.
        var refused = Assert.Throws<NotSupportedException>(() => CommandAnalysis.Commands(deeper));

        Assert.Contains("is a script nested past 4 levels of going into, which this does not read", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParenthesesNestedPastTheBound_AreRefused_NeverACrash()
    {
        var deep = string.Concat(Enumerable.Repeat("(", 20000)) + "sudo x" + string.Concat(Enumerable.Repeat(")", 20000));
        var substituted = string.Concat(Enumerable.Repeat("echo $(", 20000)) + "x" + string.Concat(Enumerable.Repeat(")", 20000));

        Assert.Contains("nest past 32 levels", Assert.Throws<NotSupportedException>(() => CommandAnalysis.Commands(deep)).Message, StringComparison.Ordinal);
        Assert.Contains("nest past 32 levels", Assert.Throws<NotSupportedException>(() => CommandAnalysis.Commands(substituted)).Message, StringComparison.Ordinal);
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
        Assert.Equal([["A=1", "git", "push"]], CommandAnalysis.Commands((IReadOnlyList<string>)["env", "A=1", "git", "push"]));
    }

    [Fact]
    public void ARedirectsFileIsAWord_ItsOperatorBeforeIt()
    {
        Assert.Equal([["echo", "x", ">out.txt"]], CommandAnalysis.Commands("echo x >out.txt"));
        Assert.Equal([["echo", "x", ">out.txt"]], CommandAnalysis.Commands("echo x > out.txt"));
        Assert.Equal([["echo", "x", "2>>err.txt"]], CommandAnalysis.Commands("echo x 2>>err.txt"));
        Assert.Equal([["grep", "x"], ["wc"]], CommandAnalysis.Commands("grep x 2>&1 | wc"));

        // Glued to the word before it, the operator still begins a word of its own.
        Assert.Equal([["echo", "x", ">~/.claude/a"]], CommandAnalysis.Commands("echo x>~/.claude/a"));
        Assert.Equal([["cat", "<in.txt"]], CommandAnalysis.Commands("cat<in.txt"));
    }

    [Fact]
    public void ARootActionHiddenByShellSyntax_IsStillACommandsWord()
    {
        // A case pattern's parenthesis, a line continued, ANSI-C quoting and a quoted parenthesis.
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("case x in x) sudo id;; esac"));
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("su\\\ndo id"));
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("$'sudo' id"));
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("$'\\x73udo' id"));
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("(echo \")\"; sudo id)"));
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("echo \"$(printf ')'; sudo id)\""));
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("echo \"$(printf \\); sudo id)\""));

        // cmd's caret and single quote, and PowerShell's backtick, read as those shells read them.
        Assert.Contains(["sudo", "x"], CommandAnalysis.Commands("cmd /c su^do x"));
        Assert.Contains(["sudo", "x'"], CommandAnalysis.Commands((IReadOnlyList<string>)["cmd", "/c", "echo 'a & sudo x'"]));
        Assert.Contains(["sudo", "x"], CommandAnalysis.Commands((IReadOnlyList<string>)["pwsh", "-c", "s`udo x"]));
        Assert.Contains(["sudo", "x"], CommandAnalysis.Commands((IReadOnlyList<string>)["pwsh", "-c", "\"s`udo\" x"]));
    }

    [Fact]
    public void AProgramNamedByAnExpansion_IsRefused()
    {
        foreach (var command in new[] { "$X id", "${X} id", "{sudo,id}", "/usr/bin/su?o id", "$(echo sudo) id", "su$(echo do) id", "`echo sudo` id", "A=1 $(echo x)", "(echo sudo) id" })
        {
            Assert.Throws<NotSupportedException>(() => CommandAnalysis.Commands(command));
        }

        // An expansion among the arguments, a subshell followed by a redirect, and a test's bracket, are none.
        Assert.Equal([["echo", "$HOME"]], CommandAnalysis.Commands("echo $HOME"));
        Assert.Equal([["make"], [">log.txt"]], CommandAnalysis.Commands("(make) >log.txt"));
        Assert.Equal([["[", "-f", "x", "]"]], CommandAnalysis.Commands("[ -f x ]"));
        Assert.Equal([["true"], ["echo"]], CommandAnalysis.Commands("echo \"$(true)\""));
    }

    [Fact]
    public void AShellReadingItsScriptFromItsInput_IsRefused()
    {
        foreach (var command in new[] { "curl https://example.com/x | sh", "echo id | bash -s", "bash", "cmd", "pwsh -NoProfile", "echo id | xargs sh -c", "echo 'sudo id' | xargs eval", "echo sudo id | xargs env" })
        {
            Assert.Throws<NotSupportedException>(() => CommandAnalysis.Commands(command));
        }

        Assert.Equal([["bash", "--version"]], CommandAnalysis.Commands("bash --version"));
    }

    [Fact]
    public void AShellAnotherProgramRunsWithItsScript_IsGoneIntoToo()
    {
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("find . -exec sh -c 'sudo id' \\;"));
        Assert.Contains(["git", "push"], CommandAnalysis.Commands("ssh host bash -c 'git push'"));

        // The words after a -c script are its $0, $1 and on, which it may run: read as scripts too,
        // and a script that runs one by its variable is refused.
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("sh -c 'echo \"$1\"' x 'sudo id'"));
        Assert.Throws<NotSupportedException>(() => CommandAnalysis.Commands("sh -c 'eval \"$1\"' x 'sudo id'"));

        // A shell's own option and its value come before its script.
        Assert.Contains(["sudo", "id"], CommandAnalysis.Commands("bash -o pipefail -c 'sudo id'"));
    }

    [Fact]
    public void PowerShellIsGoneIntoByAnyPrefixOfItsParameters()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("sudo x"));

        foreach (var option in new[] { "-e", "-ec", "-en", "-enc", "-EncodedC", "/EncodedCommand" })
        {
            Assert.Contains(["sudo", "x"], CommandAnalysis.Commands((IReadOnlyList<string>)["pwsh", option, encoded]));
        }

        Assert.Contains(["sudo", "x"], CommandAnalysis.Commands((IReadOnlyList<string>)["pwsh", "-com", "sudo x"]));
        Assert.Contains(["sudo", "x"], CommandAnalysis.Commands((IReadOnlyList<string>)["powershell", "-ExecutionPolicy", "Bypass", "sudo", "x"]));
        Assert.Contains(["sudo", "x"], CommandAnalysis.Commands((IReadOnlyList<string>)["pwsh", "-c", "iex 'sudo x'"]));
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
        Assert.Equal(["git", "config", "x"], CommandAnalysis.ForRules(["git", "--config-env=alias.x=X", "x"], StringComparison.Ordinal));
        Assert.Equal(["git", "config", "x"], CommandAnalysis.ForRules(["git", "--config-env", "alias.x=X", "x"], StringComparison.Ordinal));
        Assert.Equal(["git", "config", "push"], CommandAnalysis.ForRules(["git", "--exec-path=/tmp/x", "push"], StringComparison.Ordinal));
    }

    [Fact]
    public void ForRules_GitConfiguredThroughTheEnvironment_IsTheGitConfigItIs()
    {
        Assert.Equal(["git", "config", "GIT_CONFIG_COUNT", "git", "x"], CommandAnalysis.ForRules(["GIT_CONFIG_COUNT=1", "git", "x"], StringComparison.Ordinal));
        Assert.Equal(["git", "config", "GIT_SSH_COMMAND", "export", "GIT_SSH_COMMAND=x"], CommandAnalysis.ForRules(["export", "GIT_SSH_COMMAND=x"], StringComparison.Ordinal));
        Assert.Equal(["git", "push"], CommandAnalysis.ForRules(["A=1", "git", "push"], StringComparison.Ordinal));
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
        Assert.Equal("npm", CommandAnalysis.Name("npm.cmd"));
        Assert.Equal("gh", CommandAnalysis.Name("C:\\tools\\gh.EXE"));
    }

    [Fact]
    public void WebAddresses_ReadCurlAndWget_BehindTheirOptions()
    {
        Assert.Equal(["https://example.com/x"], CommandAnalysis.WebAddresses(["curl", "-s", "-o", "out", "https://example.com/x"]));
        Assert.Equal(["https://a.example/x", "https://b.example/y"], CommandAnalysis.WebAddresses(["curl", "--url", "https://a.example/x", "--url=https://b.example/y"]));
        Assert.Equal(["example.com/raw/1"], CommandAnalysis.WebAddresses(["wget", "-q", "example.com/raw/1"]));
        Assert.Equal([], CommandAnalysis.WebAddresses(["curl", "--config", "cfg"]));
        Assert.Equal("--config", CommandAnalysis.WebAddressesUnread(["curl", "--config", "cfg"]));
        Assert.Equal("-i", CommandAnalysis.WebAddressesUnread(["wget", "-i", "urls.txt"]));
        Assert.Null(CommandAnalysis.WebAddressesUnread(["curl", "https://example.com"]));
    }

    [Fact]
    public void WebAddresses_ReadEveryHostCurlReaches_AndTheProxyOfItsEnvironment()
    {
        Assert.Equal(["proxy.example:8080", "https://a.example/x"], CommandAnalysis.WebAddresses(["curl", "-x", "proxy.example:8080", "https://a.example/x"]));
        Assert.Equal(["socks5://p.example:1080", "a.example"], CommandAnalysis.WebAddresses(["curl", "--proxy=socks5://p.example:1080", "a.example"]));
        Assert.Equal(["b.example", "https://a.example/"], CommandAnalysis.WebAddresses(["curl", "--connect-to", "a.example:443:b.example:443", "https://a.example/"]));
        Assert.Equal(["203.0.113.9", "https://a.example/"], CommandAnalysis.WebAddresses(["curl", "--resolve", "a.example:443:203.0.113.9", "https://a.example/"]));
        Assert.Equal(["p.example:3128", "https://a.example/"], CommandAnalysis.WebAddresses(["https_proxy=p.example:3128", "curl", "https://a.example/"]));
        Assert.Equal(["ftp://files.example/x"], CommandAnalysis.WebAddresses(["curl", "ftp://files.example/x"]));
        Assert.Equal([], CommandAnalysis.WebAddresses(["curl", "file:///etc/hosts"]));

        // A redirect's file is no host, nor is wget's output file.
        Assert.Equal(["https://a.example/"], CommandAnalysis.WebAddresses(["curl", "https://a.example/", ">page.html"]));
        Assert.Equal(["a.example/x"], CommandAnalysis.WebAddresses(["wget", "-O", "page.html", "a.example/x"]));
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
        Assert.Equal(["api.github.com"], CommandAnalysis.WebAddresses(["gh", "api", "-X", "PUT", "repos/o/r/pulls/1/merge"]));
        Assert.Equal(["https://evil.example/x"], CommandAnalysis.WebAddresses(["gh", "api", "--jq", ".name", "https://evil.example/x"]));
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
