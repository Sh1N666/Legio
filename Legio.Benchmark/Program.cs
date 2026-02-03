using BenchmarkDotNet.Running;
using Legio.Benchmark.Core;

namespace Legio.Benchmark;

public class Program
{
    public static void Main(string[] args)
    {
        // Odpalamy benchmark
        var summary = BenchmarkRunner.Run<LegioVsNet>();
    }
}