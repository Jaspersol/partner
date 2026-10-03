using Partner.Core.Agent.Hooks;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent;

/// <summary>
/// The memory wire, end to end at the Core level: what a turn recalls, and what it leaves behind.
/// <para>
/// These exist because the failure this fixes is invisible — a store that stays empty looks exactly like a
/// store nobody has used. Nothing errors; the memory view is simply blank forever.
/// </para>
/// </summary>
public sealed class MemoryHookTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "partner-memory-hook-tests", Guid.NewGuid().ToString("n"));

    private JsonlSessionStore? _session;
    private MemoryStore? _memory;

    public void Dispose()
    {
        _memory?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private (JsonlSessionStore Session, MemoryStore Memory) Open()
    {
        var (session, _) = JsonlSessionStore.Open(_dir);
        _session = session;
        _memory = new MemoryStore(Path.Combine(_dir, "memory.db"));
        return (session, _memory);
    }

    private static HookContext Context(string payload) => new()
    {
        SessionId = "main",
        Source = InputSource.Text,
        InputPayload = payload,
        WorkspaceDirectory = ".",
    };

    private static async Task ExchangeAsync(JsonlSessionStore session, string user, string assistant)
    {
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = user,
            Meta = new Dictionary<string, string> { ["itemId"] = "item-1" },
        });

        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Text,
            Body = assistant,
            Meta = new Dictionary<string, string> { ["itemId"] = "item-1" },
        });
    }

    [Fact]
    public async Task A_finished_exchange_leaves_something_behind()
    {
        var (session, memory) = Open();
        await ExchangeAsync(session, "我喜欢用深色主题", "好，已经切到深色");

        await new MemoryDistilHook(memory, session).RunAsync(Context("我喜欢用深色主题"), default);

        // Before this existed, this assertion is what failed: the store was empty and nothing said why.
        var records = await memory.ListAsync();
        Assert.Single(records);
        Assert.Contains("深色", records[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_record_points_at_the_event_it_came_from()
    {
        var (session, memory) = Open();
        await ExchangeAsync(session, "我在写一个叫 partner 的项目", "好");

        await new MemoryDistilHook(memory, session).RunAsync(Context("我在写一个叫 partner 的项目"), default);

        // The link is what makes deletion able to reach a memory later, and what stops a retry writing it twice.
        var record = Assert.Single(await memory.ListAsync());
        Assert.Equal(1, record.SourceEventSeq);
    }

    [Fact]
    public async Task A_repeated_run_of_the_same_turn_does_not_double_count_it()
    {
        // A turn retried after a crash would otherwise record the same fact twice, and recall would return it
        // as two independent pieces of evidence — the one place a duplicate is load-bearing on being wrong.
        var (session, memory) = Open();
        await ExchangeAsync(session, "我的部署脚本在 scripts 目录", "知道了");
        var hook = new MemoryDistilHook(memory, session);

        await hook.RunAsync(Context("我的部署脚本在 scripts 目录"), default);
        await hook.RunAsync(Context("我的部署脚本在 scripts 目录"), default);

        Assert.Single(await memory.ListAsync());
    }

    [Theory]
    [InlineData("谢谢", "不客气")]
    [InlineData("好的", "嗯")]
    [InlineData("你是谁", "我是 Partner，一个本地助手")]
    [InlineData("你能做什么", "我可以读写文件、执行命令")]
    public async Task Turns_that_carry_no_fact_about_the_person_are_not_remembered(string user, string assistant)
    {
        // Acknowledgements and questions about the assistant would otherwise fill the store with things the
        // person never claimed, and a memory store nobody reads is the same as no memory store.
        var (session, memory) = Open();
        await ExchangeAsync(session, user, assistant);

        await new MemoryDistilHook(memory, session).RunAsync(Context(user), default);

        Assert.Empty(await memory.ListAsync());
    }

    [Fact]
    public async Task A_question_with_no_answer_is_not_a_fact()
    {
        var (session, memory) = Open();
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "我的数据库密码是什么",
            Meta = new Dictionary<string, string> { ["itemId"] = "item-1" },
        });

        await new MemoryDistilHook(memory, session).RunAsync(Context("我的数据库密码是什么"), default);

        // Recording the question would store the very thing a person asked us not to keep.
        Assert.Empty(await memory.ListAsync());
    }

    [Fact]
    public async Task What_was_remembered_comes_back_into_the_next_turn()
    {
        var (session, memory) = Open();
        await memory.WriteAsync(new MemoryRecord
        {
            RecordId = "m-1",
            Tier = MemoryTier.Semantic,
            Scope = MemoryScope.Recall,
            Body = "用户不喜欢被反复确认",
            Importance = 0.9,
        });

        var ctx = Context("我不太喜欢被打断");
        await new MemoryRecallHook(memory).RunAsync(ctx, default);

        // The whole point of storing it: the next prompt already knows, without being told again.
        Assert.NotEmpty(ctx.InjectedPrompts);
        Assert.Contains("不喜欢被反复确认", string.Join('\n', ctx.InjectedPrompts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_injected_when_nothing_is_remembered()
    {
        var (session, memory) = Open();
        var ctx = Context("今天天气不错");

        await new MemoryRecallHook(memory).RunAsync(ctx, default);

        // Injecting an empty header would put a heading in the prompt with nothing under it, every turn.
        Assert.Empty(ctx.InjectedPrompts);
    }

    [Fact]
    public async Task Deleting_an_event_deletes_what_was_remembered_from_it()
    {
        // This is the leak the cascade exists to close: the person asked for it gone, and it would otherwise be
        // recalled — and injected — into a conversation weeks later where they never said it.
        var (session, memory) = Open();
        await ExchangeAsync(session, "我用的是公司内网账号 alice", "明白了");
        await new MemoryDistilHook(memory, session).RunAsync(Context("我用的是公司内网账号 alice"), default);
        Assert.Single(await memory.ListAsync());

        var removed = await MemoryDeletion.CascadeAsync(memory, 1, 1);

        Assert.Equal(1, removed);
        Assert.Empty(await memory.ListAsync());
    }

    [Fact]
    public async Task Deleting_an_unrelated_event_leaves_the_memory_alone()
    {
        // The link is exact: a record goes only when the event it came from went.
        var (session, memory) = Open();
        await ExchangeAsync(session, "我喜欢用深色主题", "好");
        await new MemoryDistilHook(memory, session).RunAsync(Context("我喜欢用深色主题"), default);

        var removed = await MemoryDeletion.CascadeAsync(memory, 900, 950);

        Assert.Equal(0, removed);
        Assert.Single(await memory.ListAsync());
    }
}
