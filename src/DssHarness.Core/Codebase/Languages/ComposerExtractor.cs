namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A Composer package's <c>composer.json</c>: one <c>project</c> node over the whole file, named by the package's
/// <c>name</c> - its header the line that names it, its summary the first sentence of its <c>description</c> - with a
/// <c>projectReference</c> to each package it requires, by that package's name, which links where the repository holds a
/// package of that name - a <c>path</c> repository's - and a name with no <c>/</c>, PHP's platform's (<c>php</c>,
/// <c>ext-*</c>, <c>lib-*</c>, <c>composer-plugin-api</c>), none.
/// PHP names a class by its namespace, which the linker finds by scope, so the file declares no address root. Where the
/// file is no JSON object, the line it stops at is marked dynamic.
/// </summary>
public sealed class ComposerExtractor : ICodeGraphExtractor
{
    /// <summary>The sections that name a package's dependencies.</summary>
    private static readonly string[] Sections = ["require", "require-dev"];

    /// <inheritdoc/>
    public string Language => "composer";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = new([new("composer.json")], []);

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "Composer packages (composer.json): a project node for each, named by its name, its summary from its description, and a projectReference to each package it requires that the repository holds - a path repository's",
        [
            "a package from Packagist, from git or from an archive: no project of the repository, unless one of the repository's packages has its name",
            "the autoload's psr-4, classmap and files: a class is found by its namespace, whichever directory holds it, and the files are no imports",
            "what repositories, replace, provide, conflict and suggest name: not read as references",
            JsonOutline.Unread,
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // vendor/package; php, ext-json, lib-curl and composer-plugin-api are the platform's, no package's.
        return PackageManifest.Read(Language, input, Sections, name => name.Contains('/', StringComparison.Ordinal));
    }
}
