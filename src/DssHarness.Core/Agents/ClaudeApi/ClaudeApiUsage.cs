using System.Text.Json;
using DssHarness.Core.Agents.Http;

namespace DssHarness.Core.Agents.ClaudeApi;

/// <summary>
/// What one request to the Claude API used, as its answer says it: each <c>usage</c> it carries - the one of its
/// <c>message_start</c>, each of its <c>message_delta</c>s - taken in turn, each count it holds standing for the request
/// whole, so the last said of each holds. The one reader of the API's usage, for a conversation's request and the
/// harness's own native search alike.
/// </summary>
/// <param name="cacheTtl">The lifetime the request asked its cache writes kept for, which says where a write the answer gives whole alone belongs.</param>
internal sealed class ClaudeApiUsage(string? cacheTtl)
{
    private long _input;
    private long _output;
    private long _cacheRead;
    private long _cacheWrite;
    private long? _cacheWrite5m;
    private long? _cacheWrite1h;
    private long _searches;

    /// <summary>Takes what a usage says, each count it holds standing for the request whole: the last said of each holds.</summary>
    /// <param name="usage">A <c>usage</c> object of the answer; anything else says nothing.</param>
    public void Take(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _input = ApiAnswers.Count(usage, "input_tokens") ?? _input;
        _output = ApiAnswers.Count(usage, "output_tokens") ?? _output;
        _cacheRead = ApiAnswers.Count(usage, "cache_read_input_tokens") ?? _cacheRead;
        _cacheWrite = ApiAnswers.Count(usage, "cache_creation_input_tokens") ?? _cacheWrite;

        if (usage.TryGetProperty("cache_creation", out var written) && written.ValueKind == JsonValueKind.Object)
        {
            _cacheWrite5m = ApiAnswers.Count(written, "ephemeral_5m_input_tokens") ?? _cacheWrite5m;
            _cacheWrite1h = ApiAnswers.Count(written, "ephemeral_1h_input_tokens") ?? _cacheWrite1h;
        }

        if (usage.TryGetProperty("server_tool_use", out var server) && server.ValueKind == JsonValueKind.Object)
        {
            _searches = ApiAnswers.Count(server, "web_search_requests") ?? _searches;
        }
    }

    /// <summary>
    /// What the request used: the cache it wrote by how long it is kept, as the API says it - or, where it says the
    /// whole alone, by the lifetime the request asked for.
    /// </summary>
    public AgentUsage Used()
    {
        var (fiveMinutes, anHour) = _cacheWrite5m is not null || _cacheWrite1h is not null
            ? (_cacheWrite5m ?? 0, _cacheWrite1h ?? 0)
            : cacheTtl == ClaudeApiRequest.AnHour ? (0L, _cacheWrite) : (_cacheWrite, 0L);

        return new AgentUsage(_input, _output, _cacheRead, fiveMinutes, anHour, _searches);
    }
}
