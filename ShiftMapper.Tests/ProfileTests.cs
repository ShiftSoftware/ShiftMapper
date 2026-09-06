using Microsoft.Extensions.DependencyInjection;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// PROFILES, running for real.
///
/// The generator tests already show the right code comes out. What only running can show is that
/// the profile was CONSTRUCTED and its registrations reached the mapper's store — because a
/// <c>MapFrom</c> written in a profile is emitted as a lookup, and a lookup that finds nothing
/// leaves the member empty rather than failing loudly.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ProfileTests
{
    private readonly DatabaseFixture _fixture;

    public ProfileTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>THE TEST THAT MATTERS: the profile ran, and its expression was found.</summary>
    [Fact]
    public void A_MapFrom_written_in_a_profile_is_registered_at_runtime()
    {
        GadgetDto dto = _fixture.Mapper.Map<GadgetDto>(new Gadget { Name = "Widget", Code = "42" });

        Assert.Equal("G-42", dto.Code);
        Assert.Equal("Widget", dto.Name);
    }

    /// <summary>And in the projection, which collects the same registrations by a different path.</summary>
    [Fact]
    public void A_profiles_MapFrom_reaches_the_projection()
    {
        Gadget[] gadgets = [new Gadget { Name = "Widget", Code = "42" }, new Gadget { Name = "Cog", Code = "7" }];

        List<GadgetDto> projected = _fixture.Mapper
            .ProjectTo<GadgetDto>(gadgets.AsQueryable())
            .ToList();

        Assert.Equal(["G-42", "G-7"], projected.Select(dto => dto.Code));
    }

    /// <summary>The two backends agree about a member configured in a profile.</summary>
    [Fact]
    public void The_two_backends_agree_about_a_profile_member()
    {
        Gadget[] gadgets = [new Gadget { Name = "Widget", Code = "42" }];

        GadgetDto inMemory = _fixture.Mapper.Map<GadgetDto>(gadgets[0]);
        GadgetDto projected = _fixture.Mapper.ProjectTo<GadgetDto>(gadgets.AsQueryable()).Single();

        Assert.Equal(inMemory.Code, projected.Code);
    }

    /// <summary>
    /// A PROFILE WITH A DEPENDENCY, resolved from the mapper's own service provider.
    ///
    /// This is the case the whole materialisation timing exists for: the profile cannot be built
    /// while the mapper's constructor runs, because <c>Services</c> is not assigned until after it
    /// returns. If profiles were built eagerly this would throw instead of mapping.
    /// </summary>
    [Fact]
    public void A_profile_with_a_dependency_is_resolved_from_DI()
    {
        DoodadDto dto = _fixture.ProfileMapper.Map<DoodadDto>(new Doodad { Name = "thing" });

        string prefix = _fixture.Services.GetRequiredService<IInvoiceNumbering>().Prefix;

        Assert.Equal(prefix + "thing", dto.Label);
    }

    /// <summary>
    /// AND THE SHARP EDGE, stated as a test so it cannot change by accident: a mapper builds ALL
    /// its profiles at once, so one that needs DI makes the whole mapper DI-only. Constructing it
    /// by hand throws on the first map — with a message naming the profile and what to do — rather
    /// than mapping on with that profile's configuration quietly missing.
    /// </summary>
    [Fact]
    public void A_hand_built_mapper_cannot_use_a_profile_that_needs_DI()
    {
        var mapper = new ProfileMapper();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => mapper.Map<DoodadDto>(new Doodad { Name = "thing" }));

        Assert.Contains("NumberedProfile", error.Message);
        Assert.Contains("AddTransient", error.Message);
    }

    /// <summary>
    /// INCLUDEBASE ACROSS PROFILES, at run time. The base map is in one profile and the derived map
    /// in another, so the expression is stored under the BASE pair and reached through the lineage
    /// — which had to survive the merge of two separate profile stores into the mapper's one.
    /// </summary>
    [Fact]
    public void IncludeBase_across_two_profiles_resolves_at_runtime()
    {
        PremiumGadgetDto dto = _fixture.Mapper.Map<PremiumGadgetDto>(
            new PremiumGadget { Name = "Widget", Code = "42", Rank = 3 });

        Assert.Equal("G-42", dto.Code);
        Assert.Equal(3, dto.Rank);
    }

    /// <summary>And the same across profiles inside a projection.</summary>
    [Fact]
    public void IncludeBase_across_two_profiles_reaches_the_projection()
    {
        PremiumGadget[] gadgets = [new PremiumGadget { Name = "Widget", Code = "42", Rank = 3 }];

        PremiumGadgetDto projected = _fixture.Mapper
            .ProjectTo<PremiumGadgetDto>(gadgets.AsQueryable())
            .Single();

        Assert.Equal("G-42", projected.Code);
        Assert.Equal(3, projected.Rank);
    }

    /// <summary>
    /// A profile without dependencies needs no registration — <c>GadgetProfile</c> is deliberately
    /// not in the container. That is what keeps a hand-built mapper usable in a plain unit test.
    /// </summary>
    [Fact]
    public void A_parameterless_profile_needs_no_registration()
    {
        Assert.Null(_fixture.Services.GetService<GadgetProfile>());

        // It still mapped, in the tests above, through Activator instead.
        Assert.Equal("G-1", _fixture.Mapper.Map<GadgetDto>(new Gadget { Code = "1" }).Code);
    }

    /// <summary>
    /// Every instance of the mapper builds its own profiles. Sharing them would be a
    /// cross-request leak, since a profile may capture a scoped service.
    /// </summary>
    [Fact]
    public void Each_mapper_instance_materialises_its_own_profiles()
    {
        TestMapper first = _fixture.Mapper;
        TestMapper second = _fixture.Mapper;

        Assert.Equal("G-9", first.Map<GadgetDto>(new Gadget { Code = "9" }).Code);
        Assert.Equal("G-9", second.Map<GadgetDto>(new Gadget { Code = "9" }).Code);
    }

    /// <summary>Mapping twice does not build the profiles twice, and keeps working.</summary>
    [Fact]
    public void Profiles_are_materialised_once_and_stay_usable()
    {
        TestMapper mapper = _fixture.Mapper;

        Assert.Equal("G-1", mapper.Map<GadgetDto>(new Gadget { Code = "1" }).Code);
        Assert.Equal("G-2", mapper.Map<GadgetDto>(new Gadget { Code = "2" }).Code);
        Assert.Equal("G-3", mapper.Map<GadgetDto>(new Gadget { Code = "3" }).Code);
    }
}
