using System.Globalization;
using System.Text.Json;

namespace Partner.Core.Agent.Web.Engines;

/// <summary>
/// Weather as a looked-up record rather than a page about weather. This is the engine the voice path lives on:
/// "北京今天天气" gets a temperature in seconds instead of eight blue links to weather sites.
/// <para>
/// It is the only engine that makes two requests — geocode the place, then read the forecast — which is what
/// <see cref="IEngineFollowUp"/> exists for. It never runs unless <see cref="Handles"/> matched, so the extra
/// round trip is always on a query that actually asked for weather.
/// </para>
/// </summary>
public sealed class OpenMeteoEngine : IWebSearchEngine
{
    private const string GeocodeApi = "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name=";

    private const string ForecastApi =
        "https://api.open-meteo.com/v1/forecast?timezone=auto&forecast_days=1"
        + "&current=temperature_2m,weather_code,wind_speed_10m,relative_humidity_2m"
        + "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max";

    /// <summary>
    /// Words that mark a query as a weather question. Longest first, so 天气预报 is consumed before 天气 and no
    /// stray 预报 is left to be mistaken for a place name.
    /// </summary>
    private static readonly string[] WeatherWords =
    [
        "天气预报", "会不会下雨", "会不会下雪", "多少度", "气温", "温度", "天气", "下雨", "降雨", "降水",
        "下雪", "降雪", "风速", "风力", "湿度", "预报", "几度", "今天", "明天", "后天", "现在", "当前", "怎么样", "如何",
    ];

    private static readonly string[] WeatherWordsLatin =
    [
        "weather", "temperature", "forecast", "precipitation", "humidity", "rain", "raining", "snow", "windy",
        "wind", "hot", "cold", "today", "tomorrow", "tonight", "now", "current", "how", "what", "will", "is",
        "it", "the", "in", "at", "for",
    ];

    public EngineTraits Traits { get; } = new(
        Id: "open-meteo",
        Name: "Open-Meteo 天气",
        Kinds: ContentKinds.Weather,
        Scripts: QueryScripts.Any,

        // Offline and pure, so the planner can decide this without a request: a weather word plus something left
        // over that reads as a place.
        Handles: CanAnswer,
        Nature: ResultNature.Record,
        SingleSite: true,
        ExemptFromHostFilter: true,
        Rate: RateLimit.Unlimited,
        Role: EnginePoolRole.Specialist);

    public HttpRequestMessage Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{GeocodeApi}{Uri.EscapeDataString(PlaceOf(query.Text))}&language={(QueryScript.HasCjk(query.Text) ? "zh" : "en")}");

