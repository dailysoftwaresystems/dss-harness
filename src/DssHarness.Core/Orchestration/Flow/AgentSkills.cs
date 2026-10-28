using System.Text;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One skill's <c>SKILL.md</c>, read: Claude Code's own file, unchanged - the frontmatter's keys honoured, recorded or
/// refused, the body the skill's text as written. Nothing is put into a prompt until the caller asks for the block
/// (<see cref="AgentSkillFound.Block"/>), for the body's substitutions are a run's own facts.
/// </summary>
/// <param name="Name">The skill's name: the folder's, which the frontmatter's <c>name</c> must say again where it names one.</param>
/// <param name="Description">What the frontmatter's <c>description</c> says; null where the file names none.</param>
/// <param name="Body">The body after the frontmatter, as written.</param>
/// <param name="AllowedTools">The tools the frontmatter's <c>allowed-tools</c> names; empty where it names none.</param>
/// <param name="DeclaredArguments">The names the frontmatter's <c>arguments</c> key declares; empty where it declares none.</param>
public sealed record AgentSkillFile(
    string Name,
    string? Description,
    string Body,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DeclaredArguments);

/// <summary>
/// Reads a skill's <c>SKILL.md</c>: the frontmatter split at its closing <c>---</c>, its keys each honoured, recorded or
/// refused by name and line, and the body held to the reads it may make. Every problem is said at once, each with the
/// line of the file it stands on - the one owner of a skill's rules, asked by the lookup and by whoever checks a skill
/// again when a step starts.
/// </summary>
public static class AgentSkillFileParser
{
    /// <summary>At most this many characters of a body, after its substitutions, are put into a prompt.</summary>
    public const int BodyLimit = 64000;

    /// <summary>
    /// The keys a skill's frontmatter takes, in the order help lists them: those this tool knows, whether it honours,
    /// records or refuses each.
    /// </summary>
    public static readonly IReadOnlyList<string> Known =
    [
        "name", "description", "when_to_use", "argument-hint", "arguments", "user-invocable",
        "disable-model-invocation", "allowed-tools", "paths", "metadata", "license", "compatibility", "shell",
        "model", "effort", "context", "agent", "background", "hooks", "disallowed-tools",
    ];

    /// <summary>The keys read and recorded, with nothing to do: they say how and when Claude Code offers a skill.</summary>
    public static readonly IReadOnlyList<string> Recorded =
    [
        "description", "when_to_use", "argument-hint", "arguments", "user-invocable",
        "disable-model-invocation", "paths", "metadata", "license", "compatibility",
    ];

    /// <summary>
    /// Reads <paramref name="text"/>, the whole of one <c>SKILL.md</c>, as the skill <paramref name="folder"/> names:
    /// its problems each name the line of the file they stand on, and the file is returned null only where nothing of it
    /// reads - the frontmatter is no YAML - so that every other problem is said beside the skill it is of.
    /// </summary>
    /// <param name="text">The file's whole text.</param>
    /// <param name="folder">The skill's name: the folder's, which the file's frontmatter must say again where it names one.</param>
    /// <returns>The file, or null where its frontmatter is not one mapping; and every problem, each with its line.</returns>
    public static (AgentSkillFile? Skill, IReadOnlyList<string> Problems) Parse(string text, string folder)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var problems = new List<string>();
        var (frontmatter, body, closed) = Split(text);

        if (frontmatter is null)
        {
            problems.Add(closed
                ? "its frontmatter is not closed (no second '---' line)"
                : "it holds no frontmatter: '---' on the first line, the skill's keys, '---' again, and the skill's text after it");
            return (null, problems);
        }

        // The frontmatter's first line is the file's second, so a leading line's end puts every problem's line where the
        // file has it.
        var root = YamlReading.Root(Environment.NewLine + frontmatter, "a SKILL.md holds one, so that nothing has to decide which of them a prompt names", problems);

        if (root is null)
        {
            return (null, problems);
        }

        string? name = null;
        string? description = null;
        var tools = new List<string>();
        var arguments = new List<string>();

