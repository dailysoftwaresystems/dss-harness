using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The shape of every record a run keeps, held to the options each is written and read with
/// (<see cref="JsonStateFile.Options"/>): a member that is null is left out as it is written, and a member the reader's
/// constructor needs is refused where it is left out - so a member a record may leave null must be one the reader can do
/// without (a default) or one written as null all the same. A record that breaks it is written once and never read
/// again: every command over the run refuses it as one that cannot be read.
/// </summary>
public sealed class RunRecordShapeTests
{
    /// <summary>
    /// Every member a run's records may leave null reads back where it is left out: a constructor's parameter that may be
    /// null is optional, or written as null all the same - the one rule, held over every type the run's store reads,
    /// whatever reaches it, the frozen definition among them.
    /// </summary>
    [Fact]
    public void EveryMemberARunsRecordsMayLeaveNull_ReadsBackWhereItIsLeftOut()
    {
        var unreadable = Reachable(RecordsTheRunsStoreReads()).SelectMany(Unreadable).Order(StringComparer.Ordinal).ToList();

        // Named whole, every one at once, so a failure says each member to put right.
        Assert.True(unreadable.Count == 0, $"members a run's records cannot read back where they are left out: {string.Join(", ", unreadable)}");
    }

    /// <summary>
    /// The walk the rule above takes reaches the definition's nested records: a frozen definition's recovery and its
    /// codebase graph are among the types it holds to the rule, so a nested record cannot slip past it.
    /// </summary>
    [Fact]
    public void TheWalk_ReachesTheDefinitionsNestedRecords()
    {
        var reached = Reachable(RecordsTheRunsStoreReads());

        Assert.Contains(typeof(WorkflowDefinition), reached);
        Assert.Contains(typeof(FlowRecover), reached);
        Assert.Contains(typeof(FlowCodebaseGraph), reached);
        Assert.Contains(typeof(WorkflowStep), reached);
        Assert.Contains(typeof(FlowQuestionsFile), reached);
        Assert.Contains(typeof(FlowRefusedAnswer), reached);
    }

