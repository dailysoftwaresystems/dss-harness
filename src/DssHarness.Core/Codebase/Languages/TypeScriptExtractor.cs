namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// TypeScript, through the reader JavaScript is read by, with TypeScript's types: every class, interface, enum, type
/// alias, namespace, function, variable, constructor and its parameter properties, method, accessor and field with its
/// lines, signature and summary - a declaration file's each a prototype; imports and exports, a module one scope whose
/// own names no importer sees; extends, implements and override; each call with what its receiver is where the file says,
/// a value typed any marked dynamic; and a test file's describe, it and test calls as tests.
/// </summary>
public sealed class TypeScriptExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "typescript";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".ts", ".mts", ".cts", ".tsx");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "TypeScript (.ts, .mts, .cts, .tsx), through the reader JavaScript is read by, with its types: every class, interface, enum, type alias, namespace, function, variable, constructor and its parameter properties, method, accessor and field with its signature, summary and body - a declaration file's (.d.ts) each a prototype; imports and exports, each tried as TypeScript tries it, a module one scope whose own names no importer sees; extends, implements and override; each call with what its receiver is where the file says, a value typed any marked dynamic; describe, it and test calls in test files",
        [
            .. JsHelp.Unseen,
            "an overload chosen by its argument types: a call binds each overload of its name that takes its arguments' count",
            "a type TypeScript infers, or one a union, an intersection or a generic's argument says: a call on such a value is bound by its name alone",
            "a declaration a decorator or a code generator adds, and a module or a global another file augments (declare module, declare global): read where the augmenting file declares it, as its own",
            "a call whose type arguments hold a conditional type (f<T extends U ? A : B>()): not seen as a call",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new JsReading(Language, input.Path, input.Text, typescript: true).Read();
    }
}
