using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ShiftMapper.Tests.Model;

/// <summary>
/// One seeded SQLite database, shared by every test that needs a real query.
///
/// SQLITE RATHER THAN THE EF IN-MEMORY PROVIDER, deliberately. The in-memory provider runs LINQ
/// against objects, so it happily "translates" things no database can — a projection that only
/// works because the provider fell back to C# would pass there and fail in production. SQLite
/// makes the translation real: if the generated expression cannot become SQL, the test throws.
///
/// The connection is held open for the fixture's lifetime because a SQLite in-memory database
/// exists only while a connection to it does.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public DatabaseFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();

        // Registered exactly as an application would, so the tests exercise the real path:
        // constructor injection, plus the Services property AddShiftMapper fills in. What each
        // mapper includes (NumberedMapper, with its dependency; GadgetMapper, without) is
        // registered along with it — nothing is registered by hand.
        //
        // FOUR mappers, so IMapper resolves to a composite over all of them.
        services.AddShiftMapper();

        _services = services.BuildServiceProvider();

        using TestDbContext context = CreateContext();
        context.Database.EnsureCreated();
        Seed(context);
    }

    /// <summary>A mapper resolved from DI, the way application code gets one.</summary>
    /// <summary>THE mapper: every map declared in this project, and in Contoso.Platform, behind one object.</summary>
    public Mapper Mapper => _services.GetRequiredService<Mapper>();

    /// <summary>The mapper including the mapper that needs DI.</summary>
    public IServiceProvider Services => _services;

    public TestDbContext CreateContext()
    {
        DbContextOptions<TestDbContext> options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new TestDbContext(options);
    }

    private static void Seed(TestDbContext context)
    {
        var acme = new Brand
        {
            Id = 1,
            Name = "Acme",
            Country = "Iraq",
            ISOCode = "IQ",
            FoundedYear = 1994,
            Tags = new List<string> { "tools", "industrial" },
            Aliases = new List<string> { "ACME Corp" },
        };

        var globex = new Brand
        {
            Id = 2,
            Name = "Globex",
            Country = "Turkey",
            ISOCode = "TR",
            FoundedYear = 2001,
            Tags = new List<string> { "electronics" },

            // LEFT NULL ON PURPOSE. This is the row the null-collection policy is measured on,
            // and a seeded empty list would have proved nothing.
            Aliases = null,
        };

        var erbil = new Stock
        {
            Id = 1,
            Name = "Erbil Main",
            City = "Erbil",
            Code = "EBL",
            BayNumbers = new List<int> { 1, 2, 3 },
        };

        var duhok = new Stock
        {
            Id = 2,
            Name = "Duhok Depot",
            City = "Duhok",
            Code = "DHK",
            BayNumbers = new List<int> { 7 },
        };

        var hammer = new Product
        {
            Id = 1,
            Name = "Hammer",
            Sku = "HM-1",
            Price = 12.50m,
            QuantityOnHand = 40,
            BrandId = acme.Id,
            StockId = erbil.Id,
        };

        var drill = new Product
        {
            Id = 2,
            Name = "Drill",
            Sku = "DR-9",
            Price = 99.00m,
            QuantityOnHand = 5,
            BrandId = globex.Id,
            StockId = duhok.Id,
        };

        var first = new Invoice
        {
            Id = 1,
            Number = "0001",
            CustomerName = "Ali",
            CustomerEmail = "ali@example.com",
            IssuedAt = new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc),
        };

        var second = new Invoice
        {
            Id = 2,
            Number = "0002",
            CustomerName = "Sara",
            CustomerEmail = "sara@example.com",
            IssuedAt = new DateTime(2026, 2, 1, 14, 0, 0, DateTimeKind.Utc),
        };

        context.AddRange(acme, globex, erbil, duhok, hammer, drill, first, second);

        context.AddRange(
            new InvoiceLine { Id = 1, InvoiceId = first.Id, ProductId = hammer.Id, Quantity = 2, UnitPrice = 12.50m },
            new InvoiceLine { Id = 2, InvoiceId = first.Id, ProductId = drill.Id, Quantity = 1, UnitPrice = 99.00m },
            new InvoiceLine { Id = 3, InvoiceId = second.Id, ProductId = hammer.Id, Quantity = 4, UnitPrice = 12.50m });

        context.SaveChanges();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// Creating and seeding the database once is worth more than the isolation of doing it per class
/// — nothing in the suite writes to it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
