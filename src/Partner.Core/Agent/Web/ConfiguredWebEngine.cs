using System.Text.Json;

namespace Partner.Core.Agent.Web;

/// <summary>Serializable engine configuration supplied by the settings layer.</summary>
public sealed record WebEngineDefinition(
    string Id,
    string Name,
    bool Enabled = true,
    string? PresetId = null,
    string? UrlTemplate = null,
    string? ResultsPath = null,
    string? TitleField = null,
    string? UrlField = null,
    string? SnippetField = null);

/// <summary>Builds the immutable runtime engine list from settings.</summary>
public static class ConfiguredWebEngines
{
    public static string HealthId(WebEngineDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.PresetId)) return definition.Id;
        var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] {
            definition.UrlTemplate, definition.ResultsPath, definition.TitleField, definition.UrlField, definition.SnippetField }));
        return definition.Id + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public static IReadOnlyList<IWebSearchEngine> Resolve(IReadOnlyList<WebEngineDefinition>? definitions)
    {
        if (definitions is null) return WebEngines.All;
        var result = new List<IWebSearchEngine>();
        foreach (var definition in definitions)
        {
            if (!definition.Enabled) continue;
            if (!string.IsNullOrWhiteSpace(definition.PresetId) && WebEngines.Find(definition.PresetId) is { } preset)
            {
                result.Add(new NamedEngine(preset, definition.Id, definition.Name));
            }
            else if (!string.IsNullOrWhiteSpace(definition.UrlTemplate))
            {
                result.Add(new CustomJsonEngine(definition));
            }
        }
        return result;
    }

    private sealed class NamedEngine(IWebSearchEngine inner, string id, string name) : IWebSearchEngine
    {
        public EngineTraits Traits { get; } = inner.Traits with { Id = id, Name = name };
        public HttpRequestMessage Build(SearchQuery query) => inner.Build(query);
        public async Task<ParseResult> ParseAsync(WebResponse response, SearchQuery query, IEngineFollowUp follow, CancellationToken ct = default)
        {
            var parsed = await inner.ParseAsync(response, query, follow, ct).ConfigureAwait(false);
            if (parsed.Hits.Count == 0 || parsed.Status != ParseStatus.Ok) return parsed;
            return parsed with { Hits = parsed.Hits.Select(hit => hit with { Engine = id }).ToList() };
        }
    }

    private sealed class CustomJsonEngine : IWebSearchEngine
    {
        private readonly WebEngineDefinition _definition;
        public EngineTraits Traits { get; }

        public CustomJsonEngine(WebEngineDefinition definition)
        {
            _definition = definition;
            Traits = new EngineTraits(definition.Id, definition.Name, ContentKinds.General, QueryScripts.Any,
                null, ResultNature.Ranked, false, false, RateLimit.Unlimited, EnginePoolRole.Base);
        }

        public HttpRequestMessage Build(SearchQuery query)
        {
            var url = _definition.UrlTemplate!.Replace("{query}", Uri.EscapeDataString(query.Text), StringComparison.Ordinal);
            return new HttpRequestMessage(HttpMethod.Get, url);
        }

        public Task<ParseResult> ParseAsync(WebResponse response, SearchQuery query, IEngineFollowUp follow, CancellationToken ct = default)
        {
            if (response.Status is >= 400) return Task.FromResult(ParseResult.Unparseable($"{Traits.Name}: HTTP {response.Status}"));
            if (string.IsNullOrWhiteSpace(response.Body)) return Task.FromResult(ParseResult.Unparseable($"{Traits.Name}: 响应为空"));
            try
            {
                using var document = JsonDocument.Parse(response.Body);
                var node = document.RootElement;
                if (!string.IsNullOrWhiteSpace(_definition.ResultsPath))
                    foreach (var segment in _definition.ResultsPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
                            return Task.FromResult(ParseResult.Unparseable($"{Traits.Name}: 结果路径不存在"));
                    }
                if (node.ValueKind != JsonValueKind.Array) return Task.FromResult(ParseResult.Unparseable($"{Traits.Name}: 结果不是数组"));
                var hits = new List<SearchHit>();
                var rank = 0;
                var count = node.GetArrayLength();
                var limit = Math.Clamp(query.Limit, WebOptions.MinResults, WebOptions.MaxResultsCeiling);
                foreach (var item in node.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var title = GetString(item, _definition.TitleField);
                    var url = GetString(item, _definition.UrlField);
                    if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                        || WebAddressGuard.Reject(parsed) is not null) continue;
                    hits.Add(new SearchHit(title!, parsed.ToString(), GetString(item, _definition.SnippetField) ?? string.Empty,
                        null, DateSource.None, Traits.Id, rank++));
                    if (hits.Count >= limit) break;
                }
                return Task.FromResult(hits.Count > 0 ? ParseResult.Ok(hits)
                    : count == 0 ? ParseResult.Empty($"{Traits.Name}: 没有结果")
                    : ParseResult.Unparseable($"{Traits.Name}: 结果缺少有效标题或链接"));
            }
            catch (JsonException) { return Task.FromResult(ParseResult.Unparseable($"{Traits.Name}: JSON 无法解析")); }
        }

        private static string? GetString(JsonElement item, string? field)
        {
            if (string.IsNullOrWhiteSpace(field)) return null;
            var value = item;
            foreach (var segment in field.Split('.'))
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return null;
            }
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
    }
}
