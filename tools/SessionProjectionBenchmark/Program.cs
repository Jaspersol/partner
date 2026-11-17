using System.Diagnostics;
using System.Text;
using Partner.Core.Contracts;
using Partner.Core.Sessions;

if (args.Length < 2 || (args[0] != "generate" && args[0] != "measure"))
{
    Console.Error.WriteLine("Usage: generate <directory> <count> | measure <directory>");
    return 1;
}

var directory = Path.GetFullPath(args[1]);
if (args[0] == "generate")
{
    if (args.Length != 3 || !int.TryParse(args[2], out var count) || count < 1)
        throw new ArgumentException("A positive event count is required.");
    var path = Path.Combine(directory, "session", "events.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(file, new UTF8Encoding(false));
    for (var i = 1; i <= count; i++)
    {
        writer.WriteLine(SessionJson.Serialize(new SessionEvent
        {
            Seq = i,
            At = DateTimeOffset.UnixEpoch.AddSeconds(i),
            Kind = i % 2 == 0 ? SessionEventKind.Assistant : SessionEventKind.User,
            Source = InputSource.Text,
            Body = $"Message {i}: reproducible session projection benchmark 数据 😀",
        }));
    }
    return 0;
}

var timer = Stopwatch.StartNew();
var (store, repaired) = JsonlSessionStore.Open(directory);
var openMs = timer.Elapsed.TotalMilliseconds;
await using (store)
{
    timer.Restart();
    var view = await store.ProjectAsync();
    var initialMs = timer.Elapsed.TotalMilliseconds;
    await store.AppendAsync(new SessionEvent
    {
        Kind = SessionEventKind.User,
        Source = InputSource.Text,
        Body = "benchmark append",
    });
    timer.Restart();
    var updated = await store.ProjectAsync();
    var rebuildMs = timer.Elapsed.TotalMilliseconds;
    Console.WriteLine($"events={store.LastSeq - 1} repairedBytes={repaired} initialItems={view.Items.Count} updatedItems={updated.Items.Count}");
    Console.WriteLine($"open_ms={openMs:F2} initial_projection_ms={initialMs:F2} post_append_projection_ms={rebuildMs:F2} peak_working_set_bytes={Process.GetCurrentProcess().PeakWorkingSet64}");
}
return 0;
