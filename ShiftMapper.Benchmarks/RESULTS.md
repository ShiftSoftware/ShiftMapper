# Comparison results

The full BenchmarkDotNet report for `ComparisonBenchmarks`, as produced by

```
dotnet run -c Release --project ShiftMapper.Benchmarks -- --filter *Comparison*
```

Committed here because `BenchmarkDotNet.Artifacts/` is ignored, and a number in the README should be
traceable to the run that produced it. The README's **Performance** section is the reading of this
table; this file is the table. Re-run and replace it when the generated shape changes.

Each category has its own baseline (`Ratio` reads within a group). The `Collection10k` ShiftMapper
row is GC-noisy — 1.7 MB of allocation per call — which is why the README cites its median.

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9445/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-13700H 2.40GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                    | Categories    | Mean            | Error         | StdDev         | Median          | Ratio | RatioSD | Gen0     | Gen1     | Gen2    | Allocated | Alloc Ratio |
|-------------------------- |-------------- |----------------:|--------------:|---------------:|----------------:|------:|--------:|---------:|---------:|--------:|----------:|------------:|
| Collection10k_ShiftMapper | Collection10k |   442,180.92 ns | 48,981.974 ns | 142,882.728 ns |   360,152.29 ns |  1.08 |    0.44 | 140.1367 | 102.5391 |       - | 1760096 B |        1.00 |
| Collection10k_AutoMapper  | Collection10k | 1,890,018.92 ns | 88,672.989 ns | 255,841.868 ns | 1,786,946.97 ns |  4.62 |    1.26 | 199.2188 | 185.5469 | 56.6406 | 2102475 B |        1.19 |
| Collection10k_Mapperly    | Collection10k |   140,639.73 ns |  2,547.107 ns |   2,382.565 ns |   140,347.73 ns |  0.34 |    0.08 |  82.7637 |  46.3867 |       - | 1040056 B |        0.59 |
|                           |               |                 |               |                |                 |       |         |          |          |         |           |             |
| NestedGraph_ShiftMapper   | NestedGraph   |       942.87 ns |     16.743 ns |      18.610 ns |       935.84 ns |  1.00 |    0.03 |   0.2632 |   0.0029 |       - |    3312 B |        1.00 |
| NestedGraph_AutoMapper    | NestedGraph   |     1,136.74 ns |     19.983 ns |      37.533 ns |     1,127.27 ns |  1.21 |    0.05 |   0.2880 |   0.0019 |       - |    3624 B |        1.09 |
| NestedGraph_Mapperly      | NestedGraph   |       398.08 ns |      9.707 ns |      28.621 ns |       408.38 ns |  0.42 |    0.03 |   0.1988 |   0.0014 |       - |    2496 B |        0.75 |
|                           |               |                 |               |                |                 |       |         |          |          |         |           |             |
| Projection_ShiftMapper    | Projection    |       343.24 ns |      3.572 ns |       3.341 ns |       343.51 ns |  1.00 |    0.01 |   0.0334 |        - |       - |     424 B |        1.00 |
| Projection_AutoMapper     | Projection    |       697.42 ns |      7.270 ns |       6.800 ns |       696.65 ns |  2.03 |    0.03 |   0.0687 |        - |       - |     872 B |        2.06 |
| Projection_Mapperly       | Projection    |     8,269.89 ns |    156.977 ns |     180.775 ns |     8,255.91 ns | 24.10 |    0.56 |   1.3123 |   0.0153 |       - |   16552 B |       39.04 |
|                           |               |                 |               |                |                 |       |         |          |          |         |           |             |
| Single_ShiftMapper        | Single        |        24.57 ns |      0.361 ns |       0.338 ns |        24.56 ns |  1.00 |    0.02 |   0.0134 |        - |       - |     168 B |        1.00 |
| Single_AutoMapper         | Single        |        57.95 ns |      1.170 ns |       1.752 ns |        58.02 ns |  2.36 |    0.08 |   0.0147 |        - |       - |     184 B |        1.10 |
| Single_Mapperly           | Single        |        11.94 ns |      0.268 ns |       0.384 ns |        11.81 ns |  0.49 |    0.02 |   0.0076 |        - |       - |      96 B |        0.57 |
