using System.Text;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>What a child is given on its standard input, read back as the child reads it.</summary>
internal static class ChildInputs
{
    /// <summary>All of <paramref name="input"/>, as the text a child reads; empty where there is none.</summary>
    public static string Read(this ChildInput? input)
    {
        if (input is null)
        {
            return string.Empty;
        }

        using var written = new MemoryStream();
        input.WriteTo(written);

        return Encoding.UTF8.GetString(written.ToArray());
    }
}
