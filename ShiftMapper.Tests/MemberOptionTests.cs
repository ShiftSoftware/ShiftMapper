using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// <c>MapFromSource</c> and <c>Condition</c>, running for real.
///
/// They answer two different questions about the one line the generator writes. The first is
/// WHERE the value comes from and who converts it; the second is WHETHER the assignment happens
/// at all. Only the first survives into a projection, and the tests below are arranged so that
/// fact is visible rather than asserted.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MemberOptionTests
{
    private readonly DatabaseFixture _fixture;

    public MemberOptionTests(DatabaseFixture fixture) => _fixture = fixture;

    // -----------------------------------------------------------------
    // MAPFROMSOURCE.
    // -----------------------------------------------------------------

    [Fact]
    public void A_source_typed_expression_is_converted_by_the_table()
    {
        using TestDbContext context = _fixture.CreateContext();

        Invoice invoice = context.Invoices.Include(i => i.Lines).OrderBy(i => i.Id).First();

        InvoiceCountDto dto = _fixture.Mapper.Map<InvoiceCountDto>(invoice);

        // Two lines on the first seeded invoice, written out as text by ValueConverter rather
        // than by a hand-written ToString in the map.
        Assert.Equal("2", dto.LineCount);
    }

    /// <summary>
    /// AND IT PROJECTS, which is what separates this from converting by hand. The conversion
    /// travels into the projection as a lambda the generated file wrote, spliced onto the
    /// developer's own expression rather than invoked from it — so EF sees one expression and
    /// turns the count into a correlated subquery.
    /// </summary>
    [Fact]
    public void A_source_typed_expression_projects()
    {
        using TestDbContext context = _fixture.CreateContext();

        List<InvoiceCountDto> dtos = _fixture.Mapper
            .ProjectTo<InvoiceCountDto>(context.Invoices.OrderBy(invoice => invoice.Id))
            .ToList();

        Assert.Equal(["2", "1"], dtos.Select(dto => dto.LineCount));
    }

    /// <summary>
    /// And the two backends agree, which is the whole reason to route the value through the
    /// conversion table instead of writing <c>.ToString()</c> in the map. A hand-written one has
    /// no format provider, so it reads the machine's culture and the two halves would differ on a
    /// German server while agreeing on this one.
    /// </summary>
    [Fact]
    public void The_two_backends_agree_about_a_converted_value()
    {
        using TestDbContext context = _fixture.CreateContext();
        Mapper mapper = _fixture.Mapper;

        List<Invoice> entities = context.Invoices
            .Include(invoice => invoice.Lines)
            .OrderBy(invoice => invoice.Id)
            .ToList();

        List<InvoiceCountDto> projected = mapper
            .ProjectTo<InvoiceCountDto>(context.Invoices.OrderBy(invoice => invoice.Id))
            .ToList();

        for (int i = 0; i < entities.Count; i++)
            Assert.Equal(mapper.Map<InvoiceCountDto>(entities[i]).LineCount, projected[i].LineCount);
    }

    /// <summary>The count really is worked out by the database, not by loading every line.</summary>
    [Fact]
    public void The_conversion_becomes_part_of_the_query()
    {
        using TestDbContext context = _fixture.CreateContext();

        string sql = _fixture.Mapper
            .ProjectTo<InvoiceCountDto>(context.Invoices)
            .ToQueryString();

        Assert.Contains("COUNT(*)", sql);
        Assert.DoesNotContain("Quantity", sql);
    }

    // -----------------------------------------------------------------
    // CONDITION — the update overload, which is what it is for.
    // -----------------------------------------------------------------

    /// <summary>
    /// The PUT problem, and the point of the whole feature. Without a condition every mapped
    /// member is assigned every time, so a caller who sent one field blanks the rest.
    /// </summary>
    [Fact]
    public void A_declined_condition_leaves_the_destination_alone()
    {
        var existing = new Profile { Name = "Ali", City = "Erbil", Age = 41 };

        // Only the city was sent. Name arrives as "", Age as 0.
        var update = new ProfileUpdate { City = "Duhok" };

        Profile returned = _fixture.Mapper.Map(update, existing);

        Assert.Same(existing, returned);
        Assert.Equal("Duhok", returned.City);

        // Untouched — not set to the default, not blanked.
        Assert.Equal("Ali", returned.Name);
        Assert.Equal(41, returned.Age);
    }

    [Fact]
    public void An_accepted_condition_assigns_as_usual()
    {
        var existing = new Profile { Name = "Ali", City = "Erbil", Age = 41 };
        var update = new ProfileUpdate { Name = "Sara", City = "Duhok", Age = 30 };

        Profile returned = _fixture.Mapper.Map(update, existing);

        Assert.Equal("Sara", returned.Name);
        Assert.Equal("Duhok", returned.City);
        Assert.Equal(30, returned.Age);
    }

    /// <summary>
    /// ON A CREATE, "left alone" means the object's OWN initializer. The map builds the
    /// destination and then assigns the conditioned members, so a declined one keeps whatever the
    /// property was declared with rather than being set to <c>default</c>.
    /// </summary>
    [Fact]
    public void On_a_create_a_declined_condition_leaves_the_property_initializer()
    {
        Profile created = _fixture.Mapper.Map<Profile>(new ProfileUpdate { City = "Duhok" });

        Assert.Equal("Duhok", created.City);
        Assert.Equal("unset-name", created.Name);
        Assert.Equal(-1, created.Age);
    }

    /// <summary>The predicate is given the value about to be assigned, and the destination as it stands.</summary>
    [Fact]
    public void The_predicate_sees_the_value_and_the_destination()
    {
        Profile created = _fixture.Mapper.Map<Profile>(
            new ProfileUpdate { Name = "   ", City = "Erbil", Age = -5 });

        // Whitespace and a negative age are what the two predicates refuse.
        Assert.Equal("unset-name", created.Name);
        Assert.Equal(-1, created.Age);
        Assert.Equal("Erbil", created.City);
    }

    /// <summary>
    /// AND THE MAP HAS NO PROJECTION. A projection is one member initializer handed to the
    /// database; there is no way to leave a binding out per row. The build says so as SM0017, and
    /// asking anyway throws a message naming the map rather than quietly returning data that
    /// disagrees with <c>Map</c>.
    /// </summary>
    [Fact]
    public void A_conditioned_map_cannot_be_projected_and_says_so()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            #pragma warning disable SM0037 // the throw is exactly what this test asserts
            () => _fixture.Mapper.ProjectTo<Profile>(Array.Empty<ProfileUpdate>().AsQueryable()));
            #pragma warning restore SM0037

        Assert.Contains("behind a Condition", error.Message);
        Assert.Contains("drop the Condition", error.Message);
    }

    // -----------------------------------------------------------------
    // THE DELEGATE CACHE.
    // -----------------------------------------------------------------

    /// <summary>
    /// The customization delegate is now held in a FIELD rather than fetched per mapped object.
    /// The field is per INSTANCE, and this is the test that would catch it being made static: two
    /// mappers with different injected services must give different answers.
    /// </summary>
    [Fact]
    public void A_cached_customization_is_still_per_mapper_instance()
    {
        var invoice = new Invoice { Id = 1, Number = "0001" };

        var first = Mappers.With(new Numbering("A/"));
        var second = Mappers.With(new Numbering("B/"));

        // Mapped twice through each, so the second call is the one that reads the cached field.
        Assert.Equal("A/0001", first.Map<InvoiceDto>(invoice).Number);
        Assert.Equal("A/0001", first.Map<InvoiceDto>(invoice).Number);
        Assert.Equal("B/0001", second.Map<InvoiceDto>(invoice).Number);
        Assert.Equal("B/0001", second.Map<InvoiceDto>(invoice).Number);
    }

    /// <summary>The cache changes nothing about what a customization produces, only how often it is fetched.</summary>
    [Fact]
    public void A_cached_customization_returns_the_same_value_every_time()
    {
        using TestDbContext context = _fixture.CreateContext();

        // The whole graph: InvoiceDto carries lines, which carry products, which carry a brand
        // and a stock. Map throws on a null source, so a half-loaded entity is a null Product.
        Invoice invoice = context.Invoices
            .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Brand)
            .Include(i => i.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Stock)
            .OrderBy(i => i.Id)
            .First();

        Mapper mapper = _fixture.Mapper;

        Assert.Equal(mapper.Map<InvoiceDto>(invoice).Total, mapper.Map<InvoiceDto>(invoice).Total);
        Assert.Equal(mapper.Map<InvoiceDto>(invoice).Number, mapper.Map<InvoiceDto>(invoice).Number);
    }

    private sealed class Numbering : IInvoiceNumbering
    {
        public Numbering(string prefix) => Prefix = prefix;

        public string Prefix { get; }
    }
}
