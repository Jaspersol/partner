using Partner.Voice.Audio;
using Partner.Voice.Playback;
using Partner.Voice.Settings;
using Partner.Voice.Tts;

namespace Partner.Voice.Tests;

/// <summary>
/// 播放顺序与「先合成完再出声」（设计文档 §2.8.11）。
/// <para>
/// 真机症状：一回合里第 1 句刚出声，第 2 句才开始合成，于是句间插进一段随文本变长的静音。
/// 根因是闩锁只在 sink 构造时上过一次，而 sink 跨回合复用——第 2 个回合起没有任何东西挡住
/// 边合成边播。这组用例断言的是「每一句的音频都到达得比 Release 早」，也就是闩锁每回合都在。
/// </para>
/// </summary>
public sealed class VoiceOutputHalfTests
{
    private static VoiceOutputHalf Half(IAudioSink sink, ITtsClient tts, out PlaybackGate gate)
    {
        gate = new PlaybackGate();
        return new VoiceOutputHalf(
            new VoiceOutputSetting { Enabled = true, MuteDuringTts = false, DrainMs = 0 },
            sink,
            tts,
            gate);
    }

    [Fact]
    public async Task Every_sentence_is_synthesised_before_any_of_them_plays()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = Half(sink, tts, out _);

        output.OnText("第一句话。第二句话。第三句话。", sessionEventSeq: 1);
        await output.FlushTurnAsync();

        Assert.Equal(new[] { "第一句话。", "第二句话。", "第三句话。" }, tts.Requests);
        Assert.Equal(3, sink.EnqueuedSegments);

        // 一声都没漏出去：全部到达时闩还上着，Release 在最后。
        Assert.Equal(0, sink.EnqueuedWhileFree);
        Assert.Equal(1, sink.Releases);
    }

    [Fact]
    public async Task A_second_turn_on_the_reused_sink_is_still_buffered()
    {
        var sink = new RecordingSink();
        var tts = new ScriptedTts();
        using var output = Half(sink, tts, out _);

        output.OnText("第一回合的第一句。第一回合的第二句。", sessionEventSeq: 1);
        await output.FlushTurnAsync();

        var freeAfterFirstTurn = sink.EnqueuedWhileFree;
        var releasesAfterFirstTurn = sink.Releases;

        output.OnText("第二回合的第一句。第二回合的第二句。", sessionEventSeq: 2);
        await output.FlushTurnAsync();

        // 第二个回合同样一个字都不许在 Release 前出声——这正是真机上从第二回合开始出现的那段静音。
        Assert.Equal(freeAfterFirstTurn, sink.EnqueuedWhileFree);
        Assert.Equal(releasesAfterFirstTurn + 1, sink.Releases);
        Assert.Equal(
            new[] { "第一回合的第一句。", "第一回合的第二句。", "第二回合的第一句。", "第二回合的第二句。" },
            tts.Requests);
    }

    /// <summary>逐句记下「到达时闩着没有」，同时数 Release 次数。</summary>
    private sealed class RecordingSink : IAudioSink
    {
        private bool _held;

        public int SampleRate => 24_000;

        public string DeviceName => "recording";

        public double DrainMs { get; set; } = 400;

        public int EnqueuedSegments { get; private set; }

        public int EnqueuedWhileFree { get; private set; }

        public int Releases { get; private set; }

        public bool IsPlaying { get; private set; }

        public bool HasPending { get; private set; }

        public void Start()
        {
        }

        public void Enqueue(float[] pcm, int sampleRate, bool final = false)
        {
            ArgumentNullException.ThrowIfNull(pcm);

            // 终止标记（空数组）不算一段音频：每句末尾都有一个。
            if (pcm.Length > 0)
            {
                EnqueuedSegments++;
            }

            if (!_held)
            {
                EnqueuedWhileFree++;
                IsPlaying = true;
            }

            HasPending = !final;
        }

        public void Hold() => _held = true;

        public void Release()
        {
            _held = false;
            Releases++;
        }

        public void Stop()
        {
            IsPlaying = false;
            HasPending = false;
        }

        public void Dispose() => Stop();
    }

    /// <summary>记下每句的合成请求，按顺序返回一小段 PCM。</summary>
    private sealed class ScriptedTts : ITtsClient
    {
        public List<string> Requests { get; } = new();

        public async IAsyncEnumerable<float[]> SynthesizeAsync(
            string text,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(text);
            yield return new float[240];
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }
}
