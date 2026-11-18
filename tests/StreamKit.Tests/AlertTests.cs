using StreamKit.Core.Alerts;
using StreamKit.Core.Events;
using StreamKit.Core.Options;
using Xunit;

namespace StreamKit.Tests;

public class AlertQueueTests
{
    private static AlertItem Item(int priority, long sequence, string? id = null) => new()
    {
        Id = id ?? AlertItem.NewId(),
        Kind = AlertKind.Custom,
        Priority = priority,
        Text = "x",
        DurationMs = 1000,
        Sequence = sequence,
    };

    [Fact]
    public void DequeuesHighestPriorityFirst()
    {
        var queue = new AlertQueue(10);
        queue.TryEnqueue(Item(AlertPriority.Like, 1));
        queue.TryEnqueue(Item(AlertPriority.Gift, 2));
        queue.TryEnqueue(Item(AlertPriority.Follow, 3));

        Assert.Equal(AlertPriority.Gift, queue.Dequeue()!.Priority);
        Assert.Equal(AlertPriority.Follow, queue.Dequeue()!.Priority);
        Assert.Equal(AlertPriority.Like, queue.Dequeue()!.Priority);
        Assert.Null(queue.Dequeue());
    }

    [Fact]
    public void PreservesFifoWithinSamePriority()
    {
        var queue = new AlertQueue(10);
        queue.TryEnqueue(Item(10, 1, "a"));
        queue.TryEnqueue(Item(10, 2, "b"));

        Assert.Equal("a", queue.Dequeue()!.Id);
        Assert.Equal("b", queue.Dequeue()!.Id);
    }

    [Fact]
    public void ReplacesWorstItemWhenFullAndIncomingIsBetter()
    {
        var queue = new AlertQueue(2);
        queue.TryEnqueue(Item(10, 1));
        queue.TryEnqueue(Item(20, 2));

        Assert.True(queue.TryEnqueue(Item(30, 3)));
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.Dropped);
        Assert.Equal(30, queue.Dequeue()!.Priority);
    }

    [Fact]
    public void RejectsWorseItemWhenFull()
    {
        var queue = new AlertQueue(2);
        queue.TryEnqueue(Item(30, 1));
        queue.TryEnqueue(Item(20, 2));

        Assert.False(queue.TryEnqueue(Item(5, 3)));
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.Dropped);
    }

    [Fact]
    public void PeekDoesNotRemove()
    {
        var queue = new AlertQueue(4);
        queue.TryEnqueue(Item(10, 1, "only"));

        Assert.Equal("only", queue.Peek()!.Id);
        Assert.Equal(1, queue.Count);
    }
}

public class AlertCoalescerTests
{
    private static AlertItem Like(string user, int count, long seq) => new()
    {
        Id = AlertItem.NewId(),
        Kind = AlertKind.Like,
        Priority = AlertPriority.Like,
        Text = $"{user} mandó {count} likes",
        DurationMs = 2000,
        Count = count,
        MergeKey = $"like:{user}",
        Sequence = seq,
        Data = { ["user"] = user },
    };

    [Fact]
    public void ItemsWithoutMergeKeyAreImmediate()
    {
        var coalescer = new AlertCoalescer(new FakeClock(), new StreamKitOptions());
        var item = new AlertItem
        {
            Id = "x",
            Kind = AlertKind.Follow,
            Priority = AlertPriority.Follow,
            Text = "nuevo seguidor",
            DurationMs = 3000,
        };

        var ready = coalescer.Add(item);

        Assert.Single(ready);
        Assert.Same(item, ready[0]);
    }

    [Fact]
    public void MergesBurstAndFlushesAfterWindow()
    {
        var clock = new FakeClock();
        var options = new StreamKitOptions();
        options.Alerts.LikeCoalesceMs = 3000;
        var coalescer = new AlertCoalescer(clock, options);

        Assert.Empty(coalescer.Add(Like("ana", 1, 1)));
        clock.AdvanceSeconds(1);
        Assert.Empty(coalescer.Add(Like("ana", 2, 2)));
        Assert.Equal(1, coalescer.PendingCount);

        Assert.Empty(coalescer.FlushDue());

        clock.AdvanceSeconds(3);
        var ready = coalescer.FlushDue();

        Assert.Single(ready);
        Assert.Equal(3, ready[0].Count);
        Assert.Equal(0, coalescer.PendingCount);
    }

    [Fact]
    public void DifferentUsersDoNotMerge()
    {
        var clock = new FakeClock();
        var options = new StreamKitOptions();
        var coalescer = new AlertCoalescer(clock, options);

        coalescer.Add(Like("ana", 1, 1));
        coalescer.Add(Like("beto", 1, 2));

        Assert.Equal(2, coalescer.PendingCount);
    }
}

public class GiftStreakAggregatorTests
{
    private static readonly ViewerRef Ana = new("100", "ana", "Ana");

    private static GiftEvent Gift(int count, bool finished, int diamonds = 5) => new(
        DateTimeOffset.UtcNow,
        Ana,
        "5655",
        "Rosa",
        count,
        diamonds,
        finished);

    [Fact]
    public void AggregatesStreakIntoOneEvent()
    {
        var clock = new FakeClock();
        var aggregator = new GiftStreakAggregator(clock);

        Assert.Null(aggregator.Add(Gift(1, false)));
        Assert.Null(aggregator.Add(Gift(1, false)));
        var final = aggregator.Add(Gift(2, true));

        Assert.NotNull(final);
        Assert.Equal(4, final!.Count);
        Assert.Equal(5, final.Diamonds);
        Assert.True(final.StreakFinished);
        Assert.Equal(0, aggregator.PendingCount);
    }

    [Fact]
    public void SingleFinishedGiftPassesThrough()
    {
        var aggregator = new GiftStreakAggregator(new FakeClock());
        var gift = Gift(1, true);

        Assert.Same(gift, aggregator.Add(gift));
    }

    [Fact]
    public void FlushIdleEmitsLostStreaks()
    {
        var clock = new FakeClock();
        var aggregator = new GiftStreakAggregator(clock);
        aggregator.Add(Gift(3, false));

        Assert.Empty(aggregator.FlushIdle(TimeSpan.FromSeconds(2)));

        clock.AdvanceSeconds(3);
        var ready = aggregator.FlushIdle(TimeSpan.FromSeconds(2));

        Assert.Single(ready);
        Assert.Equal(3, ready[0].Count);
        Assert.Equal(0, aggregator.PendingCount);
    }
}
