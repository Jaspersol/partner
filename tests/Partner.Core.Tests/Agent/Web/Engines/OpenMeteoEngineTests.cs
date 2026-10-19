using Partner.Core.Agent.Web;
using Partner.Core.Agent.Web.Engines;

namespace Partner.Core.Tests.Agent.Web.Engines;

/// <summary>
/// The weather lookup, which is the one engine that makes two requests: the geocoder turns a phrase into a place
/// and a pair of coordinates, and the forecast is read from a second endpoint. Its parser is therefore also the
/// only one that can be tested from an archived response and a fake.
/// <para>
/// Two of the cases here turn on the difference between "there is no such place" and "the provider refused the
/// request": the first is an answer, the second is a failure, and reporting the second as the first tells a person
/// a city does not exist when nobody ever looked.
/// </para>
/// </summary>
public sealed class OpenMeteoEngineTests
{
    private readonly OpenMeteoEngine _engine = new();

    private static SearchQuery Query(string text = "北京今天天气") => new(text);

    private Task<ParseResult> ParseAsync(string fixture, IEngineFollowUp follow, int status = 200, string query = "北京今天天气") =>
        _engine.ParseAsync(Fixtures.Response(fixture, status), Query(query), follow);

    private Task<ParseResult> ParseBodyAsync(string body, IEngineFollowUp follow, int status = 200) =>
        _engine.ParseAsync(new WebResponse(status, body, FinalUri: null, ContentType: "application/json", Error: null), Query(), follow);

    [Fact]
    public async Task Parse_AnswersAWeatherQuestionAsOneRecord()
    {
        var follow = new FollowUp(Fixtures.Load("open-meteo-forecast.json"));

        var result = await ParseAsync("open-meteo-geocode.json", follow);

        Assert.Equal(ParseStatus.Ok, result.Status);
        var hit = Assert.Single(result.Hits);

        // The place is the geocoder's own composition, so the answer says which of the places with that name it
        // is talking about.
        Assert.Equal("北京·北京市·中国 天气", hit.Title);
        Assert.Equal("https://open-meteo.com/en/docs#latitude=39.904&longitude=116.407", hit.Url);
        Assert.Equal(
            "当前 17°C，阴，今日 10–21°C，降水概率 10%，风速 12 km/h，湿度 46%。观测时间 2026-10-19 14:15（当地时间）。",
            hit.Snippet);

        Assert.Equal("open-meteo", hit.Engine);
        Assert.Equal(0, hit.Rank);
        Assert.Equal(DateSource.Structured, hit.DateSource);

        // The published instant is the observation time the forecast carries, not the moment it was asked for.
        // For a forecast that difference is the whole point of the field.
        Assert.Equal("2026-10-19 14:15", hit.Published!.Value.ToString("yyyy-MM-dd HH:mm"));
    }

