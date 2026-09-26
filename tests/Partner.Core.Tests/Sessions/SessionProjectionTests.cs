using Partner.Core.Contracts;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Sessions;

/// <summary>
/// Projection rules (design doc §2.5.5): the view shows the latest revision, the original body stays
/// recoverable, and revision events never appear as their own content.
/// </summary>
public sealed class SessionProjectionTests
{
    private static SessionEvent Ev(long seq, SessionEventKind kind, string body, long? editedFrom = null) =>
        new()
        {
            Seq = seq,
            At = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero).AddSeconds(seq),
            Kind = kind,
            Source = InputSource.Text,
            Body = body,
            EditedFromSeq = editedFrom,
        };

    [Fact]
    public void Untouched_events_pass_through()
    {
        var view = SessionProjection.Build(new[]
        {
            Ev(1, SessionEventKind.User, "hello"),
            Ev(2, SessionEventKind.Assistant, "hi there"),
            Ev(3, SessionEventKind.Tool, "ran tool"),
        });

        Assert.Equal(3, view.LastSeq);
        Assert.Equal(new[] { "hello", "hi there", "ran tool" }, view.Items.Select(i => i.Body));
        Assert.All(view.Items, i => Assert.Null(i.OriginalBody));
        Assert.All(view.Items, i => Assert.Equal(0, i.RevisionCount));
    }

    [Fact]
    public void Edit_replaces_body_and_is_not_rendered_as_content()
    {
        var view = SessionProjection.Build(new[]
        {
            Ev(1, SessionEventKind.User, "typo here"),
            Ev(2, SessionEventKind.Assistant, "ok"),
            Ev(3, SessionEventKind.Edit, "fixed here", editedFrom: 1),
        });

        Assert.Equal(2, view.Items.Count);
        var first = view.Items[0];
        Assert.Equal(1, first.Seq);
        Assert.Equal("fixed here", first.Body);
        Assert.Equal("typo here", first.OriginalBody);
        Assert.Equal(1, first.RevisionCount);

        // The assistant turn is untouched, and the edit's own seq is not an item.
        Assert.Equal("ok", view.Items[1].Body);
        Assert.DoesNotContain(view.Items, i => i.Seq == 3);
    }

    [Fact]
    public void Last_edit_wins_and_revision_count_accumulates()
    {
        var view = SessionProjection.Build(new[]
        {
            Ev(1, SessionEventKind.User, "v1"),
            Ev(2, SessionEventKind.Edit, "v2", editedFrom: 1),
            Ev(3, SessionEventKind.Edit, "v3", editedFrom: 1),
        });

        var item = Assert.Single(view.Items);
        Assert.Equal("v3", item.Body);
        Assert.Equal("v1", item.OriginalBody);
        Assert.Equal(2, item.RevisionCount);
    }

    [Fact]
    public void Summary_events_are_content()
    {
        var view = SessionProjection.Build(new[]
        {
            Ev(1, SessionEventKind.User, "long conversation"),
            Ev(2, SessionEventKind.Summary, "summary of turn 1"),
        });

        Assert.Equal(2, view.Items.Count);
        Assert.Equal(SessionEventKind.Summary, view.Items[1].Kind);
    }

    [Fact]
    public void Projection_is_order_independent_and_rebuildable()
    {
        var events = new[]
        {
            Ev(1, SessionEventKind.User, "a"),
            Ev(2, SessionEventKind.Edit, "a2", editedFrom: 1),
            Ev(3, SessionEventKind.Schedule, "tick"),
        };

        var forward = SessionProjection.Build(events);
        var shuffled = SessionProjection.Build(events.Reverse().ToArray());

        Assert.Equal(
            forward.Items.Select(i => (i.Seq, i.Body)),
            shuffled.Items.Select(i => (i.Seq, i.Body)));
    }

    [Fact]
    public void Empty_log_projects_to_empty_view()
    {
        var view = SessionProjection.Build(Array.Empty<SessionEvent>());

        Assert.Equal(0, view.LastSeq);
        Assert.Empty(view.Items);
    }
}
