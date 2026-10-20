using Partner.Api.Voice;

namespace Partner.Api.Tests;

/// <summary>Speaking a reply while the model is still writing it, and not saying any of it twice.</summary>
public sealed class LiveSpeechTests
{
    [Fact]
    public void Only_complete_sentences_are_taken_from_the_growing_text()
    {
        var live = new LiveSpeech();

        Assert.Null(live.NextChunk("i1", "书房的灯"));
        Assert.Equal("书房的灯已经打开了。", live.NextChunk("i1", "书房的灯已经打开了。窗帘也"));
        Assert.Null(live.NextChunk("i1", "书房的灯已经打开了。窗帘也")); // nothing new and complete
        Assert.Equal("窗帘也拉上了！", live.NextChunk("i1", "书房的灯已经打开了。窗帘也拉上了！还有"));
    }

    [Fact]
    public void A_decimal_point_is_not_a_sentence_end()
    {
        var live = new LiveSpeech();

        Assert.Null(live.NextChunk("i1", "圆周率是 3.14"));
        Assert.Equal("圆周率是 3.14 左右。", live.NextChunk("i1", "圆周率是 3.14 左右。"));
    }

    [Fact]
    public void The_log_event_only_contributes_what_the_live_text_did_not()
    {
        var live = new LiveSpeech();
        live.NextChunk("i1", "第一句。第二句。第三");

        Assert.True(live.InProgress);
        Assert.Equal("第三句。", live.TakeRemainder("第一句。第二句。第三句。"));
        Assert.False(live.InProgress);
    }

    [Fact]
    public void A_step_nobody_streamed_is_spoken_whole_from_its_log_event()
    {
        Assert.Equal("整段。", new LiveSpeech().TakeRemainder("整段。"));
    }

    [Fact]
    public void The_lingering_live_copy_of_a_finished_step_is_not_mistaken_for_a_new_one()
    {
        var live = new LiveSpeech();
        live.NextChunk("i1", "第一句。");
        live.TakeRemainder("第一句。");

        // The stream is only told the step ended after the event is written, so for a moment both exist.
        Assert.Null(live.NextChunk("i1", "第一句。"));

        // The next step of the same item starts from nothing and is spoken from its first sentence.
        Assert.Equal("接着说。", live.NextChunk("i1", "接着说。"));
    }

    [Fact]
    public void A_stale_prefix_of_a_finished_step_is_ignored_too()
    {
        var live = new LiveSpeech();
        live.NextChunk("i1", "第一句。");
        live.TakeRemainder("第一句。第二句。");

        Assert.Null(live.NextChunk("i1", "第一句。"));
    }
}
