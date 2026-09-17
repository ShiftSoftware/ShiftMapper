using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftMapper.Benchmarks;

/// <summary>
/// Building the expression <c>ProjectTo</c> hands to EF — not running a query, which would
/// measure the database rather than ShiftMapper.
///
/// This is the other half of caching. The projection used to be an expression-bodied property, so
/// every call rebuilt the member initializer, re-scanned the customization store and re-grafted
/// every nested map — and once per LEVEL per call, because a nested map is reached through the
/// parent's member. <see cref="OnAWarmMapper"/> is what that costs now; the composition half of
/// <see cref="OnANewMapper"/> is roughly what it used to cost on every single call.
/// </summary>
[MemoryDiagnoser]
public class ProjectionBenchmarks
{
    private readonly ServiceProvider _provider = Container.Build();
    private IServiceScope _scope = null!;
    private Mapper _mapper = null!;

    private IQueryable<Invoice> _invoices = null!;
    private IQueryable<Brand> _brands = null!;

    [GlobalSetup]
    public void Setup()
    {
        _scope = _provider.CreateScope();
        _mapper = _scope.ServiceProvider.GetRequiredService<Mapper>();

        _invoices = new List<Invoice> { Sample.Invoice() }.AsQueryable();
        _brands = Sample.Brands(1).AsQueryable();

        // Build both projections once, so the steady-state numbers are the cached path.
        _ = _mapper.ProjectTo<InvoiceDto>(_invoices).Expression;
        _ = _mapper.ProjectTo<BrandDto>(_brands).Expression;
    }

    /// <summary>A flat map, for the floor.</summary>
    [Benchmark(Baseline = true)]
    public Expression FlatOnAWarmMapper() => _mapper.ProjectTo<BrandDto>(_brands).Expression;

    /// <summary>
    /// The four-level graph on a mapper that has already built it: the composed tree is a field
    /// now, so all that is left is the Queryable.Select call around it.
    /// </summary>
    [Benchmark]
    public Expression OnAWarmMapper() => _mapper.ProjectTo<InvoiceDto>(_invoices).Expression;

    /// <summary>
    /// The same graph composed from scratch — every level's initializer rebuilt, every
    /// customization spliced in, every nested map grafted on. A request pays this once now; it
    /// used to be the cost of each ProjectTo call.
    /// </summary>
    [Benchmark]
    public Expression OnANewMapper()
    {
        using IServiceScope scope = _provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<Mapper>().ProjectTo<InvoiceDto>(_invoices).Expression;
    }

    /// <summary>
    /// Resolving the mapper and nothing else, so the one above can be read honestly: the
    /// composition is the difference between the two, rather than the whole of it.
    /// </summary>
    [Benchmark]
    public object ResolveMapperOnly()
    {
        using IServiceScope scope = _provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<Mapper>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
