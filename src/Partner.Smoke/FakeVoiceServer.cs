using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Partner.Smoke;

/// <summary>
/// Minimal HTTP/1.1 server used to exercise the voice clients end to end without a real ASR/TTS service.
/// A raw <see cref="TcpListener"/> is deliberate: it avoids HttpListener URL ACLs entirely.
/// </summary>
internal sealed class FakeVoiceServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly List<string> _requests = new();

    public FakeVoiceServer(bool failAsr = false, bool failTts = false, string transcript = "turn on the desk lamp")
    {
        FailAsr = failAsr;
        FailTts = failTts;
        Transcript = transcript;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{Port}";
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public bool FailAsr { get; }

    public bool FailTts { get; }

    public string Transcript { get; }

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>Last ASR request body (used to assert the WAV payload shape).</summary>
    public string? LastAsrBody { get; private set; }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1];

            while (!header.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    return;
                }

                header.Append((char)buffer[0]);
            }

            var lines = header.ToString().Split("\r\n");
            var requestLine = lines[0];
            var contentLength = lines
                .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(l => int.Parse(l.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault();

            var body = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset), ct);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            var bodyText = Encoding.UTF8.GetString(body);
            lock (_gate)
            {
                _requests.Add(requestLine);
            }

            if (requestLine.Contains("/v1/chat/completions", StringComparison.Ordinal))
            {
                LastAsrBody = bodyText;
                await RespondAsrAsync(stream, ct);
            }
            else if (requestLine.Contains("/tts", StringComparison.Ordinal))
            {
                await RespondTtsAsync(stream, ct);
            }
            else
            {
                await WriteAsync(stream, 404, "Not Found", "text/plain", Array.Empty<byte>(), ct);
            }
        }
    }

    private async Task RespondAsrAsync(NetworkStream stream, CancellationToken ct)
    {
        if (FailAsr)
        {
            await WriteAsync(stream, 500, "Internal Server Error", "text/plain", Encoding.UTF8.GetBytes("asr down"), ct);
            return;
        }

        var json = $"{{\"choices\":[{{\"message\":{{\"content\":\"language Chinese<asr_text>{Transcript}\"}}}}]}}";
        await WriteAsync(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes(json), ct);
    }

    private async Task RespondTtsAsync(NetworkStream stream, CancellationToken ct)
    {
        if (FailTts)
        {
            await WriteAsync(stream, 503, "Service Unavailable", "text/plain", Encoding.UTF8.GetBytes("tts down"), ct);
            return;
        }

        // 2400 samples of 24 kHz float32 = 100 ms of audio.
        var samples = new float[2400];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / 24_000.0));
        }

        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        await WriteAsync(stream, 200, "OK", "application/octet-stream", bytes, ct);
    }

    private static async Task WriteAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        byte[] body,
        CancellationToken ct)
    {
        var header = $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
