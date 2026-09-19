using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// TYPE-PAIR CONVERSIONS — one rule, written once, answering wherever the pair appears in the
/// maps of the mapper that declared it, or of every mapper that adds the pack holding it.
///
/// The question running through all of it is the one every step since 5 has come back to: does it
/// reach BOTH backends? A conversion that only works in memory is a `foreach` with extra steps.
/// </summary>
public class GlobalConversionTests
{
    private const string Types =
        """
        using ShiftMapper;
        using Microsoft.Extensions.DependencyInjection;
        using System;
        using System.Collections.Generic;
        using System.Linq.Expressions;

        public class Money { public decimal Amount { get; set; } }

        public class Brand
        {
            public Money Price { get; set; } = new();
            public long Id { get; set; }
        }

        public class BrandDto
        {
            public string Price { get; set; } = "";
            public string Id { get; set; } = "";
        }
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Types + "\n" + body);

    // -----------------------------------------------------------------
    // THE CORE.
    // -----------------------------------------------------------------

    /// <summary>
    /// A pair the built-in table refuses — <c>Money</c> to <c>string</c> — becomes mapped, in both
    /// the create method and the projection.
    /// </summary>
    [Fact]
    public void A_registered_pair_is_mapped_in_both_backends()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(
                        memory: m => m.Amount.ToString(),
                        query: m => m.Amount.ToString());

                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles()
           // The in-memory form calls the registered delegate.
           .Emits("Customizations.Conversion<global::Money, string>(typeof(global::TestMapper))")
           // The projection carries a MARKER that Compose replaces with the registered tree.
           .Emits("global::ShiftMapper.MapCustomizations.Splice<global::Money, string>");

        // And the member is no longer unmappable.
        run.None("SM0002");
    }

    /// <summary>
    /// WITHOUT the registration the very same map reports SM0002, which is what makes the test
    /// above mean anything.
    /// </summary>
    [Fact]
    public void Without_the_registration_the_pair_is_unmappable()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        Assert.Contains("SM0002", run.Ids());
    }

    /// <summary>
    /// A REGISTERED PAIR BEATS THE BUILT-IN TABLE. <c>long</c> to <c>string</c> already converts,
    /// and a rule written for it must win — otherwise a framework's hash ids would be ignored in
    /// silence, which is the one behaviour this library is arranged never to have.
    /// </summary>
    [Fact]
    public void A_registered_pair_wins_over_the_built_in_conversion()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
                    CreateConversion<long, string>(id => "H" + id, id => "H" + id);

                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Conversion<long, string>(typeof(global::TestMapper))")
           // The built-in spelling for long -> string is gone from this member.
           .DoesNotEmit("ToInvariantString(source.Id)");
    }

    /// <summary>
    /// ASSIGNABILITY, NOT IDENTITY: a rule registered for a BASE type answers for everything that
    /// derives from it. This is what lets a framework write one rule for an entity hierarchy it has
    /// never seen the members of.
    /// </summary>
    [Fact]
    public void A_conversion_registered_for_a_base_type_fires_for_a_derived_one()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class EntityBase { public long Id { get; set; } }
            public class Customer : EntityBase { }

            public class Label { public string Text { get; set; } = ""; }

            public class Order { public Customer Customer { get; set; } = new(); }
            public class OrderDto { public Label Customer { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<EntityBase, Label>(
                        e => new Label { Text = e.Id.ToString() },
                        e => new Label { Text = e.Id.ToString() });

                    CreateMap<Order, OrderDto>();
                }
            }
            """);

        // The lookup is emitted over the MEMBER's own types; the runtime resolves the base
        // registration and hands it back through Func's contravariance.
        run.Compiles().Emits("Customizations.Conversion<global::Customer, global::Label>(typeof(global::TestMapper))");
    }

    /// <summary>A <c>ForMember</c> on a particular member still wins over a global rule.</summary>
    [Fact]
    public void A_ForMember_still_wins_over_a_global_conversion()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());

                    CreateMap<Brand, BrandDto>()
                        .ForMember(d => d.Price, opt => opt.MapFrom(s => "fixed"));
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Value<global::Brand, global::BrandDto, string>(\"Price\")")
           .DoesNotEmit("Customizations.Conversion<global::Money, string>(");
    }

    // -----------------------------------------------------------------
    // TRANSITIVITY.
    // -----------------------------------------------------------------

    /// <summary>
    /// It applies to the ELEMENT TYPE of a collection, which is most of the value: the rule is
    /// written for a pair, not for a shape.
    /// </summary>
    [Fact]
    public void A_conversion_applies_to_collection_elements()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Money { public decimal Amount { get; set; } }
            public class Cart { public List<Money> Prices { get; set; } = new(); }
            public class CartDto { public List<string> Prices { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
                    CreateMap<Cart, CartDto>();
                }
            }
            """);

        run.Compiles().Emits("Customizations.Conversion<global::Money, string>(typeof(global::TestMapper))");

        // THE LAMBDA CANNOT BE `static`. A global conversion reaches the mapper's own
        // Customizations, and static forbids capturing — the generated file would not compile.
        // The control is that the SAME emitter still writes `static` where nothing is captured.
        run.DoesNotEmit("static item => (Customizations");
    }

    /// <summary>And to the VALUE type of a dictionary, by the same recursion.</summary>
    [Fact]
    public void A_conversion_applies_to_dictionary_values()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Money { public decimal Amount { get; set; } }
            public class Feed { public Dictionary<string, Money> Prices { get; set; } = new(); }
            public class FeedDto { public Dictionary<string, string> Prices { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
                    CreateMap<Feed, FeedDto>();
                }
            }
            """);

        run.Compiles().Emits("Customizations.Conversion<global::Money, string>(typeof(global::TestMapper))");
    }

    // -----------------------------------------------------------------
    // THE QUERY FORM, AND ITS ABSENCE.
    // -----------------------------------------------------------------

    /// <summary>
    /// OMITTING THE QUERY FORM IS A DECLARATION, not an oversight: it says the pair cannot be
    /// translated. Every map that touches it loses its projection, and the build says so — which is
    /// the entire reason to do this at compile time.
    /// </summary>
    [Fact]
    public void A_conversion_without_a_query_form_costs_the_projection()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString());
                    CreateConversion<long, string>(id => id.ToString(), id => id.ToString());

                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles();

        string message = run.Single("SM0030").GetMessage();

        Assert.Contains("'Money' to 'string'", message);
        Assert.Contains("no query form", message);

        // In memory it works exactly as before; only the projection is refused, and it THROWS
        // rather than going missing — a missing projection member is CS0103 once nested.
        run.Emits("Customizations.Conversion<global::Money, string>(typeof(global::TestMapper))")
           .Emits("throw new global::System.InvalidOperationException");
    }

    /// <summary>Supplying the query form positionally counts, not only as a named argument.</summary>
    [Fact]
    public void A_positional_query_form_counts()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
                    CreateConversion<long, string>(id => id.ToString(), id => id.ToString());

                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles();
        run.None("SM0030");
    }

    // -----------------------------------------------------------------
    // WHERE IT IS DECLARED.
    // -----------------------------------------------------------------

    /// <summary>
    /// DECLARED IN A PACK, which is the shape that matters: a framework ships the pack, the
    /// application adds it, and every map of the mapper that added it picks the rule up. The
    /// generated call names the PACK as the scope, so the runtime looks in exactly that store.
    /// </summary>
    [Fact]
    public void A_conversion_declared_in_a_pack_reaches_the_mappers_maps()
    {
        GeneratorRun run = Run(
            """
            public class ConversionPack : ShiftMapperConversions
            {
                public ConversionPack() =>
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddConversions<ConversionPack>();
                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles().Emits("Customizations.Conversion<global::Money, string>(typeof(global::ConversionPack))");
        run.None("SM0002");
    }

    /// <summary>
    /// A memory-only conversion declared IN A PACK, which is the shape the sample uses and the
    /// combination the two tests above miss between them: one declares memory-only on the mapper,
    /// the other declares a full pair in a pack.
    /// </summary>
    [Fact]
    public void A_memory_only_conversion_in_a_pack_still_costs_the_projection()
    {
        GeneratorRun run = Run(
            """
            public class ConversionPack : ShiftMapperConversions
            {
                public ConversionPack()
                {
                    CreateConversion<Money, string>(memory: m => m.Amount.ToString());
                    CreateConversion<long, string>(id => id.ToString(), id => id.ToString());
                }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddConversions<ConversionPack>();
                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles();

        Assert.Contains("no query form", run.Single("SM0030").GetMessage());
    }

    // -----------------------------------------------------------------
    // SCOPE — a rule reaches the maps of the mapper that wrote it, and nothing else.
    // -----------------------------------------------------------------

    /// <summary>
    /// A conversion written in an INCLUDED mapper applies to that mapper's own maps and NOT to the
    /// including mapper's: the included mapper's map converts, the includer's own map still
    /// reports the pair as unmappable.
    /// </summary>
    [Fact]
    public void An_included_mappers_conversion_stays_with_its_own_maps()
    {
        GeneratorRun run = Run(
            """
            public class Coin { public Money Price { get; set; } = new(); }
            public class CoinDto { public string Price { get; set; } = ""; }

            public partial class CoinMapper : ShiftMapperBase
            {
                public CoinMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
                    CreateMap<Coin, CoinDto>();
                }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Conversion<global::Money, string>(typeof(global::CoinMapper))");

        // Brand.Price -> BrandDto.Price is the INCLUDER's map, and the rule does not reach it.
        Assert.Contains(run.All("SM0002"), d => d.GetMessage().Contains("BrandDto.Price"));
    }

    /// <summary>
    /// The INCLUDING mapper's own rule reaches the maps it included — near beats far, and the
    /// includer is the nearer authority for what it composes.
    /// </summary>
    [Fact]
    public void An_including_mappers_conversion_reaches_the_included_maps()
    {
        GeneratorRun run = Run(
            """
            public class Coin { public Money Price { get; set; } = new(); }
            public class CoinDto { public string Price { get; set; } = ""; }

            public partial class CoinMapper : ShiftMapperBase
            {
                public CoinMapper() => CreateMap<Coin, CoinDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => m.Amount.ToString(), m => m.Amount.ToString());
                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Conversion<global::Money, string>(typeof(global::TestMapper))");

        // TestMapper's half converts CoinDto.Price; only CoinMapper's OWN half, which has no rule
        // for the pair, reports it.
        string testMapperFile = run.GeneratedFiles.Single(file => file.Contains("class GeneratedMapper"));
        Assert.Contains("Customizations.Conversion<global::Money, string>(typeof(global::TestMapper))(source.Price)", testMapperFile);
        Assert.All(run.All("SM0002"), d => Assert.Contains("CoinDto.Price", d.GetMessage()));
    }

    /// <summary>
    /// NEAREST WINS: the declaring mapper's own rule beats the includer's, which beats a pack the
    /// includer added, which beats a pack the registration gave every mapper.
    /// </summary>
    [Fact]
    public void The_nearest_declaration_answers()
    {
        GeneratorRun run = Run(
            """
            public class Coin { public Money Price { get; set; } = new(); }
            public class CoinDto { public string Price { get; set; } = ""; }

            public class Global : ShiftMapperConversions
            {
                public Global() => CreateConversion<Money, string>(m => "global", m => "global");
            }

            public class Own : ShiftMapperConversions
            {
                public Own() => CreateConversion<Money, string>(m => "own", m => "own");
            }

            public partial class CoinMapper : ShiftMapperBase
            {
                public CoinMapper()
                {
                    CreateConversion<Money, string>(m => "coin", m => "coin");
                    CreateMap<Coin, CoinDto>();
                }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddConversions<Own>();
                    CreateMap<Brand, BrandDto>();
                }
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddConversions<Global>();
                    });
            }
            """);

        run.Compiles()
           // Coin's own map: its own rule.
           .Emits("Customizations.Conversion<global::Money, string>(typeof(global::CoinMapper))")
           // Brand's map on TestMapper: the pack it added, ahead of the registration's.
           .Emits("Customizations.Conversion<global::Money, string>(typeof(global::Own))")
           .DoesNotEmit("Customizations.Conversion<global::Money, string>(typeof(global::Global))");
    }

    /// <summary>
    /// A pack given to EVERY mapper by the registration reaches a mapper that never mentions it.
    /// </summary>
    [Fact]
    public void A_registration_wide_pack_reaches_every_mapper()
    {
        GeneratorRun run = Run(
            """
            public class Global : ShiftMapperConversions
            {
                public Global() => CreateConversion<Money, string>(m => "global", m => "global");
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddConversions<Global>();
                    });
            }
            """);

        run.Compiles()
           .Emits("Customizations.Conversion<global::Money, string>(typeof(global::Global))");
        run.None("SM0002");
    }

    /// <summary>
    /// TWO PACKS AT THE SAME DISTANCE claiming one pair is an error (SM0031) — unless something
    /// nearer settles it.
    /// </summary>
    [Fact]
    public void Two_packs_at_one_level_claiming_a_pair_is_an_error_unless_settled_nearer()
    {
        const string packs =
            """
            public class First : ShiftMapperConversions
            {
                public First() => CreateConversion<Money, string>(m => "first", m => "first");
            }

            public class Second : ShiftMapperConversions
            {
                public Second() => CreateConversion<Money, string>(m => "second", m => "second");
            }
            """;

        GeneratorRun clash = Run(packs +
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddConversions<First>();
                    AddConversions<Second>();
                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        Assert.Contains("First", clash.Single("SM0031").GetMessage());
        Assert.Contains("Second", clash.Single("SM0031").GetMessage());

        GeneratorRun settled = Run(packs +
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(m => "mine", m => "mine");
                    AddConversions<First>();
                    AddConversions<Second>();
                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        settled.Compiles().None("SM0031");
        settled.Emits("Customizations.Conversion<global::Money, string>(typeof(global::TestMapper))");
    }

    /// <summary>
    /// THE REGRESSION THE SAMPLE CAUGHT. A map with NESTED members goes through the resolve pass,
    /// which rebuilds the model to settle them — and a rebuild that forgets a field loses it in
    /// silence. The refusal survived every test above because none of their maps had anything to
    /// resolve.
    /// </summary>
    [Fact]
    public void A_refusal_survives_the_nested_resolve_pass()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Money { public decimal Amount { get; set; } }
            public class Stock { public string Code { get; set; } = ""; }
            public class StockDto { public string Code { get; set; } = ""; }

            public class Product
            {
                public Money Price { get; set; } = new();
                public Stock Stock { get; set; } = new();
            }

            public class ProductDto
            {
                public string Price { get; set; } = "";
                public StockDto Stock { get; set; } = new();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<Money, string>(memory: m => m.Amount.ToString());

                    CreateMap<Stock, StockDto>();
                    CreateMap<Product, ProductDto>();
                }
            }
            """);

        run.Compiles();

        Assert.Contains("no query form", run.Single("SM0030").GetMessage());
    }

    /// <summary>A mapper that registers none is completely unaffected.</summary>
    /// <summary>
    /// A CONVERSION THAT KNOWS WHAT IT IS CONVERTING. The two-argument form is handed the property
    /// pair as a literal — the same one the built-in parsers get — so a rule that refuses a value
    /// can say which field it refused, which is the whole reason a framework writes one.
    /// </summary>
    [Fact]
    public void A_conversion_may_take_the_mapping_and_is_handed_it_as_a_literal()
    {
        GeneratorRun run = Run(
            $$"""
            public class Shipment { public long BrandId { get; set; } }

            public class ShipmentDto { public string BrandId { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<string, long>(
                        (text, mapping) => text.Length == 0
                            ? throw new InvalidOperationException("blank at " + mapping)
                            : long.Parse(text),
                        text => long.Parse(text));

                    CreateMap<ShipmentDto, Shipment>();
                }
            }

            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new Mapper();

                    long filled = mapper.MapToShipment(new ShipmentDto { BrandId = "7" }).BrandId;

                    try
                    {
                        mapper.MapToShipment(new ShipmentDto());
                        return "no throw";
                    }
                    catch (InvalidOperationException e)
                    {
                        return filled + "|" + e.Message;
                    }
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.ConversionWithMapping<string, long>(typeof(global::TestMapper))(source.BrandId, \"ShipmentDto.BrandId -> Shipment.BrandId\")");

        Assert.Equal("7|blank at ShipmentDto.BrandId -> Shipment.BrandId", run.Load().GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void A_mapper_without_conversions_is_unchanged()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Brand { public long Id { get; set; } }
            public class BrandDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles().DoesNotEmit("Customizations.Conversion<");
        run.None("SM0030");
    }
}
