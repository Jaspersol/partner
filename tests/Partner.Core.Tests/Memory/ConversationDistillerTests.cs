using Partner.Core.Memory;

namespace Partner.Core.Tests.Memory;

/// <summary>
/// What turns into a memory, and what does not.
/// <para>
/// The distiller is the only automatic writer, so every one of its refusals is a decision about what the store
/// will be full of. It had no direct tests at all until this file, which is why a two-character greeting turned
/// out to be the difference between "the store has memories" and "the store is empty".
/// </para>
/// </summary>
public sealed class ConversationDistillerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static DistilledMemory? Distill(string user, string answer = "已记录，你用的是公司内网账号 alice。") =>
        ConversationDistiller.Distill(user, answer, 7, Now);

    [Theory]
    [InlineData("你好")]           // Two characters — the case that made the store look broken.
    [InlineData("嗯")]
    [InlineData("  ")]
    public async Task A_greeting_too_short_to_carry_anything_is_not_written(string user)
    {
        // A greeting is not a statement about the person, so nothing should be kept. This one is caught by the
        // length gate rather than the chitchat list, which matters because the two lists do not agree.
        Assert.Null(Distill(user));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("谢谢")]
    [InlineData("多谢了")]
    [InlineData("好的")]
    [InlineData("明白了")]
    [InlineData("辛苦了")]
    [InlineData("再见")]
    public async Task A_farewell_is_not_written(string user)
    {
        Assert.Null(Distill(user));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("你是谁")]
    [InlineData("你能做什么")]
    [InlineData("who are you")]
    public async Task A_question_about_the_assistant_itself_is_not_written(string user)
    {
        // The next turn's system prompt already answers it; a memory of it says nothing and costs a slot.
        Assert.Null(Distill(user));
        await Task.CompletedTask;
    }

    [Fact]
    public void A_statement_about_the_person_is_written_with_the_answer_alongside_it()
    {
        var memory = Distill("我用的是公司内网账号 alice");

        Assert.NotNull(memory);
        Assert.Equal(7, memory!.SourceEventSeq);
        Assert.Contains("alice", memory.Body);
    }

    [Fact]
    public void The_longer_answer_wins_and_the_users_words_are_kept_when_they_are_longer()
    {
        var shortAnswer = Distill("我用的是公司内网账号 alice", "好的，知道了。");
        Assert.NotNull(shortAnswer);
        Assert.Contains("alice", shortAnswer!.Body);
        Assert.Contains("好的，知道了。", shortAnswer.Body);
    }

    [Fact]
    public void A_question_with_no_answer_is_not_a_fact()
    {
        // Recording it would fill the store with things the person asked — the opposite of what a memory is for.
        Assert.Null(Distill("数据库怎么连", string.Empty));
        Assert.Null(Distill("数据库怎么连", "   "));
    }

    [Fact]
    public void A_body_is_capped_because_it_has_to_be_re_injected()
    {
        var memory = Distill("我用的是公司内网账号 alice", new string('答', 900));

        Assert.NotNull(memory);
        Assert.True(memory!.Body.Length <= ConversationDistiller.MaxBodyLength + 1);
        Assert.EndsWith("…", memory.Body);
    }

    [Fact]
    public void Stated_preferences_are_worth_more_than_a_one_off_answer()
    {
        // Everything here is read off the wording. Nothing guesses importance from length, which is why a long
        // one-off answer and a short stated preference do not trade places.
        var preference = Distill("我喜欢用深色模式")!.Importance;
        var oneOff = Distill("今天下午三点的会议改到四点了")!.Importance;

        Assert.True(preference > oneOff, $"preference {preference} should outrank one-off {oneOff}");
        Assert.InRange(preference, 0.0, 1.0);
        Assert.InRange(oneOff, 0.0, 1.0);
    }
}