        foreach (var (keyNode, valueNode) in root.Children)
        {
            var key = YamlReading.KeyOf(keyNode, problems);

            if (key is null)
            {
                continue;
            }

            var line = keyNode.Start.Line;

            if (key == "name")
            {
                name = YamlReading.RequireScalar(valueNode, "name", problems);

                if (name is not null && !string.Equals(name, folder, StringComparison.Ordinal))
                {
                    problems.Add($"its folder is '{folder}' and line {line} names it '{name}'; Claude Code would call it '{name}'");
                }

                continue;
            }

            if (key == "allowed-tools")
            {
                tools.AddRange(ToolsOf(valueNode));
                continue;
            }

            if (key == "arguments")
            {
                arguments.AddRange(ArgumentsOf(valueNode));
            }

            if (Recorded.Contains(key, StringComparer.Ordinal) || key == "shell")
            {
                // Read and recorded, or nothing to do: a placeholder names a skill outright, and no command runs when it
                // is loaded.
                if (key == "description")
                {
                    description = YamlReading.ScalarOf(valueNode);
                }

                continue;
            }

            if (Refused(key) is { } why)
            {
                problems.Add($"line {line} sets '{key}', which {why}; here its text is put into the step's own prompt. Put a copy without the key in .harness-config/skills/{folder}");
            }
            else
            {
                problems.Add($"line {line} sets '{key}', a key this tool does not know. Known: {string.Join(", ", Known)}");
            }
        }

        var bodyText = body ?? string.Empty;

        problems.AddRange(BodyProblems(bodyText, frontmatter, arguments));

        if (bodyText.Length > BodyLimit)
        {
            problems.Add($"it is {bodyText.Length.ToString("n0", System.Globalization.CultureInfo.InvariantCulture)} characters, and at most 64,000 are put into a prompt");
        }

