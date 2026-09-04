using System.Linq.Expressions;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// A mapper whose only job is to hand the tests its customization store, which is
/// <c>protected</c> on <see cref="ShiftMapperBase"/> because the generated half of a mapper is
/// the only thing that should normally touch it.
/// </summary>
public partial class CustomizationProbe : ShiftMapperBase
{
    public CustomizationProbe()
    {
        CreateMap<Brand, BrandDto>()
            .ForMember(d => d.Country, opt => opt.MapFrom(s => s.Country + " (" + s.ISOCode + ")"));

        // Registered and then withdrawn, which is what "last call wins" means at runtime.
        CreateMap<Stock, StockDto>()
            .ForMember(d => d.Code, opt => opt.MapFrom(s => s.Code.ToUpperInvariant()))
            .ForMember(d => d.Code, opt => opt.Ignore());
    }

    public MapCustomizations Store => Customizations;
}

/// <summary>
/// <see cref="MapCustomizations"/> — where the expressions handed to <c>MapFrom</c> live, and the
/// one piece of the declaration API that does real work at runtime.
/// </summary>
public class MapCustomizationsTests
{
    private static MapCustomizations Store => new CustomizationProbe().Store;

    [Fact]
    public void A_registered_expression_is_recorded_against_its_map_and_member()
    {
        MapCustomizations store = Store;

        Assert.True(store.Has(typeof(Brand), typeof(BrandDto), "Country"));

        // Not for a member nobody customized, and not for some other pair of types that happens
        // to have a member of the same name.
        Assert.False(store.Has(typeof(Brand), typeof(BrandDto), "Name"));
        Assert.False(store.Has(typeof(Stock), typeof(StockDto), "Country"));
    }

    /// <summary>
    /// <c>Ignore</c> after a <c>MapFrom</c> withdraws the expression. Without that, the abandoned
    /// tree would still be sitting in the store — and Compose binds everything it finds, so the
    /// in-memory maps would leave the property alone while a projection quietly filled it.
    /// </summary>
    [Fact]
    public void An_Ignore_after_a_MapFrom_withdraws_the_expression()
    {
        Assert.False(Store.Has(typeof(Stock), typeof(StockDto), "Code"));
    }

    [Fact]
    public void A_customization_is_compiled_once_and_reused()
    {
        MapCustomizations store = Store;

        Func<Brand, string> first = store.Value<Brand, BrandDto, string>("Country");
        Func<Brand, string> second = store.Value<Brand, BrandDto, string>("Country");

        Assert.Same(first, second);
        Assert.Equal("Iraq (IQ)", first(new Brand { Country = "Iraq", ISOCode = "IQ" }));
    }

