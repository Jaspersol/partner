using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Partner.Api.Tests;

/// <summary>
/// Attachments: the bytes are files in the workspace, and what travels through the log is an id.
/// </summary>
public sealed partial class ViewEndpointsTests
{
    /// <summary>A 1×1 PNG: enough to be a real image, small enough to inline in a test.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");


    /// <summary>Uploads and asserts it was accepted, returning the parsed body.</summary>
    private async Task<JsonElement> UploadAsync(byte[] bytes, string contentType = "image/png", string? fileName = null)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/media") { Content = content };
        if (fileName is not null)
        {
            // Percent-encoded, exactly as the browser client sends it: a header carries ASCII only.
            request.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
        }

        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
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
        var uploaded = await UploadAsync(bytes, "image/png", "lamp.png");
        var mediaId = uploaded.GetProperty("mediaId").GetString()!;

        var fetched = await _http.GetAsync($"/api/media/{mediaId}");

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("image/png", fetched.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await fetched.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_document_is_accepted_and_named_so_the_model_can_open_it()
    {
        // The point of storing the person's name is that the model is told a path it can read. An attachment
        // identified only by an opaque id is something it cannot act on, whatever the type.
        var uploaded = await UploadAsync("col 1\n1,2\n"u8.ToArray(), "text/csv", "季度报表.csv");

        var name = uploaded.GetProperty("fileName").GetString();
        var path = uploaded.GetProperty("path").GetString();

        Assert.Equal("季度报表.csv", name);
        Assert.NotNull(path);
        // The path must exist, not merely be reported.
        Assert.True(File.Exists(path), $"附件路径不存在：{path}");
        Assert.Equal("col 1\n1,2\n", await File.ReadAllTextAsync(path!));

        var fetched = await _http.GetAsync($"/api/media/{uploaded.GetProperty("mediaId").GetString()}");
        Assert.Equal("text/csv", fetched.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_name_from_the_client_is_a_label_and_not_a_location()
    {
        var uploaded = await UploadAsync(new byte[] { 1, 2, 3 }, "application/octet-stream", "..\\..\\escape.txt");

        var path = uploaded.GetProperty("path").GetString()!;
        Assert.StartsWith(_workspace, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("escape.txt", uploaded.GetProperty("fileName").GetString());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task An_upload_with_no_usable_type_and_no_usable_name_is_refused()
    {
        // Refused before storage: a client that cannot say what it is sending has a bug.
        using var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-nonsense");

        var response = await _http.PostAsync("/api/media", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("无法确定附件类型", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_attachment_is_refused_at_delivery()
    {
        // A silently empty turn would look like the model ignored the picture.
        var response = await _http.PostAsJsonAsync("/api/input", new
        {
            source = "text",
            payload = "看看这张图",
            media = new[] { "no-such-image" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("未知的附件", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attachment_is_recorded_as_an_id_not_as_bytes()
    {
        var mediaId = (await UploadAsync(TinyPng())).GetProperty("mediaId").GetString()!;

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
