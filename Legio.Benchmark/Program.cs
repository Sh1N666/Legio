using BenchmarkDotNet.Running;
using Legio.Benchmark.Core;

namespace Legio.Benchmark;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            string mode = args[0].ToLowerInvariant();
            switch (mode)
            {
                case "--quick":
                    StabilityProfiler.RunSuite("quick");
                    return;
                case "--stability":
                    StabilityProfiler.RunSuite("stability");
                    return;
                case "--heavy":
                    StabilityProfiler.RunSuite("heavy");
                    return;
                case "--new":
                    StabilityProfiler.RunSuite("new");
                    return;
                case "--timeline":
                    StabilityProfiler.RunSuite("timeline");
                    return;
            }
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}