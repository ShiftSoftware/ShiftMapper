using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// A mapper whose customization CLOSES OVER its constructor argument, which is the case that
/// decides where a compiled delegate may be kept.
/// </summary>
/// <summary>Destinations of these probes' own: one pair per probe, so none clashes with another.</summary>
public class CapturedBrandDto
{
    public string Name { get; set; } = string.Empty;
}

public class CapturedLocalBrandDto
{
    public string Name { get; set; } = string.Empty;
}

public class CapturingProbe : ShiftMapperBase
{
    private readonly string _prefix;

    // Every mapper class in the project is built by the generated mapper on first use, so a probe
    // needs a constructor the container can satisfy as well as the one the tests drive.
    public CapturingProbe()
        : this(string.Empty)
    {
    }

    public CapturingProbe(string prefix)
    {
        _prefix = prefix;

        CreateMap<Brand, CapturedBrandDto>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => _prefix + s.Name));
    }

    public MapCustomizations Store => Customizations;
}

/// <summary>
/// A mapper whose customization closes over a LOCAL rather than a field. The compiler lifts it
/// into a closure object, which reaches the store as a reference constant exactly as a captured
/// field does — so it is refused for sharing on the same terms.
/// </summary>
public class CapturedLocalProbe : ShiftMapperBase
{
    public CapturedLocalProbe()
        : this(string.Empty)
    {
    }

    public CapturedLocalProbe(string suffix)
    {
        string local = suffix.ToUpperInvariant();

        CreateMap<Brand, CapturedLocalBrandDto>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name + local));
    }

    public MapCustomizations Store => Customizations;
}

/// <summary>
/// What the caching is about: the work that used to be repeated per request, and the one
/// case where repeating it is the only correct answer.
///
/// These are the tests that would not fail if the caching were removed — the values would all
/// still be right, only slower — so they assert on IDENTITY rather than on results.
/// </summary>
public class CachingTests
{
    // -----------------------------------------------------------------
    // COMPILING A CUSTOMIZATION — once per process where that is safe.
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>AddShiftMapper</c> registers mappers as Scoped, so before this every request compiled
    /// every expression it touched again, at hundreds of microseconds each. The delegate is now
    /// keyed by the mapper CLASS, so the second instance finds the first one's.
    /// </summary>
    [Fact]
    public void A_customization_that_captures_nothing_is_compiled_once_per_process()
    {
        Func<Brand, string> first = new CustomizationProbe().Store.Value<Brand, ProbeBrandDto, string>("Country");
        Func<Brand, string> second = new CustomizationProbe().Store.Value<Brand, ProbeBrandDto, string>("Country");

        Assert.Same(first, second);
    }

    /// <summary>
    /// THE CASE THAT MAKES THE CACHE CONDITIONAL, and the reason it is not simply keyed by mapper
    /// type and left at that.
    ///
    /// A compiled delegate is bound to whatever its expression captured. Sharing one that closed
    /// over an injected service would hand every later request the FIRST request's service — for
    /// a scoped DbContext or a per-request tenant, a bug that nothing reports and that only
    /// appears under load.
    /// </summary>
    [Fact]
    public void A_customization_that_captures_the_mapper_is_compiled_per_instance()
    {
        Func<Brand, string> first = new CapturingProbe("A/").Store.Value<Brand, CapturedBrandDto, string>("Name");
        Func<Brand, string> second = new CapturingProbe("B/").Store.Value<Brand, CapturedBrandDto, string>("Name");

        Assert.NotSame(first, second);

        // And it matters: each one carries the prefix its own mapper was built with.
        Assert.Equal("A/Acme", first(new Brand { Name = "Acme" }));
        Assert.Equal("B/Acme", second(new Brand { Name = "Acme" }));
    }

    /// <summary>
    /// A captured LOCAL is refused on the same terms as a captured field — the compiler lifts it
    /// into a closure object, and a closure object is a reference constant like any other.
    /// </summary>
    [Fact]
    public void A_customization_that_captures_a_local_is_compiled_per_instance()
    {
        Func<Brand, string> first = new CapturedLocalProbe("a").Store.Value<Brand, CapturedLocalBrandDto, string>("Name");
        Func<Brand, string> second = new CapturedLocalProbe("b").Store.Value<Brand, CapturedLocalBrandDto, string>("Name");

        Assert.NotSame(first, second);
        Assert.Equal("AcmeA", first(new Brand { Name = "Acme" }));
        Assert.Equal("AcmeB", second(new Brand { Name = "Acme" }));
    }

    /// <summary>
    /// The same thing through the real front door: two scopes, two mappers, two different
    /// services, and the map has to follow the scope it is running in.
    /// </summary>
    [Fact]
    public void A_scoped_mapper_uses_its_own_scopes_services()
    {
        var services = new ServiceCollection();
        services.AddScoped<IInvoiceNumbering>(_ => new PrefixNumbering("A/"));
        services.AddShiftMapper();

        using ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();
        Mapper mapper = scope.ServiceProvider.GetRequiredService<Mapper>();

        Assert.Equal("A/0001", mapper.Map<InvoiceDto>(new Invoice { Number = "0001" }).Number);

        // A second container, a different service, the same mapper CLASS. If the compiled
        // delegate were shared this would still say "A/".
        var other = new ServiceCollection();
        other.AddScoped<IInvoiceNumbering>(_ => new PrefixNumbering("B/"));
        other.AddShiftMapper();

        using ServiceProvider otherProvider = other.BuildServiceProvider();
        using IServiceScope otherScope = otherProvider.CreateScope();

        Assert.Equal(
            "B/0001",
            otherScope.ServiceProvider.GetRequiredService<Mapper>()
                .Map<InvoiceDto>(new Invoice { Number = "0001" }).Number);
    }

