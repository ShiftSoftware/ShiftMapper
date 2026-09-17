using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// OTHER MAPPER CLASSES, running for real.
///
/// The generator tests already show the right code comes out. What only running can show is that
/// each mapper class was CONSTRUCTED and its registrations reached the generated mapper's store —
/// because a <c>MapFrom</c> written in a mapper class is emitted as a lookup, and a lookup that
/// finds nothing leaves the member empty rather than failing loudly.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class IncludeTests
{
    private readonly DatabaseFixture _fixture;

    public IncludeTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>THE TEST THAT MATTERS: the mapper class ran, and its expression was found.</summary>
    [Fact]
    public void A_MapFrom_written_in_another_mapper_class_is_registered_at_runtime()
    {
        GadgetDto dto = _fixture.Mapper.Map<GadgetDto>(new Gadget { Name = "Widget", Code = "42" });

        Assert.Equal("G-42", dto.Code);
        Assert.Equal("Widget", dto.Name);
    }

    /// <summary>And in the projection, which collects the same registrations by a different path.</summary>
    [Fact]
    public void Another_mapper_classs_MapFrom_reaches_the_projection()
    {
        Gadget[] gadgets = [new Gadget { Name = "Widget", Code = "42" }, new Gadget { Name = "Cog", Code = "7" }];

        List<GadgetDto> projected = _fixture.Mapper
            .ProjectTo<GadgetDto>(gadgets.AsQueryable())
            .ToList();

        Assert.Equal(["G-42", "G-7"], projected.Select(dto => dto.Code));
    }

    /// <summary>The two backends agree about a member configured in another mapper class.</summary>
    [Fact]
    public void The_two_backends_agree_about_a_member_from_another_class()
    {
        Gadget[] gadgets = [new Gadget { Name = "Widget", Code = "42" }];

        GadgetDto inMemory = _fixture.Mapper.Map<GadgetDto>(gadgets[0]);
        GadgetDto projected = _fixture.Mapper.ProjectTo<GadgetDto>(gadgets.AsQueryable()).Single();

        Assert.Equal(inMemory.Code, projected.Code);
    }

    /// <summary>
    /// A MAPPER CLASS WITH A DEPENDENCY is built from the container, on first use — after the
    /// generated mapper's <c>Services</c> is assigned. Nothing registered it: the generated
    /// mapper's metadata says it composes NumberedMapper, and the container builds the class with
    /// its IInvoiceNumbering injected.
    /// </summary>
    [Fact]
    public void A_mapper_class_with_a_dependency_is_built_from_DI()
    {
        DoodadDto dto = _fixture.Mapper.Map<DoodadDto>(new Doodad { Name = "thing" });

        string prefix = _fixture.Services.GetRequiredService<IInvoiceNumbering>().Prefix;

        Assert.Equal(prefix + "thing", dto.Label);
    }

    /// <summary>
    /// AND THE SHARP EDGE, stated as a test so it cannot change by accident: the generated mapper
    /// builds EVERY mapper class at once, so one class that needs DI makes the whole assembly's
    /// mapper DI-only. Built outside a container it throws on the first map — with a message
    /// naming the class — rather than mapping on with its configuration quietly missing.
    /// </summary>
    [Fact]
    public void A_mapper_built_outside_a_container_cannot_use_a_class_that_needs_DI()
    {
        Mapper mapper = Mapper.Create(typeof(TestMapper).Assembly);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => mapper.Map<DoodadDto>(new Doodad { Name = "thing" }));

        Assert.Contains("NumberedMapper", error.Message);
        Assert.Contains("AddShiftMapper", error.Message);
    }

    /// <summary>
    /// INCLUDEBASE ACROSS CLASSES, at run time. The base map is in one class and the derived map in
    /// another, so the expression is stored under the BASE pair and reached through the lineage —
    /// which had to survive the merge of two separate stores into the generated mapper's one.
    /// </summary>
    [Fact]
    public void IncludeBase_across_two_mapper_classes_resolves_at_runtime()
    {
        PremiumGadgetDto dto = _fixture.Mapper.Map<PremiumGadgetDto>(
            new PremiumGadget { Name = "Deluxe", Code = "77", Rank = 1 });

        Assert.Equal("G-77", dto.Code);
        Assert.Equal(1, dto.Rank);
    }

    [Fact]
    public void IncludeBase_across_two_mapper_classes_reaches_the_projection()
    {
        PremiumGadget[] gadgets = [new PremiumGadget { Name = "Deluxe", Code = "77", Rank = 1 }];

        PremiumGadgetDto projected = _fixture.Mapper
            .ProjectTo<PremiumGadgetDto>(gadgets.AsQueryable())
            .Single();

        Assert.Equal("G-77", projected.Code);
        Assert.Equal(1, projected.Rank);
    }

    /// <summary>
    /// Every Mapper instance builds its own mapper classes. Sharing them would be a cross-request
    /// leak, since a mapper class may capture a scoped service.
    /// </summary>
    [Fact]
    public void Each_mapper_instance_materialises_its_own_classes()
    {
        Mapper first = Mappers.Fresh();
        Mapper second = Mappers.Fresh();

        Assert.NotSame(first, second);
        Assert.Equal("G-9", first.Map<GadgetDto>(new Gadget { Code = "9" }).Code);
        Assert.Equal("G-9", second.Map<GadgetDto>(new Gadget { Code = "9" }).Code);
    }

    /// <summary>Mapping twice does not build the classes twice, and keeps working.</summary>
    [Fact]
    public void Classes_are_materialised_once_and_stay_usable()
    {
        Mapper mapper = _fixture.Mapper;

        Assert.Equal("G-1", mapper.Map<GadgetDto>(new Gadget { Code = "1" }).Code);
        Assert.Equal("G-2", mapper.Map<GadgetDto>(new Gadget { Code = "2" }).Code);
        Assert.Equal("G-3", mapper.Map<GadgetDto>(new Gadget { Code = "3" }).Code);
    }
}
