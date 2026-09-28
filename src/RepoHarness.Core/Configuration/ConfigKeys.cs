using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// The keys a section of <c>config.json</c> takes, read from the contract the file is read with.
/// </summary>
/// <remarks>
/// So that what help lists is what the reader accepts: a key is here exactly when the file may hold
/// it, spelled as the file spells it, and required exactly when the file is refused without it. What
/// each does is the <see cref="DescriptionAttribute"/> on the member that reads it, beside that member.
/// </remarks>
public static class ConfigKeys
{
    /// <summary>Every key a <typeparamref name="TSection"/> takes, each nested section's with its own.</summary>
    /// <typeparam name="TSection">The type a section of the file is read into.</typeparam>
    public static IReadOnlyList<KeyDescription> Of<TSection>() => Of(typeof(TSection));

    private static IReadOnlyList<KeyDescription> Of(Type section)
        =>
        [
            .. JsonConfigOptions.Default.GetTypeInfo(section).Properties
                .Where(property => !Ignored(property))
                .Select(property => new KeyDescription(property.Name, MeaningOf(property))
                {
                    Required = property.IsRequired,
                    Keys = SectionIn(property.PropertyType) is { } nested ? Of(nested) : [],
                }),
        ];

    /// <summary>
    /// Whether the file never reads <paramref name="property"/>: the contract lists such a member
    /// too, and it is no key of the file's.
    /// </summary>
    private static bool Ignored(JsonPropertyInfo property)
        => property.AttributeProvider?
            .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .OfType<JsonIgnoreAttribute>()
            .Any(attribute => attribute.Condition == JsonIgnoreCondition.Always) == true;

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
        => type == typeof(string)
            ? null
            : type.IsGenericType
                ? SectionIn(type.GetGenericArguments()[^1])
                : type.IsClass && type.Namespace == typeof(ConfigKeys).Namespace ? type : null;
}
