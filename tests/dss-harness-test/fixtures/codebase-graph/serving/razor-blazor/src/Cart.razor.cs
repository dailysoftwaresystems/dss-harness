namespace Shop.Web.Data;

/// <summary>The cart component's code-behind: what its markup and its code block share.</summary>
public partial class Cart
{
    /// <summary>The most lines a cart holds.</summary>
    public const int MaxLines = 50;

    private bool HasRoom => Carts.Lines.Count < MaxLines;
}
