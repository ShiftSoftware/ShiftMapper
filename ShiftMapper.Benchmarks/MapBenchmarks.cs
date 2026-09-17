using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftMapper.Benchmarks;

/// <summary>
/// The in-memory maps.
///
/// The one that matters for caching is at the bottom. <c>PerRequest</c> resolves a NEW mapper
/// from a new scope for every iteration, which is what <c>AddShiftMapper</c>'s default Scoped
/// lifetime does on every request — and before the compile cache was keyed by mapper type, that
/// meant compiling every customization the request touched, all over again.
/// </summary>
[MemoryDiagnoser]
public class MapBenchmarks
{
    private readonly ServiceProvider _provider = Container.Build();
    private IServiceScope _scope = null!;
    private Mapper _mapper = null!;

    private Brand _brand = null!;
    private Invoice _invoice = null!;
    private List<Brand> _brands = null!;

    [GlobalSetup]
    public void Setup()
    {
        _scope = _provider.CreateScope();
        _mapper = _scope.ServiceProvider.GetRequiredService<Mapper>();

        _brand = Sample.Brand();
        _invoice = Sample.Invoice();
        _brands = Sample.Brands(10_000);

        // Warm the cache, so the steady-state benchmarks measure mapping rather than the first
        // call's one-off compilation.
        _ = _mapper.MapToInvoiceDto(_invoice);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    /// <summary>One object, a conversion and a collection copy. The floor for everything else.</summary>
    [Benchmark(Baseline = true)]
    public BrandDto Single() => _mapper.MapToBrandDto(_brand);

    /// <summary>
    /// The same map through the generic dispatcher. The gap is the typeof chain — and for a
    /// struct destination it would also be a box per call.
    /// </summary>
    [Benchmark]
    public BrandDto SingleThroughDispatcher() => _mapper.Map<BrandDto>(_brand);

    /// <summary>Three levels plus a collection of ten lines, with two customizations on the way.</summary>
    [Benchmark]
    public InvoiceDto NestedGraph() => _mapper.MapToInvoiceDto(_invoice);

    /// <summary>Ten thousand objects, one at a time — the shape a list endpoint has.</summary>
    [Benchmark]
    public int Collection()
    {
        int total = 0;

        foreach (Brand brand in _brands)
            total += _mapper.MapToBrandDto(brand).Id;

        return total;
    }

    /// <summary>
    /// A REQUEST: open a scope, resolve the mapper, map once, throw it all away.
    ///
    /// The mapper class has one customization that closes over its injected service, so that one
    /// is compiled per instance and shows up here. The other two come out of the per-process cache.
    /// </summary>
    [Benchmark]
    public InvoiceDto PerRequest()
    {
        using IServiceScope scope = _provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<Mapper>().MapToInvoiceDto(_invoice);
    }
}
