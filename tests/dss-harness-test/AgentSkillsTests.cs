using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// A skill in a prompt: the <c>SKILL.md</c> reader (Claude Code's own file, unchanged - its frontmatter's keys each
/// honoured, recorded or refused, its body held to the reads it may make), the lookup in the three places, the text put
/// into a prompt and the folder handed to a step, and the definition's check refusing a missing or unusable skill with
/// every place looked for it.
/// </summary>
public sealed class AgentSkillsTests
{
    private readonly TempDirectory _temp = new();

    /// <summary>The one skill the tests read, in the tool's own place under the main checkout.</summary>
    private string SkillAt(string place, string name, string text)
    {
        var folder = Path.Combine(_temp.Path, place.Replace('/', Path.DirectorySeparatorChar), name);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), text);

        return folder;
    }

    private string GoodSkill => """
        ---
        name: release-notes
        description: writes the release notes from the changes
        when_to_use: where a release is asked for
        allowed-tools: Read, Grep
        ---
        Read ${CLAUDE_SKILL_DIR}/changes.md and write the notes.
        The project is in ${CLAUDE_PROJECT_DIR}; this session is ${CLAUDE_SESSION_ID} at ${CLAUDE_EFFORT}.
        A dollar is written \$5.
        """;

    [Fact]
    public void AFileReadsItsKeysAndItsBodyVerbatim()
    {
        var (skill, problems) = AgentSkillFileParser.Parse(GoodSkill, "release-notes");

        Assert.Empty(problems);
        Assert.Equal("release-notes", skill?.Name);
        Assert.Equal("writes the release notes from the changes", skill?.Description);
        Assert.Equal(["Read", "Grep"], skill?.AllowedTools);
        Assert.Equal(
            "Read ${CLAUDE_SKILL_DIR}/changes.md and write the notes.\nThe project is in ${CLAUDE_PROJECT_DIR}; this session is ${CLAUDE_SESSION_ID} at ${CLAUDE_EFFORT}.\nA dollar is written \\$5.",
            skill?.Body);
    }

    /// <summary>The recorded keys are read and recorded, with nothing to do; <c>arguments</c> declares names a body read of one is refused by.</summary>
    [Fact]
    public void TheRecordedKeysAreReadAndArgumentsDeclareNames()
    {
        var (skill, problems) = AgentSkillFileParser.Parse(
            """
            ---
            name: release-notes
            description: writes the notes
            argument-hint: [from, to]
            arguments:
              - from
              - name: to
                description: where the notes end
            user-invocable: false
            disable-model-invocation: true
            paths: [~/notes/**]
            metadata:
              owner: releases
            license: MIT
            compatibility: 2.1.287
            shell: pwsh
            ---
            Writes the notes, from the beginning to the end.
            """,
            "release-notes");

        Assert.Empty(problems);
        Assert.Equal("writes the notes", skill?.Description);
        Assert.Equal(["from", "to"], skill?.DeclaredArguments);
        Assert.Empty(skill!.AllowedTools);
    }

    [Fact]
    public void AFrontmatterThatIsAbsentOrNotClosedIsRefused()
    {
        var (absent, absentProblems) = AgentSkillFileParser.Parse("Writes the notes.\n", "release-notes");
        var (open, openProblems) = AgentSkillFileParser.Parse("---\nname: release-notes\nWrites the notes.\n", "release-notes");

        Assert.Null(absent);
        Assert.Equal(["it holds no frontmatter: '---' on the first line, the skill's keys, '---' again, and the skill's text after it"], absentProblems);
        Assert.Null(open);
        Assert.Equal(["its frontmatter is not closed (no second '---' line)"], openProblems);
    }

    [Fact]
    public void ANameThatIsNotTheFoldersIsRefused()
    {
        var (skill, problems) = AgentSkillFileParser.Parse("---\nname: notes\ndescription: writes the notes\n---\nWrites them.\n", "release-notes");

        Assert.Null(skill);
        Assert.Equal(["its folder is 'release-notes' and line 2 names it 'notes'; Claude Code would call it 'notes'"], problems);
    }

    /// <summary>Each key that changes what runs the skill in Claude Code is refused with its line, for text put into a prompt runs in the step's own agent.</summary>
    [Theory]
    [InlineData("model", "changes what runs the skill in Claude Code, and the model of a step is the step's own configuration")]
    [InlineData("effort", "changes what runs the skill in Claude Code, and the effort of a step is the step's own configuration")]
    [InlineData("context", "runs a skill in a sub-agent of Claude Code")]
    [InlineData("agent", "names the sub-agent of Claude Code a skill runs in")]
    [InlineData("background", "runs a skill in the background of Claude Code")]
    [InlineData("hooks", "makes commands run when the skill is loaded, and this tool runs nothing to build a prompt")]
    [InlineData("disallowed-tools", "takes tools away in Claude Code, and a skill grants nothing and takes nothing: what an agent may do is its definition's and the policy's")]
    public void AKeyThatChangesWhatRunsTheSkill_IsRefusedWithItsLine(string key, string why)
    {
        var (skill, problems) = AgentSkillFileParser.Parse($"---\nname: release-notes\ndescription: writes the notes\n{key}: x\n---\nWrites them.\n", "release-notes");

        Assert.Null(skill);
        Assert.Equal([$"line 4 sets '{key}', which {why}; here its text is put into the step's own prompt. Put a copy without the key in .harness-config/skills/release-notes"], problems);
    }

    [Fact]
    public void AKeyThisToolDoesNotKnowIsRefusedNamingTheKnown()
    {
        var (skill, problems) = AgentSkillFileParser.Parse("---\nname: release-notes\ndescription: writes the notes\nfrobnicate: x\n---\nWrites them.\n", "release-notes");

        Assert.Null(skill);
        Assert.Equal(
            $"line 4 sets 'frobnicate', a key this tool does not know. Known: {string.Join(", ", AgentSkillFileParser.Known)}",
            Assert.Single(problems));
    }

    /// <summary>A read of an argument, a command run when the skill is loaded, and the reads only a plugin has or this tool does not give a skill: each names its line.</summary>
    [Theory]
    [InlineData("Uses $ARGUMENTS.", "line 7 reads an argument, and a placeholder gives a skill no arguments")]
    [InlineData("Uses $ARGUMENTS[0].", "line 7 reads an argument, and a placeholder gives a skill no arguments")]
    [InlineData("Uses $1 and $2.", "line 7 reads an argument, and a placeholder gives a skill no arguments")]
    [InlineData("Uses !`git log`.", "line 7 runs a command when the skill is loaded, which this tool never does")]
    [InlineData("Uses !(git log).", "line 7 runs a command when the skill is loaded, which this tool never does")]
    [InlineData("Uses ${CLAUDE_PLUGIN_ROOT}.", "line 7 reads ${CLAUDE_PLUGIN_ROOT}, which only a plugin has")]
    [InlineData("Uses ${CLAUDE_PLUGIN_DATA}.", "line 7 reads ${CLAUDE_PLUGIN_DATA}, which only a plugin has")]
    [InlineData("Uses ${CLAUDE_FROBNICATE}.", "line 7 reads ${CLAUDE_FROBNICATE}, which this tool does not give a skill")]
    public void ABodyReadThisToolRefuses_IsRefusedNamingItsLine(string line, string problem)
    {
        var text = $"---\nname: release-notes\ndescription: writes the notes\narguments: [from]\n---\nWrites them.\n{line}\n";

        var (skill, problems) = AgentSkillFileParser.Parse(text, "release-notes");

        Assert.Null(skill);
        Assert.Equal([problem], problems);
    }

    /// <summary>A <c>$name</c> is refused only where the skill's own <c>arguments</c> key declares it; an undeclared one, an exclamation mark in prose and the four substitutions are the skill's text.</summary>
    [Fact]
    public void AnUndeclaredNameAnExclamationMarkAndTheSubstitutionsAreLeftAsWritten()
    {
        var (skill, problems) = AgentSkillFileParser.Parse(
            """
            ---
            name: release-notes
            description: writes the notes
            ---
            The $HOME of the machine, and \$ a dollar.
            How surprising! Not a command, and !(not one either - unmatched.
            ${CLAUDE_SKILL_DIR}, ${CLAUDE_PROJECT_DIR}, ${CLAUDE_SESSION_ID} and ${CLAUDE_EFFORT}.
            """,
            "release-notes");

        Assert.Empty(problems);
        Assert.NotNull(skill);
    }

    [Fact]
    public void ABodyPastSixtyFourThousandCharactersIsRefused()
    {
        // The body is the file's whole text after the closing '---', its last line's end among it.
        var body = new string('x', AgentSkillFileParser.BodyLimit);
        var (skill, problems) = AgentSkillFileParser.Parse($"---\nname: release-notes\ndescription: writes the notes\n---\n{body}\n", "release-notes");

        Assert.Null(skill);
        Assert.Equal([$"it is {(AgentSkillFileParser.BodyLimit + 1).ToString("n0", System.Globalization.CultureInfo.InvariantCulture)} characters, and at most 64,000 are put into a prompt"], problems);
    }

    /// <summary>The block: the body after its substitutions, the folder handed to the step stated, and the bound held again after them.</summary>
    [Fact]
    public void ABlockStatesTheSkillItsPlaceAndTheFolderHandedToTheStep()
    {
        var skill = Found(SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill));

        Assert.Equal(
            "<skill name=\"release-notes\" from=\".harness-config/skills\" directory=\"work/a7/skills/release-notes\">"
                + "\nFiles this skill refers to are in that directory. Read them from there.\n\n"
                + "Read work/a7/skills/release-notes/changes.md and write the notes.\n"
                + "The project is in C:\\repo; this session is s1 at high.\n"
                + "A dollar is written $5.\n"
                + "</skill>",
            skill.Block("work/a7/skills/release-notes", @"C:\repo", "s1", "high", out var problem));

        Assert.Null(problem);
    }

    [Fact]
    public void ABodyPastItsBoundAfterTheSubstitutionsIsRefused()
    {
        // Under the bound as written, past it once the folder's long path is substituted for the token.
        var directory = string.Join("/", Enumerable.Repeat("work/a7/skills", 6)) + "/release-notes";
        var body = new string('x', AgentSkillFileParser.BodyLimit - 100) + "${CLAUDE_SKILL_DIR}";
        SkillAt(AgentSkills.HarnessPlace, "release-notes", $"---\nname: release-notes\ndescription: d\n---\n{body}\n");
        var read = Find("release-notes");

        Assert.NotNull(read);
        Assert.Null(read.Block(directory, @"C:\repo", "s1", "high", out var problem));
        Assert.Equal(
            $"it is {(AgentSkillFileParser.BodyLimit - 100 + directory.Length + 1).ToString("n0", System.Globalization.CultureInfo.InvariantCulture)} characters after its substitutions, and at most 64,000 are put into a prompt",
            problem);
    }

    /// <summary>The first place that has the folder decides, and the repository's come before the user's.</summary>
    [Fact]
    public void TheFirstPlaceThatHasTheFolderDecides_TheRepositorysBeforeTheUsers()
    {
        using var user = new TempDirectory();
        SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);
        SkillAt(AgentSkills.ClaudePlace, "release-notes", GoodSkill);

        var own = Path.Combine(user.Path, "skills", "release-notes");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "SKILL.md"), GoodSkill);

        var found = Find("release-notes", user: user.Path);

        Assert.Equal(".harness-config/skills", found?.From);
        Assert.Equal(Path.Combine(_temp.Path, AgentSkills.HarnessPlace.Replace('/', Path.DirectorySeparatorChar), "release-notes"), found?.Folder);
        Assert.NotNull(found?.Digest);
        Assert.NotNull(found?.FolderDigest);

        // The repository's Claude Code skills before the user's, and the user's directory found by the environment.
        Directory.Delete(Path.Combine(_temp.Path, AgentSkills.HarnessPlace.Replace('/', Path.DirectorySeparatorChar), "release-notes"), true);
        Assert.Equal(".claude/skills", Find("release-notes", user: user.Path)?.From);

        Directory.Delete(Path.Combine(_temp.Path, AgentSkills.ClaudePlace.Replace('/', Path.DirectorySeparatorChar), "release-notes"), true);
        Assert.Equal(Path.Combine(user.Path, "skills"), Find("release-notes", user: user.Path)?.From);
    }

    [Fact]
    public void TheUsersDirectoryIsFoundByTheEnvironmentVariable()
    {
        using var configured = new TempDirectory();
        var own = Path.Combine(configured.Path, "skills", "release-notes");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "SKILL.md"), GoodSkill);

        var found = Find("release-notes", user: configured.Path);

        Assert.Equal(own, found?.Folder);
    }

    [Fact]
    public void AMissingSkillNamesEveryPlaceLookedFor()
    {
        var skills = Skills();

        Assert.Equal(
            $"there is no such skill. Looked for:{Environment.NewLine}"
                + $"  {Path.Combine(_temp.Path, ".harness-config", "skills", "release-notes", "SKILL.md")}{Environment.NewLine}"
                + $"  {Path.Combine(_temp.Path, ".claude", "skills", "release-notes", "SKILL.md")}{Environment.NewLine}"
                + $"  {Path.Combine(_temp.Path, "user", ".claude", "skills", "release-notes", "SKILL.md")}{Environment.NewLine}"
                + "A plugin's skill, a managed one and a .claude/commands file are not looked for: copy the skill's folder to .harness-config/skills/release-notes.",
            skills.Missing("release-notes"));
        Assert.Null(skills.Find("release-notes"));
    }

    /// <summary>A <c>SKILL.md</c> that is there and cannot be used is refused: the next place is not tried in its stead.</summary>
    [Fact]
    public void ABrokenSkillIsNotFallenPastToTheNextPlace()
    {
        SkillAt(AgentSkills.HarnessPlace, "release-notes", "---\nname: notes\ndescription: broken\n---\nWrites.\n");
        SkillAt(AgentSkills.ClaudePlace, "release-notes", GoodSkill);

        var found = Find("release-notes");
        Assert.NotNull(found);

        Assert.Equal(
            [$"skill 'release-notes' ('{Path.Combine(_temp.Path, ".harness-config", "skills", "release-notes", "SKILL.md")}') cannot be used: its folder is 'release-notes' and line 2 names it 'notes'; Claude Code would call it 'notes'"],
            found.ProblemsFor(step: "release", agent: null, AgentDefinition.AllCapabilities));
    }

    [Fact]
    public void AFolderHoldingNoSkillMdIsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, AgentSkills.HarnessPlace, "release-notes"));

        var found = Find("release-notes");
        Assert.NotNull(found);

        Assert.Equal(["cannot be used: its folder is there and holds no SKILL.md"], found.Problems);
    }

    /// <summary><c>allowed-tools</c> is honoured as what the skill needs: each tool one the step's agent has, by the one table, and a tool the table does not name refused outright.</summary>
    [Fact]
    public void AllowedToolsAreHeldToTheAgentsCapabilities()
    {
        var skill = Found(SkillAt(AgentSkills.HarnessPlace, "release-notes", "---\nname: release-notes\ndescription: d\nallowed-tools: [Read, Bash, mcp__other__tool]\n---\nWrites.\n"));

        Assert.Equal(
            [
                $"skill 'release-notes' ('{skill.File}') cannot be used by step 'review.comments': it asks for the tool 'Bash', and agent 'reviewer-comments' has no 'run' capability",
                $"skill 'release-notes' ('{skill.File}') cannot be used by step 'review.comments': it asks for the tool 'mcp__other__tool', which no capability of this tool names - an MCP tool, the sub-agent tool and the skill tool among them - and no client of this tool gives an agent one",
            ],
            skill.ProblemsFor("review.comments", "reviewer-comments", ["read"]));
    }

    [Fact]
    public void AFolderHoldingALinkIsRefused()
    {
        var folder = SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);
        var assets = Path.Combine(folder, "assets");
        Directory.CreateDirectory(assets);

        TestLinks.OrSkip(() => File.CreateSymbolicLink(Path.Combine(assets, "logo.png"), Path.Combine(_temp.Path, "elsewhere.png")));

        var found = Find("release-notes");
        Assert.NotNull(found);

        Assert.Equal(["cannot be used: its folder holds a link ('assets/logo.png'), and a skill's folder is copied whole"], found.Problems);
        Assert.Null(found.FolderDigest);
    }

    [Fact]
    public void AFolderOfMoreThanFiveHundredFilesIsRefused()
    {
        var folder = SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);

        for (var file = 0; file <= AgentSkills.FileLimit; file++)
        {
            File.WriteAllText(Path.Combine(folder, $"extra-{file}.txt"), "x");
        }

        var found = Find("release-notes");
        Assert.NotNull(found);

        Assert.Equal([$"cannot be used: its folder holds {AgentSkills.FileLimit + 2} files, and at most {AgentSkills.FileLimit} are handed to a step"], found.Problems);
    }

    [Fact]
    public void AFolderOfMoreThanSixteenMegabytesIsRefused()
    {
        var folder = SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);
        var large = Path.Combine(folder, "large.bin");
        File.WriteAllText(large, new string('x', (int)AgentSkills.SizeLimit + 1));

        var found = Find("release-notes");
        Assert.NotNull(found);

        var bytes = new FileInfo(Path.Combine(folder, "SKILL.md")).Length + new FileInfo(large).Length;

        Assert.Equal(
            [$"cannot be used: its folder holds {bytes.ToString("n0", System.Globalization.CultureInfo.InvariantCulture)} bytes, and at most 16 MB are handed to a step"],
            found.Problems);
    }

    /// <summary>The digests a step's record names: the SHA-256 of the SKILL.md and of the folder, the folder's over each file's path and content in order.</summary>
    [Fact]
    public void TheDigestsAreOfTheFileAndOfTheFolderWhole()
    {
        var folder = SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllText(Path.Combine(folder, "assets", "notes.txt"), "first");
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "second");

        var found = Find("release-notes");
        Assert.NotNull(found);
        var again = Find("release-notes");
        Assert.NotNull(again);

        Assert.Equal(FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes(GoodSkill)), found.Digest);
        Assert.Equal(found.FolderDigest, again.FolderDigest);

        File.WriteAllText(Path.Combine(folder, "extra.txt"), "changed");
        var changed = Find("release-notes");
        Assert.NotNull(changed);
        Assert.NotEqual(found.FolderDigest, changed.FolderDigest);
    }

    /// <summary>The folder handed to the step: copied whole into the agent's work directory, in place of any earlier copy.</summary>
    [Fact]
    public void TheFolderIsCopiedWholeIntoTheWorkDirectory_InPlaceOfAnEarlierCopy()
    {
        var folder = SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllText(Path.Combine(folder, "assets", "notes.txt"), "first");

        var work = Path.Combine(_temp.Path, "work", "a7");
        var earlier = Path.Combine(work, "skills", "release-notes");
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(earlier, "SKILL.md"), "an earlier copy");
        File.WriteAllText(Path.Combine(earlier, "stale.txt"), "of an earlier copy");

        var found = Find("release-notes");
        Assert.NotNull(found);
        var handed = Skills().Copy(found, work);

        Assert.Equal(Path.Combine(work, "skills", "release-notes"), handed);
        Assert.Equal(GoodSkill, File.ReadAllText(Path.Combine(handed, "SKILL.md")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(handed, "assets", "notes.txt")));
        Assert.Equal(["SKILL.md", "assets"], Directory.GetFileSystemEntries(handed).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>The definition's check: a missing skill named with every place looked for it, at the line of the prompt that names it.</summary>
    [Fact]
    public void AMissingSkillIsRefusedWithEveryPlaceLookedFor()
    {
        var problems = Checked(prompt: "Use {skill.release-notes} here.\n");

        Assert.Equal(
            [
                $"prompt 'prompts/implement.md' line 1 names {{skill.release-notes}}, and there is no such skill. Looked for:{Environment.NewLine}"
                    + $"  {Path.Combine(_temp.Path, ".harness-config", "skills", "release-notes", "SKILL.md")}{Environment.NewLine}"
                    + $"  {Path.Combine(_temp.Path, ".claude", "skills", "release-notes", "SKILL.md")}{Environment.NewLine}"
                    + $"  {Path.Combine(_temp.Path, "user", ".claude", "skills", "release-notes", "SKILL.md")}{Environment.NewLine}"
                    + "A plugin's skill, a managed one and a .claude/commands file are not looked for: copy the skill's folder to .harness-config/skills/release-notes.",
            ],
            problems);
    }

    /// <summary>A skill that is there and cannot be used: the same sentence the lookup owns, said under the definition's check.</summary>
    [Fact]
    public void AnUnusableSkillIsRefusedWhenTheDefinitionIsChecked()
    {
        SkillAt(AgentSkills.HarnessPlace, "release-notes", "---\nname: release-notes\ndescription: d\nallowed-tools: [Bash]\n---\nUses ${CLAUDE_PLUGIN_ROOT}.\n");

        var problems = Checked(prompt: "Use {skill.release-notes} here.\n");

        Assert.Equal(
            [
                $"skill 'release-notes' ('{Path.Combine(_temp.Path, ".harness-config", "skills", "release-notes", "SKILL.md")}') cannot be used: line 6 reads ${{CLAUDE_PLUGIN_ROOT}}, which only a plugin has",
            ],
            problems);
    }

    /// <summary>A skill named twice in one prompt: once is enough, and both lines are said - twice on one line among them.</summary>
    [Fact]
    public void ASkillNamedTwiceInOnePromptIsRefused()
    {
        SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);

        Assert.Equal(
            ["prompt 'prompts/implement.md' names {skill.release-notes} twice (lines 1 and 2): once is enough"],
            Checked(prompt: "Use {skill.release-notes} here.\nAnd {skill.release-notes} again.\n"));
        Assert.Equal(
            ["prompt 'prompts/implement.md' names {skill.release-notes} twice (lines 1 and 1): once is enough"],
            Checked(prompt: "Use {skill.release-notes} and {skill.release-notes} here.\n"));
    }

    /// <summary>A placeholder that names no skill at all is refused, not passed over as the engine's own root, and one that reads an index is too.</summary>
    [Fact]
    public void APlaceholderThatNamesNoSkillIsRefused()
    {
        Assert.Equal(
            ["step 'implement' prompt 'prompts/implement.md' line 1 names '{skill}' alone: name one - {skill.<name>} names it by its folder's name"],
            Checked(prompt: "Use {skill.} here.\n"));
        Assert.Equal(
            ["prompt 'prompts/implement.md' line 1 names {skill.release-notes[0]}, which reads an index: a skill is named by its folder's name alone"],
            Checked(prompt: "Use {skill.release-notes[0]} here.\n"));
    }

    /// <summary>A file that ends at its closing '---' reads an empty body, and an empty file is refused for holding no frontmatter - neither ends the check.</summary>
    [Fact]
    public void AFileThatEndsAtItsClosingLineOrIsEmpty_IsRefusedNeverThrown()
    {
        var (closed, noBody) = AgentSkillFileParser.Parse("---\nname: release-notes\ndescription: d\n---", "release-notes");
        var (empty, noFrontmatter) = AgentSkillFileParser.Parse("", "release-notes");

        Assert.Empty(noBody);
        Assert.Equal("release-notes", closed?.Name);
        Assert.Equal(string.Empty, closed?.Body);
        Assert.Null(empty);
        Assert.Equal(["it holds no frontmatter: '---' on the first line, the skill's keys, '---' again, and the skill's text after it"], noFrontmatter);

        SkillAt(AgentSkills.HarnessPlace, "empty-skill", string.Empty);
        var found = Find("empty-skill");
        Assert.NotNull(found);
        Assert.StartsWith("cannot be used: it holds no frontmatter", Assert.Single(found.Problems), StringComparison.Ordinal);
    }

    /// <summary>A good skill is no problem; one named by a resumePrompt is checked the same; and where the caller gave no lookup none is looked for.</summary>
    [Fact]
    public void AGoodSkillIsNoProblem_AndAResumePromptIsCheckedTheSame()
    {
        SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);

        Assert.Empty(Checked(prompt: "Use {skill.release-notes} here.\n"));
        Assert.Equal(
            [$"resumePrompt 'prompts/again.md' line 1 names {{skill.another-skill}}, and there is no such skill. Looked for:{Environment.NewLine}  {Path.Combine(_temp.Path, ".harness-config", "skills", "another-skill", "SKILL.md")}{Environment.NewLine}  {Path.Combine(_temp.Path, ".claude", "skills", "another-skill", "SKILL.md")}{Environment.NewLine}  {Path.Combine(_temp.Path, "user", ".claude", "skills", "another-skill", "SKILL.md")}{Environment.NewLine}A plugin's skill, a managed one and a .claude/commands file are not looked for: copy the skill's folder to .harness-config/skills/another-skill."],
            Checked(resume: "Use {skill.another-skill} here.\n"));
        Assert.Empty(Checked(prompt: "Use {skill.release-notes} here.\n", lookup: null));
    }

    /// <summary>The body of an agent definition is the system prompt, sent as written: a skill is named in a step's prompt.</summary>
    [Fact]
    public void AnAgentBodyNamesNoSkill()
    {
        SkillAt(AgentSkills.HarnessPlace, "release-notes", GoodSkill);

        var problems = Checked(
            agent: """
                ---
                name: implementer
                description: does the work
                capabilities: [read]
                ---
                You do the work.
                The notes are written by {skill.release-notes}.
                """,
            prompt: "Do the work.\n");

        Assert.Equal(["agent 'implementer' names {skill.release-notes} in its body (line 7): the body is the system prompt and is sent as written. Name the skill in a step's prompt"], problems);
    }

    /// <summary>Every problem of every prompt is reported together, each refusal one line of its own.</summary>
    private IReadOnlyList<string> Checked(string? prompt = null, string? resume = null, string? agent = null, AgentSkills? lookup = null)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompts/implement.md"] = prompt ?? "Do the work.\n",
        };

        if (resume is not null)
        {
            files["prompts/again.md"] = resume;
        }

        agent ??= """
            ---
            name: implementer
            description: does the work
            capabilities: [read]
            ---
            You do the work.
            """;

        files["agents/implementer.md"] = agent;

        // A step that resumes itself runs again - inside a repeat - for its resumePrompt is what it is asked with then.
        var text = resume is null
            ? "name: flow\nsteps:\n  - name: implement\n    type: reason\n    agent: implementer\n    prompt: prompts/implement.md\n    outputs: schemas/implement.json\n"
            : "name: flow\nsteps:\n  - name: planning\n    repeat:\n      until: implement.done\n      maxIterations: 3\n    steps:\n      - name: implement\n        type: reason\n        agent: implementer\n        prompt: prompts/implement.md\n        resumePrompt: prompts/again.md\n        resume: implement\n        outputs: schemas/implement.json\n";

        files["schemas/implement.json"] = """{ "type": "object", "properties": { } }""";

        return [.. WorkflowValidator.Validate(
            WorkflowFileParser.Parse("flow.yml", text),
            hasCommitTemplate: false,
            path => files.GetValueOrDefault(path),
            skills: lookup ?? Skills())];
    }

    private AgentSkills Skills()
        => new(new PhysicalFileSystem(FilePermissionsFactory.Create()), HostDoubles.Platform(home: Path.Combine(_temp.Path, "home")), _temp.Path, name => name == ClaudeTranscripts.ConfigDirectoryVariable ? Path.Combine(_temp.Path, "user", ".claude") : null);

    private AgentSkillFound? Find(string name, string? user = null)
        => new AgentSkills(
            new PhysicalFileSystem(FilePermissionsFactory.Create()),
            HostDoubles.Platform(home: Path.Combine(_temp.Path, "home")),
            _temp.Path,
            variable => variable == ClaudeTranscripts.ConfigDirectoryVariable ? user ?? Path.Combine(_temp.Path, "user", ".claude") : null).Find(name);

    private AgentSkillFound Found(string folder)
    {
        var found = Find(Path.GetFileName(folder)!);
        Assert.NotNull(found);
        return found;
    }
}
