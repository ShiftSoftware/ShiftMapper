using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// How names are matched, and the three places that decision can be made: the map's own lambda,
/// the mapper's <c>ConfigureDefaults</c>, and ShiftMapper's own default of case-insensitive.
/// Precedence runs innermost-first.
/// </summary>
public class PropertyMatchingTests
{
    /// <summary>
    /// The default. An EF entity mirroring a database column is often spelled <c>ISOCode</c>
    /// while the DTO spells it <c>IsoCode</c>, and nothing should have to be written for that.
    /// </summary>
    [Fact]
    public void The_case_insensitive_fallback_is_on_by_default()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string ISOCode { get; set; } = ""; }
            public class Destination { public string IsoCode { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Assert.Empty(run.Ids());

        // Each side is spelled exactly as its OWN type declares it.
        run.Compiles().Emits("IsoCode = source.ISOCode,");
    }

    [Fact]
    public void A_map_can_turn_the_fallback_off()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string ISOCode { get; set; } = ""; }
            public class Destination { public string IsoCode { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>(o => o.Matching = PropertyMatching.CaseSensitive);
            }
            """);

        Assert.Equal(new[] { "SM0001" }, run.Ids());
        run.Compiles().DoesNotEmit("IsoCode =");
    }

    /// <summary>
    /// <c>ConfigureDefaults</c> is an OVERRIDE, a member of the class — not something called from
    /// the constructor, where it would compile and do nothing. It is read from the class symbol,
    /// so it counts wherever in a partial mapper it is written.
    /// </summary>
    [Fact]
    public void ConfigureDefaults_sets_the_default_for_every_map_in_the_mapper()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string ISOCode { get; set; } = ""; }
            public class Destination { public string IsoCode { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }
            """);

        Assert.Equal(new[] { "SM0001" }, run.Ids());
        run.Compiles();
    }

    /// <summary>Innermost wins: the map's own lambda beats the mapper's defaults.</summary>
    [Fact]
    public void A_map_overrides_the_mappers_defaults()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string ISOCode { get; set; } = ""; }
            public class Destination { public string IsoCode { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>(o => o.Matching = PropertyMatching.CaseInsensitive);

                protected override void ConfigureDefaults(MapOptions options)
                    => options.Matching = PropertyMatching.CaseSensitive;
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("IsoCode = source.ISOCode,");
    }

    /// <summary>
    /// Writing the option once on <c>CreateMap</c> and getting both directions is the least
    /// surprising reading of <c>CreateMap(...).ReverseMap()</c>.
    /// </summary>
    [Fact]
    public void ReverseMap_inherits_the_forward_maps_matching()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string ISOCode { get; set; } = ""; }
            public class Destination { public string IsoCode { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>(o => o.Matching = PropertyMatching.CaseSensitive)
                        .ReverseMap();
            }
            """);

        // One in each direction: SM0001 forward, its quieter twin SM0006 on the way back.
        Assert.Equal(new[] { "SM0001", "SM0006" }, run.Ids());
        run.Compiles();
    }

    [Fact]
    public void ReverseMap_can_state_its_own_matching()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string ISOCode { get; set; } = ""; }
            public class Destination { public string IsoCode { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>(o => o.Matching = PropertyMatching.CaseSensitive)
                        .ReverseMap(o => o.Matching = PropertyMatching.CaseInsensitive);
            }
            """);

        // Forward still complains; the way back matches and says nothing.
        Assert.Equal(new[] { "SM0001" }, run.Ids());
        run.Compiles().Emits("ISOCode = source.IsoCode,");
    }

    /// <summary>
    /// Exact is tried FIRST in both modes, which is what stops a type carrying both <c>Id</c> and
    /// <c>ID</c> ever swapping them.
    /// </summary>
    [Fact]
    public void An_exact_match_always_wins_over_the_fallback()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public int ID { get; set; } }
            public class Destination { public int ID { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        // Not ambiguous: the exact spelling settles it before the fallback is consulted.
        run.None("SM0007");
        run.Compiles().Emits("ID = source.ID,");
    }

    /// <summary>
    /// A <c>new</c>-hiding or overriding property appears twice in the base chain, and assigning
    /// the same member twice in one object initializer is CS1912 — so the most-derived
    /// declaration is the one that counts, exactly as it would at runtime.
    /// </summary>
    [Fact]
    public void An_overridden_property_is_bound_once()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class SourceBase { public virtual int Id { get; set; } }
            public class Source : SourceBase { public override int Id { get; set; } }

            public class DestinationBase { public virtual int Id { get; set; } }
            public class Destination : DestinationBase { public override int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles();

        int bindings = 0;
        for (int i = run.Generated.IndexOf("Id = source.Id,", StringComparison.Ordinal); i >= 0;
             i = run.Generated.IndexOf("Id = source.Id,", i + 1, StringComparison.Ordinal))
        {
            bindings++;
        }

        // Once in the create method and once in the projection — never twice in either.
        Assert.Equal(2, bindings);
    }

    /// <summary>Inherited properties map, so a shared base class does not have to be repeated.</summary>
    [Fact]
    public void Inherited_properties_are_matched()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class AuditBase { public int CreatedBy { get; set; } }
            public class Source : AuditBase { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } public int CreatedBy { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("CreatedBy = source.CreatedBy,");
    }
}