    /// <summary>
    /// Asking for a customization that was never registered means the generated code and this
    /// store disagree, which can only happen with a stale generated file — so the message says
    /// so rather than throwing a bare KeyNotFoundException.
    /// </summary>
    [Fact]
    public void Asking_for_a_customization_nobody_registered_says_to_rebuild()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Store.Value<Brand, BrandDto, string>("Name"));

        Assert.Contains("BrandDto.Name", error.Message);
        Assert.Contains("Rebuild", error.Message);
    }

    // -----------------------------------------------------------------
    // COMPOSE — merging the customizations into the generated projection.
    // -----------------------------------------------------------------

    /// <summary>
    /// The result has to stay ONE member initializer over ONE parameter, because that is the only
    /// shape EF turns into a SELECT list. So the expression is INLINED into the initializer
    /// rather than invoked from it.
    /// </summary>
    [Fact]
    public void Compose_inlines_the_customization_into_the_member_initializer()
    {
        Expression<Func<Brand, BrandDto>> conventions =
            source => new BrandDto { Name = source.Name };

        Expression<Func<Brand, BrandDto>> composed =
            Store.Compose(conventions);

        var init = Assert.IsType<MemberInitExpression>(composed.Body);
        Assert.Contains(init.Bindings, binding => binding.Member.Name == "Country");
        Assert.Single(composed.Parameters);

        // Compiling proves the parameter swap worked: the customization was written against its
        // own parameter object, and an expression referring to a parameter its lambda does not
        // declare cannot be compiled at all.
        BrandDto mapped = composed.Compile()(new Brand { Name = "Acme", Country = "Iraq", ISOCode = "IQ" });

        Assert.Equal("Acme", mapped.Name);
        Assert.Equal("Iraq (IQ)", mapped.Country);
    }

    /// <summary>
    /// The generator already leaves customized properties out of the conventions. Dropping them
    /// here as well keeps Compose correct on its own terms rather than on trust — and binding the
    /// same member twice in one initializer would not even compile.
    /// </summary>
    [Fact]
    public void Compose_replaces_a_convention_for_a_customized_property()
    {
        Expression<Func<Brand, BrandDto>> conventions =
            source => new BrandDto { Name = source.Name, Country = source.Country };

        Expression<Func<Brand, BrandDto>> composed = Store.Compose(conventions);

        var init = (MemberInitExpression)composed.Body;

        Assert.Single(init.Bindings, binding => binding.Member.Name == "Country");
        Assert.Equal("Iraq (IQ)", composed.Compile()(new Brand { Country = "Iraq", ISOCode = "IQ" }).Country);
    }

    /// <summary>A map with nothing to merge gets its own expression back, untouched.</summary>
    [Fact]
    public void Compose_returns_the_conventions_unchanged_when_there_is_nothing_to_merge()
    {
        Expression<Func<Stock, StockDto>> conventions =
            source => new StockDto { Name = source.Name };

        Assert.Same(conventions, Store.Compose(conventions));
    }

    [Fact]
    public void Compose_rejects_a_projection_that_is_not_an_object_initializer()
    {
        // A METHOD CALL. A bare `new BrandDto()` is accepted — see the test below — because a
        // destination built entirely through its constructor has nothing left to initialise, and
        // that is exactly what a record projection looks like.
        Expression<Func<Brand, BrandDto>> notAnInitializer = source => Make(source);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Store.Compose(notAnInitializer, new MapCustomizations.NestedBinding(
                "Name", "Name", (Expression<Func<Brand, string>>)(brand => brand.Name), null)));

        Assert.Contains("is not an object initializer", error.Message);
    }

    private static BrandDto Make(Brand brand) => new() { Name = brand.Name };

    /// <summary>
    /// A bare <c>new</c> IS an object initializer as far as this is concerned — one with no
    /// members in it. That is the shape a record's projection takes, and refusing it would have
    /// meant no record could be projected while carrying a MapFrom.
    /// </summary>
    [Fact]
    public void Compose_accepts_a_projection_that_is_a_bare_construction()
    {
        Expression<Func<Brand, BrandDto>> construction = source => new BrandDto();

        // The probe's own MapFrom for Country is the customization being merged in, and where it
        // has to land is a member initializer this method builds from nothing.
        Expression<Func<Brand, BrandDto>> composed = Store.Compose(construction);

        BrandDto dto = composed.Compile()(new Brand { Country = "Iraq", ISOCode = "IQ" });

        Assert.Equal("Iraq (IQ)", dto.Country);
    }

    // -----------------------------------------------------------------
    // NESTED BINDINGS — how a child map is grafted into its parent.
    // -----------------------------------------------------------------

    /// <summary>
    /// A nested map cannot be a method call: EF has to read the projection all the way down, and
    /// a call to another mapping method is opaque. So the child's own composed projection is
    /// grafted in.
    /// </summary>
    [Fact]
    public void A_nested_binding_is_inlined_rather_than_invoked()
    {
        Expression<Func<Brand, BrandDto>> child =
            brand => new BrandDto { Name = brand.Name };

        Expression<Func<Product, ProductDto>> parent =
            product => new ProductDto { Name = product.Name };

        Expression<Func<Product, ProductDto>> composed = new CustomizationProbe().Store.Compose(
            parent,
            new MapCustomizations.NestedBinding("Brand", "Brand", child, Builder: null));

        ProductDto mapped = composed.Compile()(new Product
        {
            Name = "Hammer",
            Brand = new Brand { Name = "Acme" },
        });

        Assert.Equal("Acme", mapped.Brand.Name);

        // No Invoke anywhere in the tree — that is the difference between one SQL query and EF
        // loading whole entities to run C# over them.
        Assert.DoesNotContain("Invoke", composed.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A nullable navigation is a LEFT JOIN that can come back empty, so the DTO should be null
    /// rather than an object full of defaults.
    /// </summary>
    [Fact]
    public void A_nullable_nested_binding_guards_against_no_related_row()
    {
        Expression<Func<Brand, BrandDto>> child = brand => new BrandDto { Name = brand.Name };
        Expression<Func<Product, ProductDto>> parent = product => new ProductDto { Name = product.Name };

        Expression<Func<Product, ProductDto>> composed = new CustomizationProbe().Store.Compose(
            parent,
            new MapCustomizations.NestedBinding("Brand", "Brand", child, Builder: null, Nullable: true));

        ProductDto mapped = composed.Compile()(new Product { Name = "Hammer", Brand = null! });

        Assert.Null(mapped.Brand);
    }

    /// <summary>
    /// A collection is written as Select followed by the shape the destination wants — the same
    /// pair of calls the compiler emits for a hand-written correlated projection, which is
    /// exactly why EF recognises it.
    /// </summary>
    [Fact]
    public void A_collection_nested_binding_selects_then_builds_the_shape()
    {
        Expression<Func<InvoiceLine, InvoiceLineDto>> child =
            line => new InvoiceLineDto { Quantity = line.Quantity };

        Expression<Func<Invoice, InvoiceDto>> parent =
            invoice => new InvoiceDto { CustomerName = invoice.CustomerName };

        Expression<Func<Invoice, InvoiceDto>> composed = new CustomizationProbe().Store.Compose(
            parent,
            new MapCustomizations.NestedBinding("Lines", "Lines", child, Builder: "ToList"));

        InvoiceDto mapped = composed.Compile()(new Invoice
        {
            CustomerName = "Ali",
            Lines =
            {
                new InvoiceLine { Quantity = 2 },
                new InvoiceLine { Quantity = 5 },
            },
        });

        Assert.Equal(new[] { 2, 5 }, mapped.Lines.Select(line => line.Quantity));
    }
}
