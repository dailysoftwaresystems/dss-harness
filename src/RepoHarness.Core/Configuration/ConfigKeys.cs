using System.ComponentModel;
using System.Text.Json.Serialization.Metadata;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// The keys a section of <c>config.json</c> takes, read from the contract the file is read with.
/// </summary>
/// <remarks>
/// So that what help lists is what the reader accepts: a key is here exactly when the file may hold
/// it, spelled as the file spells it, and required exactly when the file must name it. What each does
/// is the <see cref="DescriptionAttribute"/> on the member that reads it, beside that member.
/// </remarks>
public static class ConfigKeys
{
    /// <summary>Every key a <typeparamref name="TSection"/> takes, each nested section's with its own.</summary>
    /// <typeparam name="TSection">The type a section of the file is read into.</typeparam>
    public static IReadOnlyList<KeyDescription> Of<TSection>() => Of(typeof(TSection));

    private static IReadOnlyList<KeyDescription> Of(Type section)
        =>
        [
            .. JsonConfigOptions.Default.GetTypeInfo(section).Properties.Select(property => new KeyDescription(property.Name, MeaningOf(property))
            {
                Required = property.IsRequired,
                Keys = SectionIn(property.PropertyType) is { } nested ? Of(nested) : [],
            }),
        ];

    private static string MeaningOf(JsonPropertyInfo property)
        => property.AttributeProvider?
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .Select(attribute => attribute.Description)
            .FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// The section a value of <paramref name="type"/> holds: the type itself when it is one of this
    /// file's own, or what a list or a map of named entries holds; <see langword="null"/> for a value
    /// with no keys of its own, such as text, a number or an environment.
    /// </summary>
    private static Type? SectionIn(Type type)
    {
        if (type.IsGenericType)
        {
            return SectionIn(type.GetGenericArguments()[^1]);
        }

        return type.IsClass && type.Namespace == typeof(ConfigKeys).Namespace ? type : null;
    }
}
