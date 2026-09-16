using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// INCLUDED MAPPERS, running for real.
///
/// The generator tests already show the right code comes out. What only running can show is that
/// the included mapper was CONSTRUCTED and its registrations reached the including mapper's store
/// — because a <c>MapFrom</c> written in an included mapper is emitted as a lookup, and a lookup
/// that finds nothing leaves the member empty rather than failing loudly.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class IncludeTests
{
    private readonly DatabaseFixture _fixture;

    public IncludeTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>THE TEST THAT MATTERS: the included mapper ran, and its expression was found.</summary>
    [Fact]
    public void A_MapFrom_written_in_an_included_mapper_is_registered_at_runtime()
    {
        GadgetDto dto = _fixture.Mapper.Map<GadgetDto>(new Gadget { Name = "Widget", Code = "42" });

        Assert.Equal("G-42", dto.Code);
        Assert.Equal("Widget", dto.Name);
    }

    /// <summary>And in the projection, which collects the same registrations by a different path.</summary>
    [Fact]
    public void An_included_mappers_MapFrom_reaches_the_projection()
    {
        Gadget[] gadgets = [new Gadget { Name = "Widget", Code = "42" }, new Gadget { Name = "Cog", Code = "7" }];

        List<GadgetDto> projected = _fixture.Mapper
            .ProjectTo<GadgetDto>(gadgets.AsQueryable())
            .ToList();

        Assert.Equal(["G-42", "G-7"], projected.Select(dto => dto.Code));
    }

    /// <summary>The two backends agree about a member configured in an included mapper.</summary>
    [Fact]
    public void The_two_backends_agree_about_an_included_member()
    {
        Gadget[] gadgets = [new Gadget { Name = "Widget", Code = "42" }];

        GadgetDto inMemory = _fixture.Mapper.Map<GadgetDto>(gadgets[0]);
        GadgetDto projected = _fixture.Mapper.ProjectTo<GadgetDto>(gadgets.AsQueryable()).Single();

        Assert.Equal(inMemory.Code, projected.Code);
    }

    /// <summary>
    /// AN INCLUDED MAPPER IS A MAPPER: it has its own generated methods and can be used on its own,
    /// resolved from the container where <c>AddShiftMapper</c> registered it along with the mapper
    /// that includes it.
    /// </summary>
    [Fact]
    public void An_included_mapper_is_injectable_and_maps_on_its_own()
    {
        GadgetMapper gadgets = _fixture.Services.GetRequiredService<GadgetMapper>();

        Assert.Equal("G-5", gadgets.Map<GadgetDto>(new Gadget { Code = "5" }).Code);
        Assert.Equal("G-5", gadgets.MapToGadgetDto(new Gadget { Code = "5" }).Code);
    }

    /// <summary>
    /// AN INCLUDED MAPPER WITH A DEPENDENCY, resolved from the including mapper's own service
    /// provider.
    ///
    /// This is the case the whole materialisation timing exists for: it cannot be built while the
    /// including mapper's constructor runs, because <c>Services</c> is not assigned until after it
    /// returns. If included mappers were built eagerly this would throw instead of mapping.
    /// </summary>
    [Fact]
    public void An_included_mapper_with_a_dependency_is_resolved_from_DI()
    {
        DoodadDto dto = _fixture.IncludingMapper.Map<DoodadDto>(new Doodad { Name = "thing" });

        string prefix = _fixture.Services.GetRequiredService<IInvoiceNumbering>().Prefix;

        Assert.Equal(prefix + "thing", dto.Label);
    }

    /// <summary>
    /// AND THE SHARP EDGE, stated as a test so it cannot change by accident: a mapper builds
    /// EVERYTHING it includes at once, so one include that needs DI makes the whole mapper DI-only.
    /// Constructing it by hand throws on the first map — with a message naming the included mapper
    /// — rather than mapping on with its configuration quietly missing.
    /// </summary>
    [Fact]
    public void A_hand_built_mapper_cannot_use_an_included_mapper_that_needs_DI()
    {
        var mapper = new IncludingMapper();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => mapper.Map<DoodadDto>(new Doodad { Name = "thing" }));

        Assert.Contains("NumberedMapper", error.Message);
        Assert.Contains("AddShiftMapper", error.Message);
    }

    /// <summary>
    /// INCLUDEBASE ACROSS MAPPERS, at run time. The base map is in one mapper and the derived map
    /// in another, so the expression is stored under the BASE pair and reached through the lineage
    /// — which had to survive the merge of two separate stores into the including mapper's one.
    /// </summary>
    [Fact]
    public void IncludeBase_across_two_included_mappers_resolves_at_runtime()
    {
        PremiumGadgetDto dto = _fixture.Mapper.Map<PremiumGadgetDto>(
            new PremiumGadget { Name = "Widget", Code = "42", Rank = 3 });

        Assert.Equal("G-42", dto.Code);
        Assert.Equal(3, dto.Rank);
    }

    /// <summary>And the same across mappers inside a projection.</summary>
    [Fact]
    public void IncludeBase_across_two_included_mappers_reaches_the_projection()
    {
        PremiumGadget[] gadgets = [new PremiumGadget { Name = "Widget", Code = "42", Rank = 3 }];

        PremiumGadgetDto projected = _fixture.Mapper
            .ProjectTo<PremiumGadgetDto>(gadgets.AsQueryable())
            .Single();

        Assert.Equal("G-42", projected.Code);
        Assert.Equal(3, projected.Rank);
    }

    /// <summary>
    /// An included mapper without dependencies needs no registration at all — a hand-built mapper
    /// constructs it directly, which is what keeps a plain unit test usable.
    /// </summary>
    [Fact]
    public void A_parameterless_included_mapper_works_without_a_container()
    {
        var mapper = new TestMapper(new InvoiceNumbering());

        Assert.Equal("G-1", mapper.Map<GadgetDto>(new Gadget { Code = "1" }).Code);
    }

    /// <summary>
    /// Every instance of the mapper builds its own included mappers. Sharing them would be a
    /// cross-request leak, since an included mapper may capture a scoped service.
    /// </summary>
    [Fact]
    public void Each_mapper_instance_materialises_its_own_includes()
    {
        TestMapper first = _fixture.Mapper;
        TestMapper second = _fixture.Mapper;

        Assert.Equal("G-9", first.Map<GadgetDto>(new Gadget { Code = "9" }).Code);
        Assert.Equal("G-9", second.Map<GadgetDto>(new Gadget { Code = "9" }).Code);
    }

    /// <summary>Mapping twice does not build the includes twice, and keeps working.</summary>
    [Fact]
    public void Includes_are_materialised_once_and_stay_usable()
    {
        TestMapper mapper = _fixture.Mapper;

        Assert.Equal("G-1", mapper.Map<GadgetDto>(new Gadget { Code = "1" }).Code);
        Assert.Equal("G-2", mapper.Map<GadgetDto>(new Gadget { Code = "2" }).Code);
        Assert.Equal("G-3", mapper.Map<GadgetDto>(new Gadget { Code = "3" }).Code);
    }

    /// <summary>
    /// TWO MAPPERS THAT INCLUDE EACH OTHER terminate at run time as they do in the generator: the
    /// union of what they declare, built once.
    /// </summary>
    [Fact]
    public void Mappers_that_include_each_other_terminate()
    {
        var left = new LeftMapper();
        var right = new RightMapper();

        Assert.Equal("Widget", left.Map<GadgetDto>(new Gadget { Name = "Widget" }).Name);
        Assert.Equal("thing", right.Map<DoodadDto>(new Doodad { Name = "thing" }).Label);
        Assert.Equal("thing", left.Map<DoodadDto>(new Doodad { Name = "thing" }).Label);
    }
}
