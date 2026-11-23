using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>A Python project's pyproject.toml and setup.cfg, held to the contract's kit, and the rules of its own the kit does not name.</summary>
public sealed class PyprojectExtractorTests : CodeGraphExtractorContractTests<PyprojectExtractor>
{
    private const string Setuptools = """"
        [project]
        name = "shop"
        description = """
        A shop's
        cart."""
        dependencies = [
          "requests>=2",  # what it needs
        ]

        [tool.setuptools.package-dir]
        "" = "src"

        [tool.setuptools.packages.find]
        where = ["src", "./lib/"]

        [tool.pytest.ini_options]
        pythonpath = "tests/helpers"
        """";

    private const string Tools = """
        [tool.poetry]
        name = "app"
        packages = [
            { include = "app", from = "src" },
            { include = "extra" },
        ]

        [tool.hatch.build.targets.wheel]
        packages = ["lib/app", "flat"]

        [tool.pdm.build]
        package-dir = 'pdm_src'
        """;

    private const string Cfg = """
        [metadata]
        name = shop-cfg

        [options]
        package_dir =
            = src
        packages = find:

        [options.packages.find]
        where = src

        [tool:pytest]
        pythonpath = src tests
        """;

    /// <inheritdoc/>
    public override IReadOnlyList<CodeGraphFixture> Fixtures { get; } =
    [
        new("pyproject.toml", Setuptools) { Nodes = [new("pyproject:pyproject.toml", CodeNodeKinds.Project, (1, 17))] },
        new("pkg/pyproject.toml", Tools) { Nodes = [new("pyproject:pkg/pyproject.toml", CodeNodeKinds.Project, (1, 12))] },
        new("setup.cfg", Cfg) { Nodes = [new("pyproject:setup.cfg", CodeNodeKinds.Project, (1, 13))] },
    ];

    /// <summary>
    /// A project is named by its name - <c>[project]</c>'s, <c>[tool.poetry]</c>'s, <c>[metadata]</c>'s - its header the line that names
    /// it, and declares each root of the layout its words name, each once and in the order the file writes them: setuptools'
    /// <c>package-dir</c> root and <c>packages.find</c>'s <c>where</c>, pytest's <c>pythonpath</c>, Poetry's <c>packages</c>' <c>from</c>,
    /// the directory of each of Hatch's wheel <c>packages</c> - one that stands in the file's own directory none - and PDM's
    /// <c>package-dir</c>; a multi-line string, an array over lines and a comment in it read as TOML reads them, and a setup.cfg's
    /// option continued on the lines indented under it.
    /// </summary>
    [Fact]
    public void AProject_IsNamedByItsName_AndDeclaresEachRootItsWordsName()
    {
        var setuptools = Read("pyproject.toml", Setuptools);
        var node = Assert.Single(setuptools.Nodes);

        Assert.Equal(("shop", "name = \"shop\""), (node.Name, node.Signature));
        Assert.Equal(["src", "lib", "tests/helpers"], setuptools.LayoutRoots!);
        Assert.Empty(setuptools.Dynamic);

        var tools = Read("pkg/pyproject.toml", Tools);

        Assert.Equal("app", Assert.Single(tools.Nodes).Name);
        Assert.Equal(["src", "lib", "pdm_src"], tools.LayoutRoots!);

        var cfg = Read("setup.cfg", Cfg);

        Assert.Equal(("shop-cfg", "name = shop-cfg"), (Assert.Single(cfg.Nodes).Name, cfg.Nodes[0].Signature));
        Assert.Equal(["src", "tests"], cfg.LayoutRoots!);
    }

    /// <summary>
    /// A value no string writes, a directory <c>package-dir</c> maps one package to and one a setup.cfg's interpolation computes name no
    /// root, and a file that names none declares none, named by its file's name where it names no project; a value whose bracket never
    /// closes is read to the file's end, the line it begins on marked; and arrays nested however deep are read on the reading's own stack.
    /// </summary>
    [Fact]
    public void AValueNoStringWrites_OrOneTheFileComputes_NamesNoRoot_AndOneThatNeverCloses_IsMarked()
    {
        var none = Read("pyproject.toml", "[tool.setuptools.package-dir]\npkg = \"lib/pkg\"\n\n[tool.pytest.ini_options]\npythonpath = 3\n");

        Assert.Null(none.LayoutRoots);
        Assert.Equal("pyproject.toml", Assert.Single(none.Nodes).Name);
        Assert.Null(Read("setup.cfg", "[options]\npackage_dir =\n    = %(base)s/src\n").LayoutRoots);

        var open = Read("pyproject.toml", "[tool.setuptools.packages.find]\nwhere = [\"src\",\n");

        Assert.Equal(["src"], open.LayoutRoots!);
        Assert.Equal([2], open.Dynamic.Select(mark => mark.Line));

        const int Depth = 5_000;
        var deep = Read("pyproject.toml", "[tool.setuptools.packages.find]\nwhere = " + new string('[', Depth) + "\"src\"" + new string(']', Depth) + "\n");

        Assert.Equal(["src"], deep.LayoutRoots!);
        Assert.Empty(deep.Dynamic);
    }

    /// <summary>
    /// A string's value is TOML's - a multi-line one's too, its line break right after its opener none of it, a quote before its closer
    /// its own, a backslash that ends a line's text taking the blanks and the line break after it, and an escape of two hexadecimal
    /// digits a character - and a one-line string that never closes on its line is read to its line's end, the line marked, the file
    /// going on past it; a setup.cfg's option is continued by a line indented deeper than the one that set it alone, and a section's
    /// name is the text from its <c>[</c> to its last <c>]</c>, as configparser reads them.
    /// </summary>
    [Fact]
    public void AStringsValue_IsTomls_AndASetupCfgsOptions_AreConfigparsers()
    {
        var toml = Read("pyproject.toml", "[project]\nname = \"\"\"shop\"\"\"\"\n\n[tool.pytest.ini_options]\npythonpath = [\"\"\"\nsrc\"\"\", '''lib''',\"\"\"te\\\n    sts\"\"\", \"\\x61pp\"]\n");

        Assert.Equal("shop\"", Assert.Single(toml.Nodes).Name);
        Assert.Equal(["src", "lib", "tests", "app"], toml.LayoutRoots!);
        Assert.Empty(toml.Dynamic);

        var open = Read("pyproject.toml", "[tool.pytest.ini_options]\npythonpath = \"src\n\n[project]\nname = \"shop\"\n");

        Assert.Null(open.LayoutRoots);
        Assert.Equal("shop", Assert.Single(open.Nodes).Name);
        Assert.Equal([(2, "a string that never closes on its line: the rest of the line is read as it")], open.Dynamic.Select(mark => (mark.Line, mark.Why)));

        var cfg = Read("setup.cfg", "[options.packages.find]\n  where = src\n  exclude = tests\n[tool:pytest]  ; pytest's\npythonpath = lib\n");

        Assert.Equal(["src", "lib"], cfg.LayoutRoots!);
    }

    private static FileExtract Read(string path, string text) => new PyprojectExtractor().Extract(new CodeFileInput(path, text));
}
