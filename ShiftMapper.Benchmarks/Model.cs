namespace ShiftMapper.Benchmarks;

/// <summary>A service to inject, so one customization closes over the mapper and one does not.</summary>
public interface INumbering
{
    string Prefix { get; }
}

public sealed class Numbering : INumbering
{
    public Numbering(string prefix = "IQ/") => Prefix = prefix;

    public string Prefix { get; }
}

public class Brand
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public string ISOCode { get; set; } = string.Empty;

    public int FoundedYear { get; set; }

    public List<string> Tags { get; set; } = new();
}

public class BrandDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    /// <summary>int to string — a conversion, so the map is not a straight copy of every field.</summary>
    public string FoundedYear { get; set; } = string.Empty;

    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Filled from <c>ISOCode</c> by the case-insensitive fallback.</summary>
    public string IsoCode { get; set; } = string.Empty;
}

public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public Brand Brand { get; set; } = null!;
}

public class ProductDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public BrandDto Brand { get; set; } = null!;
}

public class InvoiceLine
{
    public int Id { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public Product Product { get; set; } = null!;
}

public class InvoiceLineDto
{
    public int Id { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineTotal { get; set; }

    public ProductDto Product { get; set; } = null!;
}

public class Invoice
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    public List<InvoiceLine> Lines { get; set; } = new();
}

public class InvoiceDto
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    public decimal Total { get; set; }

    public IReadOnlyList<InvoiceLineDto> Lines { get; set; } = [];
}

/// <summary>
/// The mapper class under measurement. Three levels of nesting, a conversion, a collection, and two
/// customizations — one of which closes over the injected service and one of which does not,
/// because that difference is what decides whether a compiled delegate can be reused across
/// instances. Nothing is generated onto it: the benchmarks map through the <see cref="Mapper"/>
/// that <see cref="Container"/> builds, which holds this assembly's generated mapper.
/// </summary>
public class BenchmarkMapper : ShiftMapperBase
{
    private readonly INumbering _numbering;

    public BenchmarkMapper(INumbering numbering)
    {
        _numbering = numbering;

        CreateMap<Brand, BrandDto>();
        CreateMap<Product, ProductDto>();

        CreateMap<InvoiceLine, InvoiceLineDto>()
            .ForMember(d => d.LineTotal, opt => opt.MapFrom(s => s.Quantity * s.UnitPrice));

        CreateMap<Invoice, InvoiceDto>()
            .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Lines.Sum(l => l.Quantity * l.UnitPrice)))
            // Closes over the mapper, so this one is compiled per instance — see MapCustomizations.
            .ForMember(d => d.Number, opt => opt.MapFrom(s => _numbering.Prefix + s.Number));
    }
}

/// <summary>
/// The container every benchmark resolves its <see cref="Mapper"/> from — registered the way an
/// application registers it, with the service the mapper class needs.
/// </summary>
public static class Container
{
    public static Microsoft.Extensions.DependencyInjection.ServiceProvider Build() =>
        Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
            Microsoft.Extensions.DependencyInjection.ShiftMapperServiceCollectionExtensions.AddShiftMapper(
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<INumbering, Numbering>(
                    new Microsoft.Extensions.DependencyInjection.ServiceCollection())));
}

/// <summary>Builds the object graph the benchmarks map, so setup is not part of what is measured.</summary>
public static class Sample
{
    public static Brand Brand() => new()
    {
        Id = 1,
        Name = "Acme",
        Country = "Iraq",
        ISOCode = "IQ",
        FoundedYear = 1994,
        Tags = new List<string> { "tools", "industrial" },
    };

    public static Invoice Invoice(int lines = 10)
    {
        Brand brand = Brand();

        var product = new Product
        {
            Id = 1,
            Name = "Hammer",
            Sku = "HM-1",
            Price = 12.50m,
            Brand = brand,
        };

        var invoice = new Invoice
        {
            Id = 1,
            Number = "0001",
            CustomerName = "Ali",
            IssuedAt = new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc),
        };

        for (int i = 1; i <= lines; i++)
        {
            invoice.Lines.Add(new InvoiceLine
            {
                Id = i,
                Quantity = i,
                UnitPrice = 12.50m,
                Product = product,
            });
        }

        return invoice;
    }

    public static List<Brand> Brands(int count)
    {
        var brands = new List<Brand>(count);

        for (int i = 0; i < count; i++)
        {
            Brand brand = Brand();
            brand.Id = i;
            brands.Add(brand);
        }

        return brands;
    }
}
