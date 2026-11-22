```

BenchmarkDotNet v0.13.12, Freedesktop SDK 25.08 (Flatpak runtime)
AMD Ryzen 7 7700, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.25.52411), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 10.0.0 (10.0.25.52411), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method       | Job        | Toolchain              | ItemCount | Complexity | Mean           | Error        | StdDev       | Median         | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------- |----------- |----------------------- |---------- |----------- |---------------:|-------------:|-------------:|---------------:|------:|--------:|----------:|------------:|
| **Serial_Loop**  | **DefaultJob** | **Default**                | **1000000**   | **1**          |     **3,918.0 μs** |     **26.26 μs** |     **24.57 μs** |     **3,908.2 μs** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Parallel_For | DefaultJob | Default                | 1000000   | 1          |       876.5 μs |     15.91 μs |     14.88 μs |       873.0 μs |  0.22 |    0.00 |    4703 B |          NA |
| Legio_Engine | DefaultJob | Default                | 1000000   | 1          |       957.0 μs |     18.95 μs |     21.06 μs |       951.2 μs |  0.24 |    0.01 |         - |          NA |
|              |            |                        |           |            |                |              |              |                |       |         |           |             |
| Serial_Loop  | InProcess  | InProcessEmitToolchain | 1000000   | 1          |     3,935.2 μs |     51.97 μs |     48.61 μs |     3,938.5 μs |  1.00 |    0.00 |         - |          NA |
| Parallel_For | InProcess  | InProcessEmitToolchain | 1000000   | 1          |       876.9 μs |     12.82 μs |     18.39 μs |       872.6 μs |  0.22 |    0.01 |    4721 B |          NA |
| Legio_Engine | InProcess  | InProcessEmitToolchain | 1000000   | 1          |       929.0 μs |     17.61 μs |     19.57 μs |       930.0 μs |  0.24 |    0.01 |         - |          NA |
|              |            |                        |           |            |                |              |              |                |       |         |           |             |
| **Serial_Loop**  | **DefaultJob** | **Default**                | **1000000**   | **100**        | **1,101,584.0 μs** |  **1,554.86 μs** |  **1,378.35 μs** | **1,101,213.3 μs** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Parallel_For | DefaultJob | Default                | 1000000   | 100        |    83,611.9 μs |    708.41 μs |    662.65 μs |    83,687.7 μs |  0.08 |    0.00 |    4901 B |          NA |
| Legio_Engine | DefaultJob | Default                | 1000000   | 100        |   222,579.3 μs | 15,397.63 μs | 44,178.69 μs |   215,822.7 μs |  0.23 |    0.04 |         - |          NA |
|              |            |                        |           |            |                |              |              |                |       |         |           |             |
| Serial_Loop  | InProcess  | InProcessEmitToolchain | 1000000   | 100        | 1,102,314.2 μs |  1,176.38 μs |  1,100.39 μs | 1,102,093.4 μs |  1.00 |    0.00 |         - |          NA |
| Parallel_For | InProcess  | InProcessEmitToolchain | 1000000   | 100        |    84,295.4 μs |  1,492.37 μs |  1,395.96 μs |    83,974.2 μs |  0.08 |    0.00 |    5317 B |          NA |
| Legio_Engine | InProcess  | InProcessEmitToolchain | 1000000   | 100        |   222,374.4 μs | 15,503.64 μs | 44,482.84 μs |   208,938.8 μs |  0.21 |    0.05 |         - |          NA |
