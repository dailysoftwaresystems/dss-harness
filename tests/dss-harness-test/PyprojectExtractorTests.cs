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

    private static FileExtract Read(string path, string text) => new PyprojectExtractor().Extract(new CodeFileInput(path, text));
}
