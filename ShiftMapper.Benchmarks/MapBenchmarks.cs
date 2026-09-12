using BenchmarkDotNet.Attributes;

namespace ShiftMapper.Benchmarks;

/// <summary>
/// The in-memory maps.
///
/// The two that matter for caching are at the bottom. <c>PerRequest*</c> builds a NEW mapper for
/// every iteration, which is what <c>AddShiftMapper</c>'s default Scoped lifetime does on every
/// request — and before the compile cache was keyed by mapper type, that meant compiling every
/// customization the request touched, all over again.
/// </summary>
[MemoryDiagnoser]
public class MapBenchmarks
{
    private readonly BenchmarkMapper _mapper = new(new Numbering());
    private readonly INumbering _numbering = new Numbering();

    private Brand _brand = null!;
    private Invoice _invoice = null!;
    private List<Brand> _brands = null!;

    [GlobalSetup]
    public void Setup()
    {
        _brand = Sample.Brand();
        _invoice = Sample.Invoice();
        _brands = Sample.Brands(10_000);

        // Warm both caches, so the steady-state benchmarks measure mapping rather than the first
        // call's one-off compilation.
        _ = _mapper.MapToInvoiceDto(_invoice);
        _ = new SharedOnlyMapper().MapToInvoiceDto(_invoice);
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
    /// A REQUEST: resolve a mapper, map once, throw it away.
    ///
    /// This mapper has one customization that closes over its injected service, so that one is
    /// compiled per instance and shows up here. The other two are shared.
    /// </summary>
    [Benchmark]
    public InvoiceDto PerRequestWithACapturedService() =>
        new BenchmarkMapper(_numbering).MapToInvoiceDto(_invoice);

    /// <summary>
    /// The same request against a mapper that captures nothing: every customization comes out of
    /// the per-process cache, so what is left is building the expression trees the constructor
    /// writes. The gap between this and the one above is the price of closing over a service.
    /// </summary>
    [Benchmark]
    public InvoiceDto PerRequestCapturingNothing() =>
        new SharedOnlyMapper().MapToInvoiceDto(_invoice);
}
