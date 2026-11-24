namespace Shop.Config;

/// <summary>Reads the shop's options from its configuration, by the keys the files declare.</summary>
public static class OptionsReader
{
    /// <summary>The key the payments timeout is read by.</summary>
    public const string TimeoutKey = "TimeoutSeconds";

    /// <summary>The key the free shipping threshold is read by.</summary>
    public const string FreeOverKey = "freeOverCents";

    /// <summary>The section the rules are read from, as the rules file names its root.</summary>
    public const string RulesSection = "rules";

    /// <summary>Reads the payments options from a configuration.</summary>
    /// <param name="configuration">The configuration's values, by their keys as .NET spells them.</param>
    /// <returns>The payments options the configuration names.</returns>
    public static PaymentsOptions Payments(IReadOnlyDictionary<string, string?> configuration)
    {
        var options = new PaymentsOptions();
        options.TimeoutSeconds = Whole(configuration[$"{PaymentsOptions.Section}:{TimeoutKey}"]);
        options.Retries = Whole(configuration["Payments:Retries"]);
        return options;
    }

    /// <summary>The most items an order holds, by the rules file's MaxItems rule.</summary>
    /// <param name="rules">The rules' values, by each rule's name.</param>
    /// <returns>The rule's value.</returns>
    public static int MaxItems(IReadOnlyDictionary<string, string?> rules)
    {
        return Whole(rules["MaxItems"]);
    }

    /// <summary>Whether shipping is free for a cart, by the shipping file's threshold.</summary>
    /// <param name="cents">The cart's total in cents.</param>
    /// <param name="configuration">The configuration's values, by their keys as .NET spells them.</param>
    public static bool ShipsFree(int cents, IReadOnlyDictionary<string, string?> configuration)
    {
        return cents >= Whole(configuration[$"shipping:{FreeOverKey}"]);
    }

    /// <summary>A number as its configuration value writes it, 0 where it writes none.</summary>
    private static int Whole(string? text)
    {
        return text is null ? 0 : int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }
}
