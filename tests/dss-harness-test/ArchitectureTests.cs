using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using DssHarness.Core.Application;

namespace DssHarness.Tests;

/// <summary>
/// The boundaries the application layer drew, held so they cannot drift: the command line reaches Core through
/// its use cases, its output and its results alone; it builds no container; nothing in Core outside its
/// application layer reaches into it; and no command's wiring outgrows a file a person reads whole.
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>
    /// Every Core type the command line references is in the application layer, its help, its output or its
    /// results - or is reached from a public member of a use case, read from the metadata and never listed by
    /// hand: a request, a result, and what they are made of - or is one of the few reviewed below, each with the
    /// reason it stays where it is.
    /// </summary>
    [Fact]
    public void TheCommandLine_ReferencesTheCoreOnlyThroughItsUseCasesItsOutputAndItsResults()
    {
        var referenced = CoreTypesTheCommandLineReferences();
        var allowed = TypesReachedFromTheUseCases();
        var offenders = new List<string>();

        foreach (var type in referenced)
        {
            if (AllowedNamespaces.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal))
                || allowed.Contains(type)
                || Reviewed.ContainsKey(type))
            {
                continue;
            }

            offenders.Add(type);
        }

        Assert.Empty(offenders);

        // Each reviewed entry is a type the plan's own rules name: kept where its rule is, because Core's own
        // sentences or its wire say its name, and no second owner is made to hand it on. A type that becomes
        // reachable from a use case comes off this list rather than staying on it.
        foreach (var (type, reason) in Reviewed)
        {
            if (!referenced.Contains(type))
            {
                Assert.Fail($"'{type}' is reviewed as \"{reason}\" but the command line references it no more; take it off the list.");
            }

            if (allowed.Contains(type) || AllowedNamespaces.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal)))
            {
                Assert.Fail($"'{type}' is reviewed as \"{reason}\" but a use case reaches it now; take it off the list.");
            }
        }
    }

    /// <summary>
    /// No container is built in the command line, the hidden commands' included: the composition root lives in
    /// Core, and a front end resolves a use case without knowing how its services are held together.
    /// </summary>
    [Fact]
    public void TheCommandLine_ReferencesNoContainer()
    {
        using var stream = File.OpenRead(CliRunner.CliAssemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        foreach (var handle in md.TypeReferences)
        {
            var reference = md.GetTypeReference(handle);

            Assert.False(
                md.GetString(reference.Name) is "ServiceCollection" or "ServiceProvider" or "IServiceScopeFactory",
                $"the command line references '{md.GetString(reference.Namespace)}.{md.GetString(reference.Name)}': its composition root lives in Core");
        }
    }

    /// <summary>
    /// Nothing in Core outside <c>Application\</c>, <c>Composition\</c> and <c>Help\</c> reaches into those
    /// layers: none names the composition root or the help, and none takes a type of the application layer that
    /// a use case does not hand it - only the layer's contracts (its use cases, their requests and results) are
    /// shared below it, so the layer's own machinery (its sessions, its scopes, its pipeline) can move without
    /// the machinery under it following.
    /// </summary>
    [Fact]
    public void TheCoreOutsideItsApplicationLayer_NeverReachesIntoIt()
    {
        var root = Path.Combine(Prose.RepositoryRoot(), "src", "DssHarness.Core");
        var machinery = NamesTheApplicationLayerOwns;
        var reaching = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            if (IsInsideItsOwnLayers(Path.GetRelativePath(root, file)))
            {
                continue;
            }

            foreach (var layer in new[] { "DssHarness.Core.Composition", "DssHarness.Core.Help" })
            {
                if (text.Contains(layer, StringComparison.Ordinal))
                {
                    reaching.Add($"{Path.GetFileName(file)} names {layer}");
                }
            }

            foreach (var name in machinery.Where(name => Regex.IsMatch(text, $"(?<![\\w.]){Regex.Escape(name)}\\b")))
            {
                reaching.Add($"{Path.GetFileName(file)} takes '{name}', the application layer's own");
            }
        }

        Assert.Empty(reaching);
    }

    /// <summary>
    /// Each command file stays within a budget a person reads whole, the two that declare several commands'
    /// options apart: wiring is all a command is, and wiring that outgrows its file has work hiding in it.
    /// </summary>
    [Fact]
    public void EachCommandFile_StaysWithinItsLineBudget()
    {
        var commands = Path.Combine(Prose.RepositoryRoot(), "src", "DssHarness.Cli", "Commands");

        foreach (var file in Directory.EnumerateFiles(commands, "*.cs"))
        {
            if (OverBudgetByDesign.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var lines = File.ReadLines(file).Count();

            Assert.True(lines <= Budget, $"{file} holds {lines} lines, past the {Budget} a command's wiring takes");
        }
    }

    /// <summary>The namespaces a front end may reach of Core directly.</summary>
    private static readonly string[] AllowedNamespaces =
    [
        "DssHarness.Core.Application",
        "DssHarness.Core.Help",
        "DssHarness.Core.Output",
        "DssHarness.Core.Results",
    ];

    /// <summary>Lines a command's wiring takes; wiring past it has work hiding in it.</summary>
    private const int Budget = 220;

    /// <summary>The two files that declare several commands' options, over the budget by design.</summary>
    private static readonly HashSet<string> OverBudgetByDesign = ["AnchorCommands.cs", "AgentCommands.cs"];

    /// <summary>
    /// The types the plan keeps where their rules are, each with its reason: a name Core's own sentences or
    /// wire say, a program the tool publishes as itself, a prompt the machine's own console asks, a status a
    /// command's whole contract is, or the rendering of the report a command's use case answers.
    /// </summary>
    private static readonly Dictionary<string, string> Reviewed = new(StringComparer.Ordinal)
    {
        // The composition root every front end calls once.
        ["DssHarness.Core.Composition.ServiceRegistration"] = "the composition root, called once by the console's host as every front end calls it",

        // The host wire and the hidden commands that speak it.
        ["DssHarness.Core.Processes.ContainedPlaces"] = "the places a contained child may write, which the hidden command that places one builds",

        // The console's own pieces, which only a front end can be or show.
        ["DssHarness.Core.Orchestration.Flow.FlowTerminal"] = "the console's own counting of a run session's interrupts, which only a front end can take",

        // The anchor commands' option names, kept where Core's own refusal sentences say them.
        ["DssHarness.Core.Anchors.AnchorCellNames"] = "the cells' names and the option that selects them all, which Core's own sentences name",
        ["DssHarness.Core.Anchors.AnchorCellInput"] = "one cell as the command line gave it, carrying its two options' own names for the refusal that names them",
        ["DssHarness.Core.Anchors.AnchorCellInputs"] = "resolves a cell's text from the option that gave it, where the command line reads it",

        // The run and its origins: named by every command that reports one.
        ["DssHarness.Core.Repository.CommandOrigin"] = "how a command line came to run, which the host agent's own serving of one sets",

        // What a front end may not do for itself: prompt a person.
        ["DssHarness.Core.Secrets.ISuperuserPrompt"] = "the console's own prompt, which only a front end can ask",
        ["DssHarness.Core.Secrets.PromptAvailability"] = "whether a prompt can be asked at all, which the console alone knows",
        ["DssHarness.Core.Secrets.MaskedPassword"] = "reading what a person types without echoing it, which only the console can",
        ["DssHarness.Core.Agents.SideTasks.SideTaskTerminal"] = "the console's own terminal for a side-task's chat, which only a front end can show",
        ["DssHarness.Core.Processes.IsolatedShell"] = "the hand-over of a shell command a program outside the isolation gives, which the program itself answers",

        // The renderers of the reports their commands' use cases answer: the text is the report's own, the
        // document kind is the session's, and neither is the command's to write.
        ["DssHarness.Core.Legs.LegsReports"] = "renders the report the legs command's use case answers",
        ["DssHarness.Core.Tools.ToolProvisionReports"] = "renders the report the install-missing-tools command's use case answers",
        ["DssHarness.Core.Agents.AgentProviderReports"] = "renders the report the list-agent-providers command's use case answers",
        ["DssHarness.Core.Anchors.AnchorReports"] = "renders the report the anchor commands' use case answers",
        ["DssHarness.Core.Anchors.AnchorCitationReports"] = "renders the report the check-anchor-citations command's use case answers",
        ["DssHarness.Core.Ci.CiLegsReports"] = "renders the report the check-ci-legs command's use case answers",
        ["DssHarness.Core.Orchestration.Flow.WorkflowExportReports"] = "renders the report the export-workflow command's use case answers",
        ["DssHarness.Core.Orchestration.Flow.WorkflowValidationReports"] = "renders the report the validate-workflow command's use case answers",
        ["DssHarness.Core.Codebase.CodebaseGraphQueryReports"] = "renders the report the query-codebase-graph command's use case answers",
        ["DssHarness.Core.LineEndings.LineEndingReports"] = "renders the report the fix-line-endings command's use case answers",
        ["DssHarness.Core.Orchestration.Flow.FlowReports"] = "renders the report the flow verbs' use case answers",
        ["DssHarness.Core.Orchestration.OrchestrationReports"] = "renders the report the orchestrator and agent verbs' use case answers",
        ["DssHarness.Core.SetUps.SetUpReports"] = "renders the report the set-up commands' use case answers",
        ["DssHarness.Core.Agents.SideTasks.SideTaskReports"] = "renders the report the side-task commands' use case answers",
        ["DssHarness.Core.Runs.RunStopReports"] = "renders the report the stop-run command's use case answers",
        ["DssHarness.Core.Worktrees.WorktreeReports"] = "renders the report the worktree commands' use case answers",

        // What the command line itself owns: the point past which a stop is no longer safe.
        ["DssHarness.Core.Execution.PointOfNoReturn"] = "how long the parser waits for a command past its point of no return, which the parser asks",

        // The hidden process commands the program starts for its own machinery.
        ["DssHarness.Core.Processes.ContainExec"] = "the hidden command that places a contained child, which the program runs for itself",
        ["DssHarness.Core.Processes.IsolateExec"] = "the hidden command that starts an isolated child's place on Windows, which the program runs for itself",
        ["DssHarness.Core.Hosts.HangUp"] = "the hang-up a held machine waits out, which the hidden hold's own wiring takes",

        // The owners of an option's name, kept where its rule is because Core's own sentences name the option.
        ["DssHarness.Core.Runners.CommandLineInputs"] = "the option that gives a runner its command line, which Core's own sentences name",
        ["DssHarness.Core.Runners.StepSelection"] = "the option that selects a runner's steps, which Core's own sentences name",

        // The owners of an option's values, which Core's own sentences name.
        ["DssHarness.Core.Codebase.GraphRelations"] = "the graph's own edge names, which the option that follows one lists as its values",
    };

    /// <summary>
    /// The names the application layer owns, spelled nowhere else in Core: its own machinery - what no use case
    /// hands to the layer below it (a session, a scope, a document kind, a pipeline event), every type of the
    /// layer a public member of a use case does not reach. Computed, never listed by hand, so a type that becomes
    /// a contract stops being machinery by itself.
    /// </summary>
    private static IReadOnlyList<string> NamesTheApplicationLayerOwns
    {
        get
        {
            var core = typeof(IUseCase).Assembly;
            var reached = TypesReachedFromTheUseCases();
            var owned = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var type in core.GetTypes())
            {
                if ((type.Namespace ?? string.Empty).StartsWith("DssHarness.Core.Application", StringComparison.Ordinal))
                {
                    // A name the layer shares says nothing on its own; a name only it spells, for a type no use
                    // case reaches, is its machinery.
                    owned[type.Name] = (!owned.TryGetValue(type.Name, out var ownedAlready) || ownedAlready)
                        && !reached.Contains(Name(type));
                    continue;
                }

                // Everything the rest of Core spells itself - its types, its members, the contracts it implements -
                // is shared vocabulary, never the layer's own machinery.
                owned[type.Name] = false;

                foreach (var member in type.GetMembers())
                {
                    owned[member.Name] = false;
                }

                foreach (var implemented in type.GetInterfaces())
                {
                    owned[implemented.Name] = false;
                }
            }

            return [.. owned.Where(entry => entry.Value).Select(entry => entry.Key)];
        }
    }

    /// <summary>Whether a file of Core is inside the application layer, its composition and its help.</summary>
    private static bool IsInsideItsOwnLayers(string relativePath)
        => relativePath.StartsWith("Application", StringComparison.Ordinal)
            || relativePath.StartsWith("Composition", StringComparison.Ordinal)
            || relativePath.StartsWith("Help", StringComparison.Ordinal);

    /// <summary>
    /// Every type of Core the command line's own assembly references, read from its metadata: each type its
    /// types, fields and calls name, nested types spelled as the metadata spells them.
    /// </summary>
    private static HashSet<string> CoreTypesTheCommandLineReferences()
    {
        using var stream = File.OpenRead(CliRunner.CliAssemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in md.TypeReferences)
        {
            var reference = md.GetTypeReference(handle);

            if (ScopeNamesTheCore(md, reference.ResolutionScope))
            {
                referenced.Add(FullName(md, handle));
            }
        }

        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);

            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                var parent = (TypeReferenceHandle)member.Parent;
                var reference = md.GetTypeReference(parent);

                if (ScopeNamesTheCore(md, reference.ResolutionScope))
                {
                    referenced.Add(FullName(md, parent));
                }
            }
        }

        return referenced;
    }

    /// <summary>Whether a type reference's scope resolves, however deeply nested it is, to the Core assembly.</summary>
    private static bool ScopeNamesTheCore(MetadataReader md, EntityHandle scope)
        => scope.Kind switch
        {
            HandleKind.AssemblyReference => Ordinal(
                md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
                "DssHarness.Core"),
            HandleKind.TypeReference => ScopeNamesTheCore(
                md,
                md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope),
            _ => false,
        };

    /// <summary>String equality the metadata's own way.</summary>
    private static bool Ordinal(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>A type reference's full name, nested types spelled <c>Ns.Outer/Inner</c> as the metadata does.</summary>
    private static string FullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var reference = md.GetTypeReference(handle);
        var name = md.GetString(reference.Name);

        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{FullName(md, (TypeReferenceHandle)reference.ResolutionScope)}/{name}"
            : $"{md.GetString(reference.Namespace)}.{name}";
    }

    /// <summary>
    /// The types a public member of a use case reaches, read from the assemblies themselves and never listed by
    /// hand: each use case's requests and results, and everything they are made of - their members' types, the
    /// types those reach in turn, their bases, their interfaces and their nested types.
    /// </summary>
    private static HashSet<string> TypesReachedFromTheUseCases()
    {
        var core = typeof(IUseCase).Assembly;
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<Type>();

        foreach (var type in core.GetTypes())
        {
            if (typeof(IUseCase).IsAssignableFrom(type) && type != typeof(IUseCase))
            {
                Enqueue(type);
            }
        }

        while (frontier.Count > 0)
        {
            var type = frontier.Dequeue();

            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case MethodInfo method:
                        Add(method.ReturnType);

                        foreach (var parameter in method.GetParameters())
                        {
                            Add(parameter.ParameterType);
                        }

                        break;

                    case PropertyInfo property:
                        Add(property.PropertyType);
                        break;

                    case FieldInfo field:
                        Add(field.FieldType);
                        break;

                    case EventInfo raised:
                        Add(raised.EventHandlerType!);
                        break;
                }
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                Enqueue(nested);
            }

            if (type.BaseType is { } baseType)
            {
                Add(baseType);
            }

            foreach (var implemented in type.GetInterfaces())
            {
                Add(implemented);
            }
        }

        return reached;

        void Enqueue(Type type)
        {
            if (type.Assembly == core && reached.Add(Name(type)))
            {
                frontier.Enqueue(type);
            }
        }

        void Add(Type type)
        {
            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                Add(type.GetElementType()!);
                return;
            }

            if (type.IsConstructedGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Add(argument);
                }

                Enqueue(type.GetGenericTypeDefinition());
                return;
            }

            Enqueue(type);
        }
    }

    /// <summary>A type's full name as the metadata spells it, nested types apart with <c>/</c>.</summary>
    private static string Name(Type type)
        => type.IsNested
            ? $"{Name(type.DeclaringType!)}/{type.Name}"
            : $"{type.Namespace}.{type.Name}";
}
