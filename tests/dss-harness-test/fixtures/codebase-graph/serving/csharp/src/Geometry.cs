using Shapes;

namespace Shapes;

/// <summary>Work over many shapes.</summary>
public static class Geometry
{
    /// <summary>The total of the shapes' areas.</summary>
    public static double Total(IEnumerable<IShape> shapes)
    {
        var sum = 0.0;

        foreach (var shape in shapes)
        {
            sum += shape.Area();
        }

        return sum;
    }

    /// <summary>The largest of the shapes' areas.</summary>
    public static double Largest(IEnumerable<IShape> shapes)
    {
        var largest = 0.0;

        foreach (var shape in shapes)
        {
            if (shape.Area() > largest)
            {
                largest = shape.Area();
            }
        }

        return largest;
    }

    /// <summary>How many shapes there are.</summary>
    public static int Count(IEnumerable<IShape> shapes)
    {
        var count = 0;

        foreach (var shape in shapes)
        {
            count++;
        }

        return count;
    }

    /// <summary>The shapes' mean area.</summary>
    public static double Mean(IEnumerable<IShape> shapes)
    {
        var total = Total(shapes);
        var count = Count(shapes);
        return count == 0 ? 0 : total / count;
    }

    /// <summary>The median of the shapes' areas.</summary>
    public static double Median(IEnumerable<IShape> shapes)
    {
        var areas = shapes.Select(shape => shape.Area()).Order().ToList();
        return areas.Count == 0 ? 0 : areas[(areas.Count - 1) / 2];
    }

    /// <summary>Every shape whose area is past a bound.</summary>
    public static IReadOnlyList<IShape> Past(IEnumerable<IShape> shapes, double bound)
    {
        var kept = new List<IShape>();

        foreach (var shape in shapes)
        {
            if (shape.Area() > bound)
            {
                kept.Add(shape);
            }
        }

        return kept;
    }

    /// <summary>Every shape whose area is within a bound.</summary>
    public static IReadOnlyList<IShape> Within(IEnumerable<IShape> shapes, double bound)
    {
        var kept = new List<IShape>();

        foreach (var shape in shapes)
        {
            if (shape.Area() <= bound)
            {
                kept.Add(shape);
            }
        }

        return kept;
    }

    /// <summary>The smallest of the shapes' areas.</summary>
    public static double Smallest(IEnumerable<IShape> shapes)
    {
        var smallest = double.MaxValue;

        foreach (var shape in shapes)
        {
            if (shape.Area() < smallest)
            {
                smallest = shape.Area();
            }
        }

        return smallest == double.MaxValue ? 0 : smallest;
    }

    /// <summary>The span of the shapes' areas, largest to smallest.</summary>
    public static double Span(IEnumerable<IShape> shapes)
    {
        return Largest(shapes) - Smallest(shapes);
    }
}
