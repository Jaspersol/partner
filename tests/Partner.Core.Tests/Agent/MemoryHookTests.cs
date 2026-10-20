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
    public async Task A_sub_agents_report_is_not_filed_as_something_the_person_said()
    {
        // The same words, the same exchange: from a person they become a memory, from a sub-agent's report they
        // must not — recall would later serve "用户喜欢深色主题" back as a fact about someone who never said it.
        var (session, memory) = Open();
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Agent,
            Body = "我喜欢用深色主题",
        });
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Assistant,
            Source = InputSource.Agent,
            Body = "好，已经切到深色",
        });

        var context = new HookContext
        {
            SessionId = "main",
            Source = InputSource.Agent,
            InputPayload = "我喜欢用深色主题",
            WorkspaceDirectory = ".",
        };
        await new MemoryDistilHook(memory, session).RunAsync(context, default);

        Assert.Empty(await memory.ListAsync());
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
    public async Task A_tool_only_turn_is_remembered_from_a_successful_act()
    {
        // Desktop answers in tool results, not prose; without this path the whole day's desktop work left no
        // memory at all — the model had "said" nothing the distiller recognised.
        var (session, memory) = Open();
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "播放器里点一下播放",
        });

        const string actResult = "LeftClick · 注入成功 · 校验：画面已变化 · 目标 '播放' [button] @ 播放器";
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.ToolResult,
            Source = InputSource.Text,
            Body = actResult,
            Meta = new Dictionary<string, string> { ["name"] = "desktop_act", ["ok"] = "true" },
        });

        await new MemoryDistilHook(memory, session).RunAsync(Context("播放器里点一下播放"), default);

        var record = Assert.Single(await memory.ListAsync());
        Assert.Contains("播放", record.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_background_act_is_remembered_like_any_other_successful_act()
    {
        // A click carried by a page's debug protocol reads "已在后台执行"; if the memory filter only knew the
        // older phrases, a day of background work would leave nothing behind.
        var (session, memory) = Open();
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "播放器里点一下播放",
        });

        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.ToolResult,
            Source = InputSource.Text,
            Body = "LeftClick · 已在后台执行（没有动你的前台窗口和指针） · 校验：画面已变化 · 目标 '播放' [button] @ 播放器",
            Meta = new Dictionary<string, string> { ["name"] = "desktop_act", ["ok"] = "true" },
        });

        await new MemoryDistilHook(memory, session).RunAsync(Context("播放器里点一下播放"), default);

        Assert.Single(await memory.ListAsync());
    }

    [Fact]
    public async Task A_all_failed_tool_turn_leaves_no_memory()
    {
        // Only executed-and-verified act results count: a refusal or an unverifiable click is not a fact the
        // store can vouch for, and a failed day of clicking is not worth remembering.
        var (session, memory) = Open();
        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.User,
            Source = InputSource.Text,
            Body = "删掉那个文件夹",
        });

        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.ToolResult,
            Source = InputSource.Text,
            Body = "未注入：approval denied · 目标 '确认' [button] @ 对话框",
            Meta = new Dictionary<string, string> { ["name"] = "desktop_act", ["ok"] = "false" },
        });

        var ctx = Context("删掉那个文件夹");
        await new MemoryDistilHook(memory, session).RunAsync(ctx, default);

        Assert.Empty(await memory.ListAsync());

        // Nothing was distilled, so a turn that later succeeds must still be able to record the same seq.
        var second = await memory.FindBySourceAsync(1);
        Assert.Null(second);
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
    public async Task A_memory_from_a_turn_the_prompt_still_carries_is_not_injected()
    {
        // Memory is written at the end of every turn, so the exchange right above is already in the store when
        // the next prompt is built. Recalling it would send the same statement twice and label the second copy
        // "from an earlier session" — when it is the turn directly above.
        var (session, memory) = Open();
        await ExchangeAsync(session, "我不太喜欢被打断", "明白，以后不打断你");
        await new MemoryDistilHook(memory, session).RunAsync(Context("我不太喜欢被打断"), default);
        Assert.Single(await memory.ListAsync());

        var ctx = Context("我不太喜欢被打断");
        await new MemoryRecallHook(memory, session).RunAsync(ctx, default);

        Assert.Empty(ctx.InjectedPrompts);
    }

    [Fact]
    public async Task A_memory_whose_turn_left_the_window_comes_back()
    {
        // The other half of the rule: exclusion follows the prompt, not the clock. Once compaction retracts the
        // turn, the conversation no longer carries it — and that is the moment the memory is the only copy.
        var (session, memory) = Open();
        await ExchangeAsync(session, "我不太喜欢被打断", "明白，以后不打断你");
        await new MemoryDistilHook(memory, session).RunAsync(Context("我不太喜欢被打断"), default);

        await session.AppendAsync(new SessionEvent
        {
            Kind = SessionEventKind.Retract,
            Source = InputSource.Text,
            Body = "summary of the first exchange",
            Meta = SessionRetraction.Between(1, 2),
        });

        var ctx = Context("我不太喜欢被打断");
        await new MemoryRecallHook(memory, session).RunAsync(ctx, default);

        Assert.Contains("打断", string.Join('\n', ctx.InjectedPrompts), StringComparison.Ordinal);
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
    public async Task Injection_stops_at_the_budget_and_says_so()
    {
        // Long bodies used to go into the prompt unbounded: recall took its fixed limit and injected every
        // hit in full, so one giant note could crowd out the conversation itself. The hook now runs through
        // the store's injection budget and marks the cut.
        // A tight budget store of its own: the default 5000-token budget would need ten of these bodies (about 510 tokens each) to
        // flinch, and the test wants the cut, not the count.
        var (session, _) = JsonlSessionStore.Open(_dir);
        _session = session;
        _memory = new MemoryStore(Path.Combine(_dir, "tight.db"), new MemoryStoreOptions { InjectionTokenLimit = 1000 });
        var memory = _memory;

        for (var i = 0; i < 4; i++)
        {
            await memory.WriteAsync(new MemoryRecord
            {
                RecordId = $"big-{i}",
                Tier = MemoryTier.Semantic,
                Scope = MemoryScope.Recall,
                Body = "细节" + new string('情', 500) + $" 第 {i} 条",
            });
        }

        var ctx = Context("细节");
        await new MemoryRecallHook(memory).RunAsync(ctx, default);

        var injected = string.Join('\n', ctx.InjectedPrompts);
        Assert.Contains("token budget", injected, StringComparison.Ordinal);
        Assert.NotEmpty(ctx.InjectedPrompts);
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

    [Fact]
    public async Task Deleting_a_long_span_reads_it_in_one_query()
    {
        // Deleting a conversation walks its seq range. One query per seq made the cost of removing a long thread
        // grow with its length — a thousand queries for a thousand events — and the walk was justified by the
        // column having no index. It has one now, so the span is a single indexed range read.
        var (_, memory) = Open();
        await memory.WriteBatchAsync(new[]
        {
            new MemoryRecord
            {
                RecordId = "seq-100", Tier = MemoryTier.Semantic, Scope = MemoryScope.Recall,
                Body = "第一条", SourceEventSeq = 100,
            },
            new MemoryRecord
            {
                RecordId = "seq-500", Tier = MemoryTier.Semantic, Scope = MemoryScope.Recall,
                Body = "第二条", SourceEventSeq = 500,
            },
            new MemoryRecord
            {
                RecordId = "seq-900", Tier = MemoryTier.Semantic, Scope = MemoryScope.Recall,
                Body = "第三条", SourceEventSeq = 900,
            },
        });

        var removed = await MemoryDeletion.CascadeAsync(memory, 1, 1000);

        Assert.Equal(3, removed);
        Assert.Equal(1, memory.SourceQueries);
        Assert.Empty(await memory.ListAsync());
    }
}
