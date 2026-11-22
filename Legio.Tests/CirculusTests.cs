using Legio.Core.Armarium;
using Legio.Protocol;
using Xunit;

namespace Legio.Tests;

public class CirculusTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-8)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(100)]
    public void Constructor_RejectsInvalidCapacities(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Circulus(capacity));
    }

    [Fact]
    public void SingleThread_WriteRead_And_FullBoundary()
    {
        var ring = new Circulus(4);

        for (int i = 0; i < 4; i++)
        {
            var t = new Tessera { ItemCount = i + 1, CohortId = i, DurationTicks = (i + 1) * 100 };
            Assert.True(ring.TryWrite(in t));
        }

        // 5th write must fail cleanly (buffer full)
        var extra = new Tessera { ItemCount = 999 };
        Assert.False(ring.TryWrite(in extra));

        for (int i = 0; i < 4; i++)
        {
            Assert.True(ring.TryRead(out Tessera read));
            Assert.Equal(i + 1, read.ItemCount);
            Assert.Equal(i, read.CohortId);
            Assert.Equal((i + 1) * 100, read.DurationTicks);
        }

        Assert.False(ring.TryRead(out _));
    }

    [Fact]
    public void MultiProducer_StressTest_PreservesPayloadIntegrityWithoutCorruptedReads()
    {
        const int producerCount = 8;
        const int itemsPerProducer = 15_000;
        const int totalExpected = producerCount * itemsPerProducer;

        // Large enough ring + active consumer prevents drops while stressing Vyukov CAS & barriers.
        var ring = new Circulus(8192);
        var seen = new int[totalExpected];
        long readCount = 0;
        long writtenCount = 0;
        bool producersDone = false;

        var consumer = new Thread(() =>
        {
            while (!Volatile.Read(ref producersDone) || ring.TryRead(out _))
            {
                while (ring.TryRead(out Tessera t))
                {
                    // Verify payload fields were never torn or partially written
                    Assert.Equal(t.ItemCount * 17L, t.DurationTicks);
                    Assert.Equal(t.ItemCount % producerCount, t.CenturioId);

                    int idx = t.ItemCount;
                    Assert.InRange(idx, 0, totalExpected - 1);
                    seen[idx]++;
                    readCount++;
                }
            }
        });
        consumer.Start();

        var startBarrier = new Barrier(producerCount);
        var producers = new Thread[producerCount];

        for (int p = 0; p < producerCount; p++)
        {
            int producerId = p;
            producers[p] = new Thread(() =>
            {
                startBarrier.SignalAndWait();
                for (int i = 0; i < itemsPerProducer; i++)
                {
                    int uniqueId = i * producerCount + producerId;
                    var t = new Tessera
                    {
                        ItemCount = uniqueId,
                        DurationTicks = uniqueId * 17L,
                        CenturioId = producerId,
                        CohortId = producerId
                    };

                    // Spin if momentarily full so we can verify exact 1-to-1 delivery under high contention
                    var spin = new SpinWait();
                    while (!ring.TryWrite(in t))
                    {
                        spin.SpinOnce();
                    }

                    Interlocked.Increment(ref writtenCount);
                }
            });
            producers[p].Start();
        }

        foreach (var t in producers) t.Join();
        Volatile.Write(ref producersDone, true);
        consumer.Join();

        Assert.Equal(totalExpected, writtenCount);
        Assert.Equal(totalExpected, readCount);
        for (int i = 0; i < totalExpected; i++)
        {
            Assert.Equal(1, seen[i]);
        }
    }
}
