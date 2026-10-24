using System.Globalization;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The ids of one file's extract, each unique, as every implementation tells two alike apart: a declaration that would
/// take an id already given takes it with its place among those of that id after it - <c>~2</c>, <c>~3</c> - the next
/// one no declaration has.
/// </summary>
internal sealed class ExtractIds
{
    /// <summary>The last place given each id a declaration would take: 1 for the id itself.</summary>
    private readonly Dictionary<string, int> _places = new(StringComparer.Ordinal);
    private readonly HashSet<string> _given = new(StringComparer.Ordinal);

    /// <summary><paramref name="id"/>, or it with its place among the ids given so far alike.</summary>
    /// <param name="id">The id the declaration would take.</param>
    public string Next(string id)
    {
        var place = _places.GetValueOrDefault(id);
        var given = id;

        while (!_given.Add(given))
        {
            place = Math.Max(place, 1) + 1;
            given = $"{id}~{place.ToString(CultureInfo.InvariantCulture)}";
        }

        _places[id] = Math.Max(place, 1);
        return given;
    }
}
