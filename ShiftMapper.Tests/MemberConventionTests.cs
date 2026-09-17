using Contoso.Platform;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// MEMBER-SHAPED CONVENTIONS, running for real, and declared in a REFERENCED ASSEMBLY.
///
/// <para>The generator tests show the right code comes out. What only running can show is that the
/// two backends agree — the whole reason this is a compile-time member-init rather than an
/// <c>AfterMap</c>, which would work here and vanish from every projection.</para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MemberConventionTests
{
    private readonly DatabaseFixture _fixture;

    public MemberConventionTests(DatabaseFixture fixture) => _fixture = fixture;

    private Mapper Mapper => _fixture.Mapper;

    private static FiledDocument Document => new()
    {
        FolderId = 7,
        Folder = new Folder { Id = 7, Name = "Contracts" },
    };

    /// <summary>
    /// THE TEST THAT MATTERS: a shaped member filled by a rule this project never wrote, from a
    /// package that has never seen these types.
    /// </summary>
    [Fact]
    public void A_shaped_member_is_filled_in_memory()
    {
        FiledDocumentDto dto = Mapper.Map<FiledDocumentDto>(Document);

        // "H7", NOT "7" - and that is the two kinds of rule meeting. The convention says Value comes
        // from {Member}ID; the value then goes through the ORDINARY conversion table, where the same
        // package's long-to-string hash-id rule is waiting. Neither rule mentions the other.
        Assert.Equal("H7", dto.Folder.Value);
        Assert.Equal("Contracts", dto.Folder.Text);
    }

    /// <summary>And in the PROJECTION, which is the half an AfterMap could never reach.</summary>
    [Fact]
    public void A_shaped_member_is_filled_in_a_projection()
    {
        FiledDocument[] documents = [Document];

        FiledDocumentDto projected = Mapper
            .ProjectTo<FiledDocumentDto>(documents.AsQueryable())
            .Single();

        Assert.Equal("H7", projected.Folder.Value);
        Assert.Equal("Contracts", projected.Folder.Text);
    }

    /// <summary>
    /// The two backends agree, which is the only acceptable answer and the entire argument for
    /// resolving the rule to text at compile time.
    /// </summary>
    [Fact]
    public void The_two_backends_agree()
    {
        FiledDocument[] documents = [Document];

        FiledDocumentDto inMemory = Mapper.Map<FiledDocumentDto>(documents[0]);
        FiledDocumentDto projected = Mapper.ProjectTo<FiledDocumentDto>(documents.AsQueryable()).Single();

        Assert.Equal(inMemory.Folder.Value, projected.Folder.Value);
        Assert.Equal(inMemory.Folder.Text, projected.Folder.Text);
    }

    /// <summary>
    /// THE NULL GUARD. A navigation that was not loaded is ordinary data, not a mistake, so the
    /// in-memory path answers with the default rather than throwing.
    /// </summary>
    [Fact]
    public void A_missing_navigation_does_not_throw_in_memory()
    {
        FiledDocumentDto dto = Mapper.Map<FiledDocumentDto>(new FiledDocument { FolderId = 7 });

        Assert.Equal("H7", dto.Folder.Value);
        Assert.True(string.IsNullOrEmpty(dto.Folder.Text));
    }

    /// <summary>
    /// THE WRITE DIRECTION, derived from the reversible entry: the id comes back off the shaped
    /// member. The text does not, and should not.
    /// </summary>
    [Fact]
    public void The_reverse_direction_fills_the_id()
    {
        FiledDocument entity = Mapper.Map<FiledDocument>(new FiledDocumentDto
        {
            // Plain digits here: the package's hash rule is long-to-string only, so the way back
            // uses the ordinary parse. A framework wanting a round trip would declare both halves.
            Folder = new SelectDto { Value = "7", Text = "Contracts" },
        });

        Assert.Equal(7, entity.FolderId);
    }

    // -----------------------------------------------------------------
    // ONE RULE, TWO SHAPES.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE ID-ONLY SHAPE: the related type nominates no display member, so the optional entry drops
    /// and the id is still set — in BOTH backends, from the same one rule.
    ///
    /// <para>With a required <c>Fill</c> this would be an unmapped member and a build warning, and
    /// the framework would need a second rule for every entity that leaves its label to whatever
    /// renders it.</para>
    /// </summary>
    [Fact]
    public void An_entity_that_nominates_no_name_gets_the_id_only()
    {
        ShelvedDocument[] documents =
            [new ShelvedDocument { BinId = 7, Bin = new Bin { Id = 7, Name = "Bay 12" } }];

        ShelvedDocumentDto inMemory = Mapper.Map<ShelvedDocumentDto>(documents[0]);
        ShelvedDocumentDto projected = Mapper.ProjectTo<ShelvedDocumentDto>(documents.AsQueryable()).Single();

        // The id, through the package's hash rule as always.
        Assert.Equal("H7", inMemory.Bin.Value);
        Assert.Equal("H7", projected.Bin.Value);

        // And no text — note the Bin HAS a Name. Nothing nominated it, so nothing reads it.
        Assert.True(string.IsNullOrEmpty(inMemory.Bin.Text));
        Assert.True(string.IsNullOrEmpty(projected.Bin.Text));
    }

    /// <summary>
    /// The other way an entry drops: a source carrying the foreign key and NO navigation beside it,
    /// which is the shape of a request body. Here the type DOES nominate a name — there is simply
    /// nothing to read it from.
    /// </summary>
    [Fact]
    public void A_source_with_only_the_key_still_fills_the_id()
    {
        LooseDocument[] documents = [new LooseDocument { FolderId = 7 }];

        FiledDocumentDto inMemory = Mapper.Map<FiledDocumentDto>(documents[0]);
        FiledDocumentDto projected = Mapper.ProjectTo<FiledDocumentDto>(documents.AsQueryable()).Single();

        Assert.Equal("H7", inMemory.Folder.Value);
        Assert.Equal("H7", projected.Folder.Value);

        Assert.True(string.IsNullOrEmpty(inMemory.Folder.Text));
        Assert.True(string.IsNullOrEmpty(projected.Folder.Text));
    }
}
