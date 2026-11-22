namespace Shop.Config;

/// <summary>What the payments section of the configuration says.</summary>
public sealed class PaymentsOptions
{
    /// <summary>The section the payments options are read from.</summary>
    public const string Section = "Payments";

    /// <summary>How long a payment may take, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>How many times a payment is tried again.</summary>
    public int Retries { get; set; } = 1;

    /// <summary>What a cart must cost for its shipping to be free, in cents.</summary>
    public int FreeOverCents { get; set; } = 5000;
}