        WebTransport.ApplyHeaders(
            request,
            QueryScript.HasCjk(query.Text) ? "zh-CN,zh;q=0.9,en;q=0.4" : "en;q=0.9",
            WebRequestKind.Data,
            "application/json");
        return request;
    }

    public async Task<ParseResult> ParseAsync(
        WebResponse response,
        SearchQuery query,
        IEngineFollowUp follow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(follow);

        var body = response.Body ?? string.Empty;

        if (response.Status is 403 or 429)
        {
            return ParseResult.Blocked($"open-meteo: 地理编码 HTTP {response.Status}");
        }

        if (response.Status >= 400)
        {
            return ParseResult.Unparseable($"open-meteo: 地理编码 HTTP {response.Status}");
        }

        string place;
        double latitude;
        double longitude;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // A refused request comes back as an error document, so the error has to be read before the absence of
            // results is allowed to mean anything. Taken for "no such place" it would be a failure reported as an
            // answer: the person is told the place does not exist when the truth is that nobody looked.
            if (root.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.True)
            {
                var reason = root.TryGetProperty("reason", out var why) ? why.GetString() : null;
                return ParseResult.Unparseable($"open-meteo: 地理编码被拒绝（{reason ?? "未给出原因"}）");
            }

            // Omitting the list altogether is this provider's way of saying it matched nothing, so unlike the error
            // document above it is a real answer.
            if (!root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return ParseResult.Empty($"open-meteo: 没有找到地点「{PlaceOf(query.Text)}」");
            }

            var first = results[0];
            place = Compose(first);
            latitude = first.GetProperty("latitude").GetDouble();
            longitude = first.GetProperty("longitude").GetDouble();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ParseResult.Unparseable($"open-meteo: 地理编码解析失败（{error.Message}）");
        }

        var (forecastBody, forecastError) = await follow.GetAsync(
            new Uri($"{ForecastApi}&latitude={latitude.ToString("0.####", CultureInfo.InvariantCulture)}"
                + $"&longitude={longitude.ToString("0.####", CultureInfo.InvariantCulture)}"),
            ct).ConfigureAwait(false);

        if (forecastBody is null)
        {
            return ParseResult.Unparseable($"open-meteo: 取预报失败（{forecastError ?? "无响应"}）");
        }

        try
        {
            using var document = JsonDocument.Parse(forecastBody);
            var root = document.RootElement;

            var current = root.TryGetProperty("current", out var currentValue) ? currentValue : default;
            var temperature = Number(current, "temperature_2m");
            var wind = Number(current, "wind_speed_10m");
            var humidity = Number(current, "relative_humidity_2m");
            var code = current.ValueKind == JsonValueKind.Object && current.TryGetProperty("weather_code", out var codeValue)
                ? codeValue.GetInt32()
                : -1;

            var daily = root.TryGetProperty("daily", out var dailyValue) ? dailyValue : default;
            var high = FirstOf(daily, "temperature_2m_max");
            var low = FirstOf(daily, "temperature_2m_min");
            var precipitation = FirstOf(daily, "precipitation_probability_max");

            var parts = new List<string>();
            if (temperature is { } now)
            {
                parts.Add($"当前 {Round(now)}°C");
            }

            if (Describe(code) is { } description)
            {
                parts.Add(description);
            }

            if (high is { } max && low is { } min)
            {
                parts.Add($"今日 {Round(min)}–{Round(max)}°C");
            }

            if (precipitation is { } chance)
            {
                parts.Add($"降水概率 {Round(chance)}%");
            }

            if (wind is { } speed)
            {
                parts.Add($"风速 {Round(speed)} km/h");
            }

            if (humidity is { } wet)
            {
                parts.Add($"湿度 {Round(wet)}%");
            }

            var observed = current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty("time", out var timeValue)
                && timeValue.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(timeValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;

            return ParseResult.Ok(
            [
                new SearchHit(
                    Title: $"{place} 天气",
                    Url: $"https://open-meteo.com/en/docs#latitude={latitude.ToString("0.###", CultureInfo.InvariantCulture)}"
                        + $"&longitude={longitude.ToString("0.###", CultureInfo.InvariantCulture)}",
                    Snippet: $"{string.Join("，", parts)}。观测时间 {observed:yyyy-MM-dd HH:mm}（当地时间）。",

                    // The publisher's own timestamp on its own record, so this is first-hand — and for a forecast
                    // it is the whole point.
                    Published: observed,
                    DateSource: DateSource.Structured,
                    Engine: Traits.Id,
                    Rank: 0),
            ]);
        }
        catch (JsonException error)
        {
            return ParseResult.Unparseable($"open-meteo: 预报解析失败（{error.Message}）");
        }
    }

    /// <summary>
    /// Whether this engine can answer the query outright, decided offline: a weather word <i>and</i> something
    /// left over that reads as a place.
    /// <para>
    /// The weather word is not decoration. <see cref="Handles"/> is consulted for every query before any other
    /// routing, so a test of "is there a place in here" alone would route every search in the session — "Rust
    /// 教程", "quantum computing breakthrough" — to a weather lookup, spending a pool slot and a network round
    /// trip on questions nobody asked about the weather.
    /// </para>
    /// </summary>
    private static bool CanAnswer(string text) => HasWeatherWord(text) && PlaceOf(text).Length > 0;

    /// <summary>Whether a weather word is present, by the same rules the stripping uses below.</summary>
    private static bool HasWeatherWord(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return WeatherWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))
            || WeatherWordsLatin.Any(word => IsWholeWord(text, word));
    }

    /// <summary>
    /// The place, which is the query with the weather words and the English filler taken out. Returns the empty
    /// string when there is nothing left, which is the signal that the query was "今天天气" and no place at all.
    /// </summary>
    private static string PlaceOf(string? text)
    {
        var place = text ?? string.Empty;

        foreach (var word in WeatherWords)
        {
            place = place.Replace(word, " ", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var word in WeatherWordsLatin)
        {
            place = ReplaceWord(place, word);
        }

        place = new string(place.Select(character => char.IsLetterOrDigit(character) || character > 0x7F ? character : ' ').ToArray());

        // A single character is not a place. "热" alone, or the residue of a Chinese sentence, would geocode to
        // somewhere real and answer a question nobody asked.
        return place.Trim().Length >= 2 ? place.Trim() : string.Empty;
    }

    /// <summary>Replaces a whole Latin word, so "rain" does not eat the middle of "Rainsville".</summary>
    private static string ReplaceWord(string text, string word)
    {
        var result = text;
        var at = 0;

        while ((at = result.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (IsWholeWord(result, at, word.Length))
            {
                result = string.Concat(result.AsSpan(0, at), " ", result.AsSpan(at + word.Length));
            }

            at += 1;
        }

        return result;
    }

    /// <summary>Whether the match at <paramref name="at"/> is a whole word rather than the middle of one.</summary>
    private static bool IsWholeWord(string text, int at, int length) =>
        (at == 0 || !char.IsLetter(text[at - 1]))
        && (at + length >= text.Length || !char.IsLetter(text[at + length]));

    /// <summary>Whether the word appears anywhere in the text as a whole word.</summary>
    private static bool IsWholeWord(string text, string word)
    {
        var at = 0;

        while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (IsWholeWord(text, at, word.Length))
            {
                return true;
            }

            at += 1;
        }

        return false;
    }

    private static string Compose(JsonElement result)
    {
        var name = String(result, "name");
        var region = String(result, "admin1");
        var country = String(result, "country");

        var parts = new List<string> { name };
        if (region.Length > 0 && !string.Equals(region, name, StringComparison.Ordinal))
        {
            parts.Add(region);
        }

        if (country.Length > 0)
        {
            parts.Add(country);
        }

        return string.Join("·", parts.Where(part => part.Length > 0));
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static double? FirstOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var array)
        && array.ValueKind == JsonValueKind.Array
        && array.GetArrayLength() > 0
        && array[0].ValueKind == JsonValueKind.Number
            ? array[0].GetDouble()
            : null;

    private static string Round(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// WMO weather codes, as words. The API's own documentation gives the codes and their meanings; this is that
    /// table, in Chinese, so a voice answer does not read out a number.
    /// </summary>
    private static string? Describe(int code) => code switch
    {
        0 => "晴",
        1 => "大致晴朗",
        2 => "局部多云",
        3 => "阴",
        45 => "有雾",
        48 => "雾凇",
        51 => "毛毛雨",
        53 => "小雨",
        55 => "中雨",
        56 or 57 => "冻雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "米雪",
        80 => "阵雨",
        81 => "强阵雨",
        82 => "暴雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷阵雨伴冰雹",
        _ => null,
    };
}
