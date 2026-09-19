using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// The in-memory half of the two backends: the generated <c>Map</c> methods.
/// </summary>
public class MappingTests
{
    private static Mapper NewMapper() => Mappers.Fresh();

    private static Brand NewBrand() => new()
    {
        Id = 1,
        Name = "Acme",
        Country = "Iraq",
        ISOCode = "IQ",
        FoundedYear = 1994,
        Tags = new List<string> { "tools", "industrial" },
    };

    private static Stock NewStock() => new()
    {
        Id = 7,
        Name = "Erbil Main",
        City = "Erbil",
        Code = "EBL",
        BayNumbers = new List<int> { 1, 2, 3 },
    };

    // -----------------------------------------------------------------
    // MATCHING AND CONVERSION
    // -----------------------------------------------------------------

    [Fact]
    public void Properties_that_match_by_name_are_copied()
    {
        BrandDto dto = NewMapper().Map<BrandDto>(NewBrand());

        Assert.Equal(1, dto.Id);
        Assert.Equal("Acme", dto.Name);
        Assert.Equal("Iraq", dto.Country);
    }

    /// <summary>An entity spelling a column <c>ISOCode</c> still fills a DTO's <c>IsoCode</c>.</summary>
    [Fact]
    public void The_case_insensitive_fallback_fills_a_differently_spelled_property()
    {
        Assert.Equal("IQ", NewMapper().Map<BrandDto>(NewBrand()).IsoCode);
    }

    [Fact]
    public void A_property_whose_type_differs_is_converted()
    {
        Assert.Equal("1994", NewMapper().Map<BrandDto>(NewBrand()).FoundedYear);
    }

    /// <summary>
    /// The DTO gets its OWN list. Sharing the entity's would mean adding to the DTO quietly adds
    /// to the entity EF is tracking.
    /// </summary>
    [Fact]
    public void A_collection_is_copied_rather_than_shared()
    {
        Brand brand = NewBrand();

        BrandDto dto = NewMapper().Map<BrandDto>(brand);

        Assert.Equal(new[] { "tools", "industrial" }, dto.Tags);
        Assert.NotSame(brand.Tags, dto.Tags);
    }

    [Fact]
    public void A_collection_whose_elements_differ_is_converted_one_element_at_a_time()
    {
        StockTextDto dto = NewMapper().Map<StockTextDto>(NewStock());

        Assert.Equal(new[] { "1", "2", "3" }, dto.BayNumbers);
    }

    // -----------------------------------------------------------------
    // REVERSEMAP
    // -----------------------------------------------------------------

    [Fact]
    public void A_reversed_map_runs_its_conversions_the_other_way()
    {
        Mapper mapper = NewMapper();

        StockDto dto = mapper.Map<StockDto>(NewStock());
        Assert.Equal("7", dto.Id);

        Stock back = mapper.Map<Stock>(dto);
        Assert.Equal(7, back.Id);
        Assert.Equal("Erbil Main", back.Name);
    }

    /// <summary>
    /// Empty text is what arrives when a client POSTs something with no id yet, and it reads back
    /// as the destination's default rather than throwing.
    /// </summary>
    [Fact]
    public void Empty_text_reads_back_as_the_destinations_default()
    {
        Stock created = NewMapper().Map<Stock>(new StockDto { Id = string.Empty, Name = "New" });

        Assert.Equal(0, created.Id);
    }

    /// <summary>Text that is not empty and does not parse names both properties as it fails.</summary>
    [Fact]
    public void Text_that_does_not_parse_throws_naming_the_two_properties()
    {
        FormatException error = Assert.ThrowsAny<FormatException>(
            () => NewMapper().Map<Stock>(new StockDto { Id = "not-a-number" }));

        Assert.Contains("StockDto.Id -> Stock.Id", error.Message);
    }

    /// <summary>An ignored property keeps whatever its own initializer gave it.</summary>
    [Fact]
    public void An_ignored_property_is_left_alone()
    {
        Stock back = NewMapper().Map<Stock>(new StockDto { Id = "7" });

        Assert.Empty(back.Products);
    }

    [Fact]
    public void Elements_convert_in_both_directions()
    {
        Mapper mapper = NewMapper();

        StockTextDto dto = mapper.Map<StockTextDto>(NewStock());
        Stock back = mapper.Map<Stock>(dto);

        Assert.Equal(new[] { 1, 2, 3 }, back.BayNumbers);
    }

    // -----------------------------------------------------------------
    // FORMEMBER
    // -----------------------------------------------------------------

    [Fact]
    public void A_customized_property_is_filled_from_its_expression()
    {
        InvoiceLineDto dto = NewMapper().Map<InvoiceLineDto>(new InvoiceLine
        {
            Id = 1,
            Quantity = 3,
            UnitPrice = 12.50m,
            Product = new Product { Brand = new Brand(), Stock = new Stock() },
        });

        Assert.Equal(37.50m, dto.LineTotal);
    }

