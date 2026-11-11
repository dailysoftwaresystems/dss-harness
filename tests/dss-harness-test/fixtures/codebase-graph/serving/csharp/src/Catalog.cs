using System.Collections.Concurrent;
using Shapes;

namespace Shapes.Catalog;

/// <summary>A catalog of named items - work the shapes' report does not reach.</summary>
public static class Catalog
{
    private static readonly ConcurrentDictionary<string, string> Items = new(StringComparer.Ordinal);

    /// <summary>Adds an item to the catalog.</summary>
    public static void Add(string key, string value)
    {
        Items[key] = value;
    }

    /// <summary>Finds an item of the catalog.</summary>
    public static string? Find(string key)
    {
        return Items.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>The catalog's size.</summary>
    public static int Size()
    {
        return Items.Count;
    }

    /// <summary>Clears the catalog.</summary>
    public static void Clear()
    {
        Items.Clear();
    }
}
