using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// <c>IShiftMapper</c> at runtime — the door a library comes in through.
///
/// The point of the interface is that code can be written against it without naming
/// <c>TestMapper</c>, so most of these tests go through <see cref="PretendFramework"/> at the foot
/// of the file, which is written exactly the way a library would have to write it: one
/// constructor parameter, of the interface type, and no idea what implements it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ShiftMapperInterfaceTests
{
    private readonly DatabaseFixture _fixture;

    public ShiftMapperInterfaceTests(DatabaseFixture fixture) => _fixture = fixture;

    private IShiftMapper Mapper => _fixture.Services.GetRequiredService<IShiftMapper>();

    private static Brand ABrand() => new()
    {
        Id = 7,
        Name = "Acme",
        Country = "Iraq",
        ISOCode = "IQ",
        FoundedYear = 1994,
        Tags = new List<string> { "tools" },
    };

    // -----------------------------------------------------------------
    // Creating
    // -----------------------------------------------------------------

    [Fact]
    public void A_source_typed_only_as_object_is_mapped_by_its_runtime_type()
    {
        object source = ABrand();

        BrandDto dto = Mapper.Map<BrandDto>(source);

        Assert.Equal("Acme", dto.Name);
        Assert.Equal("IQ", dto.IsoCode);
        Assert.Equal("1994", dto.FoundedYear);
    }

    [Fact]
    public void Both_types_can_be_given_as_type_arguments()
    {
        BrandDto dto = Mapper.Map<Brand, BrandDto>(ABrand());

        Assert.Equal("Acme", dto.Name);
    }

    /// <summary>
    /// The two create doors agree, member for member. They are different code paths into the same
    /// generated method, and a difference between them would be the kind of bug nobody looks for.
    /// </summary>
    [Fact]
    public void The_two_create_doors_produce_the_same_values()
    {
        Brand brand = ABrand();

        BrandDto viaObject = Mapper.Map<BrandDto>(brand);
        BrandDto viaTypeArguments = Mapper.Map<Brand, BrandDto>(brand);
        BrandDto viaTheMapperItself = _fixture.Mapper.Map<BrandDto>(brand);

        Assert.Equal(viaTheMapperItself.Name, viaObject.Name);
        Assert.Equal(viaTheMapperItself.IsoCode, viaObject.IsoCode);
        Assert.Equal(viaTheMapperItself.FoundedYear, viaTypeArguments.FoundedYear);
        Assert.Equal(viaTheMapperItself.Tags, viaTypeArguments.Tags);
    }

    /// <summary>
    /// A SUBCLASS of a mapped type maps through its base. This is not a nicety: an EF entity read
    /// through lazy-loading proxies is an instance of a generated subclass, and refusing it would
    /// make the object door useless in exactly the applications this interface is for.
    /// </summary>
    [Fact]
    public void A_subclass_of_a_mapped_type_maps_through_its_base()
    {
        object proxy = new BrandProxy { Name = "Acme", ISOCode = "IQ", FoundedYear = 1994 };

        BrandDto dto = Mapper.Map<BrandDto>(proxy);

        Assert.Equal("Acme", dto.Name);
    }

    /// <summary>
    /// The same fallback, reached the other way: TSource is a type nobody registered, so the pair
    /// door hands over to the runtime type rather than refusing. Generic library code binds
    /// TSource to whatever its own caller had, which is very often a base — or object.
    /// </summary>
    [Fact]
    public void An_unmapped_type_argument_falls_through_to_the_runtime_type()
    {
        BrandDto dto = Mapper.Map<object, BrandDto>(ABrand());

        Assert.Equal("Acme", dto.Name);
    }

    /// <summary>A struct destination comes back boxed, and correct — the documented cost of this door.</summary>
    [Fact]
    public void A_struct_destination_is_mapped_too()
    {
        BrandKeyDto key = Mapper.Map<Brand, BrandKeyDto>(ABrand());

        Assert.Equal(7, key.Id);
        Assert.Equal(1994, key.FoundedYear);
    }

    // -----------------------------------------------------------------
    // Updating
    // -----------------------------------------------------------------

    [Fact]
    public void An_existing_destination_is_updated_in_place_and_handed_back()
    {
        var existing = new BrandDto { Name = "stale", Country = "stale" };

        BrandDto returned = Mapper.Map<Brand, BrandDto>(ABrand(), existing);

        Assert.Same(existing, returned);
        Assert.Equal("Acme", existing.Name);
        Assert.Equal("Iraq", existing.Country);
    }

    /// <summary>
    /// The update door needs the exact declared pair, and says so. There is no runtime-type
    /// fallback here on purpose: the destination handed in is the object being written to, and
    /// choosing a different map for it would write different members than the caller asked for.
    /// </summary>
    [Fact]
    public void Updating_through_an_unmapped_type_argument_is_refused()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Mapper.Map<object, BrandDto>(ABrand(), new BrandDto()));

        Assert.Contains("no map registered", error.Message);
        Assert.Contains("onto an existing", error.Message);
    }

    /// <summary>A struct has no update method, and the message says why rather than leaving it odd.</summary>
    [Fact]
    public void Updating_a_struct_destination_is_refused_with_the_reason()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Mapper.Map<Brand, BrandKeyDto>(ABrand(), default));

        Assert.Contains("would write to a copy", error.Message);
    }

    // -----------------------------------------------------------------
    // Projecting — the reason the interface is worth having
    // -----------------------------------------------------------------

    /// <summary>
    /// A library can hand EF one expression for the whole graph without knowing which mapper the
    /// application registered. Asserted on the SQL, not the values: values would come out right
    /// even if EF had given up and run the projection in C#.
    /// </summary>
    [Fact]
    public void A_projection_through_the_interface_is_translated_to_sql()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = Mapper.ProjectTo<Brand, BrandDto>(context.Brands).ToQueryString();

        Assert.Contains("FROM \"Brands\"", sql);
        Assert.Contains("CAST(\"b\".\"FoundedYear\" AS TEXT)", sql);
        Assert.DoesNotContain("Products", sql);
    }

    [Fact]
    public void A_projection_through_the_interface_returns_the_same_rows()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<string> viaInterface = Mapper.ProjectTo<Brand, BrandDto>(context.Brands)
            .OrderBy(b => b.Id).Select(b => b.Name).ToList();

        List<string> viaMapper = _fixture.Mapper.ProjectTo<BrandDto>(context.Brands)
            .OrderBy(b => b.Id).Select(b => b.Name).ToList();

        Assert.Equal(viaMapper, viaInterface);
    }

    /// <summary>
    /// A queryable's element type is fixed when it is created, so there is no runtime value to
    /// fall back to and an unmapped TSource can only be refused.
    /// </summary>
    [Fact]
    public void Projecting_from_an_unmapped_source_is_refused()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Mapper.ProjectTo<string, BrandDto>(new List<string>().AsQueryable()));

        Assert.Contains("no map registered", error.Message);
        Assert.Contains("CreateMap<Source, Destination>()", error.Message);
    }

    // -----------------------------------------------------------------
    // CanMap
    // -----------------------------------------------------------------

    [Fact]
    public void CanMap_answers_for_a_declared_pair()
    {
        Assert.True(Mapper.CanMap(typeof(Brand), typeof(BrandDto)));
        Assert.True(Mapper.CanMap(typeof(Product), typeof(ProductDto)));

        // Registered by ReverseMap, which is a map like any other.
        Assert.True(Mapper.CanMap(typeof(StockDto), typeof(Stock)));

        // A struct destination is still a destination Map can produce.
        Assert.True(Mapper.CanMap(typeof(Brand), typeof(BrandKeyDto)));
    }

    [Fact]
    public void CanMap_is_false_for_a_pair_nobody_declared()
    {
        Assert.False(Mapper.CanMap(typeof(Brand), typeof(ProductDto)));
        Assert.False(Mapper.CanMap(typeof(string), typeof(BrandDto)));
        Assert.False(Mapper.CanMap(typeof(Brand), typeof(Brand)));
    }

    /// <summary>
    /// CanMap follows the create doors rule for rule, subclasses included. An answer that said
    /// false where Map succeeds would send framework code down a fallback path for a map that
    /// works perfectly well — which is the exact mistake the method exists to prevent.
    /// </summary>
    [Fact]
    public void CanMap_agrees_with_Map_about_a_subclass()
    {
        Assert.True(Mapper.CanMap(typeof(BrandProxy), typeof(BrandDto)));

        BrandDto dto = Mapper.Map<BrandDto>(new BrandProxy { Name = "Acme" });

        Assert.Equal("Acme", dto.Name);
    }

    // -----------------------------------------------------------------
    // Failures and guards
    // -----------------------------------------------------------------

    [Fact]
    public void A_pair_with_no_map_throws_a_message_naming_both_types_and_the_fix()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Mapper.Map<ProductDto>(ABrand()));

        Assert.Contains("Brand", error.Message);
        Assert.Contains("ProductDto", error.Message);
        Assert.Contains("CreateMap<Source, Destination>()", error.Message);
    }

    [Fact]
    public void Every_door_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => Mapper.Map<BrandDto>(null!));
        Assert.Throws<ArgumentNullException>(() => Mapper.Map<Brand, BrandDto>(null!));
        Assert.Throws<ArgumentNullException>(() => Mapper.Map<Brand, BrandDto>(ABrand(), null!));
        Assert.Throws<ArgumentNullException>(() => Mapper.Map<Brand, BrandDto>(null!, new BrandDto()));
        Assert.Throws<ArgumentNullException>(() => Mapper.ProjectTo<Brand, BrandDto>(null!));
        Assert.Throws<ArgumentNullException>(() => Mapper.CanMap(null!, typeof(BrandDto)));
        Assert.Throws<ArgumentNullException>(() => Mapper.CanMap(typeof(Brand), null!));
    }

    // -----------------------------------------------------------------
    // Registration
    // -----------------------------------------------------------------

    /// <summary>
    /// Two registrations, one object. A library injecting the interface and the application
    /// injecting its own mapper have to be talking to the same mapper — a second instance would
    /// mean a second customization store and, for a scoped mapper, a second set of everything it
    /// was injected with.
    /// </summary>
    [Fact]
    public void The_interface_and_the_mapper_resolve_to_the_same_instance()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<Mapper>(),
            scope.ServiceProvider.GetRequiredService<IShiftMapper>());
    }

    [Fact]
    public void The_interface_keeps_the_lifetime_it_was_registered_with()
    {
        using ServiceProvider provider = BuildProvider();

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IShiftMapper>(),
            second.ServiceProvider.GetRequiredService<IShiftMapper>());
    }

    /// <summary>
    /// The acceptance test for the interface: a class written the way a library has to write
    /// one — no mention of TestMapper anywhere in it — reads, writes and projects.
    /// </summary>
    [Fact]
    public void A_library_that_never_names_the_mapper_can_map()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        var framework = ActivatorUtilities.CreateInstance<PretendFramework>(scope.ServiceProvider);

        Assert.True(framework.Handles<Brand, BrandDto>());
        Assert.Equal("Acme", framework.Read<Brand, BrandDto>(ABrand()).Name);

        using TestDbContext context = _fixture.CreateContext();
        Assert.Contains("FROM \"Brands\"", framework.List<Brand, BrandDto>(context.Brands).ToQueryString());
    }

    /// <summary>
    /// A Mapper built with nothing registered serves the typed methods — their generated mapper is
    /// built on first use — and refuses the run-time door with a message that says what to do.
    /// </summary>
    [Fact]
    public void A_mapper_with_nothing_registered_refuses_the_runtime_door_and_says_why()
    {
        IShiftMapper bare = new Mapper();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => bare.CanMap(typeof(Brand), typeof(BrandDto)));

        Assert.Contains("no registered generated mapper", error.Message);
        Assert.Contains("AddShiftMapper()", error.Message);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
        services.AddShiftMapper();

        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Stands in for an EF lazy-loading proxy: a subclass of a mapped entity that nothing declared a
/// map for.
/// </summary>
public class BrandProxy : Brand
{
}

/// <summary>
/// A framework, in miniature. Every method here is generic over the entity and the DTO, and
/// nothing in the class names a mapper — which is the whole thing the interface has to make possible.
/// </summary>
public sealed class PretendFramework
{
    private readonly IShiftMapper _mapper;

    public PretendFramework(IShiftMapper mapper) => _mapper = mapper;

    public bool Handles<TEntity, TDto>() => _mapper.CanMap(typeof(TEntity), typeof(TDto));

    public TDto Read<TEntity, TDto>(TEntity entity) => _mapper.Map<TEntity, TDto>(entity);

    public IQueryable<TDto> List<TEntity, TDto>(IQueryable<TEntity> entities) =>
        _mapper.ProjectTo<TEntity, TDto>(entities);
}
