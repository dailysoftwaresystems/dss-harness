using System.Text;
using Shapes;

namespace Shapes;

/// <summary>Report shapes for a person.</summary>
public static class Report
{
    /// <summary>One line that describes the shape.</summary>
    public static string Describe(IShape shape)
    {
        return $"{shape.Name()} has area {shape.Area():0.00}";
    }

    /// <summary>A table of the shapes' areas.</summary>
    public static string Table(IEnumerable<IShape> shapes)
    {
        var builder = new StringBuilder();

        foreach (var shape in shapes)
        {
            builder.AppendLine(Describe(shape));
        }

        return builder.ToString();
    }

    /// <summary>The report's heading.</summary>
    public static string Heading(string title)
    {
        return $"# {title}";
    }

    /// <summary>The shapes as one line each, comma apart.</summary>
    public static string List(IEnumerable<IShape> shapes)
    {
        return string.Join(", ", shapes.Select(Describe));
    }

    /// <summary>The shapes as a row of a comma-separated table.</summary>
    public static string Csv(IEnumerable<IShape> shapes)
    {
        var builder = new StringBuilder("name,area");

        foreach (var shape in shapes)
        {
            builder.AppendLine();
            builder.Append($"{shape.Name()},{shape.Area():0.00}");
        }

        return builder.ToString();
    }

    /// <summary>The shapes as a markdown table.</summary>
    public static string Markdown(IEnumerable<IShape> shapes)
    {
        var builder = new StringBuilder("| name | area |");

        foreach (var shape in shapes)
        {
            builder.AppendLine();
            builder.Append($"| {shape.Name()} | {shape.Area():0.00} |");
        }

        return builder.ToString();
    }

    /// <summary>The report's footing.</summary>
    public static string Footing(int count, double total)
    {
        return $"{count} shapes, total area {total:0.00}";
    }

    /// <summary>The report's whole page: heading, table, footing.</summary>
    public static string Page(string title, IEnumerable<IShape> shapes)
    {
        return $"{Heading(title)}\n\n{Table(shapes)}\n\n{Footing(Geometry.Count(shapes), Geometry.Total(shapes))}\n";
    }
}
