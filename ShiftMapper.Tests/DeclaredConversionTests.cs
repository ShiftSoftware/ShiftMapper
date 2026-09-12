using Contoso.Platform;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// THE COMPILE-TIME EXTENSION CONTRACT, running across a REAL assembly boundary.
///
/// <para><c>Contoso.Platform</c> is referenced as a compiled library — no source, no analyzer,
/// nothing but metadata. Nothing in this project declares a conversion. Everything below works
/// because of two assembly attributes over there, which is the whole claim of the contract.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DeclaredConversionTests
{
    private readonly DatabaseFixture _fixture;

    public DeclaredConversionTests(DatabaseFixture fixture) => _fixture = fixture;

    private DeclaredMapper Mapper => _fixture.DeclaredMapper;

    /// <summary>
    /// A conversion declared by a referenced assembly runs. It is a DIRECT CALL in the generated
    /// code rather than a lookup, so this also proves the generator resolved the name from
    /// metadata rather than hoping something would be registered later.
    /// </summary>
    [Fact]
    public void A_declared_conversion_runs_in_memory()
    {
        DocumentDto dto = Mapper.Map<DocumentDto>(new Document
        {
            Id = 42,
            Files = """[{"name":"a.pdf","url":"/a.pdf"}]""",
        });

        Assert.Equal("H42", dto.Id);
        Assert.Single(dto.Files);
        Assert.Equal("a.pdf", dto.Files[0].Name);
    }

    /// <summary>
    /// AND IT BEATS THE BUILT-IN TABLE. <c>long</c> to <c>string</c> already converts, so without
    /// this the id would be "42" and the framework's rule would have been ignored in silence.
    /// </summary>
    [Fact]
    public void A_declared_conversion_beats_the_built_in_one()
    {
        Assert.Equal("H42", Mapper.Map<DocumentDto>(new Document { Id = 42 }).Id);
    }

    /// <summary>It reaches collection elements, because the rule is written for a PAIR.</summary>
    [Fact]
    public void A_declared_conversion_reaches_collection_elements()
    {
        DocumentDto dto = Mapper.Map<DocumentDto>(new Document { Related = [7, 8] });

        Assert.Equal(["H7", "H8"], dto.Related);
    }

    /// <summary>
    /// THE HALF THAT ONLY RUNNING CAN SHOW: the query form registered by the generated mapper is
    /// spliced into the projection. The memory form is called by name and needs nothing at run
    /// time; the tree is the one thing a name cannot stand in for.
    /// </summary>
    [Fact]
    public void A_declared_query_form_is_spliced_into_a_projection()
    {
        Document[] documents = [new Document { Id = 42, Related = [7] }, new Document { Id = 43 }];

        List<DocumentIdDto> projected = Mapper
            .ProjectTo<DocumentIdDto>(documents.AsQueryable())
            .ToList();

        Assert.Equal(["H42", "H43"], projected.Select(dto => dto.Id));
        Assert.Equal(["H7"], projected[0].Related);
    }

    /// <summary>
    /// AND THE TWO BACKENDS AGREE, which for a package's rules is the thing nothing else checks.
    /// The framework author writes both forms by hand; only running both can show they match.
    /// </summary>
    [Fact]
    public void The_two_backends_agree_about_a_declared_conversion()
    {
        Document[] documents = [new Document { Id = 42, Related = [7, 8] }];

        DocumentIdDto inMemory = Mapper.Map<DocumentIdDto>(documents[0]);
        DocumentIdDto projected = Mapper.ProjectTo<DocumentIdDto>(documents.AsQueryable()).Single();

        Assert.Equal(inMemory.Id, projected.Id);
        Assert.Equal(inMemory.Related, projected.Related);
    }

    /// <summary>
    /// A pair the framework declared WITHOUT a query form costs the projection, and says which pair
    /// — the build said the same thing as SM0030 while compiling this file.
    /// </summary>
    [Fact]
    public void A_declared_pair_with_no_query_form_refuses_to_project()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            #pragma warning disable SM0037 // the throw is exactly what this test asserts
            () => Mapper.ProjectTo<DocumentDto>(Array.Empty<Document>().AsQueryable()));
            #pragma warning restore SM0037

        Assert.Contains("no query form", error.Message);
        Assert.Contains("FileDto", error.Message);
    }

    /// <summary>But the same map is unaffected in memory, which is what "Map is unaffected" means.</summary>
    [Fact]
    public void The_same_map_still_works_in_memory()
    {
        DocumentDto dto = Mapper.Map<DocumentDto>(new Document
        {
            Files = """[{"name":"b.pdf"},{"name":"c.pdf"}]""",
        });

        Assert.Equal(["b.pdf", "c.pdf"], dto.Files.Select(file => file.Name));
    }
}
