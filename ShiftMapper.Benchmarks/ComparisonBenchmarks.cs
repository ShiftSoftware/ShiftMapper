using System.Linq.Expressions;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace ShiftMapper.Benchmarks;

/// <summary>
/// SHIFTMAPPER AGAINST AUTOMAPPER AND MAPPERLY, on four shapes: one object, a nested graph, a
/// large collection, and building a projection.
///
/// <para>Every mapper is asked the same question: the four maps in <see cref="SharedOnlyMapper"/>,
/// declared for each library in <see cref="Competitors"/>. Every mapper is warm, every
/// configuration is built once and shared, and nothing captures a service — so each is on the
/// fastest path it has. Where one needs a different spelling to say the same thing, it gets it.</para>
///
/// <para><b>What the three are.</b> AutoMapper reads its configuration at RUN time and compiles
/// expression trees on first use — a runtime mapper. Mapperly and ShiftMapper are both SOURCE
/// generators, so the Mapperly column is the one that matters: it separates "what source
/// generation buys" from "what ShiftMapper's particular generated shape costs".</para>
///
/// <para>Each category has its own baseline, so the ratio column reads within a row group rather
/// than against an unrelated shape.</para>
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ComparisonBenchmarks
{
    private readonly SharedOnlyMapper _shift = new();
    private readonly IMapper _auto = Competitors.AutoMapperInstance;
    private readonly MapperlyMapper _mapperly = new();

    private Brand _brand = null!;
    private Invoice _invoice = null!;
    private List<Brand> _brands = null!;
    private IQueryable<Invoice> _invoices = null!;

    [GlobalSetup]
    public void Setup()
    {
        _brand = Sample.Brand();
        _invoice = Sample.Invoice();
        _brands = Sample.Brands(10_000);
        _invoices = new List<Invoice> { _invoice }.AsQueryable();

        // Warm every path once. AutoMapper compiles its expression trees on first use and
        // ShiftMapper composes its projection on first use; neither first call is what a steady
        // state looks like, and the steady state is what a request sees.
        _ = _shift.MapToInvoiceDto(_invoice);
        _ = _auto.Map<InvoiceDto>(_invoice);
        _ = _mapperly.MapToInvoiceDto(_invoice);

        _ = _shift.ProjectTo<InvoiceDto>(_invoices).Expression;
        _ = _invoices.ProjectTo<InvoiceDto>(Competitors.AutoMapperConfiguration).Expression;
        _ = _mapperly.ProjectToInvoiceDto(_invoices).Expression;
    }

    // ---------------------------------------------------------------- 1. one object

    /// <summary>One object: six members, one conversion, one collection copy, one case-insensitive match.</summary>
    [BenchmarkCategory("Single"), Benchmark(Baseline = true)]
    public BrandDto Single_ShiftMapper() => _shift.MapToBrandDto(_brand);

    [BenchmarkCategory("Single"), Benchmark]
    public BrandDto Single_AutoMapper() => _auto.Map<BrandDto>(_brand);

    [BenchmarkCategory("Single"), Benchmark]
    public BrandDto Single_Mapperly() => _mapperly.MapToBrandDto(_brand);

    // ---------------------------------------------------------------- 2. a nested graph

    /// <summary>Four levels, a collection of ten lines, and three computed members on the way.</summary>
    [BenchmarkCategory("NestedGraph"), Benchmark(Baseline = true)]
    public InvoiceDto NestedGraph_ShiftMapper() => _shift.MapToInvoiceDto(_invoice);

    [BenchmarkCategory("NestedGraph"), Benchmark]
    public InvoiceDto NestedGraph_AutoMapper() => _auto.Map<InvoiceDto>(_invoice);

    [BenchmarkCategory("NestedGraph"), Benchmark]
    public InvoiceDto NestedGraph_Mapperly() => _mapperly.MapToInvoiceDto(_invoice);

    // ---------------------------------------------------------------- 3. ten thousand objects

    /// <summary>The shape a list endpoint has: one call, ten thousand objects out.</summary>
    [BenchmarkCategory("Collection10k"), Benchmark(Baseline = true)]
    public List<BrandDto> Collection10k_ShiftMapper() => _shift.MapToBrandDtoList(_brands);

    [BenchmarkCategory("Collection10k"), Benchmark]
    public List<BrandDto> Collection10k_AutoMapper() => _auto.Map<List<BrandDto>>(_brands);

    [BenchmarkCategory("Collection10k"), Benchmark]
    public List<BrandDto> Collection10k_Mapperly() => _mapperly.MapToBrandDtoList(_brands);

    // ---------------------------------------------------------------- 4. the projection

    /// <summary>
    /// Building the expression <c>ProjectTo</c> hands to a query provider — NOT running a query,
    /// which would measure the database. The nested graph, on a warm mapper.
    ///
    /// <para>The three arrive at the same place by different routes: Mapperly's is a static lambda
    /// written at compile time; ShiftMapper composes its tree once and caches it in a field;
    /// AutoMapper builds it from the configuration. See <c>--shapes</c> in Program.cs for what
    /// each tree actually contains, which matters more than this row.</para>
    /// </summary>
    [BenchmarkCategory("Projection"), Benchmark(Baseline = true)]
    public Expression Projection_ShiftMapper() => _shift.ProjectTo<InvoiceDto>(_invoices).Expression;

    [BenchmarkCategory("Projection"), Benchmark]
    public Expression Projection_AutoMapper() =>
        _invoices.ProjectTo<InvoiceDto>(Competitors.AutoMapperConfiguration).Expression;

    [BenchmarkCategory("Projection"), Benchmark]
    public Expression Projection_Mapperly() => _mapperly.ProjectToInvoiceDto(_invoices).Expression;
}
