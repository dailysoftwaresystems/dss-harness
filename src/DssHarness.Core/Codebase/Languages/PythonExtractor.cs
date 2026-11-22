namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// Python, through the structural scanner C, C++ and Dart are read by, with Python's lexical rules and declarations: its
/// module a namespace named by its dotted path; every class, function, method, constructor and assigned name with its lines,
/// signature and summary; its imports, absolute ones bound to files by the package layout; base lists; each call with what
/// its receiver is where the file says; each type an annotation names; and a test file's tests and fixtures.
/// </summary>
public sealed class PythonExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "python";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".py", ".pyi", ".pyw");

    /// <inheritdoc/>
    public bool Outlines => false;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Python (.py, .pyi and .pyw), through the structural scanner C, C++ and Dart are read by: the module a namespace named by its path in dots, an __init__.py its directory's package, every name it declares reachable by an importer, and its own imported names with them, as a package re-exports what it imports; every class, function, method, constructor and assigned name - a module-level or class-level name in capitals a constant, any other a field - with its signature, summary and body, nested classes and functions kept inside the node that holds them; imports - import a.b.c a module looked for by the package layout, from the directory above the file's package and then each directory above it to the tree's root, as a package's __init__.py or the module's .py; from a.b import c, d as e showing each name and binding it, the names the file's importers's too; a relative one a path beside the file; from m import * the names m's __all__ names where it is a list of string literals and else the names not written with a leading underscore; base lists, the first base inherits and the others mixesIn, and a method that has the name of one its bases declare an overrides edge; each call with what its receiver is where the file says - self and cls the type's own, super() its base's, a module's name an import's prefix - and a call of a capitalized name an instantiates of the class it names; each type an annotation, a return annotation, an isinstance, an issubclass or an except names; each attribute and each name a body reads without calling it and does not bind itself; each decorator's expression and its arguments, the language's own decorators kept as modifiers by their dotted name; each string literal the file holds - a triple-quoted one's and a raw one's too, an f-string's constant parts each one of its own, its fields read as code, an __all__'s entries among them - by its value as Python reads its escapes, its line and the node it is written in; and a test file's test* functions, Test* classes, TestCase subclasses and their test* methods as tests, each parameter a test requires a reference to the fixture that provides it, a function pytest's @fixture decorates",
        [
            // What a file shows of a limit is said by sites of that file: each sign a mark's kind (<see cref="PythonReading"/>) or candidates.
            new CodeGraphLimit("computed attribute access: getattr, setattr, delattr and hasattr reach a member by a name the code computes - marked dynamic, no edge") { Signs = [PythonReading.ComputedAttribute] },
            new CodeGraphLimit("dynamic imports: importlib.import_module and __import__ name a module by a computed name - marked dynamic; and which roots a project's path names is not known: a bare import is looked for from the directory above the file's package, then from each directory above it to the tree's root, and a path the project installs from outside the tree, or one a PYTHONPATH names, is not among them") { Signs = [PythonReading.DynamicImport] },
            "monkey-patching and decorators that rewrite what they wrap, metaclass effects, and code generated at build time: not read",
            new CodeGraphLimit("exec and eval: code a string holds - marked dynamic, and not linked") { Signs = [PythonReading.ComputedCode] },
            new CodeGraphLimit("a duck-typed receiver: a call on a parameter, a local or a value the file does not type is bound by its name alone, at candidates where several declarations share it - a member of an annotated parameter's type to each override of it; a call through a value a body binds - a parameter, a local given a function - is marked dynamic and not linked") { Signs = [CodeGraphLimit.Candidates, PythonReading.FunctionValue] },
            "a function written with a capital first letter: a call of its name is read as making a class of that name, never as calling the function",
            new CodeGraphLimit("a module's __all__ the code computes or changes after assigning it a list - __all__ = [...] + more, __all__.append - which is not read, the file marked dynamic, so from m import * binds the names not written with a leading underscore") { Signs = [PythonReading.ComputedExports] },
            "names a module gains by a * import of its own, which are bound in it and are not reached through it by its importers; a name it imports under another name is reached through it by that name alone",
            "a name a file neither declares nor imports: it binds nothing, the repository's declarations not being looked through for it, so a class named in a base list without being imported makes no edge",
            "lambdas, comprehensions and conditional expressions as nodes of their own: what they call is the calls of the declaration they are written in",
            "a test written through a helper of its own, and not a test* function of a test file or a test* method of a Test* class or a TestCase subclass, as a test",
            "an annotation written as a call - Union[A, B], Optional[A] - beyond the capitalized names it writes, the call's own among them; one the code builds at run time names nothing",
            "pyproject.toml, requirements.txt, setup.cfg and a lock file: listed only, for no manifest of Python is read - a project's dependencies, its build backend and its tools' configuration are not in the graph",        ])
    {
        Holds = "its tokens, read by position and never one string of the file: a name, a number, a punctuator and a string each by where it begins and ends, a string's value decoded from its pieces, an f-string's fields scanned as texts of their own where they stand",
    };

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new PythonReading(Language, input.Path, input.Text).Read();
    }
}
