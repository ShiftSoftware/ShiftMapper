using BenchmarkDotNet.Running;

// ShiftMapper's benchmarks. Nothing here runs in CI — these take minutes and are for deciding
// whether a change to the generated shape was worth making.
//
//   dotnet run -c Release --project ShiftMapper.Benchmarks -- --filter *
//   dotnet run -c Release --project ShiftMapper.Benchmarks -- --filter *Projection*
//
// Release only: BenchmarkDotNet refuses a Debug build, and it is right to.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Needed only so the switcher above has an assembly to look in.</summary>
public partial class Program;
