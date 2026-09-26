using Partner.Core.Agent.Llm;
using Partner.Core.Memory;
using Partner.Core.Agent.Tools;

namespace Partner.Core.Tests.Memory;

/// <summary>
/// Counting tokens for a budget. The local estimate is deliberately crude, so the design leans on a
/// provider-backed count where one exists and never trusts the optimistic side of a disagreement.
/// </summary>
public sealed class TokenCounterTests
{
    [Fact]
    public async Task A_counter_without_a_native_implementation_answers_from_the_local_estimate()
    {
        ITokenCounter counter = new ApproximateTokenCounter();

        // The default interface method: implementing Count is enough, CountAsync follows from it.
        Assert.Equal(counter.Count("12345678"), await counter.CountAsync("12345678"));
    }

    [Fact]
    public void The_local_estimate_rounds_up_so_non_empty_text_never_costs_nothing()
    {
        var counter = new ApproximateTokenCounter();

        Assert.Equal(0, counter.Count(string.Empty));
        Assert.Equal(1, counter.Count("a"));
        Assert.Equal(1, counter.Count("abcd"));
        Assert.Equal(2, counter.Count("abcde"));
    }

    [Fact]
    public async Task The_dual_arm_takes_the_larger_estimate()
    {
        var counter = new DualArmTokenCounter(new FixedCounter(3), new FixedCounter(11));

        // Never the optimistic side: an undercount is what overflows a window.
        Assert.Equal(11, counter.Count("anything"));
        Assert.Equal(11, await counter.CountAsync("anything"));
    }

    [Fact]
    public async Task The_dual_arm_prefers_the_provider_when_it_is_the_larger_one()
    {
        var counter = new DualArmTokenCounter(new FixedCounter(40), new FixedCounter(11));

        Assert.Equal(40, counter.Count("anything"));
        Assert.Equal(40, await counter.CountAsync("anything"));
    }

    [Fact]
    public void The_cache_answers_repeats_and_evicts_the_oldest()
    {
        var cache = new TokenCountCache(capacity: 2);

        cache.Set("one", 1);
        cache.Set("two", 2);

        Assert.True(cache.TryGet("one", out var first));
        Assert.Equal(1, first);

        cache.Set("three", 3);

        // FIFO: the first entry is gone, the newer two are not.
        Assert.False(cache.TryGet("one", out _));
        Assert.True(cache.TryGet("two", out _));
        Assert.True(cache.TryGet("three", out _));
        Assert.Equal(2, cache.Size);
    }

    [Fact]
    public void The_cache_does_not_confuse_different_texts()
    {
        var cache = new TokenCountCache();

        cache.Set("a", 1);
        cache.Set("b", 2);

        Assert.True(cache.TryGet("a", out var a));
        Assert.True(cache.TryGet("b", out var b));
        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public void The_cache_counts_hits_and_misses_for_diagnostics()
    {
        var cache = new TokenCountCache();
        cache.Set("known", 5);

        cache.TryGet("known", out _);
        cache.TryGet("unknown", out _);

        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    private sealed class FixedCounter(int value) : ITokenCounter
    {
        public int Count(string text) => value;
    }
}