    /// <summary>
    /// A definition that leaves out every key it may - a recovery that names no maxVisits, a codebase graph that names
    /// only what it excludes - is frozen and read back as it ran: the run's store writes it and reads it whole, its
    /// digest the same, so every command over the run can go on with it.
    /// </summary>
    [Fact]
    public void ADefinitionLeavingOutEveryKeyItMay_IsReadBackAsItWasFrozen()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            codebaseGraph:
              exclude: ['vendor/**']
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: one
                type: orient
                onFailure: recover
            """);

        Assert.Null(definition.Recover!.MaxVisits);
        Assert.Null(definition.CodebaseGraph!.Include);

        harness.FileSystem.CreateDirectory(layout.Directory);
        harness.OrchestrationStore.WriteDefinition(layout, definition with { Path = string.Empty });

        var frozen = harness.OrchestrationStore.ReadDefinition(layout);

        Assert.NotNull(frozen);
        Assert.Null(frozen.Recover!.MaxVisits);
        Assert.Equal(FlowRecover.MostVisits, frozen.Recover.VisitsAllowed);
        Assert.Equal(["vendor/**"], frozen.CodebaseGraph!.Exclude);
        Assert.Null(frozen.CodebaseGraph.Include);
        Assert.Equal(FlowRunContext.DigestOf(definition), FlowRunContext.DigestOf(frozen));
    }

    /// <summary>
    /// The definition of a file, as the run's store froze it and as an earlier release wrote it, for the tests of what an
    /// earlier release's frozen definition reads as: an advisor on the configuration's tier, a codebase graph, an input,
    /// a param and a recovery, each leaving out a member that may be null; a sweep listing its arms and one naming none;
    /// and a test step whose filter is one text.
    /// </summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, WorkflowDefinition Definition, string Ours) FrozenByThisBuild(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
            codebaseGraph:
              exclude: ['vendor/**']
            inputs:
              - name: request
                kind: text
            params:
              - name: tone
                default: plain
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: one
                type: orient
                onFailure: recover
              - name: swept
                type: check-mutations
                arms: [arm-1, arm-2]
              - name: every-arm
                type: check-mutations
              - name: tested
                type: test
                filter: run.affectedTests
            """);

        harness.FileSystem.CreateDirectory(layout.Directory);
        harness.OrchestrationStore.WriteDefinition(layout, definition);

        return (harness, layout, definition, File.ReadAllText(layout.DefinitionFile));
    }

    /// <summary>
    /// A definition an earlier release froze reads as that release meant it: its advisor's retired kinds - written
    /// empty where the file named none, or naming kinds - and its graph's retired maxFileBytes read and ignored, and
    /// every member that may be null and that it left out - an advisor's tier, model, effort and client, a recovery's
    /// maxVisits, an input's and a param's description and default - read as null. So it is the definition this build
    /// reads of the same file, digest for digest, and written again it is what this build writes of it, nothing retired
    /// in it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("failure")]
    public void ADefinitionAnEarlierReleaseFroze_ReadsAsTheOneThisBuildReadsOfTheSameFile(string kinds)
    {
        using var temp = new TempDirectory();
        var (harness, layout, definition, ours) = FrozenByThisBuild(temp);
        var earlier = EarlierRelease.WithoutNulls(EarlierRelease.WithKinds(EarlierRelease.Frozen(layout), kinds.Length == 0 ? [] : [kinds]));
        var graph = earlier["codebaseGraph"]!.AsObject();

        graph.Insert(graph.IndexOf("exclude") + 1, "maxFileBytes", 1_048_576);
        File.WriteAllText(layout.DefinitionFile, earlier.ToJsonString(JsonStateFile.Options) + "\n");

        var frozen = harness.OrchestrationStore.ReadDefinition(layout)!;

        Assert.All(new[] { frozen.Advisor!.Tier, frozen.Advisor.Model, frozen.Advisor.Effort, frozen.Advisor.Client, frozen.Inputs[0].Description, frozen.Inputs[0].Default, frozen.Params[0].Description }, Assert.Null);
        Assert.Null(frozen.Recover!.MaxVisits);
        Assert.Equal("plain", frozen.Params[0].Default);
        Assert.Equal(FlowRunContext.DigestOf(definition), FlowRunContext.DigestOf(frozen));

        harness.OrchestrationStore.WriteDefinition(layout, frozen);

        Assert.Equal(ours, File.ReadAllText(layout.DefinitionFile));
    }

    /// <summary>
    /// A definition an earlier release froze holding a member in a shape since changed reads as that release meant it:
    /// a sweep's arms - a list written on every step, empty on each that named none - read as the list the parser makes of
    /// the ids the file lists, and as no arms, every arm swept, where it named none; a test step's filter - one text - read
    /// as the expression the parser makes of the same scalar. So it is the definition this build reads of the same file,
    /// digest for digest, and written again it is what this build writes of it, in this build's shape alone.
    /// </summary>
    [Theory]
    [InlineData("arms")]
    [InlineData("filter")]
    public void ADefinitionAnEarlierReleaseFrozeInAShapeSinceChanged_ReadsAsTheOneThisBuildReadsOfTheSameFile(string reshaped)
    {
        using var temp = new TempDirectory();
        var (harness, layout, definition, ours) = FrozenByThisBuild(temp);
        var listed = EarlierRelease.WithArmsListed(EarlierRelease.WithKinds(EarlierRelease.Frozen(layout)));
        var earlier = reshaped == "arms" ? listed : EarlierRelease.WithFilterText(listed);

        File.WriteAllText(layout.DefinitionFile, earlier.ToJsonString(JsonStateFile.Options) + "\n");

        var frozen = harness.OrchestrationStore.ReadDefinition(layout)!;
        var swept = frozen.Steps.Single(step => step.Name == "swept").Arms!;
        var filter = frozen.Steps.Single(step => step.Name == "tested").Filter!;

        Assert.Equal(["arm-1", "arm-2"], swept.Written);
        Assert.Null(swept.Expression);

        // The steps that named no arms - the recovery's among them, at its depth - hold none, as the file read now does.
        Assert.All(frozen.Steps.Where(step => step.Name != "swept").Concat(frozen.Recover!.Steps), step => Assert.Null(step.Arms));
        Assert.Empty(filter.Written);
        Assert.Equal("run.affectedTests", filter.Expression);
        Assert.Equal(FlowRunContext.DigestOf(definition), FlowRunContext.DigestOf(frozen));

        harness.OrchestrationStore.WriteDefinition(layout, frozen);

        Assert.Equal(ours, File.ReadAllText(layout.DefinitionFile));
    }

    /// <summary>
    /// A reshaped member is read in the shapes a release wrote it in and in no other: a sweep's arms given as one text and
    /// a test step's filter given as a list - a list written out is this build's record of one - are each refused, the
    /// filter at the line it stands on, and never read as nothing.
    /// </summary>
    /// <param name="step">The step whose member is written in a shape no release wrote it in.</param>
    /// <param name="member">The member.</param>
    /// <param name="value">What it is written as.</param>
    /// <param name="why">The refusal, its line and the byte after the value's first token in it - each from 0 - as {0} and {1}.</param>
    [Theory]
    [InlineData("swept", "arms", "\"arm-1\"", "Expected an array, found String")]
    [InlineData("tested", "filter", "[\"run.affectedTests\"]","The JSON value could not be converted to DssHarness.Core.Orchestration.Flow.WorkflowDefinition. Path: $.steps | LineNumber: {0} | BytePositionInLine: {1}")]
    public void AReshapedMemberInAShapeNoReleaseWroteItIn_IsRefused(string step, string member, string value, string why)
    {
        using var temp = new TempDirectory();
        var (harness, layout, _, _) = FrozenByThisBuild(temp);
        var frozen = EarlierRelease.WithArmsListed(EarlierRelease.Frozen(layout));

        frozen["steps"]!.AsArray().Single(written => (string?)written!["name"] == step)![member] = JsonNode.Parse(value);

        var text = frozen.ToJsonString(JsonStateFile.Options) + "\n";
        var lines = text.Split('\n');
        var line = Array.FindIndex(lines, written => written.TrimStart().StartsWith($"\"{member}\": {value[0]}", StringComparison.Ordinal));

        File.WriteAllText(layout.DefinitionFile, text);

        var (read, refused) = harness.OrchestrationStore.ReadDefinitionOrWhy(layout);

        Assert.Null(read);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, why, line, lines[line].IndexOf(value[0], StringComparison.Ordinal) + 1), refused);
    }

    /// <summary>
    /// Each reshaped member's two shapes are told apart by how its JSON opens: this build's is a record, written as an
    /// object, and the earlier one never is - so neither shape of any member can be read as the other.
    /// </summary>
    [Fact]
    public void EveryReshapedMember_IsARecordNow_AndWasNeverOneBefore()
    {
        var options = FrozenDefinition.ReadOptions;

        Assert.Equal(new[] { (typeof(WorkflowStep), "arms"), (typeof(WorkflowStep), "filter") }, FrozenDefinition.Reshaped.Select(reshaped => (reshaped.Record, reshaped.Member)));

        foreach (var (record, member, shape, _) in FrozenDefinition.Reshaped)
        {
            var now = options.GetTypeInfo(record).Properties.Single(property => property.Name == member).PropertyType;

            Assert.Equal(JsonTypeInfoKind.Object, options.GetTypeInfo(now).Kind);
            Assert.NotEqual(JsonTypeInfoKind.Object, options.GetTypeInfo(shape).Kind);
        }
    }

    /// <summary>
    /// A frozen definition is read as strictly as every state file otherwise: a retired member in a shape no release wrote
    /// it in, a member no release wrote, and a member that needs a value left out are each refused, and never read as
    /// nothing - only what an earlier release meant by its own form is taken as it meant it.
    /// </summary>
    [Theory]
    [InlineData("kinds", "Expected an array, found Number")]
    [InlineData("unknown", "The JSON property 'unknown' could not be mapped to any .NET member contained in type 'DssHarness.Core.Orchestration.Flow.FlowAdvisor'")]
    [InlineData("agent", "JSON deserialization for type 'DssHarness.Core.Orchestration.Flow.FlowAdvisor' was missing required properties including: 'agent'")]
    public void AFrozenDefinitionsMemberNoReleaseWroteSo_IsRefused(string member, string why)
    {
        using var temp = new TempDirectory();
        var (harness, layout, _, _) = FrozenByThisBuild(temp);
        var frozen = EarlierRelease.Frozen(layout);
        var advisor = frozen["advisor"]!.AsObject();

        if (member == "agent")
        {
            _ = advisor.Remove(member);
        }
        else
        {
            advisor[member] = 5;
        }

        File.WriteAllText(layout.DefinitionFile, frozen.ToJsonString(JsonStateFile.Options) + "\n");

        var (read, refused) = harness.OrchestrationStore.ReadDefinitionOrWhy(layout);

        Assert.Null(read);
        Assert.Equal(why, refused);
    }

    /// <summary>The types the run's store reads, each its record whole: what every <c>Read</c> of <see cref="OrchestrationStore"/> answers.</summary>
    private static IEnumerable<Type> RecordsTheRunsStoreReads()
        => typeof(OrchestrationStore).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name.StartsWith("Read", StringComparison.Ordinal) && method.ReturnType != typeof(void))
            .Select(method => method.ReturnType);

    /// <summary>Every type of this tool's own that <paramref name="roots"/> reach through the members they serialize.</summary>
    private static HashSet<Type> Reachable(IEnumerable<Type> roots)
    {
        var reached = new HashSet<Type>();
        var waiting = new Queue<Type>(roots);

        while (waiting.TryDequeue(out var type))
        {
            foreach (var own in OwnTypesIn(type))
            {
                if (!reached.Add(own) || own.IsEnum || own.GetCustomAttribute<JsonConverterAttribute>() is not null)
                {
                    continue;
                }

                foreach (var property in Serialized(own))
                {
                    waiting.Enqueue(property.PropertyType);
                }

                foreach (var derived in own.GetCustomAttributes<JsonDerivedTypeAttribute>())
                {
                    waiting.Enqueue(derived.DerivedType);
                }
            }
        }

        return reached;
    }

    /// <summary>The types of this tool's own that <paramref name="type"/> is or holds: itself, a nullable's value, an array's items, a generic's arguments.</summary>
    private static IEnumerable<Type> OwnTypesIn(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } value)
        {
            return OwnTypesIn(value);
        }

        if (type.IsArray)
        {
            return OwnTypesIn(type.GetElementType()!);
        }

        if (type.IsGenericType && type.Assembly != typeof(OrchestrationStore).Assembly)
        {
            return type.GetGenericArguments().SelectMany(OwnTypesIn);
        }

        return type.Assembly == typeof(OrchestrationStore).Assembly ? [type] : [];
    }

    /// <summary>The members of <paramref name="type"/> its record holds: each public property read, that no <see cref="JsonIgnoreAttribute"/> leaves out always.</summary>
    private static IEnumerable<PropertyInfo> Serialized(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always });

    /// <summary>
    /// The members of <paramref name="type"/> that cannot be read back where they were null, by name: a constructor's
    /// parameter that may be null, neither optional nor written as null, and a required member that may be null and is
    /// not written as null.
    /// </summary>
    private static IEnumerable<string> Unreadable(Type type)
    {
        var nullability = new NullabilityInfoContext();
        var properties = Serialized(type).ToList();

        bool WrittenAsNull(string name)
            => properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Never };

        var constructor = ReadersConstructor(type);

        foreach (var parameter in constructor?.GetParameters() ?? [])
        {
            if (!parameter.IsOptional && MayBeNull(parameter.ParameterType, nullability.Create(parameter)) && !WrittenAsNull(parameter.Name!))
            {
                yield return $"{type.Name}({parameter.Name})";
            }
        }

        foreach (var property in properties.Where(property => property.GetCustomAttribute<RequiredMemberAttribute>() is not null || property.GetCustomAttribute<JsonRequiredAttribute>() is not null))
        {
            if (MayBeNull(property.PropertyType, nullability.Create(property)) && !WrittenAsNull(property.Name))
            {
                yield return $"{type.Name}.{property.Name}";
            }
        }
    }

    /// <summary>The constructor the serializer reads <paramref name="type"/> through: the one marked for it, else the one public constructor with parameters where the type has no parameterless one.</summary>
    private static ConstructorInfo? ReadersConstructor(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        return constructors.FirstOrDefault(constructor => constructor.GetCustomAttribute<JsonConstructorAttribute>() is not null)
            ?? (constructors.Any(constructor => constructor.GetParameters().Length == 0) ? null : constructors.SingleOrDefault());
    }

    /// <summary>Whether a member of <paramref name="type"/>, as its annotations say, may be null.</summary>
    private static bool MayBeNull(Type type, NullabilityInfo nullability)
        => Nullable.GetUnderlyingType(type) is not null || (!type.IsValueType && nullability.WriteState == NullabilityState.Nullable);
}
