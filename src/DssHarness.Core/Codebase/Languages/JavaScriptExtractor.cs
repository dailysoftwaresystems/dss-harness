namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// JavaScript, through the structural scanner C, C++ and Dart are read by, with one reader TypeScript shares: every
/// class, function, variable - a function, an arrow or a class bound to one read as one - method, accessor and field with
/// its lines, signature and summary; ECMAScript's and CommonJS's imports and exports, a module one scope whose own names
/// no importer sees; base lists; each call with what its receiver is where the file says, a JSX element's component
/// among them; and a test file's describe, it and test calls as tests.
/// </summary>
public sealed class JavaScriptExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "javascript";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".js", ".mjs", ".cjs", ".jsx");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "JavaScript (.js, .mjs, .cjs, .jsx), through the structural scanner C, C++ and Dart are read by: every class, function, variable - a function, an arrow or a class bound to one read as one - method, accessor and field with its signature, summary and body; ECMAScript's and CommonJS's imports and exports, each tried as Node tries it, a module one scope whose own names no importer sees; extends; each call with what its receiver is where the file says, a JSX element's component among them; describe, it and test calls in test files",
        JsHelp.Unseen);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new JsReading(Language, input.Path, input.Text, typescript: false).Read();
    }
}

/// <summary>What the reader JavaScript and TypeScript share does not see, as help says it for each.</summary>
internal static class JsHelp
{
    /// <summary>What neither language's reading sees.</summary>
    public static IReadOnlyList<string> Unseen { get; } =
    [
        "types inferred: a call on a value whose type the file does not write or make with new - a local given a call's result, a parameter with no type, a property another module declares - is bound by its name alone",
        "members added at run time - X.prototype.y = ..., Object.assign, a constructor's this.x = ...: not nodes, so no call reaches them",
        "a module a bundler's own configuration aliases (resolve.alias), and an export condition package.json chooses by: the first entry the repository holds",
        "a re-export that renames - export { a as b } from './x', export * as ns from './x': its names are visible as the file it re-exports declares them, and a namespace's not at all, though the file is imported",
        "a require or an import() a function calls, module.exports = { a: require('./a') } and exports.a = require('./a'): the file is imported, and no name of it bound; what a call's result exports - module.exports = factory(), a UMD wrapper's: nothing the reader names",
        "local functions, closures and the functions an object literal holds: not nodes - what they call is the calls of the declaration they are written in - but module.exports = { f() {} }, whose functions are",
        "a call through a function value - a parameter, a local, a variable no function is bound to, a field of a function type, a value's call or apply - and a component a value holds (<Icon /> of const Icon = ...): marked dynamic, and not linked",
        "a test written through a helper of its own, and not a describe, context, suite, it, test or specify call (with .only, .skip, .each and the rest) of a test file - under test/, tests/ or __tests__/, or named *.test.* or *.spec.*: not seen as a test",
        "a statement that relies on a line break ending it where JavaScript would not - a line beginning with ( or [ goes on with the line before, as JavaScript reads it",
    ];
}