        // As an agent's file is read: a skill that has a problem is not returned, for none of its problems is advisory.
        return problems.Count > 0
            ? (null, problems)
            : (new AgentSkillFile(folder, description, bodyText, [.. tools.Distinct(StringComparer.Ordinal)], [.. arguments]), problems);
    }

    /// <summary>
    /// Every refused read of the body, each naming its line, and the substitutions the block makes of it: a skill's
    /// text is the prompt's, and the run's own facts fill what the body reads of them.
    /// </summary>
    /// <param name="skill">The skill, as <see cref="Parse"/> read it.</param>
    /// <param name="skillDirectory">What <c>${CLAUDE_SKILL_DIR}</c> becomes: the folder handed to the step.</param>
    /// <param name="projectDirectory">What <c>${CLAUDE_PROJECT_DIR}</c> becomes: the agent's worktree, else the main checkout.</param>
    /// <param name="session">What <c>${CLAUDE_SESSION_ID}</c> becomes: the session's id.</param>
    /// <param name="effort">What <c>${CLAUDE_EFFORT}</c> becomes: the effort the step resolved to.</param>
    /// <param name="problem">Why it is null, where the body may not be put into a prompt; null where it was.</param>
    /// <returns>The body after its substitutions; null where it is past its bound.</returns>
    public static string? BodyOf(AgentSkillFile skill, string skillDirectory, string projectDirectory, string session, string effort, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var body = Substitute(skill.Body, skillDirectory, projectDirectory, session, effort);

        if (body.Length > BodyLimit)
        {
            problem = $"it is {body.Length.ToString("n0", System.Globalization.CultureInfo.InvariantCulture)} characters after its substitutions, and at most 64,000 are put into a prompt";
            return null;
        }

        problem = null;
        return body;
    }

    /// <summary>The body's substitutions, one pass: <c>${CLAUDE_SKILL_DIR}</c> and its three fellows, and <c>\$</c> a dollar.</summary>
    private static string Substitute(string body, string skillDirectory, string projectDirectory, string session, string effort)
    {
        var substituted = new StringBuilder(body.Length);
        var at = 0;

        while (at < body.Length)
        {
            if (body[at] == '\\' && at + 1 < body.Length && body[at + 1] == '$')
            {
                substituted.Append('$');
                at += 2;
                continue;
            }

            if (body[at] == '$' && at + 1 < body.Length && body[at + 1] == '{'
                && NamedRead(body, at) is { } read)
            {
                substituted.Append(read.Name switch
                {
                    "CLAUDE_SKILL_DIR" => skillDirectory,
                    "CLAUDE_PROJECT_DIR" => projectDirectory,
                    "CLAUDE_SESSION_ID" => session,
                    "CLAUDE_EFFORT" => effort,
                    _ => body[at..read.End],
                });
                at = read.End;
                continue;
            }

            substituted.Append(body[at]);
            at++;
        }

        return substituted.ToString();
    }

    /// <summary>
    /// Every refused read of the body, each naming the line of the file it stands on: an argument, a command run when
    /// the skill is loaded, and the reads only a plugin has or this tool does not give a skill.
    /// </summary>
    private static IEnumerable<string> BodyProblems(string body, string frontmatter, IReadOnlyList<string> arguments)
    {
        // The body's first line: one for '---', one for each of the frontmatter's lines and the line's end after the last
        // of them, one for the '---' that closes it.
        var first = frontmatter.Split('\n').Length + 2;
        var lines = body.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var at = first + index;

            var argumentsRead = false;
            var command = false;
            string? unknown = null;
            string? plugin = null;

            for (var from = 0; from < line.Length; from++)
            {
                if (line[from] == '\\' && from + 1 < line.Length && line[from + 1] == '$')
                {
                    from++;
                    continue;
                }

                if (line[from] == '$' && from + 1 < line.Length && line[from + 1] == '{' && NamedRead(line, from) is { } read)
                {
                    // The four the block replaces are the body's own to read; the rest are refused below.
                    var replaced = read.Name is "CLAUDE_SKILL_DIR" or "CLAUDE_PROJECT_DIR" or "CLAUDE_SESSION_ID" or "CLAUDE_EFFORT";

                    if (!replaced && (read.Name == "CLAUDE_PLUGIN_ROOT" || read.Name == "CLAUDE_PLUGIN_DATA"))
                    {
                        plugin ??= line[read.Start..read.End];
                    }
                    else if (!replaced && read.Name.StartsWith("CLAUDE_", StringComparison.Ordinal))
                    {
                        unknown ??= line[read.Start..read.End];
                    }

                    from = read.End - 1;
                    continue;
                }

                if (line[from] == '$' && !argumentsRead && ArgumentAt(line, from, arguments) is not null)
                {
                    argumentsRead = true;
                    continue;
                }

                if (!command && CommandAt(line, from))
                {
                    command = true;
                }
            }

            if (argumentsRead)
            {
                yield return $"line {at} reads an argument, and a placeholder gives a skill no arguments";
            }

            if (command)
            {
                yield return $"line {at} runs a command when the skill is loaded, which this tool never does";
            }

            if (plugin is { } onlyPlugin)
            {
                yield return $"line {at} reads {onlyPlugin}, which only a plugin has";
            }

            if (unknown is { } notKnown)
            {
                yield return $"line {at} reads {notKnown}, which this tool does not give a skill";
            }
        }
    }

    /// <summary>
    /// One <c>${CLAUDE_...}</c> read starting at <paramref name="at"/>, whole: where it ends and the name it reads.
    /// </summary>
    private static (int Start, int End, string Name)? NamedRead(string line, int at)
    {
        var close = line.IndexOf('}', at + 2);

        if (close < 0 || !line[(at + 2)..close].All(character => char.IsLetterOrDigit(character) || character == '_'))
        {
            return null;
        }

        return (at, close + 1, line[(at + 2)..close]);
    }

    /// <summary>
    /// The argument read at <paramref name="at"/>, the <c>$</c> itself: <c>$ARGUMENTS</c>, with an index or without, a
    /// positional <c>$2</c>, or a <c>$name</c> the skill's <c>arguments</c> key declares.
    /// </summary>
    private static string? ArgumentAt(string line, int at, IReadOnlyList<string> arguments)
    {
        var rest = line[(at + 1)..];
        var length = 0;

        while (length < rest.Length && (char.IsLetterOrDigit(rest[length]) || rest[length] == '_'))
        {
            length++;
        }

        var name = rest[..length];

        if (name == "ARGUMENTS")
        {
            return name;
        }

        if (name.Length > 0 && name.All(char.IsDigit))
        {
            return name;
        }

        return name.Length > 0 && arguments.Contains(name, StringComparer.Ordinal) ? name : null;
    }

    /// <summary>
    /// Whether a command run when the skill is loaded starts at <paramref name="at"/>: one of Claude Code's two
    /// exclamation-mark forms, <c>!`command`</c> and <c>!(command)</c> - an exclamation mark in prose is neither.
    /// </summary>
    private static bool CommandAt(string line, int at)
        => line[at] == '!'
            && at + 1 < line.Length
            && (line[at + 1] == '`' || line[at + 1] == '(')
            && (line[at + 1] == '('
                ? line.IndexOf(')', at + 2) >= 0
                : line.IndexOf('`', at + 2) >= 0);

    /// <summary>Why a key of the frontmatter is refused, as the sentence before 'here its text is put into the step's own prompt' says it; null for one that is not.</summary>
    private static string? Refused(string key) => key switch
    {
        "model" => "changes what runs the skill in Claude Code, and the model of a step is the step's own configuration",
        "effort" => "changes what runs the skill in Claude Code, and the effort of a step is the step's own configuration",
        "context" => "runs a skill in a sub-agent of Claude Code",
        "agent" => "names the sub-agent of Claude Code a skill runs in",
        "background" => "runs a skill in the background of Claude Code",
        "hooks" => "makes commands run when the skill is loaded, and this tool runs nothing to build a prompt",
        "disallowed-tools" => "takes tools away in Claude Code, and a skill grants nothing and takes nothing: what an agent may do is its definition's and the policy's",
        _ => null,
    };

    /// <summary>The tools an <c>allowed-tools</c> value names: a comma-separated string, as Claude Code writes it, or a list.</summary>
    private static IEnumerable<string> ToolsOf(YamlNode node)
        => node switch
        {
            YamlSequenceNode sequence => sequence.Children.Select(YamlReading.ScalarOf).OfType<string>().Where(tool => tool.Length > 0),
            _ => YamlReading.ScalarOf(node)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        };

    /// <summary>The names an <c>arguments</c> value declares: a list of names, or of one-member mappings that name one.</summary>
    private static IEnumerable<string> ArgumentsOf(YamlNode node)
    {
        if (node is not YamlSequenceNode sequence)
        {
            return [];
        }

        return
        [
            .. sequence.Children
                .Select(item => item is YamlMappingNode mapping && mapping.Children.FirstOrDefault(pair => YamlReading.KeyOf(pair.Key, []) == "name") is { } named
                    ? YamlReading.ScalarOf(named.Value)
                    : YamlReading.ScalarOf(item))
                .OfType<string>(),
        ];
    }

    /// <summary>
    /// The file's frontmatter and body, split at the closing of the frontmatter: null frontmatter where the file holds
    /// none, the third answer saying whether one was opened at all.
    /// </summary>
    private static (string? Frontmatter, string? Body, bool Closed) Split(string text)
    {
        var opening = text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal)
            ? text.IndexOf('\n') + 1
            : 0;

        if (opening == 0)
        {
            return (null, null, false);
        }

        var at = opening;

        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);

            if (text.AsSpan(at, (end < 0 ? text.Length : end) - at).TrimEnd('\r').SequenceEqual("---"))
            {
                // The closing '---' may end the file; the body is then empty rather than past the file's end.
                return (text[opening..at], text[(end < 0 ? text.Length : end + 1)..], true);
            }

            if (end < 0)
            {
                break;
            }

            at = end + 1;
        }

        return (null, null, true);
    }
}

