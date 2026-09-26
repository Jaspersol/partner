using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Sessions;

/// <summary>
/// Removing history. The log stays append-only: a removal is itself an event, so the raw record is intact,
/// seqs are never reused, and the view is what changes.
/// </summary>
public sealed class SessionRetractionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-retract-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private async Task<ISessionStore> SeedAsync(params string[] bodies)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);

        foreach (var body in bodies)
        {
            await store.AppendAsync(new SessionEvent
            {
                Kind = SessionEventKind.User,
                Source = InputSource.Text,
                Body = body,
            });
        }

        return store;
    }

    private static async Task<List<string>> VisibleAsync(ISessionStore store)
    {
        var view = await store.ProjectAsync();
        return view.Items.Select(item => item.Body).ToList();
    }

    [Fact]
    public async Task Deleting_one_event_leaves_the_rest_untouched()
    {
        await using var store = await SeedAsync("one", "two", "three");

        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "delete #2",
            Meta = SessionRetraction.Single(2),
        });

        Assert.Equal(new[] { "one", "three" }, await VisibleAsync(store));
    }

    [Fact]
    public async Task Retracting_removes_the_selected_event_and_everything_after_it()
    {
        await using var store = await SeedAsync("one", "two", "three", "four");

        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "retract from #2",
            Meta = SessionRetraction.From(2),
        });

        Assert.Equal(new[] { "one" }, await VisibleAsync(store));
    }

    [Fact]
    public async Task The_raw_log_keeps_everything_and_seqs_are_never_reused()
    {
        await using var store = await SeedAsync("one", "two");

        var before = await store.ProjectAsync();
        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "retract from #1",
            Meta = SessionRetraction.From(1),
        });

        var after = await store.ProjectAsync();

        Assert.Empty(after.Items);

        // The ids keep counting from where they were: a new event must not land on a removed seq.
        var next = await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "three",
        });

        Assert.True(next > before.LastSeq);
        // The retraction itself took the next seq; the removed seqs are gone for good, not recycled.
        Assert.Equal(before.LastSeq + 1, after.LastSeq);
        Assert.Equal(before.LastSeq + 2, next);
        Assert.Equal(new[] { "three" }, await VisibleAsync(store));

        // And the raw log still holds the removed bodies, so the removal is auditable.
        var raw = new List<string>();
        await foreach (var e in store.ReadAllAsync())
        {
            raw.Add(e.Body);
        }

        Assert.Contains("one", raw);
        Assert.Contains("two", raw);
    }

    [Fact]
    public async Task A_retraction_range_is_validated_instead_of_silently_doing_nothing()
    {
        await using var store = await SeedAsync("one", "two");

        // No range at all.
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "no range",
        }));

        // A backwards range.
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "backwards",
            Meta = new Dictionary<string, string> { ["fromSeq"] = "2", ["toSeq"] = "1" },
        }));

        // A range that points at the future.
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "future",
            Meta = SessionRetraction.From(99),
        }));

        Assert.Equal(new[] { "one", "two" }, await VisibleAsync(store));
    }

    [Fact]
    public async Task An_edit_inside_a_retracted_range_does_not_resurrect_the_event()
    {
        await using var store = await SeedAsync("one", "two");

        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Edit,
            Source = InputSource.Text,
            Body = "two (edited)",
            EditedFromSeq = 2,
        });

        await store.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "retract #2",
            Meta = SessionRetraction.Single(2),
        });

        Assert.Equal(new[] { "one" }, await VisibleAsync(store));
    }
}
