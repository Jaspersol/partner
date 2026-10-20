using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Agent;

/// <summary>
/// The tool-failure summary: what the runtime has seen go wrong, counted.
/// <para>
/// The value is entirely in the grouping. A log that lists every failure is the session log, which already
/// exists; the summary's job is to make a hundred identical complaints read as one row.
/// </para>
/// </summary>
public sealed class ToolFailureLogTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_failure_twice_is_one_row_counted_twice()
    {
        var log = new InMemoryToolFailureLog();
        log.Record("read", "file not found", "file not found: a.txt", Base);
        log.Record("read", "file not found", "file not found: b.txt", Base.AddMinutes(1));

        var row = Assert.Single(log.Top(10));
        Assert.Equal(2, row.Count);
        Assert.Equal(2, log.Totals.Occurrences);
        Assert.Equal(1, log.Totals.Distinct);
    }

    [Fact]
    public void The_most_frequent_failure_is_the_first_one_listed()
    {
        var log = new InMemoryToolFailureLog();
        log.Record("write", "denied", "permission denied", Base);
        for (var i = 0; i < 5; i++)
        {
            log.Record("read", "missing", "file not found", Base.AddMinutes(i));
        }

        Assert.Equal("read", log.Top(1)[0].Tool);
        Assert.Equal("read", log.Top(10)[0].Tool);
    }

    [Fact]
    public void Two_tools_failing_the_same_way_are_two_rows()
    {
        // The tool is part of the diagnosis: a search that finds nothing and a read that finds nothing are
        // different problems.
        var log = new InMemoryToolFailureLog();
        log.Record("read", "missing", "file not found", Base);
        log.Record("search", "missing", "file not found", Base);

        Assert.Equal(2, log.Top(10).Count);
    }

    [Fact]
    public void A_long_recurring_failure_does_not_push_the_others_out()
    {
        // Capacity exists so a flood of one-offs cannot crowd out the one failure that happens every turn.
        var log = new InMemoryToolFailureLog(capacity: 4);
        log.Record("read", "the recurring one", "it happened again", Base);

        for (var i = 0; i < 20; i++)
        {
            log.Record("read", $"one-off {i}", "a unique failure", Base.AddSeconds(i));
        }

        Assert.Contains(log.Top(10), failure => failure.Reason == "the recurring one");
    }

    [Fact]
    public void Messages_that_differ_only_by_their_argument_land_on_one_key()
    {
        // Without this the summary is a list of one-offs. The argument varies; the reason repeats.
        var a = ToolFailureReason.From("grep timed out after 30s on reports/");
        var b = ToolFailureReason.From("grep timed out after 45s on reports/");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Grouping_is_coarse_on_purpose_and_the_detail_survives_in_the_message()
    {
        // Numbers are masked, so `q1` and `q2` collapse: the row's job is to say "this keeps happening to this
        // tool", and a reader who needs *which* file still has it in LastMessage.
        Assert.Equal(
            ToolFailureReason.From("file not found: reports/q1.xlsx"),
            ToolFailureReason.From("file not found: reports/q2.xlsx"));

        var log = new InMemoryToolFailureLog();
        log.Record("read", ToolFailureReason.From("file not found: reports/q1.xlsx"), "file not found: reports/q1.xlsx", Base);
        log.Record("read", ToolFailureReason.From("file not found: reports/q2.xlsx"), "file not found: reports/q2.xlsx", Base);

        var row = Assert.Single(log.Top(10));
        Assert.Equal(2, row.Count);
        Assert.Contains("q2", row.LastMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_reasons_stay_apart()
    {
        // Over-merging would make a row like "the files are wrong", which is useless.
        var missing = ToolFailureReason.From("file not found: a.txt");
        var denied = ToolFailureReason.From("permission denied: a.txt");

        Assert.NotEqual(missing, denied);
    }

    [Fact]
    public void An_empty_message_still_produces_a_key()
    {
        // A failure with nothing to say is still a failure; dropping it would hide the call that produced it.
        Assert.Equal("(empty)", ToolFailureReason.From("   "));
    }

    [Fact]
    public void The_summary_keeps_the_newest_message_so_the_row_is_diagnosable()
    {
        // The grouping is lossy on purpose, so the detail has to survive somewhere: the key says what repeats,
        // the message says what it actually said this time.
        var log = new InMemoryToolFailureLog();
        log.Record("read", "missing", "file not found: first.txt", Base);
        log.Record("read", "missing", "file not found: second.txt", Base.AddMinutes(1));

        var row = Assert.Single(log.Top(10));
        Assert.Contains("second.txt", row.LastMessage, StringComparison.Ordinal);
        Assert.Equal(Base.AddMinutes(1), row.LastSeen);
    }

    [Fact]
    public void A_very_long_message_is_bounded()
    {
        // A tool that returns a megabyte of output must not put a megabyte into every row of a summary.
        var log = new InMemoryToolFailureLog();
        log.Record("bash", "failed", new string('x', 5000), Base);

        Assert.True(Assert.Single(log.Top(1)).LastMessage.Length < 300);
    }

    [Fact]
    public void A_failure_remembers_where_it_happened()
    {
        // The queue log holds the same moment durably, as finishedAt; the item id joins the two.
        var log = new InMemoryToolFailureLog();
        log.Record("read", "file not found", "file not found: a.txt", Base, "in-7");

        Assert.Equal("in-7", Assert.Single(log.Top(1)).ItemId);
    }

    [Fact]
    public void The_newest_source_wins_but_an_older_one_is_not_forgotten()
    {
        var log = new InMemoryToolFailureLog();
        log.Record("read", "missing", "a", Base, "in-1");
        log.Record("read", "missing", "b", Base.AddMinutes(1), "in-2");

        var row = Assert.Single(log.Top(1));
        Assert.Equal(2, row.Count);
        Assert.Equal("in-2", row.ItemId);

        // A repeat that did not say where it happened must not erase where the group last was.
        log.Record("read", "missing", "c", Base.AddMinutes(2));
        Assert.Equal("in-2", Assert.Single(log.Top(1)).ItemId);
    }
}