/// <summary>
/// Where a skill was found, and what it holds: the place, the folder and its file, the digests a step's record names,
/// and the file as it was read - the one answer both the definition's check and a step's start read, so that what
/// refused a definition is what a run would refuse.
/// </summary>
/// <param name="Name">The skill's name: the folder's.</param>
/// <param name="From">The place it was read, as the prompt's block says it.</param>
/// <param name="Folder">The folder it was read from, absolute.</param>
/// <param name="File">The <c>SKILL.md</c> within it, absolute.</param>
/// <param name="Digest">The SHA-256 of the <c>SKILL.md</c>, lowercase hex; null where the file could not be read.</param>
/// <param name="FolderDigest">The SHA-256 of the folder, over each file's path and content in order; null where the folder is refused.</param>
/// <param name="Files">Every file under the folder, absolute, in the walk's order; empty where the folder is refused.</param>
/// <param name="Skill">The file as it was read; null where it holds no frontmatter that reads.</param>
/// <param name="Problems">
/// Why the skill cannot be used or read, each a sentence's tail after <c>skill 'name' ('file')</c> - never empty where
/// <see cref="Skill"/> is null, and said even where it is not, for a folder that holds a link or too much is refused
/// with the file's own problems beside it.
/// </param>
public sealed record AgentSkillFound(
    string Name,
    string From,
    string Folder,
    string File,
    string? Digest,
    string? FolderDigest,
    IReadOnlyList<string> Files,
    AgentSkillFile? Skill,
    IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Every problem of the skill, said whole: <c>skill 'name' ('file') cannot be used: ...</c> for each it was refused
    /// by, and - where a step's agent is given, for the definition's check - what its <c>allowed-tools</c> asks that the
    /// agent has not.
    /// </summary>
    /// <param name="step">The step whose prompt names the skill, for the sentence; null where none is being checked.</param>
    /// <param name="agent">The step's agent's name; null where the step names none.</param>
    /// <param name="may">The capabilities the step's agent has: all four where its definition names none.</param>
    public IReadOnlyList<string> ProblemsFor(string? step, string? agent, IReadOnlyList<string> may)
    {
        ArgumentNullException.ThrowIfNull(may);

        var problems = Problems.Select(problem => $"skill '{Name}' ('{File}') {problem}").ToList();

        if (Skill is null || step is null)
        {
            return problems;
        }

        foreach (var tool in Skill.AllowedTools)
        {
            if (ClaudeCodeTools.Find(tool) is not { } named)
            {
                problems.Add(
                    $"skill '{Name}' ('{File}') cannot be used by step '{step}': it asks for the tool '{tool}', which no capability of this tool names - an MCP tool, the sub-agent tool and the skill tool among them - and no client of this tool gives an agent one");
                continue;
            }

            var capability = named.Capability.ToString().ToLowerInvariant();

            if (!may.Contains(capability, StringComparer.Ordinal))
            {
                problems.Add(
                    $"skill '{Name}' ('{File}') cannot be used by step '{step}': it asks for the tool '{tool}', and agent '{agent}' has no '{capability}' capability");
            }
        }

        return problems;
    }

    /// <summary>
    /// The text put into a prompt for this skill: the body after its substitutions inside the one block, with the
    /// folder handed to the step stated - the same on every client.
    /// </summary>
    /// <param name="directory">The folder handed to the step, absolute: <c>work/&lt;agent&gt;/skills/&lt;name&gt;/</c>.</param>
    /// <param name="projectDirectory">The agent's worktree; for an agent with none, the main checkout.</param>
    /// <param name="session">The session's id, as the step's record has it.</param>
    /// <param name="effort">The effort the step resolved to.</param>
    /// <param name="problem">Why no text may be put into a prompt; null where it was.</param>
    /// <returns>The block; null where the body is past its bound.</returns>
    public string? Block(string directory, string projectDirectory, string session, string effort, out string? problem)
        => Skill is { } skill
            ? AgentSkillFileParser.BodyOf(skill, directory, projectDirectory, session, effort, out problem) is { } body
                ? Wrap(skill.Name, From, directory, body)
                : null
            : NoSkill(out problem);

    /// <summary>The block around a body, as every client is given it.</summary>
    private static string Wrap(string name, string from, string directory, string body)
        => $"<skill name=\"{name}\" from=\"{from}\" directory=\"{directory}\">"
            + "\nFiles this skill refers to are in that directory. Read them from there.\n\n"
            + body
            + "\n</skill>";

    private string? NoSkill(out string? problem)
    {
        problem = $"skill '{Name}' ('{File}') cannot be used: its SKILL.md does not read";
        return null;
    }
}

