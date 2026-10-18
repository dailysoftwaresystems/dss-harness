using System.ComponentModel;
using System.Text.Json.Serialization.Metadata;

namespace DssHarness.Core.Configuration;

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

    private static IReadOnlyList<KeyDescription> Of(Type section) => Of(section, [section]);

    private static IReadOnlyList<KeyDescription> Of(Type section, IReadOnlyList<Type> walked)
    {
        var keys = new List<KeyDescription>();

        foreach (var property in JsonConfigOptions.Default.GetTypeInfo(section).Properties)
        {
            var nested = SectionIn(property.PropertyType);

            // A section that holds its own kind again - a step holding steps - would list its keys inside
            // its own keys without end: the guard cuts the walk there and says what the key takes, so the
            // listing stays finite and says where the rest is.
            if (nested is not null && walked.Contains(nested))
            {
                keys.Add(new KeyDescription(property.Name, $"takes a {SingularOf(nested)}'s keys")
                {
                    Required = property.IsRequired,
                });
                continue;
            }

            keys.Add(new KeyDescription(property.Name, MeaningOf(property))
            {
                Required = property.IsRequired,
                Keys = nested is { } inner ? Of(inner, [.. walked, inner]) : [],
            });
        }

        return keys;
    }

    /// <summary>The one word for what a section's kind is called, from its type's name: a <c>WorkflowStep</c> is a step.</summary>
    private static string SingularOf(Type section)
    {
        var name = section.Name;

        // The harness's model prefixes name the file the type is read from, not the thing it is.
        var bare = name.StartsWith("Workflow", StringComparison.Ordinal) ? name["Workflow".Length..] : name;

        return bare.Length == 0 ? name : $"{char.ToLowerInvariant(bare[0])}{bare[1..]}";
    }

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
