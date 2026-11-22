using Legio.Core.Executio;
using Xunit;

namespace Legio.Tests;

public class OptioTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(1)]
    [InlineData(5)]
    public void Constructor_RejectsInvalidCapacities(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Optio(capacity));
    }

    [Fact]
    public void SingleThread_LifoPop_And_FifoSteal_And_FullGuard()
    {
        var optio = new Optio(4);

        for (int i = 0; i < 4; i++)
        {
            var u = new TaskUnit { CohortSlot = 0, Start = i, Length = 10 };
            Assert.True(optio.TryPush(in u));
        }

        // Full guard must refuse 5th push instead of overwriting slot 0
        var overflow = new TaskUnit { CohortSlot = 0, Start = 999, Length = 10 };
        Assert.False(optio.TryPush(in overflow));

        // FIFO steal takes oldest (Start = 0)
        Assert.Equal(StealResult.Success, optio.TrySteal(out TaskUnit stolen));
        Assert.Equal(0, stolen.Start);

        // LIFO pop takes newest (Start = 3, then 2, then 1)
        Assert.True(optio.TryPop(out TaskUnit p1));
        Assert.Equal(3, p1.Start);

        Assert.True(optio.TryPop(out TaskUnit p2));
        Assert.Equal(2, p2.Start);

        Assert.True(optio.TryPop(out TaskUnit p3));
        Assert.Equal(1, p3.Start);

        Assert.False(optio.TryPop(out _));
        Assert.Equal(StealResult.Empty, optio.TrySteal(out _));
    }

    [Fact]
    public void ChaseLev_StressTest_EveryTaskExecutedExactlyOnce()
    {
        const int totalTasks = 40_000;
        const int thiefCount = 7;

        var optio = new Optio(1024);
        var executed = new int[totalTasks];
        long stolenTotal = 0;
        long poppedTotal = 0;
        long contendedTotal = 0;
        bool ownerDone = false;

        var startBarrier = new Barrier(thiefCount + 1);
        var thieves = new Thread[thiefCount];

        for (int t = 0; t < thiefCount; t++)
        {
            thieves[t] = new Thread(() =>
            {
                startBarrier.SignalAndWait();
                while (!Volatile.Read(ref ownerDone) || optio.HasWork)
                {
                    StealResult res = optio.TrySteal(out TaskUnit task);
                    if (res == StealResult.Success)
                    {
                        Interlocked.Increment(ref executed[task.Start]);
                        Interlocked.Increment(ref stolenTotal);
                    }
                    else if (res == StealResult.Contended)
                    {
                        Interlocked.Increment(ref contendedTotal);
                    }
                    else
                    {
                        Thread.SpinWait(2);
                    }
                }
            });
            thieves[t].Start();
        }

        // Owner pushes bursts and pops locally while 7 thieves steal concurrently
        startBarrier.SignalAndWait();
        int pushed = 0;
        while (pushed < totalTasks)
        {
            int burst = Math.Min(32, totalTasks - pushed);
            for (int b = 0; b < burst; b++)
            {
                var unit = new TaskUnit { CohortSlot = 0, Start = pushed, Length = 1 };
                if (optio.TryPush(in unit))
                {
                    pushed++;
                }
                else
                {
                    // Full: execute inline just like Centurio does
                    Interlocked.Increment(ref executed[unit.Start]);
                    Interlocked.Increment(ref poppedTotal);
                    pushed++;
                }
            }

            // Owner also pops half the burst locally (LIFO)
            for (int p = 0; p < burst / 2; p++)
            {
                if (optio.TryPop(out TaskUnit task))
                {
                    Interlocked.Increment(ref executed[task.Start]);
                    Interlocked.Increment(ref poppedTotal);
                }
            }
        }

        // Owner drains any remaining local tasks
        while (optio.TryPop(out TaskUnit task))
        {
            Interlocked.Increment(ref executed[task.Start]);
            Interlocked.Increment(ref poppedTotal);
        }

        Volatile.Write(ref ownerDone, true);
        foreach (var thief in thieves) thief.Join();

        Assert.True(stolenTotal > 0, "Expected thieves to steal tasks during high-contention stress test.");
        Assert.Equal(totalTasks, stolenTotal + poppedTotal);

        for (int i = 0; i < totalTasks; i++)
        {
            Assert.Equal(1, executed[i]);
        }
    }
}
