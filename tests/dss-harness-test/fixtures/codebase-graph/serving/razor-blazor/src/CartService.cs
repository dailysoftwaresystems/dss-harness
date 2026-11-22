namespace Shop.Web.Data;

/// <summary>The lines bought, kept and totalled.</summary>
public sealed class CartService
{
    /// <summary>The lines bought.</summary>
    public List<Line> Lines { get; } = [];

    /// <summary>Adds a line to the cart.</summary>
    /// <param name="line">The line to add.</param>
    public void Add(Line line) => Lines.Add(line);

    /// <summary>The cart's total in cents.</summary>
    /// <returns>The sum of every line's count times its price.</returns>
    public int Total()
    {
        var total = 0;
        foreach (var line in Lines)
        {
            total += line.Count * line.Cents;
        }
        return total;
    }
}

/// <summary>A line of a cart: something bought, so many of it, at a price in cents.</summary>
/// <param name="Name">What is bought.</param>
/// <param name="Count">How many of it.</param>
/// <param name="Cents">The price of one, in cents.</param>
public sealed record Line(string Name, int Count, int Cents);
