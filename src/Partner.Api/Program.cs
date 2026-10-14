using Partner.Api;
using Partner.Api.Auth;
using Partner.Desktop.Windows;

// First statement in the process on purpose: Windows fixes DPI awareness the first time it is decided, and
// the desktop layer's coordinates, captures and UI Automation rectangles are all read in that space.
DpiAwareness.EnsurePerMonitorV2();

var workspace = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Partner", "workspace");

var port = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 5280;

// The token comes from the environment (a launcher such as the Tauri shell passes it that way). When it is
// absent one is generated, so running this by hand can never leave the runtime unauthenticated.
var (token, generated) = TokenAuth.Resolve();

var (app, _, consumer, voice, localEngines, maintenance) = await ApiHost.BuildAsync(workspace, port, token);

// Reported from ApplicationStarted rather than before RunAsync: a launcher waits for this line, so it must
// mean "accepting connections", not "about to try".
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"partner-api listening on http://127.0.0.1:{port}");

    // Printed only when generated: if the token was supplied, the caller already holds it, and secrets do
    // not belong in logs.
    if (generated)
    {
        Console.WriteLine($"token: {token}");
    }
});

// A launcher (the Tauri shell) holds our stdin open. When it dies — including a hard kill that skips its
// cleanup — the pipe closes, and lingering would keep the port and the SQLite lock held. Opt-in, because a
// redirected stdin under a test harness reaches EOF immediately and must not be read as "parent is gone".
if (Environment.GetEnvironmentVariable("PARTNER_API_PARENT_WATCH") == "1")
{
    _ = Task.Run(async () =>
    {
        using var stdin = Console.OpenStandardInput();
        var buffer = new byte[256];
        while (await stdin.ReadAsync(buffer) > 0)
        {
            // Any input is ignored; only the closing of the pipe matters.
        }

        app.Lifetime.StopApplication();
    });
}

try
{
    await app.RunAsync();
}
finally
{
    // Graceful: the consumer stops claiming new work and lets the turn in flight finish.
    maintenance.Stop();
    await maintenance.DisposeAsync();
    await localEngines.DisposeAsync();
    await voice.DisposeAsync();
    await consumer.DisposeAsync();
}
