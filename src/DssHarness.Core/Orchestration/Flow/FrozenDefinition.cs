using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A run's definition as it is frozen in <c>definition.json</c>, read by this build whichever build froze it: the one
/// owner of what a definition an earlier release froze reads as. The state files' own rules hold
/// (<see cref="JsonStateFile"/>) - a member no record declares, a member written twice, a null where a value is needed and
/// a member that needs a value left out are each refused - with the two things an earlier release's definition holds
/// that this build's own never does read as what that release meant by them. A member that may be null and is left out
/// reads as null: an earlier release left out every null - before this build wrote each such member null or not - and
/// had none of the members added since, each of which says what null means as the release before it did. And a member
/// this build retired (<see cref="Retired"/>) is read in the shape it was written in and ignored: never applied, never
/// written back, so nothing read of the run's definition - its steps, its advisor, the digest a resume compares
/// (<see cref="FlowResume.RefuseAChangedDefinition"/>) - carries it. A new definition naming a retired key is refused by
/// its reader, naming what replaced it (<see cref="WorkflowFileParser.RetiredAdvisorKey"/>,
/// <see cref="WorkflowFileParser.RetiredGraphKey"/>).
/// </summary>
public static class FrozenDefinition
{
    /// <summary>
    /// Every member of a definition an earlier release froze that this build retired: the record that held it, its name
    /// as the file spells it, and the shape that release wrote it in, which it is still held to as it is read.
    /// </summary>
    public static IReadOnlyList<(Type Record, string Member, Type Shape)> Retired { get; } =
    [
        // advisor.kinds: every kind of question the engine raises is recommended now. Each release that read an advisor
        // wrote it, empty where the file named no kinds.
        (typeof(FlowAdvisor), "kinds", typeof(IReadOnlyList<string>)),

        // codebaseGraph.maxFileBytes: every file the graph claims is read whole now, whatever its size.
        (typeof(FlowCodebaseGraph), "maxFileBytes", typeof(int?)),
    ];

    /// <summary>
    /// The options <c>definition.json</c> is read with: the state files' own (<see cref="JsonStateFile.Options"/>), with
    /// what an earlier release's definition holds read as that release meant it. Never written with: this build writes
    /// its definitions as every state file is written.
    /// </summary>
    public static JsonSerializerOptions ReadOptions { get; } = new(JsonStateFile.Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AsAnEarlierReleaseFroze } },
    };

    /// <summary>Reads a run's frozen definition, whichever build froze it.</summary>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The run's <c>definition.json</c>.</param>
    /// <returns>The definition, or null where the file holds null.</returns>
    /// <exception cref="JsonException">It does not read as a definition.</exception>
    public static WorkflowDefinition? Read(IFileSystem fileSystem, string path)
        => JsonStateFile.Read<WorkflowDefinition>(fileSystem, path, ReadOptions);

    /// <summary>
    /// A record of the definition as an earlier release's frozen definition is read: each member that may be null no
    /// longer needed, and each member retired from it read and ignored.
    /// </summary>
    /// <param name="record">What the serializer knows of one record.</param>
    private static void AsAnEarlierReleaseFroze(JsonTypeInfo record)
    {
        if (record.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var member in record.Properties.Where(member => member.IsRequired && member.IsSetNullable))
        {
            member.IsRequired = false;
        }

        foreach (var (_, name, shape) in Retired.Where(retired => retired.Record == record.Type))
        {
            var read = record.CreateJsonPropertyInfo(shape, name);

            // Read whole, in its shape, and dropped: nothing gets it, so nothing writes it again.
            read.Get = null;
            read.Set = static (_, _) => { };
            record.Properties.Add(read);
        }
    }
}
