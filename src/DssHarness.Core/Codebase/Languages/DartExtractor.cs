namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// Dart, through the structural scanner C and C++ are read by, with Dart's lexical rules and declarations: every class,
/// mixin, extension, extension type, enum, typedef, function, getter, setter, operator, field, constructor and enum
/// value with its lines, signature and summary; its imports, exports and parts, a library and its parts one scope; base
/// lists and <c>@override</c>; each call a body makes by its name and arity, with what its receiver is where the file
/// says; and a test file's <c>test</c>, <c>testWidgets</c> and <c>group</c> calls as tests.
/// </summary>
public sealed class DartExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "dart";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".dart");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Dart (.dart), through the structural scanner C and C++ are read by: every class, mixin, extension, extension type, enum, typedef, function, getter, setter, operator, field and constructor with its signature, summary and body; imports, exports and parts, a library and its parts one scope whose private names no importer sees; base lists and @override; each call with what its receiver is where the file says; test, testWidgets and group calls in test files",
        [
            "types inferred: a call on a value whose type the file does not write - a local given a call's result, a closure's parameter, a getter of a type another library declares - is bound by its name alone; one on a value declared dynamic is marked dynamic",
            "an extension member chosen by the type of its receiver where the file does not write that type: bound by its name alone; one reached only through a deferred import is taken as applying",
            "a call through a function value - a parameter, a local or a field of a function type, a value's call, Function.apply: marked dynamic, and not linked",
            "which URI a configurable import (if (dart.library.io) ...) takes: the first of them the repository holds",
            "a part that names its library by the library's name (part of a.b;) shares the library's names and not its imports; one that names it by a package: URI is taken to be under the lib/ directory its own path is under",
            "local functions and closures are not nodes: what they call is the calls of the declaration they are written in",
            "a test written through a helper of its own, and not a test, testWidgets or group call of a test file (under test/, or ending _test.dart), is not seen as a test",
            "augmentations (augment ...), passed over with their bodies, and code a builder writes into a file the repository does not hold: not read",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new DartReading(Language, input.Path, input.Text).Read();
    }
}
