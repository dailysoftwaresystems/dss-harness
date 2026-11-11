namespace Shapes;

/// <summary>A thing with an area.</summary>
public interface IShape
{
    /// <summary>The shape's area.</summary>
    double Area();

    /// <summary>The shape's name.</summary>
    string Name();
}

/// <summary>A circle.</summary>
public sealed class Circle(double radius) : IShape
{
    /// <summary>The circle's radius.</summary>
    public double Radius => radius;

    /// <summary>The circle's area.</summary>
    public double Area()
    {
        return 3.14159 * radius * radius;
    }

    /// <summary>The circle's name.</summary>
    public string Name()
    {
        return $"circle of radius {radius}";
    }
}

/// <summary>A square.</summary>
public sealed class Square(double side) : IShape
{
    /// <summary>The square's side.</summary>
    public double Side => side;

    /// <summary>The square's area.</summary>
    public double Area()
    {
        return side * side;
    }

    /// <summary>The square's name.</summary>
    public string Name()
    {
        return $"square of side {side}";
    }
}

/// <summary>A triangle by its three sides.</summary>
public sealed class Triangle(double a, double b, double c) : IShape
{
    /// <summary>The triangle's first side.</summary>
    public double A => a;

    /// <summary>The triangle's second side.</summary>
    public double B => b;

    /// <summary>The triangle's third side.</summary>
    public double C => c;

    /// <summary>The triangle's area, by Heron's formula.</summary>
    public double Area()
    {
        var s = (a + b + c) / 2;
        return Math.Sqrt(s * (s - a) * (s - b) * (s - c));
    }

    /// <summary>The triangle's name.</summary>
    public string Name()
    {
        return $"triangle of sides {a}, {b}, {c}";
    }
}

/// <summary>A regular pentagon.</summary>
public sealed class Pentagon(double side) : IShape
{
    /// <summary>The pentagon's side.</summary>
    public double Side => side;

    /// <summary>The pentagon's area.</summary>
    public double Area()
    {
        return 0.25 * Math.Sqrt(5 * (5 + 2 * Math.Sqrt(5))) * side * side;
    }

    /// <summary>The pentagon's name.</summary>
    public string Name()
    {
        return $"pentagon of side {side}";
    }
}

/// <summary>A regular hexagon.</summary>
public sealed class Hexagon(double side) : IShape
{
    /// <summary>The hexagon's side.</summary>
    public double Side => side;

    /// <summary>The hexagon's area.</summary>
    public double Area()
    {
        return 1.5 * Math.Sqrt(3) * side * side;
    }

    /// <summary>The hexagon's name.</summary>
    public string Name()
    {
        return $"hexagon of side {side}";
    }
}

/// <summary>A rectangle.</summary>
public sealed class Rectangle(double width, double height) : IShape
{
    /// <summary>The rectangle's width.</summary>
    public double Width => width;

    /// <summary>The rectangle's height.</summary>
    public double Height => height;

    /// <summary>The rectangle's area.</summary>
    public double Area()
    {
        return width * height;
    }

    /// <summary>The rectangle's name.</summary>
    public string Name()
    {
        return $"rectangle of {width} by {height}";
    }
}

/// <summary>A ring between two circles.</summary>
public sealed class Ring(double inner, double outer) : IShape
{
    /// <summary>The ring's inner radius.</summary>
    public double Inner => inner;

    /// <summary>The ring's outer radius.</summary>
    public double Outer => outer;

    /// <summary>The ring's area.</summary>
    public double Area()
    {
        return 3.14159 * (outer * outer - inner * inner);
    }

    /// <summary>The ring's name.</summary>
    public string Name()
    {
        return $"ring of {inner} inside {outer}";
    }
}
