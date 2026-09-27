using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// Image attachments: the bytes are files in the workspace, and what travels through the log is an id.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    /// <summary>A 1×1 PNG: enough to be a real image, small enough to inline in a test.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private async Task<string> UploadAsync(byte[] bytes, string contentType = "image/png")
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        var response = await _http.PostAsync("/api/media", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("mediaId").GetString()!;
    }

    /// <summary>The host holds these files open, so a reader has to allow its writer back.</summary>
    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task An_uploaded_image_comes_back_by_id()
    {
        var bytes = TinyPng();
        var mediaId = await UploadAsync(bytes);

        var fetched = await _http.GetAsync($"/api/media/{mediaId}");

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("image/png", fetched.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await fetched.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_upload_that_is_not_an_image_is_refused()
    {
        using var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

        var response = await _http.PostAsync("/api/media", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("不支持", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_attachment_is_refused_at_delivery()
    {
        // A turn that silently sent nothing would look like the model ignored the picture.
        var response = await _http.PostAsJsonAsync("/api/input", new
        {
            source = "text",
            payload = "看看这张图",
            media = new[] { "no-such-image" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("未知的附图", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attachment_is_recorded_as_an_id_not_as_bytes()
    {
        var mediaId = await UploadAsync(TinyPng());

        var delivered = await _http.PostAsJsonAsync("/api/input", new
        {
            source = "text",
            payload = "看看这张图",
            media = new[] { mediaId },
        });

        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        // The queue's own log file is the thing that must stay text: it carries the id, never the image.
        var log = await ReadSharedAsync(Path.Combine(_workspace, "queue", "items.jsonl"));
        Assert.Contains(mediaId, log, StringComparison.Ordinal);
        Assert.DoesNotContain("iVBORw0KGgo", log, StringComparison.Ordinal);

        var queued = await _http.GetFromJsonAsync<JsonElement>("/api/queue");
        var item = queued.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("payload").GetString() == "看看这张图");
        Assert.Contains(mediaId, item.GetProperty("mediaIds").EnumerateArray().Select(x => x.GetString()));
    }
}
