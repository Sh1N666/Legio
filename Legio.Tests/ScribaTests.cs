using Legio.Core.Augurium;
using Legio.Protocol;
using Xunit;

namespace Legio.Tests;

public class ScribaTests
{
    [Fact]
    public void PerCohortSlots_AreIsolated()
    {
        var scriba = new Scriba(maxCohorts: 8, defaultBatchSize: 512, defaultThreadCount: 4);

        var s0 = new Strategia { BatchSize = 64, ThreadCount = 2, Confidence = 0.9f, AffinityHint = 1 };
        var s3 = new Strategia { BatchSize = 4096, ThreadCount = 8, Confidence = 0.6f, AffinityHint = 0 };

        scriba.Scribere(0, in s0);
        scriba.Scribere(3, in s3);

        var r0 = scriba.Legere(0);
        var r1 = scriba.Legere(1);
        var r3 = scriba.Legere(3);

        Assert.Equal(64, r0.BatchSize);
        Assert.Equal(2, r0.ThreadCount);
        Assert.Equal(512, r1.BatchSize);
        Assert.Equal(4, r1.ThreadCount);
        Assert.Equal(4096, r3.BatchSize);
        Assert.Equal(8, r3.ThreadCount);
    }

    [Fact]
    public void SeqLock_StressTest_NeverProducesTornReads()
    {
        var scriba = new Scriba(maxCohorts: 4, defaultBatchSize: 1, defaultThreadCount: 1);
        var initial = new Strategia { BatchSize = 1, ThreadCount = 1, Confidence = 1f, AffinityHint = 1 };
        scriba.Scribere(0, in initial);

        const int readerCount = 6;
        const int writeIterations = 100_000;
        bool writerDone = false;
        long tornCount = 0;

        var startBarrier = new Barrier(readerCount + 1);
        var readers = new Thread[readerCount];

        for (int r = 0; r < readerCount; r++)
        {
            readers[r] = new Thread(() =>
            {
                startBarrier.SignalAndWait();
                while (!Volatile.Read(ref writerDone))
                {
                    Strategia s = scriba.Legere(0);
                    // Invariant: all 4 fields must come from the exact same version `v`.
                    if (s.BatchSize != s.ThreadCount ||
                        s.BatchSize != s.AffinityHint ||
                        (int)s.Confidence != s.BatchSize)
                    {
                        Interlocked.Increment(ref tornCount);
                    }
                }
            });
            readers[r].Start();
        }

        startBarrier.SignalAndWait();
        for (int v = 1; v <= writeIterations; v++)
        {
            int val = (v % 1000) + 1;
            var strat = new Strategia
            {
                BatchSize = val,
                ThreadCount = val,
                Confidence = val,
                AffinityHint = val
            };
            scriba.Scribere(0, in strat);
        }

        Volatile.Write(ref writerDone, true);
        foreach (var reader in readers) reader.Join();

        Assert.Equal(0, tornCount);
    }
}