    /// <summary>
    /// A service injected into the mapper's constructor is an ordinary field to the expression,
    /// and the generated code lives in the same partial class — so nothing has to be set up.
    /// </summary>
    [Fact]
    public void A_customization_can_use_an_injected_service()
    {
        InvoiceDto dto = NewMapper().Map<InvoiceDto>(new Invoice { Number = "0001" });

        Assert.Equal("IQ/0001", dto.Number);
    }

    // -----------------------------------------------------------------
    // NESTED OBJECTS
    // -----------------------------------------------------------------

    [Fact]
    public void A_nested_object_maps_through_its_own_map()
    {
        ProductDto dto = NewMapper().Map<ProductDto>(new Product
        {
            Id = 1,
            Name = "Hammer",
            Price = 12.50m,
            Brand = NewBrand(),
            Stock = NewStock(),
        });

        Assert.Equal("Acme", dto.Brand.Name);
        // The nested map's own conversions apply, exactly as they do at the top level.
        Assert.Equal("1994", dto.Brand.FoundedYear);
        Assert.Equal("7", dto.Stock.Id);
    }

    /// <summary>
    /// Four levels, with nothing configured beyond the maps themselves — and the nested map's own
    /// MapFrom still applies down there.
    /// </summary>
    [Fact]
    public void The_whole_graph_maps_as_deep_as_the_maps_that_were_declared()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Hammer",
            Price = 12.50m,
            Brand = NewBrand(),
            Stock = NewStock(),
        };

        var invoice = new Invoice
        {
            Id = 5,
            Number = "0001",
            CustomerName = "Ali",
            IssuedAt = new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc),
            Lines =
            {
                new InvoiceLine { Id = 1, Quantity = 2, UnitPrice = 12.50m, Product = product },
                new InvoiceLine { Id = 2, Quantity = 1, UnitPrice = 99.00m, Product = product },
            },
        };

        InvoiceDto dto = NewMapper().Map<InvoiceDto>(invoice);

        Assert.Equal(124.00m, dto.Total);
        Assert.Equal(2, dto.Lines.Count);
        Assert.Equal(25.00m, dto.Lines[0].LineTotal);
        Assert.Equal("Hammer", dto.Lines[0].Product.Name);
        Assert.Equal("Acme", dto.Lines[0].Product.Brand.Name);
        Assert.Equal("IQ", dto.Lines[0].Product.Brand.IsoCode);
        Assert.Equal("Erbil Main", dto.Lines[0].Product.Stock.Name);
    }

    // -----------------------------------------------------------------
    // THE GENERATED SURFACE
    // -----------------------------------------------------------------

    /// <summary>
    /// The overload that copies onto an object it was handed returns that same object rather than
    /// a new one — which is what makes it usable for updating a tracked entity.
    /// </summary>
    [Fact]
    public void The_update_overload_fills_the_instance_it_was_given()
    {
        var existing = new BrandDto { Name = "stale" };

        BrandDto returned = NewMapper().Map(NewBrand(), existing);

        Assert.Same(existing, returned);
        Assert.Equal("Acme", existing.Name);
    }

    /// <summary>The extension spelling forwards to the instance and does the same work.</summary>
    [Fact]
    public void The_extension_methods_do_the_same_thing_as_the_instance_methods()
    {
        Mapper mapper = NewMapper();
        Brand brand = NewBrand();

        BrandDto viaInstance = mapper.Map<BrandDto>(brand);
        BrandDto viaExtension = brand.Map<BrandDto>(mapper);

        Assert.Equal(viaInstance.Name, viaExtension.Name);
        Assert.Equal(viaInstance.IsoCode, viaExtension.IsoCode);
        Assert.Equal(viaInstance.FoundedYear, viaExtension.FoundedYear);
    }

    [Fact]
    public void Mapping_null_throws_rather_than_returning_an_empty_object()
    {
        Assert.Throws<ArgumentNullException>(() => NewMapper().Map<BrandDto>((Brand)null!));
        Assert.Throws<ArgumentNullException>(() => NewMapper().Map(NewBrand(), (BrandDto)null!));
    }

    /// <summary>
    /// The dispatcher is generic, so asking for a destination nobody registered cannot be a
    /// compile error — the message has to say what to do instead.
    /// </summary>
    [Fact]
    public void Asking_for_a_destination_nobody_registered_says_what_to_add()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => NewMapper().Map<InvoiceDto>(NewBrand()));

        Assert.Contains("no map registered from", error.Message);
        Assert.Contains("CreateMap<Source, Destination>()", error.Message);
    }
}
