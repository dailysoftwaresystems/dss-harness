namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// PHP, through the structural scanner C, C++, Dart and JavaScript are read by, with PHP's lexical rules and declarations:
/// its code between its open and close tags; every namespace, class, interface, trait, enum, function, method,
/// constructor and promoted property, property, constant and enum case with its lines, signature and summary; its
/// <c>use</c> declarations, every name written out to the one it stands for, and the files it includes; base lists and
/// a trait's use; each call a body makes by its name and arity, with what its receiver is where the file says; and
/// PHPUnit's test methods and Pest's test calls as tests.
/// </summary>
public sealed class PhpExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "php";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".php", ".phtml");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "PHP (.php, .phtml), through the structural scanner C, C++, Dart and JavaScript are read by: its code between <?php or <?= and ?>; every namespace, class, interface, trait, enum, function, method, constructor and promoted property, property, constant and enum case with its signature, summary and body, a namespace App\\Models the scope App.Models; use declarations, each name a reference writes written out to the one it stands for, and the files require and include name; extends, implements and a trait's use; each call with what its receiver is where the file says, a function's looked for in its namespace and then the global one; PHPUnit's test methods, and Pest's test, it and describe calls in files under tests/ or test/",
        [
            "types inferred: a call on a value whose type the file does not write - a local given a call's result, a property a docblock alone types, a method's result - is bound by its name alone",
            "a class a string names - to the autoloader, a container, class_exists - a callable written as a string or an array, and a member only a magic method answers (__call, __callStatic, __get): not seen as a reference",
            "PHP's names of classes, functions and methods alike whatever their case: a name written in another case than its declaration is not linked, but one a use declaration names",
            "a constant, an enum's case and a static property a class's name reaches (Status::Active, User::ROLE, Repo::$cache), and a property a value reaches: no reference",
            "a call of a method or a function the code names by a variable ($obj->$name(), $f()), of a value it computes ($handlers['k'](), $obj->{$name}(), make()()), a new or a static call of a class the code computes, call_user_func and eval: marked dynamic, and not linked",
            "an inclusion whose path the code computes, past __DIR__ or dirname(__DIR__) joined to a text: marked dynamic; one of a text is the path from the file's directory, though PHP looks along its include_path first",
            "closures, arrow functions and anonymous classes are not nodes: what they call is the calls of the declaration they are written in",
            "a test class is one that extends a TestCase, or is named for a test and extends a class: its public methods named test..., and its methods - or a trait's - marked #[Test] or @test, are tests; a test written through a helper of its own is not seen as a test",
            "code a template engine compiles (Blade, Twig): read as PHP's text and code are; and <? before white space opens code, whether or not PHP's short_open_tag is on",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new PhpReading(Language, input.Path, input.Text).Read();
    }
}