    [Fact]
    public async Task Parse_AsksTheForecastEndpointForTheCoordinatesTheGeocoderReturned()
    {
        var follow = new FollowUp(Fixtures.Load("open-meteo-forecast.json"));
        await ParseAsync("open-meteo-geocode.json", follow);

        // The second request is the reason the follow-up seam exists at all, and it is the geocoder's answer
        // rather than the query that says where to ask.
        Assert.NotNull(follow.Asked);
        var url = follow.Asked!.ToString();

        Assert.StartsWith("https://api.open-meteo.com/v1/forecast?", url, StringComparison.Ordinal);
        Assert.Contains("latitude=39.9042&longitude=116.4074", url, StringComparison.Ordinal);
        Assert.Contains("timezone=auto", url, StringComparison.Ordinal);
        Assert.Contains("current=temperature_2m,weather_code,wind_speed_10m,relative_humidity_2m", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_NamesOnlyTheCountryWhenTheRegionIsThePlaceItself()
    {
        // The geocoder repeats a municipality in admin1. Saying it twice would read as two places.
        var follow = new FollowUp(Fixtures.Load("open-meteo-forecast.json"));
        var result = await ParseBodyAsync(
            """
            {"results":[{"name":"上海","latitude":31.2222,"longitude":121.4581,"country":"中国","admin1":"上海"}]}
            """,
            follow);

        Assert.Equal("上海·中国 天气", Assert.Single(result.Hits).Title);
    }

    [Fact]
    public async Task Parse_ReportsAPlaceItCouldNotFindAsEmptyWithoutAskingForAForecast()
    {
        var follow = new FollowUp(Fixtures.Load("open-meteo-forecast.json"));

        var result = await ParseAsync("open-meteo-no-place.json", follow, query: "舍瓦村今天天气");

        Assert.Equal(ParseStatus.Empty, result.Status);
        Assert.Contains("没有找到地点「舍瓦村」", result.Note!, StringComparison.Ordinal);
        Assert.Null(follow.Asked);
    }

    [Fact]
    public async Task Parse_ReportsAProviderErrorAsAParseFailureRatherThanAsNoPlace()
    {
        // This provider has two ways of saying "nothing for you": it omits the results list when the place simply
        // does not exist, and it sends an error document with the reason when it refuses the request. Both arrive
        // without results, so telling them apart is the parser's job — and reading the refusal as an answer tells
        // the person the place does not exist when the truth is that nobody looked.
        var follow = new FollowUp(Fixtures.Load("open-meteo-forecast.json"));

        var result = await ParseAsync("open-meteo-error.json", follow);

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("被拒绝", result.Note!, StringComparison.Ordinal);
        Assert.Contains("Parameter 'name' is required", result.Note!, StringComparison.Ordinal);
        Assert.Null(follow.Asked);
    }

    [Fact]
    public async Task Parse_ReportsAFailedForecastAsAParseFailure()
    {
        var follow = new FollowUp(Body: null, Error: "unreachable: 连接被拒绝");

        var result = await ParseAsync("open-meteo-geocode.json", follow);

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("取预报失败", result.Note!, StringComparison.Ordinal);
        Assert.Contains("连接被拒绝", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAMalformedForecastAsAParseFailure()
    {
        var follow = new FollowUp("<html><body>rate limited</body></html>");

        var result = await ParseAsync("open-meteo-geocode.json", follow);

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("预报解析失败", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_ReportsAnErrorStatusOnTheGeocodeAsAParseFailure()
    {
        var follow = new FollowUp(Fixtures.Load("open-meteo-forecast.json"));

        var result = await ParseAsync("open-meteo-geocode.json", follow, status: 500);

        Assert.Equal(ParseStatus.Unparseable, result.Status);
        Assert.Contains("HTTP 500", result.Note!, StringComparison.Ordinal);
        Assert.Null(follow.Asked);
    }

    [Fact]
    public void Handles_MatchesAWeatherQuestionThatNamesAPlace()
    {
        var handles = _engine.Traits.Handles!;

        Assert.True(handles("北京今天天气"));
        Assert.True(handles("上海天气"));

        // Whole words: "rain" inside a place name is a place name.
        Assert.True(handles("Rainville weather"));

        // A weather word and nothing that reads as a place is not a question this can answer.
        Assert.False(handles("今天天气怎么样"));
        Assert.False(handles("会不会下雨"));
        Assert.False(handles("热"));

        // And a question with no weather word at all is not one, however much of it survives the stripping. This
        // is the case that keeps every other search in the session off the weather API.
        Assert.False(handles("Rust 教程"));
        Assert.False(handles("quantum computing breakthrough"));
        Assert.False(handles(string.Empty));
    }

    [Fact]
    public void Build_AsksTheGeocoderForThePlaceLeftOverAfterTheWeatherWords()
    {
        var chinese = _engine.Build(Query("北京今天天气"));
        var chineseUrl = chinese.RequestUri!.AbsoluteUri;

        Assert.StartsWith("https://geocoding-api.open-meteo.com/v1/search?", chineseUrl, StringComparison.Ordinal);
        Assert.Contains($"name={Uri.EscapeDataString("北京")}", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("count=1", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("format=json", chineseUrl, StringComparison.Ordinal);
        Assert.Contains("language=zh", chineseUrl, StringComparison.Ordinal);

        var english = _engine.Build(Query("what is the weather in Rainville"));

        Assert.Contains("name=Rainville", english.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("language=en", english.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>A second request that answers with one canned body and remembers what it was asked for.</summary>
    private sealed class FollowUp(string? Body, string? Error = null) : IEngineFollowUp
    {
        public Uri? Asked { get; private set; }

        public Task<(string? Body, string? Error)> GetAsync(Uri url, CancellationToken ct = default)
        {
            Asked = url;
            return Task.FromResult((Body, Error));
        }
    }
}
