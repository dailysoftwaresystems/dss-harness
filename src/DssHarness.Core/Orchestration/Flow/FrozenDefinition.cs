using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A run's definition as it is frozen in <c>definition.json</c>, read by this build whichever build froze it: the one
/// owner of what a definition an earlier release froze reads as. The state files' own rules hold
/// (<see cref="JsonStateFile"/>) - a member no record declares, a member written twice, a null where a value is needed and
/// a member that needs a value left out are each refused - with the three things an earlier release's definition holds
/// that this build's own never does read as what that release meant by them. A member that may be null and is left out
/// reads as null: an earlier release left out every null - before this build wrote each such member null or not - and
/// had none of the members added since, each of which says what null means as the release before it did. A member
/// this build retired (<see cref="Retired"/>) is read in the shape it was written in and ignored: never applied, never
/// written back, so nothing read of the run's definition - its steps, its advisor, the digest a resume compares
/// (<see cref="FlowResume.RefuseAChangedDefinition"/>) - carries it. A new definition naming a retired key is refused by
/// its reader, naming what replaced it (<see cref="WorkflowFileParser.RetiredAdvisorKey"/>,
/// <see cref="WorkflowFileParser.RetiredGraphKey"/>). And a member this build reshaped (<see cref="Reshaped"/>) is read in
/// the shape that release wrote it in, as what this build's parser makes of the same words - so the definition says what
/// the unchanged entry does, digest for digest, and is written back in this build's shape alone.
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
    /// Every member of a definition an earlier release froze in a shape this build no longer writes it in: the record that
    /// holds it, its name as the file spells it, the shape that release wrote it in, and its reader. This build's shape of
    /// each is a record, written as an object, and the earlier one never was: an object is held to this build's shape,
    /// and anything else to the earlier one, made what <see cref="WorkflowFileParser"/> makes of the key the file said -
    /// neither is ever taken for the other.
    /// </summary>
    public static IReadOnlyList<(Type Record, string Member, Type Shape, JsonConverter Reader)> Reshaped { get; } =
    [
        // A sweep's arms: a list of arm ids, written on every step and empty where the file named none - which swept
        // every arm, as arms left out does now. The ids written out are the list the parser reads of the same ids.
        Reshape<IReadOnlyList<string>, FlowList>(typeof(WorkflowStep), "arms", ids => ids.Count == 0 ? null : new FlowList(ids, Expression: null)),

        // A test step's filter: one text, which the parser reads now as the expression a scalar is.
        Reshape<string, FlowList>(typeof(WorkflowStep), "filter", text => new FlowList([], text)),
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
    /// longer needed, each member retired from it read and ignored, and each member reshaped read in either shape.
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

        foreach (var (_, name, _, reader) in Reshaped.Where(reshaped => reshaped.Record == record.Type))
        {
            record.Properties.Single(member => string.Equals(member.Name, name, StringComparison.Ordinal)).CustomConverter = reader;
        }
    }

    /// <summary>One entry of <see cref="Reshaped"/>: the member, the shape an earlier release wrote it in, and its reader.</summary>
    /// <typeparam name="TShape">The shape an earlier release wrote the member in.</typeparam>
    /// <typeparam name="TNow">The member's shape in this build.</typeparam>
    /// <param name="record">The record that holds the member.</param>
    /// <param name="member">Its name, as the file spells it.</param>
    /// <param name="asParsed">What the parser makes of the same words, the member read in its earlier shape.</param>
    private static (Type Record, string Member, Type Shape, JsonConverter Reader) Reshape<TShape, TNow>(Type record, string member, Func<TShape, TNow?> asParsed)
        where TShape : class
        where TNow : class
        => (record, member, typeof(TShape), new EarlierOrNow<TShape, TNow>(asParsed));

    /// <summary>
    /// A reshaped member read in the shape its JSON is written in: an object is this build's own, a record; anything else
    /// is held to the shape an earlier release wrote it in - never an object - and made what the parser makes of the same
    /// words. Null is null in either, and never reaches it. Written as this build writes the member, though
    /// <see cref="ReadOptions"/> never write.
    /// </summary>
    /// <typeparam name="TShape">The shape an earlier release wrote the member in.</typeparam>
    /// <typeparam name="TNow">The member's shape in this build.</typeparam>
    /// <param name="asParsed">What the parser makes of the same words, the member read in its earlier shape.</param>
    private sealed class EarlierOrNow<TShape, TNow>(Func<TShape, TNow?> asParsed) : JsonConverter<TNow>
        where TShape : class
        where TNow : class
    {
        /// <inheritdoc/>
        public override TNow? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.StartObject
                ? ConverterOf<TNow>(options).Read(ref reader, typeof(TNow), options)
                : asParsed(ConverterOf<TShape>(options).Read(ref reader, typeof(TShape), options)!);

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, TNow value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, options);

        /// <summary>
        /// The converter the options read <typeparamref name="T"/> with, its own reader handed on - so a refusal of the
        /// member names where in the file it stands, as one of a record of its own would.
        /// </summary>
        private static JsonConverter<T> ConverterOf<T>(JsonSerializerOptions options) => (JsonConverter<T>)options.GetConverter(typeof(T));
    }
}
