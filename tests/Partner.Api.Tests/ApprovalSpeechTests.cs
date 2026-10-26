using Partner.Api.Voice;
using Partner.Core.Agent.Tools;

namespace Partner.Api.Tests;

/// <summary>
/// The spoken approval contract: what is read out, and which utterances count as an answer. The classifier is
/// exact on purpose — an approval is irreversible and must not be won by a substring.
/// </summary>
public sealed class ApprovalSpeechTests
{
    [Theory]
    [InlineData("批准执行", "Approve")]
    [InlineData("批准执行。", "Approve")]
    [InlineData(" 批准 ", "Approve")]
    [InlineData("拒绝执行", "Deny")]
    [InlineData("不允许执行", "Deny")]
    [InlineData("不批准执行", "Deny")]
    [InlineData("不同意", "Deny")]
    [InlineData("取消！", "Deny")]
    public void A_whole_command_is_an_answer(string transcript, string expected)
    {
        Assert.Equal(expected, ApprovalSpeech.Classify(transcript).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("可以")]
    [InlineData("允许执行")]
    [InlineData("同意")]
    [InlineData("请你批准执行")]
    [InlineData("这个允许吗")]
    [InlineData("拒绝执行吧")]
    [InlineData("打开书房的灯")]
    public void Anything_else_belongs_to_the_model(string transcript)
    {
        Assert.Equal(ApprovalIntent.None, ApprovalSpeech.Classify(transcript));
    }

    [Theory]
    [InlineData("小克小克，批准执行", "小克小克", "Approve")]
    [InlineData("小克小克小克小克批准执行", "小克小克", "Approve")]
    [InlineData("小克小克 拒绝执行。", "小克小克", "Deny")]
    [InlineData("小克小克", "小克小克", "None")]
    [InlineData("不要批准执行", "小克小克", "None")]
    public void The_wake_word_that_opened_the_session_is_stripped(string transcript, string wakeWord, string expected)
    {
        Assert.Equal(expected, ApprovalSpeech.Classify(transcript, wakeWord).ToString());
    }

    [Fact]
    public void The_prompt_names_the_action_and_the_two_commands_but_not_the_raw_arguments()
    {
        var request = new PendingApproval(
            "ap-1",
            "desktop_act",
            """{"entityId":"7","secret":"do not read this aloud"}""",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(2),
            "子 agent「桌面操控」：打开网易云放歌");

        var prompt = ApprovalSpeech.Prompt(request);

        Assert.Contains("桌面操作", prompt, StringComparison.Ordinal);
        Assert.Contains("批准执行", prompt, StringComparison.Ordinal);
        Assert.Contains("拒绝执行", prompt, StringComparison.Ordinal);
        Assert.Contains("桌面操控", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_tool_is_named_rather_than_guessed_at()
    {
        Assert.Equal("mystery_tool", ApprovalSpeech.Label("mystery_tool"));
        Assert.Equal("未知操作", ApprovalSpeech.Label(null));
    }

    [Fact]
    public void The_confirmation_says_what_was_decided_and_how_many_remain()
    {
        var request = new PendingApproval(
            "ap-2",
            "schedule_delete",
            """{"id":"nightly"}""",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(2));

        Assert.Contains("已批准：删除定时任务", ApprovalSpeech.Result(request, approved: true, remaining: 0), StringComparison.Ordinal);
        Assert.Contains("已拒绝：删除定时任务", ApprovalSpeech.Result(request, approved: false, remaining: 0), StringComparison.Ordinal);
        Assert.Contains("还有 2 个请求", ApprovalSpeech.Result(request, approved: true, remaining: 2), StringComparison.Ordinal);
    }
}