    /// <summary>
    /// Two mapper classes can fill the same property of the same pair in different ways, so the
    /// shared cache is keyed by the mapper as well as by the member.
    /// </summary>
    [Fact]
    public void Two_mapper_classes_do_not_share_each_others_customizations()
    {
        Func<Brand, string> probe = new CustomizationProbe().Store.Value<Brand, ProbeBrandDto, string>("Country");
        Func<Brand, string> capturing = new CapturingProbe("A/").Store.Value<Brand, CapturedBrandDto, string>("Name");

        Assert.NotSame(probe, capturing);
        Assert.Equal("Iraq (IQ)", probe(new Brand { Country = "Iraq", ISOCode = "IQ" }));
    }

    // -----------------------------------------------------------------
    // THE PROJECTION — built once, not once per ProjectTo call.
    // -----------------------------------------------------------------

    /// <summary>
    /// The projection used to be an expression-bodied property, so every <c>ProjectTo</c> rebuilt
    /// the member initializer, re-scanned the customization store and re-grafted every nested map
    /// — once per level, per call.
    /// </summary>
    [Fact]
    public void The_projection_is_built_once_per_mapper()
    {
        var mapper = Mappers.Fresh();
        IQueryable<Brand> source = new List<Brand>().AsQueryable();

        Assert.Same(
            ProjectionOf(mapper.ProjectTo<BrandDto>(source)),
            ProjectionOf(mapper.ProjectTo<BrandDto>(source)));
    }

    /// <summary>
    /// A whole nested graph, where the cost used to multiply with depth: the four-level invoice
    /// projection is one object, reused.
    /// </summary>
    [Fact]
    public void A_nested_projection_is_built_once_per_mapper()
    {
        var mapper = Mappers.Fresh();
        IQueryable<Invoice> source = new List<Invoice>().AsQueryable();

        Assert.Same(
            ProjectionOf(mapper.ProjectTo<InvoiceDto>(source)),
            ProjectionOf(mapper.ProjectTo<InvoiceDto>(source)));
    }

    /// <summary>
    /// Per MAPPER, not per process — and deliberately so. A composed projection has the
    /// customization expressions spliced into it, and those may close over this mapper's
    /// services, so the tree cannot outlive the instance that built it.
    /// </summary>
    [Fact]
    public void Two_mappers_do_not_share_a_projection()
    {
        IQueryable<Invoice> source = new List<Invoice>().AsQueryable();

        Assert.NotSame(
            ProjectionOf(Mappers.With(new PrefixNumbering("A/")).ProjectTo<InvoiceDto>(source)),
            ProjectionOf(Mappers.With(new PrefixNumbering("B/")).ProjectTo<InvoiceDto>(source)));
    }

    /// <summary>
    /// <c>Queryable.Select</c> wraps the projection in a Quote, so this digs it back out — the
    /// expression object itself is the thing being compared.
    /// </summary>
    private static LambdaExpression ProjectionOf<T>(IQueryable<T> query)
    {
        var call = Assert.IsAssignableFrom<MethodCallExpression>(query.Expression);
        var quoted = Assert.IsAssignableFrom<UnaryExpression>(call.Arguments[1]);

        return Assert.IsAssignableFrom<LambdaExpression>(quoted.Operand);
    }

    // -----------------------------------------------------------------
    // THE DIRECT MAP METHODS
    // -----------------------------------------------------------------

    [Fact]
    public void The_direct_method_and_the_dispatcher_agree()
    {
        var mapper = Mappers.Fresh();
        var brand = new Brand { Id = 1, Name = "Acme", ISOCode = "IQ", FoundedYear = 1994 };

        BdCompare(mapper.Map<BrandDto>(brand), mapper.MapToBrandDto(brand));

        static void BdCompare(BrandDto viaDispatcher, BrandDto viaDirect)
        {
            Assert.Equal(viaDispatcher.Id, viaDirect.Id);
            Assert.Equal(viaDispatcher.Name, viaDirect.Name);
            Assert.Equal(viaDispatcher.IsoCode, viaDirect.IsoCode);
            Assert.Equal(viaDispatcher.FoundedYear, viaDirect.FoundedYear);
        }
    }

    /// <summary>
    /// The struct case. Both routes produce the same value; only one of them has to box it to
    /// get it back to the caller.
    /// </summary>
    [Fact]
    public void A_struct_destination_can_be_mapped_without_the_dispatcher()
    {
        var mapper = Mappers.Fresh();
        var brand = new Brand { Id = 1, FoundedYear = 1994 };

        BrandKeyDto direct = mapper.MapToBrandKeyDto(brand);

        Assert.Equal(1, direct.Id);
        Assert.Equal(1994, direct.FoundedYear);
        Assert.Equal(mapper.Map<BrandKeyDto>(brand), direct);
    }

    [Fact]
    public void A_direct_method_rejects_null_like_every_other_entry_point()
    {
        var mapper = Mappers.Fresh();

        Assert.Throws<ArgumentNullException>(() => mapper.MapToBrandDto(null!));
    }

    private sealed class PrefixNumbering : IInvoiceNumbering
    {
        public PrefixNumbering(string prefix) => Prefix = prefix;

        public string Prefix { get; }
    }
}
