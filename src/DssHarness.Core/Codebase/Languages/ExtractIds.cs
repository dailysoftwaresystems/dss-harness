using System.Globalization;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The ids of one file's extract, each unique, as every implementation tells two alike apart: a declaration that would
/// take an id already given takes it with its place among those of that id after it - <c>~2</c>, <c>~3</c> - the next
/// one no declaration has. An id is a name (<see cref="QualifiedName"/>): the place is joined to it as its own part, so
/// the id it takes and the id it is given are held alike.
/// </summary>
internal sealed class ExtractIds
{
    /// <summary>The last place given each id a declaration would take: 1 for the id itself.</summary>
    private readonly Dictionary<QualifiedName, int> _places = [];
    private readonly HashSet<QualifiedName> _given = [];

    /// <summary><paramref name="id"/>, or it with its place among the ids given so far alike.</summary>
    /// <param name="id">The id the declaration would take.</param>
    public QualifiedName Next(QualifiedName id)
    {
        var place = _places.GetValueOrDefault(id);
        var given = id;

        while (!_given.Add(given))
        {
            place = Math.Max(place, 1) + 1;

            // The place is joined to the id itself, never to a place already joined: 'b' third among its
            // alike is 'b~3' even where a declaration took 'b~2' as its own id, which is the place the
            // loop is stepping past.
            given = id.Under(place.ToString(CultureInfo.InvariantCulture), "~");
        }

        _places[id] = Math.Max(place, 1);
        return given;
    }
}
