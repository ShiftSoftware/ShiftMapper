using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// Inheritance, polymorphism and open generics, running for real.
///
/// The one that most needs a running test is <c>IncludeBase</c>. Everything the developer writes is
/// stored against the type pair it was written for, so a base map's <c>MapFrom</c> is under
/// <c>AuditEntity → AuditDto</c> and a derived map asking under its own pair would find nothing.
/// The generated code cannot show whether that lookup succeeds; only running it can.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class InheritanceTests
{
    private readonly DatabaseFixture _fixture;

    public InheritanceTests(DatabaseFixture fixture) => _fixture = fixture;

    // -----------------------------------------------------------------
    // INCLUDEBASE.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE TEST THAT MATTERS. The base map's <c>MapFrom</c> is stored under the BASE pair, and the
    /// derived map resolves it by walking the lineage — in memory.
    /// </summary>
    [Fact]
    public void An_inherited_MapFrom_resolves_through_the_base_pair()
    {
        WidgetDto dto = _fixture.Mapper.Map<WidgetDto>(
            new Widget { Name = "Sprocket", Tag = "t1", Secret = "hidden" });

        Assert.Equal("audit:t1", dto.Tag);
        Assert.Equal("Sprocket", dto.Name);

        // The base map's Ignore was inherited too, so nothing filled it.
        Assert.Equal("untouched", dto.Secret);
    }

    /// <summary>
    /// And the same again in a PROJECTION, which is a different code path entirely: Compose
    /// collects customizations by type pair, so it has to walk the same lineage or the member
    /// would come back empty while Map filled it.
    /// </summary>
    [Fact]
    public void An_inherited_MapFrom_reaches_the_projection_too()
    {
        Widget[] widgets =
        [
            new Widget { Name = "Sprocket", Tag = "t1", Secret = "hidden" },
            new Widget { Name = "Cog", Tag = "t2", Secret = "hidden" },
        ];

        List<WidgetDto> projected = _fixture.Mapper
            .ProjectTo<WidgetDto>(widgets.AsQueryable())
            .ToList();

        Assert.Equal(["audit:t1", "audit:t2"], projected.Select(dto => dto.Tag));
        Assert.All(projected, dto => Assert.Equal("untouched", dto.Secret));
    }

    /// <summary>
    /// The two backends agree, which is the whole reason the lineage lives in the runtime store
    /// rather than only in the generator.
    /// </summary>
    [Fact]
    public void The_two_backends_agree_about_an_inherited_member()
    {
        Widget[] widgets = [new Widget { Name = "Sprocket", Tag = "t1", Secret = "hidden" }];

        WidgetDto inMemory = _fixture.Mapper.Map<WidgetDto>(widgets[0]);
        WidgetDto projected = _fixture.Mapper.ProjectTo<WidgetDto>(widgets.AsQueryable()).Single();

        Assert.Equal(inMemory.Tag, projected.Tag);
        Assert.Equal(inMemory.Secret, projected.Secret);
        Assert.Equal(inMemory.Name, projected.Name);
    }

    // -----------------------------------------------------------------
    // INCLUDE — polymorphism.
    // -----------------------------------------------------------------

    /// <summary>
    /// A base-typed value that is really derived maps through the derived pair. Without this it
    /// would map to a plain <c>ShapeDto</c> and the radius would be lost in silence.
    /// </summary>
    [Fact]
    public void A_derived_value_maps_through_the_derived_pair()
    {
        Shape shape = new Circle { Name = "c1", Radius = 5 };

        ShapeDto dto = _fixture.Mapper.Map<ShapeDto>(shape);

        CircleDto circle = Assert.IsType<CircleDto>(dto);
        Assert.Equal(5, circle.Radius);
        Assert.Equal("c1", circle.Name);
    }

    /// <summary>A value that really is only the base still maps as the base.</summary>
    [Fact]
    public void A_base_value_still_maps_as_the_base()
    {
        ShapeDto dto = _fixture.Mapper.Map<ShapeDto>(new Shape { Name = "s1" });

        Assert.IsType<ShapeDto>(dto, exactMatch: true);
        Assert.Equal("s1", dto.Name);
    }

    /// <summary>The collection overloads dispatch per ELEMENT, which is where it earns its keep.</summary>
    [Fact]
    public void Every_element_of_a_collection_dispatches_on_its_own()
    {
        Shape[] shapes = [new Circle { Name = "c", Radius = 3 }, new Shape { Name = "s" }];

        List<ShapeDto> dtos = _fixture.Mapper.Map<List<ShapeDto>>(shapes);

        Assert.IsType<CircleDto>(dtos[0]);
        Assert.IsType<ShapeDto>(dtos[1], exactMatch: true);
    }

    /// <summary>
    /// AND THE MAP HAS NO PROJECTION. A projection has one element type, fixed when the query is
    /// written, so asking throws a message that names the alternative rather than returning rows
    /// that quietly disagree with <c>Map</c>.
    /// </summary>
    [Fact]
    public void A_dispatching_map_cannot_be_projected_and_says_so()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => _fixture.Mapper.ProjectTo<ShapeDto>(Array.Empty<Shape>().AsQueryable()));

        Assert.Contains("dispatches on the source's runtime type", error.Message);
        Assert.Contains("OfType<Circle>()", error.Message);
    }

    // -----------------------------------------------------------------
    // AS — an interface destination.
    // -----------------------------------------------------------------

    [Fact]
    public void An_interface_destination_is_built_as_the_concrete_type()
    {
        IWidgetDto dto = _fixture.Mapper.Map<IWidgetDto>(new Widget { Name = "Sprocket" });

        Assert.IsType<ConcreteWidgetDto>(dto);
        Assert.Equal("Sprocket", dto.Name);
    }

    /// <summary>
    /// AND IT PROJECTS, unlike <c>Include</c> — the concrete type was fixed when the map was
    /// declared, so there is no per-row decision for a provider to translate.
    /// </summary>
    [Fact]
    public void An_As_map_projects()
    {
        Widget[] widgets = [new Widget { Name = "Sprocket" }, new Widget { Name = "Cog" }];

        List<IWidgetDto> projected = _fixture.Mapper
            .ProjectTo<IWidgetDto>(widgets.AsQueryable())
            .ToList();

        Assert.Equal(["Sprocket", "Cog"], projected.Select(dto => dto.Name));
        Assert.All(projected, dto => Assert.IsType<ConcreteWidgetDto>(dto));
    }

    // -----------------------------------------------------------------
    // OPEN GENERICS.
    // -----------------------------------------------------------------

    /// <summary>
    /// One declaration closed over every pair the mapper declares — and the closed map is an
    /// ordinary map, elements mapped by the element pair's own map.
    /// </summary>
    [Fact]
    public void A_closed_generic_map_works()
    {
        var page = new Page<Widget>
        {
            Total = 2,
            Items = [new Widget { Name = "Sprocket", Tag = "t1" }, new Widget { Name = "Cog", Tag = "t2" }],
        };

        PageDto<WidgetDto> dto = _fixture.Mapper.Map<PageDto<WidgetDto>>(page);

        Assert.Equal(2, dto.Total);
        Assert.Equal(["Sprocket", "Cog"], dto.Items.Select(item => item.Name));

        // The elements went through the Widget map, which inherits its Tag from the base map.
        Assert.Equal(["audit:t1", "audit:t2"], dto.Items.Select(item => item.Tag));
    }

    /// <summary>It closed over a SECOND pair from the same one declaration.</summary>
    [Fact]
    public void The_same_declaration_closed_over_another_pair()
    {
        var page = new Page<Brand> { Total = 1, Items = [new Brand { Name = "Acme", ISOCode = "IQ" }] };

        PageDto<BrandDto> dto = _fixture.Mapper.Map<PageDto<BrandDto>>(page);

        Assert.Equal(1, dto.Total);
        Assert.Equal("Acme", dto.Items[0].Name);
    }

    /// <summary>And a closed map projects like any other.</summary>
    [Fact]
    public void A_closed_generic_map_projects()
    {
        Page<Widget>[] pages =
        [
            new Page<Widget> { Total = 1, Items = [new Widget { Name = "Sprocket", Tag = "t1" }] },
        ];

        PageDto<WidgetDto> projected = _fixture.Mapper
            .ProjectTo<PageDto<WidgetDto>>(pages.AsQueryable())
            .Single();

        Assert.Equal(1, projected.Total);
        Assert.Equal("Sprocket", projected.Items[0].Name);
        Assert.Equal("audit:t1", projected.Items[0].Tag);
    }
}
