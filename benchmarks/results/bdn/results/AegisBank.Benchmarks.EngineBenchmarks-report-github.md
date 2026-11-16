```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
AMD Ryzen 7 6800H with Radeon Graphics 3.19GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.404
  [Host]    : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  EngineJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=EngineJob  IterationCount=15  LaunchCount=1  
WarmupCount=6  

```
| Method                                             | Mean      | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------------------------- |----------:|----------:|----------:|------:|--------:|----------:|------------:|
| &#39;C++ engine via wrapper (1 P/Invoke per transfer)&#39; | 64.907 ns | 2.5851 ns | 2.4181 ns | 11.65 |    0.63 |         - |          NA |
| &#39;C++ engine raw P/Invoke (pre-built structs)&#39;      | 29.477 ns | 0.9737 ns | 0.9108 ns |  5.29 |    0.26 |         - |          NA |
| &#39;C++ engine batch (1 P/Invoke per 4096 transfers)&#39; | 29.345 ns | 0.9548 ns | 0.8464 ns |  5.26 |    0.26 |         - |          NA |
| &#39;C# reference implementation&#39;                      |  5.583 ns | 0.2544 ns | 0.2380 ns |  1.00 |    0.06 |         - |          NA |
