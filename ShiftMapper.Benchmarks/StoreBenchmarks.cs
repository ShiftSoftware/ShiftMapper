using System.Globalization;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftMapper.Benchmarks;

/// <summary>
/// THE STORE'S PER-OBJECT PATHS — the members that read the generated mapper's runtime store on
/// every object mapped, rather than once per mapper.
///
/// <para><see cref="MapBenchmarks"/> has none of them. Its one conversion is the built-in
/// <c>int</c> to <c>string</c>, which is written inline, and its <c>MapFrom</c>s are fetched once
/// into a field. Here a type-pair conversion is a <c>Customizations.Conversion</c> lookup per
/// member per object, a <c>Condition</c> is a lookup per member per object, and a hook is one
/// lookup per object, so these are the numbers a change to how the store is read or filled shows
/// up in.</para>
/// </summary>
[MemoryDiagnoser]
public class StoreBenchmarks
{
    private readonly ServiceProvider _provider = Container.Build();
    private IServiceScope _scope = null!;
    private Mapper _mapper = null!;

    private Order _order = null!;
    private List<Order> _orders = null!;
    private OrderPatch _patch = null!;
    private IQueryable<Order> _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        _scope = _provider.CreateScope();
        _mapper = _scope.ServiceProvider.GetRequiredService<Mapper>();

        _order = StoreSample.Order(1);
        _orders = Enumerable.Range(0, 1_000).Select(StoreSample.Order).ToList();
        _patch = new OrderPatch { Reference = "R-1", Note = "call first", Channel = "web" };
        _query = new List<Order> { _order }.AsQueryable();

        // Warm every path, so the steady-state rows measure the lookups and not the first call's
        // resolution of each conversion.
        _ = _mapper.MapToOrderDto(_order);
        _ = _mapper.MapToOrderNote(_patch);
        _ = _mapper.ProjectTo<OrderDto>(_query).Expression;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    /// <summary>One order: six members through registered conversions, one of them the form that is told the member.</summary>
    [Benchmark(Baseline = true)]
    public OrderDto Conversions() => _mapper.MapToOrderDto(_order);

    /// <summary>A thousand orders, one at a time — six thousand conversion lookups.</summary>
    [Benchmark]
    public int ConversionsCollection()
    {
        int total = 0;

        foreach (Order order in _orders)
            total += _mapper.MapToOrderDto(order).Quantity;

        return total;
    }

    /// <summary>Three guarded members and both hooks: three Condition lookups and two hook lookups per object.</summary>
    [Benchmark]
    public OrderNote ConditionsAndHooks() => _mapper.MapToOrderNote(_patch);

    /// <summary>
    /// A REQUEST that maps one order: a new scope and a new generated mapper, so the store is
    /// filled — every mapper class and pack built and folded in — and each conversion resolved,
    /// before the one map runs.
    /// </summary>
    [Benchmark]
    public OrderDto ConversionsPerRequest()
    {
        using IServiceScope scope = _provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<Mapper>().MapToOrderDto(_order);
    }

    /// <summary>The same request building the projection instead: the fill, then every conversion spliced in by its query form.</summary>
    [Benchmark]
    public Expression ConversionsProjectionPerRequest()
    {
        using IServiceScope scope = _provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<Mapper>().ProjectTo<OrderDto>(_query).Expression;
    }
}

/// <summary>A value the built-in table cannot convert, so the pack's rule is the only way to text.</summary>
public sealed class Amount
{
    public decimal Value { get; set; }

    public string Currency { get; set; } = "IQD";
}

public class Order
{
    public long Id { get; set; }

    public long CustomerId { get; set; }

    public long BranchId { get; set; }

    public Amount Total { get; set; } = new();

    public Amount Tax { get; set; } = new();

    public string Quantity { get; set; } = "0";

    public string Reference { get; set; } = string.Empty;
}

public class OrderDto
{
    /// <summary>The three ids are hash ids: <c>long</c> to <c>string</c> through the pack, which beats the built-in rule.</summary>
    public string Id { get; set; } = string.Empty;

    public string CustomerId { get; set; } = string.Empty;

    public string BranchId { get; set; } = string.Empty;

    public string Total { get; set; } = string.Empty;

    public string Tax { get; set; } = string.Empty;

    /// <summary>Text to a number through the two-argument form, which is told the member so a refusal can name it.</summary>
    public int Quantity { get; set; }

    public string Reference { get; set; } = string.Empty;
}

/// <summary>A partial update: what is absent must leave the destination alone.</summary>
public class OrderPatch
{
    public string? Reference { get; set; }

    public string? Note { get; set; }

    public string? Channel { get; set; }
}

public class OrderNote
{
    public string Reference { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    /// <summary>Written by the hooks, which is why the map ignores it.</summary>
    public string Trace { get; set; } = string.Empty;
}

/// <summary>The rules the order map converts by — a pack, the shape a framework ships them in.</summary>
public sealed class OrderConversions : ShiftMapperConversions
{
    public OrderConversions()
    {
        CreateConversion<long, string>(
            memory: id => "H" + id,
            query: id => "H" + id);

        CreateConversion<Amount, string>(
            memory: amount => amount.Value.ToString(CultureInfo.InvariantCulture) + " " + amount.Currency,
            query: amount => amount.Value + " " + amount.Currency);

        CreateConversion<string, int>(
            memory: (text, mapping) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw new FormatException(mapping + " is not a number."),
            query: text => int.Parse(text));
    }
}

/// <summary>
/// The mapper class under measurement here. Its own class, so its pack applies to its maps alone
/// and leaves <see cref="BenchmarkMapper"/>'s as they were.
/// </summary>
public class StoreMapper : ShiftMapperBase
{
    public StoreMapper()
    {
        AddConversions<OrderConversions>();

        CreateMap<Order, OrderDto>();

        // In memory only on purpose: Conditions and hooks are what this map measures, and a map
        // carrying them has no projection (SM0017, SM0018).
#pragma warning disable SM0017, SM0018
        CreateMap<OrderPatch, OrderNote>()
            .ForMember(d => d.Reference, opt => opt.Condition((s, d, value) => value is not null))
            .ForMember(d => d.Note, opt => opt.Condition((s, d, value) => value is not null))
            .ForMember(d => d.Channel, opt => opt.Condition((s, d, value) => value is not null))
            .ForMember(d => d.Trace, opt => opt.Ignore())
            .BeforeMap((s, d) => d.Trace = "before")
            .AfterMap((s, d) => d.Trace += ", after");
#pragma warning restore SM0017, SM0018
    }
}

/// <summary>Builds the orders the store benchmarks map.</summary>
public static class StoreSample
{
    public static Order Order(int index) => new()
    {
        Id = index,
        CustomerId = 1_000 + index,
        BranchId = 7,
        Total = new Amount { Value = 125.50m + index },
        Tax = new Amount { Value = 12.55m },
        Quantity = (index % 50).ToString(CultureInfo.InvariantCulture),
        Reference = "ORD-" + index,
    };
}
