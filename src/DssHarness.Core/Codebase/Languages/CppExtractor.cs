namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// C++, through the structural scanner C is read by, with C++'s additions: namespaces, classes and their bases,
/// templates' brackets and raw strings - every namespace, type, member, function, prototype, alias, variable and macro
/// with its lines, signature and summary; <c>#include</c>s and <c>using</c>s as imports; and each call a body makes by
/// its name and arity, with what its receiver is where the file says. A <c>.h</c> file is C++'s where its code holds a
/// mark of it (<see cref="CFamilyReading.HasCppMark"/>) and C's otherwise.
/// </summary>
public sealed class CppExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "cpp";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = new(
        [],
        [
            new(".cc"), new(".cpp"), new(".cxx"), new(".hpp"), new(".hh"), new(".hxx"), new(".ipp"), new(".inl"),
            new(".h", CFamilyReading.HasCppMark),
        ]);

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "C++ (.cc, .cpp, .cxx, .hpp, .hh, .hxx, .ipp, .inl, and .h with a mark of C++), through the structural scanner C is read by: every namespace, class, member, function, prototype, alias, variable and macro with its signature, summary and body; #includes and usings; base lists and overrides; each call with what its receiver is where the file says",
        [
            "macros expanded and templates instantiated: a declaration a macro writes is not seen; a macro standing in the place of one or between its specifiers and the rest of it, a call of what a macro's parameter stands for, and a call of a function-like macro - one the file defines, or one named in capitals as macros are - are marked dynamic, the last still linked to the macro by its name",
            "which branch of an #if is taken: every branch is read, so a function defined under each of two conditions is two nodes of one name, each marked conditional - a function, a type, a namespace or a linkage block whose head is written under each and whose body is one is read by its first head, the heads the other branches write none of its body, and a bracket one branch leaves open and no other does is read as holding nothing, marked dynamic",
            "overloads chosen by their arguments' types: a call is bound by its name and arity, the type of a receiver where the file declares it - exact where the file's anonymous namespace or its static names hold it, at most unique by name alone; argument-dependent lookup",
            "the type of a value auto is given by a call, and a call on it: bound by its name alone",
            "a call through a function pointer the file shows as one - a variable, a parameter, a field of a type the file defines - a std::function, an element or a pointer to a member: marked dynamic, and not linked; one through a field of a type another file defines is linked to the field",
            "which includer's copy a header's static name or anonymous namespace is: a header's, as its macros, are the repository's",
            "a test framework's macros (TEST, TEST_F): what they write is not seen",
            "a header with no mark of C++ is C's: a C++ file's definition of what it declares is a node of its own, which no declares edge reaches from it",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new CFamilyReading(Language, input.Path, input.Text, cpp: true).Read();
    }
}
