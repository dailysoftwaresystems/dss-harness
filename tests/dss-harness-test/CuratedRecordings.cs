using System.Text.RegularExpressions;

namespace DssHarness.Tests;

/// <summary>
/// The rules every curated recording the public repository keeps is held to - the Claude Code probe's and the Claude
/// API's answers alike: every id a server gave and every uuid a stand-in, and no path of the machine that made it.
/// </summary>
internal static class CuratedRecordings
{
    /// <summary>Holds <paramref name="text"/> to the rules, so a recording curated without them is refused by the suite.</summary>
    /// <param name="text">The recording, whole.</param>
    public static void KeepNothingOfTheMachineOrTheAccount(string text)
    {
        // An id is a long run after its prefix, which a word of the protocol (msg_lifecycle_v1) is not.
        var ids = Regex.Matches(text, @"\b(?:req|msg|toolu|srvtoolu|fct)_[A-Za-z0-9]{16,}").Select(found => found.Value).ToList();
        var uuids = Regex.Matches(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Select(found => found.Value).ToList();

        Assert.All(ids, id => Assert.Matches("^(req|msg|toolu|srvtoolu|fct)_[0-9]{24}$", id));

        // ... and a word of the protocol is kept as written, never taken for an id and given a stand-in.
        Assert.DoesNotMatch(@"\b(?:req|msg|toolu|srvtoolu|fct)_[0-9]{24}_v[0-9]", text);
        Assert.All(uuids, uuid => Assert.StartsWith("00000000-0000-4000-8000-", uuid, StringComparison.Ordinal));

        // No path of the machine: no drive, no home directory under any of its spellings.
        Assert.DoesNotMatch(@"(?i)\b[a-z]:\\\\|/Users/|/home/|~\\\\|~/", text);
    }
}
