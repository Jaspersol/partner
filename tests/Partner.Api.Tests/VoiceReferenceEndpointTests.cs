using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Partner.Api.Tests;

/// <summary>The clip a person picks for voice cloning: it must land on disk under a name that is only a label.</summary>
public sealed class VoiceReferenceEndpointTests : ApiFixture
{
    protected override string WorkspaceName => "partner-voice-reference-tests";

    [Fact]
    public async Task A_wav_is_stored_in_the_workspace_and_its_path_comes_back()
    {
        using var response = await Upload("../../我的声音.wav", [1, 2, 3, 4]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var path = body.GetProperty("path").GetString()!;
        Assert.Equal("我的声音.wav", Path.GetFileName(path));
        Assert.Equal("voices", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("voice.mp3")]
    [InlineData(".wav")]
    [InlineData("")]
    public async Task Anything_that_is_not_a_named_wav_is_refused(string name)
    {
        using var response = await Upload(name, [1]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        using var response = await Upload("a.wav", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<HttpResponseMessage> Upload(string name, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/reference") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        return _http.SendAsync(request);
    }
}
