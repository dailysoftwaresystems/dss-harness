using Shapes;

namespace Shapes.Tests;

/// <summary>Tests of the geometry.</summary>
public sealed class GeometryTests
{
    /// <summary>The total adds the areas.</summary>
    [Fact]
    public void TotalAddsTheAreas()
    {
        Assert.Equal(12.56636, Geometry.Total([new Circle(1), new Circle(1), new Square(1)]), 5);
    }

    /// <summary>The largest is the greatest area.</summary>
    [Fact]
    public void LargestIsTheGreatestArea()
    {
        Assert.Equal(4, Geometry.Largest([new Square(2), new Circle(1)]), 5);
    }

    /// <summary>The report describes a shape.</summary>
    [Fact]
    public void DescribeNamesTheShape()
    {
        Assert.StartsWith("circle", Report.Describe(new Circle(2)), StringComparison.Ordinal);
    }
}