/// <summary>
/// The lookup of a skill by name, in the three places, and the folder handed to a step: the first place that has the
/// folder decides, and one that has it and cannot be used is refused - never fallen past to the user's in its stead.
/// The places are read in the main checkout, never in an agent's worktree, so what a later step is told is never
/// something an earlier agent wrote on its way.
/// </summary>
/// <param name="fileSystem">Reads the places, walks a folder and copies it.</param>
/// <param name="platform">The home directory the user's Claude directory is in by default.</param>
/// <param name="checkout">The main checkout: the first two places are under it.</param>
/// <param name="environment">Reads an environment variable; the process's own where none is given.</param>
public sealed class AgentSkills(IFileSystem fileSystem, IHostPlatform platform, string checkout, Func<string, string?>? environment = null)
{
    /// <summary>The tool's own place, under the main checkout.</summary>
    public const string HarnessPlace = ".harness-config/skills";

    /// <summary>The repository's Claude Code skills, under the main checkout.</summary>
    public const string ClaudePlace = ".claude/skills";

    /// <summary>At most this many files of a skill's folder are handed to a step.</summary>
    public const int FileLimit = 500;

    /// <summary>At most this many bytes of a skill's folder are handed to a step.</summary>
    public const long SizeLimit = 16 * 1024 * 1024;

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ClaudeTranscripts _claude = new(fileSystem, platform, environment);
    private readonly string _checkout = checkout;

