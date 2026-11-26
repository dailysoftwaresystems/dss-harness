namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// PHP, through the structural scanner C, C++, Dart and JavaScript are read by, with PHP's lexical rules and declarations:
/// its code between its open and close tags; every namespace, class, interface, trait, enum, function, method,
/// constructor and promoted property, property, constant - a const's or a define's - and enum case with its lines, signature
/// and summary; its <c>use</c> declarations, every name written out to the one it stands for, and the files it includes; base
/// lists and a trait's use; each call a body makes by its name and arity, with what its receiver is where the file says, and
/// each constant it reads - a function's and a constant's name written alone looked for in its namespace and then the global
/// one, never among its class's members; a call reaches a function or a method alone, a read a constant, a case or a property
/// alone, a <c>new</c> a class alone (<see cref="FileExtract.KindsApart"/>), and every class name is found as the whole name
/// it is written out to (<see cref="FileExtract.WrittenWhole"/>); and PHPUnit's test methods and Pest's test calls as tests.
/// </summary>
public sealed class PhpExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "php";

    /// <inheritdoc/>
    public int Version => 5;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".php", ".phtml");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "PHP (.php, .phtml), through the structural scanner C, C++, Dart and JavaScript are read by: its code between <?php or <?= and ?>; every namespace, class, interface, trait, enum, function, method, constructor and promoted property, property, constant - a const's, and one a define names by a text - and enum case with its signature, summary and body, a namespace App\\Models the scope App.Models; use declarations, each name a reference writes written out to the one it stands for, and the files require and include name; extends, implements and a trait's use; each call with what its receiver is where the file says, and each constant the code reads, a function's or a constant's name written alone looked for in its namespace and then the global one - a use function's or a use const's where one names it - and never among the members of the class the code is in, which $this->, self::, static:: and parent:: alone reach; a call reaching a function or a method alone, a constant read a constant, an enum case or a property alone, and a new a class alone - PHP's functions, constants and classes never one another's; every class name a reference or a type writes found as the whole name it is written out to; each member a member reads or writes without calling it, and each string literal the file holds - a heredoc's and a nowdoc's too, their closing indentation taken off, an interpolated one's constant parts each one of its own - by its value as PHP reads its escapes, its line and the node it is written in; PHPUnit's test methods, and Pest's test, it and describe calls in files under tests/ or test/",
        [
            new CodeGraphLimit("inferred-types", "types inferred: a call on a value whose type the file does not write - a local given a call's result, a property a docblock alone types, a method's result - is bound by its name alone") { Signs = [CodeGraphLimit.Candidates] },
            ("string-classes", "a class or a constant a string names - to the autoloader, a container, class_exists, constant, defined - a callable written as a string or an array, and a member only a magic method answers (__call, __callStatic, __get): not seen as a reference; a define of a name the code computes declares no constant"),
            ("name-case", "PHP's names of classes, functions and methods alike whatever their case: a name written in another case than its declaration is not linked, but one a use declaration names"),
            new CodeGraphLimit("variable-calls", "a call of a method or a function the code names by a variable ($obj->$name(), $f()), of a value it computes ($handlers['k'](), $obj->{$name}(), make()()), a new or a static call of a class the code computes, call_user_func and eval: marked dynamic, and not linked") { Signs = [PhpReading.VariableCall] },
            new CodeGraphLimit("computed-includes", "an inclusion whose path the code computes, past __DIR__ or dirname(__DIR__) joined to a text, and one whose dirname climbs above the tree's root: marked dynamic; one of a text is the path from the file's directory, though PHP looks along its include_path first") { Signs = [PhpReading.UnfollowedInclude] },
            ("closures", "closures, arrow functions and anonymous classes are not nodes: what they call and read is the calls and uses of the declaration they are written in, and the classes an anonymous class extends, implements and uses and the types its properties and constants write its references, a property's hooks declared names whose calls and reads are the declaration's own - as are those of a class a function's body declares"),
            ("test-classes", "a test class is one that extends a TestCase, or is named for a test and extends a class: its public methods named test..., and its methods - or a trait's - marked #[Test] or @test, are tests; a test written through a helper of its own is not seen as a test"),
            ("template-engines", "code a template engine compiles (Blade, Twig): read as PHP's text and code are; and <? before white space opens code, whether or not PHP's short_open_tag is on"),
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new PhpReading(Language, input.Path, input.Text).Read();
    }
}
