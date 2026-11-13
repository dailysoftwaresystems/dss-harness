using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    /// results - or is reached from an operation of a use case, read from the metadata and never listed by hand: a
    /// request, a result, and what they are made of - or is one of the few reviewed below, each with the reason it
    /// stays where it is.
    /// </summary>
    [Fact]
    public void TheCommandLine_ReferencesTheCoreOnlyThroughItsUseCasesItsOutputAndItsResults()
    {
        var referenced = CoreTypesTheCommandLineReferences();
        var allowed = Reached.Value;
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
    /// None of the platform's seams - the file system, the program runner, git, the host platform, the loader of a
    /// repository's context - is ever a type a front end may reach: no operation of a use case takes or answers one, no
    /// reviewed entry stands for one, and none is in a namespace a front end reaches whole. A front end that resolved
    /// one would do a use case's work itself, outside every use case.
    /// </summary>
    [Fact]
    public void TheUseCases_ReachNoneOfThePlatformsSeams()
    {
        string[] seams =
        [
            Name(typeof(DssHarness.Core.FileSystem.IFileSystem)),
            Name(typeof(DssHarness.Core.Processes.IProcessRunner)),
            Name(typeof(DssHarness.Core.Git.IGitClient)),
            Name(typeof(DssHarness.Core.Platform.IHostPlatform)),
            Name(typeof(DssHarness.Core.Repository.IHarnessContextLoader)),
        ];

        Assert.DoesNotContain(seams, Reached.Value.Contains);
        Assert.DoesNotContain(seams, Reviewed.ContainsKey);
        Assert.DoesNotContain(seams, seam => AllowedNamespaces.Any(prefix => seam.StartsWith(prefix, StringComparison.Ordinal)));
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
    /// layers: none takes a type of the composition root or the help, and none takes a type of the application layer
    /// that is not one of its contracts - its use cases with their requests and results, and the few points it is open
    /// to below it, each reviewed with its reason - so the layer's own machinery (its sessions, its scopes, its
    /// pipeline) can move without the machinery under it following. Read from the assembly, never from the source's
    /// text: every type each type below the layers names in its bases, its members' signatures, its locals and the
    /// instructions of its bodies, however its source spells the name - whole, relative to its namespace, or through a
    /// generic.
    /// </summary>
    [Fact]
    public void TheCoreOutsideItsApplicationLayer_NeverReachesIntoIt()
    {
        var core = typeof(IUseCase).Assembly;
        var below = core.GetTypes().Where(type => !InsideItsOwnLayers(type)).ToList();
        var reaching = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in below)
        {
            foreach (var named in TypesNamedBy(type).Where(named => named.Assembly == core))
            {
                if (named.Namespace is { } space && (Under(space, Composition) || Under(space, Help)))
                {
                    reaching.Add($"{Name(type)} takes '{Name(named)}', of {space}");
                }
                else if (named.Namespace is { } layer && Under(layer, Application)
                    && !Reached.Value.Contains(Name(named)) && !OpenBelow.ContainsKey(Name(named)))
                {
                    reaching.Add($"{Name(type)} takes '{Name(named)}', the application layer's own");
                }
            }
        }

        Assert.Empty(reaching);

        // Each point the layer is open to is an interface of the layer that a service below it implements: one nothing
        // below implements any more comes off the list rather than staying open.
        foreach (var (point, reason) in OpenBelow)
        {
            var open = core.GetTypes().SingleOrDefault(type => Name(type) == point);

            if (open is not { IsInterface: true, Namespace: { } space } || !Under(space, Application)
                || !below.Any(type => type.GetInterfaces().Contains(open)))
            {
                Assert.Fail($"'{point}' is open below the application layer as \"{reason}\" but nothing below it implements it; take it off the list.");
            }
        }
    }

    /// <summary>
    /// The points the application layer is open to below it, each with its reason: an interface of the layer that a
    /// service below it implements, for the layer's machinery to call what the service did - the one way the layer
    /// below may name a type of the layer that no use case hands it.
    /// </summary>
    private static readonly Dictionary<string, string> OpenBelow = new(StringComparer.Ordinal)
    {
        ["DssHarness.Core.Application.IDocumentFacts"] = "what an operation did that every document it answers with says, which the service that did it fills and the session writes",
    };

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

    /// <summary>
    /// The documents say the boundaries these tests enforce, in the terms the tests hold: the seam a front end
    /// reaches Core through, the reviewed few and why each stays, the layer below sharing only the layer's
    /// contracts, the one composition root, the budget a command's wiring keeps - so a reader of the documents
    /// and a reader of the tests are told the same thing.
    /// </summary>
    [Fact]
    public void TheLayeringTheDocumentsState_IsTheOneTheTestsHold()
    {
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "The seam a front end reaches Core through is narrower still, and held by tests rather than discipline",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "or is one of a few reviewed types, each with its reason on the list",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "A reviewed entry nothing references any more, or one a use case reaches, is taken off the list - the test demands both",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "The services a command only calls are marked as the use cases they are",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "the layer below shares the layer's contracts but never its machinery",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words(
                $"None of the platform's seams (`{nameof(DssHarness.Core.FileSystem.IFileSystem)}`, `{nameof(DssHarness.Core.Processes.IProcessRunner)}`, "
                + $"`{nameof(DssHarness.Core.Git.IGitClient)}`, `{nameof(DssHarness.Core.Platform.IHostPlatform)}`, "
                + $"`{nameof(DssHarness.Core.Repository.IHarnessContextLoader)}`) is ever among them"),
            Prose.Words(architecture),
            StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words("That is read from the core's own metadata, not its source's text"),
            Prose.Words(architecture),
            StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words($"the few points it is open to below it, each reviewed with its reason - the facts every document of an operation says (`{nameof(DssHarness.Core.Application.IDocumentFacts)}`)"),
            Prose.Words(architecture),
            StringComparison.Ordinal);
        Assert.Contains("The command line builds no container", architecture, StringComparison.Ordinal);
        Assert.Contains(
            "no command's wiring outgrows 220 lines but the two files that declare several commands' options",
            architecture,
            StringComparison.Ordinal);

        var readme = Prose.Words(File.ReadAllText(Path.Combine(Prose.RepositoryRoot(), "README.md")));

        Assert.Contains(
            "everything a command does lives in the Core library behind a use case - one interface per operation, its request and its report the contract",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "the boundary is enforced by tests that read the built command line itself",
            readme,
            StringComparison.Ordinal);
    }

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
        ["DssHarness.Core.Orchestration.Flow.WorkflowCacheReports"] = "renders the report the workflow-cache command's use case answers",
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

    /// <summary>The application layer's namespace, its use cases and their machinery.</summary>
    private const string Application = "DssHarness.Core.Application";

    /// <summary>The composition root's namespace.</summary>
    private const string Composition = "DssHarness.Core.Composition";

    /// <summary>The help's namespace.</summary>
    private const string Help = "DssHarness.Core.Help";

    /// <summary>The types the use cases reach, computed once for every test that asks.</summary>
    private static readonly Lazy<HashSet<string>> Reached = new(TypesReachedFromTheUseCases);

    /// <summary>Every opcode by its value, for reading a method's instructions.</summary>
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>Whether <paramref name="space"/> is <paramref name="layer"/> or a namespace inside it.</summary>
    private static bool Under(string space, string layer)
        => string.Equals(space, layer, StringComparison.Ordinal) || space.StartsWith(layer + ".", StringComparison.Ordinal);

    /// <summary>Whether a type of Core is inside the application layer, its composition or its help, nested types by their outermost.</summary>
    private static bool InsideItsOwnLayers(Type type)
        => type.Namespace is { } space && (Under(space, Application) || Under(space, Composition) || Under(space, Help));

    /// <summary>
    /// Every type <paramref name="type"/> names: its bases and interfaces, its generic parameters' constraints, the
    /// signatures of its fields, properties, events, methods and constructors, and in each body its locals, the
    /// exceptions it catches and every type, method and field its instructions name - with their signatures - each
    /// taken apart into what it is made of: an array's element, a generic's definition and arguments.
    /// </summary>
    private static HashSet<Type> TypesNamedBy(Type type)
    {
        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var named = new HashSet<Type>();

        if (type.BaseType is { } baseType)
        {
            Take(baseType);
        }

        foreach (var implemented in type.GetInterfaces())
        {
            Take(implemented);
        }

        foreach (var parameter in type.IsGenericTypeDefinition ? type.GetGenericArguments() : [])
        {
            foreach (var constraint in parameter.GetGenericParameterConstraints())
            {
                Take(constraint);
            }
        }

        foreach (var field in type.GetFields(Everything))
        {
            Take(field.FieldType);
        }

        foreach (var property in type.GetProperties(Everything))
        {
            Take(property.PropertyType);
        }

        foreach (var raised in type.GetEvents(Everything))
        {
            Take(raised.EventHandlerType!);
        }

        foreach (var method in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
        {
            Signature(method);

            foreach (var parameter in method.IsGenericMethodDefinition ? method.GetGenericArguments() : [])
            {
                foreach (var constraint in parameter.GetGenericParameterConstraints())
                {
                    Take(constraint);
                }
            }

            if (method.GetMethodBody() is not { } body)
            {
                continue;
            }

            foreach (var local in body.LocalVariables)
            {
                Take(local.LocalType);
            }

            foreach (var clause in body.ExceptionHandlingClauses.Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause))
            {
                Take(clause.CatchType!);
            }

            var typeArguments = type.IsGenericTypeDefinition ? type.GetGenericArguments() : null;
            var methodArguments = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

            foreach (var token in TokensIn(body.GetILAsByteArray() ?? []))
            {
                switch (type.Module.ResolveMember(token, typeArguments, methodArguments))
                {
                    case Type referenced:
                        Take(referenced);
                        break;

                    case MethodBase called:
                        Take(called.DeclaringType!);
                        Signature(called);

                        foreach (var argument in called.IsGenericMethod ? called.GetGenericArguments() : [])
                        {
                            Take(argument);
                        }

                        break;

                    case FieldInfo field:
                        Take(field.DeclaringType!);
                        Take(field.FieldType);
                        break;
                }
            }
        }

        return named;

        void Signature(MethodBase method)
        {
            if (method is MethodInfo returning)
            {
                Take(returning.ReturnType);
            }

            foreach (var parameter in method.GetParameters())
            {
                Take(parameter.ParameterType);
            }
        }

        void Take(Type taken)
        {
            if (taken.HasElementType)
            {
                Take(taken.GetElementType()!);
            }
            else if (taken.IsGenericParameter)
            {
                return;
            }
            else if (taken.IsConstructedGenericType)
            {
                Take(taken.GetGenericTypeDefinition());

                foreach (var argument in taken.GetGenericArguments())
                {
                    Take(argument);
                }
            }
            else
            {
                named.Add(taken);
            }
        }
    }

    /// <summary>The metadata tokens an IL body's instructions carry for a type, a method or a field, in order.</summary>
    private static IEnumerable<int> TokensIn(byte[] il)
    {
        var at = 0;

        while (at < il.Length)
        {
            var code = OpCodesByValue[il[at] == 0xFE ? unchecked((short)(0xFE00 | il[at + 1])) : il[at]];

            at += code.Size;

            switch (code.OperandType)
            {
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType:
                    yield return BitConverter.ToInt32(il, at);
                    at += 4;
                    break;

                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    at += 1;
                    break;

                case OperandType.InlineVar:
                    at += 2;
                    break;

                case OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString or OperandType.ShortInlineR:
                    at += 4;
                    break;

                case OperandType.InlineI8 or OperandType.InlineR:
                    at += 8;
                    break;

                case OperandType.InlineSwitch:
                    at += 4 + (4 * BitConverter.ToInt32(il, at));
                    break;
            }
        }
    }

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
    /// The types a front end may reach through the use cases, read from the assemblies themselves and never listed by
    /// hand: each use case - an interface deriving <see cref="IUseCase"/>, or a class marked as one with no such
    /// interface of its own - and the marker they share; the requests and results of each instance operation a use case
    /// declares; and from those only what data is made of - each type's public instance properties and fields, its bases
    /// and interfaces, a delegate's signature, and an array's element or a generic's arguments. A static helper beside a
    /// use case is no operation of it, a method of a request or a result is no part of what it is made of, and a
    /// constructor's parameters are a record's own properties already and a service's dependencies otherwise - a service
    /// a request carries is handed over made, never made by a front end - so none of them widens what a front end may
    /// reach.
    /// </summary>
    private static HashSet<string> TypesReachedFromTheUseCases()
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var core = typeof(IUseCase).Assembly;
        var reached = new HashSet<string>(StringComparer.Ordinal) { Name(typeof(IUseCase)) };
        var frontier = new Queue<Type>();

        foreach (var useCase in core.GetTypes().Where(IsUseCase))
        {
            reached.Add(Name(useCase));

            foreach (var operation in useCase.GetMethods(Declared))
            {
                Signature(operation);
            }
        }

        while (frontier.Count > 0)
        {
            var type = frontier.Dequeue();

            foreach (var property in type.GetProperties(Declared))
            {
                Add(property.PropertyType);
            }

            foreach (var field in type.GetFields(Declared))
            {
                Add(field.FieldType);
            }

            if (type.BaseType is { } baseType)
            {
                Add(baseType);
            }

            foreach (var implemented in type.GetInterfaces())
            {
                Add(implemented);
            }

            if (typeof(Delegate).IsAssignableFrom(type) && type.GetMethod("Invoke") is { } invoke)
            {
                Signature(invoke);
            }
        }

        return reached;

        void Signature(MethodBase method)
        {
            if (method is MethodInfo returning)
            {
                Add(returning.ReturnType);
            }

            foreach (var parameter in method.GetParameters())
            {
                Add(parameter.ParameterType);
            }
        }

        void Enqueue(Type type)
        {
            if (type.Assembly == core && reached.Add(Name(type)))
            {
                frontier.Enqueue(type);
            }
        }

        void Add(Type type)
        {
            if (type.HasElementType)
            {
                Add(type.GetElementType()!);
                return;
            }

            if (type.IsGenericParameter)
            {
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

    /// <summary>
    /// Whether a type of Core is a use case a front end resolves: an interface deriving <see cref="IUseCase"/>, or a class
    /// marked as one that implements no such interface - where it does, the interface is the use case.
    /// </summary>
    private static bool IsUseCase(Type type)
        => type != typeof(IUseCase)
            && typeof(IUseCase).IsAssignableFrom(type)
            && (type.IsInterface || !type.GetInterfaces().Any(implemented => implemented != typeof(IUseCase) && typeof(IUseCase).IsAssignableFrom(implemented)));

    /// <summary>A type's full name as the metadata spells it, nested types apart with <c>/</c>.</summary>
    private static string Name(Type type)
        => type.IsNested
            ? $"{Name(type.DeclaringType!)}/{type.Name}"
            : $"{type.Namespace}.{type.Name}";
}
