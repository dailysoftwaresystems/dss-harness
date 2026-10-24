namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// C, through the structural scanner: every function, prototype, <c>struct</c>, <c>union</c>, <c>enum</c>, <c>typedef</c>,
/// variable and macro with its lines, signature and summary; <c>#include</c>s as imports; and each call a body makes by
/// its name and arity. A <c>.h</c> file is C's where its code holds no mark of C++ - C++'s content test says the
/// opposite of this one, so exactly one says yes.
/// </summary>
public sealed class CExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "c";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = new([], [new(".c"), new(".h", probe => !CFamilyReading.HasCppMark(probe))]);

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "C (.c, and .h with no mark of C++), through a structural scanner of the tool's own: every function, prototype, struct, union, enum, typedef, variable and macro with its signature, summary and body; #includes; each call by its name and arity",
        [
            "macros expanded: a declaration a macro writes is not seen; a macro standing in the place of one or between its specifiers and the rest of it, a call of what a macro's parameter stands for, and a call of a function-like macro - one the file defines, or one named in capitals as macros are - are marked dynamic, the last still linked to the macro by its name",
            "which branch of an #if is taken: every branch is read, so a function defined under each of two conditions is two nodes of one name, each marked conditional - one whose head is written under each and whose body is one is read by its first head, and a bracket one branch leaves open and no other does is read as holding nothing, marked dynamic",
            "a call through a member, which C makes through a function pointer, through an element, and through a function pointer the file shows as one: marked dynamic, and not linked - one whose pointer type a typedef names is linked by its name",
            "which file an #include names where several of the tree end so: beside the including file first, else each of them, as candidates",
            "types: a call is bound by its name and arity - exact where its own file declares the name static, at most unique otherwise",
            "which includer's copy a header's static name is: a header's static names, as its macros, are the repository's, so two headers' of one name are one",
            "a function defined in the old style, its parameters declared after its list: not read as one",
            "a header with no mark of C++ is C's: a C++ file's definition of what it declares is a node of its own, which no declares edge reaches from it",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new CFamilyReading(Language, input.Path, input.Text, cpp: false).Read();
    }
}
