using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