    /// <summary>
    /// The three places a skill of <paramref name="name"/> is looked for, in their order: the tool's own, the
    /// repository's Claude Code skills, and the user's Claude directory - <c>CLAUDE_CONFIG_DIR</c> of the tool's own
    /// process where it is set, else <c>~/.claude</c> - each as the <c>SKILL.md</c> path within it.
    /// </summary>
    /// <param name="name">The skill's name: the folder's.</param>
    public IReadOnlyList<string> Places(string name)
        => [.. PlacesIn(name).Select(place => Path.Combine(place.Folder, "SKILL.md"))];

    /// <summary>
    /// Why no skill of <paramref name="name"/> is there, as the sentence that names every place looked for it: the
    /// three paths, and what is not looked for.
    /// </summary>
    /// <param name="name">The skill's name: the folder's.</param>
    public string Missing(string name)
        => $"there is no such skill. Looked for:{Environment.NewLine}  "
            + string.Join($"{Environment.NewLine}  ", Places(name))
            + $"{Environment.NewLine}A plugin's skill, a managed one and a .claude/commands file are not looked for: copy the skill's folder to .harness-config/skills/{name}.";

    /// <summary>The three places, each with the name its folder is stated by: the first two as the checkout spells them, the user's whole.</summary>
    private IReadOnlyList<(string From, string Folder)> PlacesIn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return
        [
            (HarnessPlace, Path.Combine(_checkout, HarnessPlace.Replace('/', Path.DirectorySeparatorChar), name)),
            (ClaudePlace, Path.Combine(_checkout, ClaudePlace.Replace('/', Path.DirectorySeparatorChar), name)),
            (Path.Combine(_claude.ConfigDirectory, "skills"), Path.Combine(_claude.ConfigDirectory, "skills", name)),
        ];
    }

    /// <summary>
    /// The skill <paramref name="name"/> names, from the first place that has its folder: the folder read, its
    /// <c>SKILL.md</c> parsed, its files walked and its digests taken, so the answer holds every problem the skill has.
    /// Null where no place has the folder.
    /// </summary>
    /// <param name="name">The skill's name: the folder's.</param>
    public AgentSkillFound? Find(string name)
    {
        foreach (var (from, folder) in PlacesIn(name))
        {
            if (!_fileSystem.DirectoryExists(folder))
            {
                continue;
            }

            var file = Path.Combine(folder, "SKILL.md");
            var problems = new List<string>();

            if (!_fileSystem.FileExists(file))
            {
                problems.Add("cannot be used: its folder is there and holds no SKILL.md");
                return new AgentSkillFound(name, from, folder, file, Digest: null, FolderDigest: null, Files: [], Skill: null, problems);
            }

            string text;

            try
            {
                text = _fileSystem.ReadAllText(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problems.Add($"cannot be read: {exception.Message}");
                return new AgentSkillFound(name, from, folder, file, Digest: null, FolderDigest: null, Files: [], Skill: null, problems);
            }

            var (skill, read) = AgentSkillFileParser.Parse(text, name);
            problems.AddRange(read.Select(problem => $"cannot be used: {problem}"));

            var (files, digest, folderProblems) = Walk(folder);
            problems.AddRange(folderProblems.Select(problem => $"cannot be used: {problem}"));

            return new AgentSkillFound(
                name,
                from,
                folder,
                file,
                Digest: FileSystem.FileContentHash.Of(Encoding.UTF8.GetBytes(text)),
                FolderDigest: digest,
                Files: files,
                skill,
                problems);
        }

        return null;
    }

    /// <summary>
    /// The folder handed to a step: the skill's folder copied whole into <c>skills/&lt;name&gt;/</c> under
    /// <paramref name="workDirectory"/>, in place of any earlier copy, and the directory it was copied to - what the
    /// prompt's block states and what <c>${CLAUDE_SKILL_DIR}</c> becomes. The work directory is a root every agent
    /// already reads and runs commands in, on every client.
    /// </summary>
    /// <param name="found">The skill, as <see cref="Find"/> read it.</param>
    /// <param name="workDirectory">The agent's own work directory.</param>
    public string Copy(AgentSkillFound found, string workDirectory)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        var into = Path.Combine(workDirectory, "skills", found.Name);

        _fileSystem.DeleteDirectory(into);
        _fileSystem.CreateDirectory(into);

        foreach (var file in _fileSystem.EnumerateFiles(found.Folder, recursive: true))
        {
            var relative = Path.GetRelativePath(found.Folder, file);
            var destination = Path.Combine(into, relative);

            if (Path.GetDirectoryName(destination) is { } parent && !_fileSystem.DirectoryExists(parent))
            {
                _fileSystem.CreateDirectory(parent);
            }

            _fileSystem.CopyFile(file, destination, overwrite: true);
        }

        return into;
    }

    /// <summary>
    /// One folder walked: its files in the walk's order, the digest over each file's path and content, and the reasons
    /// it is refused - a link, at any depth, more files or more bytes than are handed to a step.
    /// </summary>
    private (IReadOnlyList<string> Files, string? Digest, IReadOnlyList<string> Problems) Walk(string folder)
    {
        var problems = new List<string>();

        if (_fileSystem.IsLink(folder))
        {
            return ([], null, ["its folder is a link, and a skill's folder is copied whole"]);
        }

        if (_fileSystem.EnumerateDirectoryLinks(folder).FirstOrDefault() is { } link)
        {
            return ([], null, [$"its folder holds a link ('{Path.GetRelativePath(folder, link).Replace('\\', '/')}'), and a skill's folder is copied whole"]);
        }

        List<string> files;

        try
        {
            files = [.. _fileSystem.EnumerateFiles(folder, recursive: true)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ([], null, [$"its folder could not be looked in: {exception.Message}"]);
        }

        // A file that is a link, at any depth: the folder is copied whole, and a link copies what it leads to or nothing.
        if (files.FirstOrDefault(file => _fileSystem.IsLink(file)) is { } through)
        {
            return ([], null, [$"its folder holds a link ('{Path.GetRelativePath(folder, through).Replace('\\', '/')}'), and a skill's folder is copied whole"]);
        }

        if (files.Count > FileLimit)
        {
            problems.Add($"its folder holds {files.Count} files, and at most {FileLimit} are handed to a step");
        }

        long size;

        try
        {
            size = _fileSystem.DirectorySize(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ([], null, [$"its folder could not be measured: {exception.Message}"]);
        }

        if (size > SizeLimit)
        {
            problems.Add($"its folder holds {size.ToString("n0", System.Globalization.CultureInfo.InvariantCulture)} bytes, and at most 16 MB are handed to a step");
        }

        if (problems.Count > 0)
        {
            return ([], null, problems);
        }

        var digest = new StringBuilder();

        try
        {
            foreach (var relative in files.Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')).OrderBy(each => each, StringComparer.Ordinal))
            {
                digest.Append(relative).Append('\n').Append(FileSystem.FileContentHash.Of(_fileSystem.ReadAllBytes(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar))))).Append('\n');
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ([], null, [$"its folder could not be read: {exception.Message}"]);
        }

        return (files, FileSystem.FileContentHash.Of(Encoding.UTF8.GetBytes(digest.ToString())), problems);
    }
}
