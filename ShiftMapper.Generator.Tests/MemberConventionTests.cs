using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// MEMBER-SHAPED CONVENTIONS — the rule a type-pair conversion cannot express.
///
/// <para>A conversion is handed one value and asked what it becomes. This is handed a MEMBER and has
/// to go looking: <c>ProductListDto.Brand</c> is filled from <c>Product.BrandId</c> AND
/// <c>Product.Brand.Name</c>, which needs the member's name, not only its type.</para>
///
/// <para>The whole rule resolves to TEXT at compile time, which is why it reaches the projection.
/// The same rule written as an <c>AfterMap</c> works in memory and cannot appear in a list query at
/// all.</para>
/// </summary>
public class MemberConventionTests
{
    private const string Types =
        """
        using ShiftMapper;
        using System;
        using System.Collections.Generic;

        [AttributeUsage(AttributeTargets.Class)]
        public sealed class KeyAndNameAttribute : Attribute
        {
            public KeyAndNameAttribute(string value, string text) { Value = value; Text = text; }
            public string Value { get; }
            public string Text { get; }
        }

        public class SelectDTO
        {
            public string Value { get; set; } = "";
            public string Text { get; set; } = "";
        }

        [KeyAndName("Id", "Name")]
        public class Brand
        {
            public long Id { get; set; }
            public string Name { get; set; } = "";
        }

        public class Product
        {
            public long Id { get; set; }
            public long BrandId { get; set; }
            public Brand Brand { get; set; } = new();
        }

        public class ProductListDto
        {
            public string Id { get; set; } = "";
            public SelectDTO Brand { get; set; } = new();
        }
        """;

    private const string Convention =
        """
                CreateMemberConvention<SelectDTO>()
                    .NameFrom<KeyAndNameAttribute>("Text")
                    .Fill(d => d.Value, "{Member}ID")
                    .Fill(d => d.Text, "{Member}.{NameOf}");
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Types + "\n" + body);

    private static GeneratorRun RunMapper(string extra = "") =>
        Run($$"""
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
            {{Convention}}
                    {{extra}}
                    CreateMap<Product, ProductListDto>();
                }
            }
            """);

    // -----------------------------------------------------------------
    // THE CORE.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE PLAN'S "DONE WHEN": one <c>CreateMap</c> and one convention, and the shaped member comes
    /// out as an inline member-init — in the create method AND in the projection.
    /// </summary>
    [Fact]
    public void A_shaped_member_is_filled_in_both_backends()
    {
        GeneratorRun run = RunMapper();

        run.Compiles()
           // The value, from {Member}ID — matched case-insensitively against BrandId.
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)")
           // The text, from the member the ENTITY nominates in its own attribute.
           .Emits("source.Brand.Name");

        Assert.Contains("new global::SelectDTO {", run.Generated);

        // Twice at least: the create method and the projection template.
        Assert.True(
            run.Generated.Split(["new global::SelectDTO {"], StringSplitOptions.None).Length - 1 >= 2,
            "the shaped member should appear in the create method AND the projection");

        run.None("SM0001");
        run.None("SM0002");
        run.None("SM0034");
    }

    /// <summary>
    /// The IN-MEMORY spelling guards the navigation; the QUERY spelling leaves it plain, because a
    /// provider turns it into a join and an expression tree cannot hold an <c>is</c> pattern at all.
    ///
    /// <para>The query spelling marks each navigation <c>!</c>. That is not a second access — the
    /// null-forgiving operator is erased at compile time and puts no node in the tree — it says the
    /// missing guard is DELIBERATE, so a nullable navigation does not hand the developer CS8602 in a
    /// file they cannot edit.</para>
    /// </summary>
    [Fact]
    public void The_navigation_is_guarded_in_memory_and_plain_in_the_query()
    {
        RunMapper().Compiles()
            .Emits("source.Brand is null ? default(string)! : source.Brand.Name")
            .Emits("Text = source.Brand!.Name");
    }

    /// <summary>
    /// And the <c>!</c> goes on NAVIGATIONS ONLY, not on the value at the end of the path — nothing
    /// is dereferenced there, so suppressing anything would be noise.
    /// </summary>
    [Fact]
    public void A_single_step_query_path_carries_no_suppression()
    {
        RunMapper().Compiles()
            .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)");

        Assert.DoesNotContain("source.BrandId!", RunMapper().Generated);
    }

    /// <summary>
    /// <c>{NameOf}</c> reads the member the ENTITY nominates. That indirection is what lets one rule
    /// serve entities the framework has never seen — an entity calling its display member
    /// <c>Title</c> is served by the same rule.
    /// </summary>
    [Fact]
    public void NameOf_follows_the_attribute_on_the_entity()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class KeyAndNameAttribute : Attribute
            {
                public KeyAndNameAttribute(string value, string text) { Value = value; Text = text; }
                public string Value { get; }
                public string Text { get; }
            }

            public class SelectDTO { public string Text { get; set; } = ""; }

            // This entity calls its display member TITLE, and nothing else changes.
            [KeyAndName("Id", "Title")]
            public class Brand
            {
                public long Id { get; set; }
                public string Title { get; set; } = "";
            }

            public class Product { public Brand Brand { get; set; } = new(); }
            public class ProductListDto { public SelectDTO Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<SelectDTO>()
                        .NameFrom<KeyAndNameAttribute>("Text")
                        .Fill(d => d.Text, "{Member}.{NameOf}");

                    CreateMap<Product, ProductListDto>();
                }
            }
            """);

        run.Compiles().Emits("source.Brand.Title");
    }

    /// <summary>An explicit <c>ForMember</c> always beats the convention.</summary>
    [Fact]
    public void A_ForMember_wins_over_the_convention()
    {
        GeneratorRun run = Run($$"""
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
            {{Convention}}
                    CreateMap<Product, ProductListDto>()
                        .ForMember(d => d.Brand, opt => opt.MapFrom(s => new SelectDTO { Value = "x" }));
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Value<global::Product, global::ProductListDto, global::SelectDTO>(\"Brand\")")
           .DoesNotEmit("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)");
    }

    /// <summary>
    /// THE WRITE DIRECTION, derived from the reversible entry: a destination <c>BrandId</c> filled
    /// from the source's shaped <c>Brand.Value</c>. The Text entry does not reverse, and should not.
    /// </summary>
    [Fact]
    public void The_reverse_direction_fills_the_id_from_the_shaped_member()
    {
        GeneratorRun run = Run($$"""
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
            {{Convention}}
                    CreateMap<ProductListDto, Product>();
                }
            }
            """);

        run.Compiles().Emits("source.Brand.Value");
    }

    /// <summary>
    /// A convention that composes with a GLOBAL CONVERSION. Nothing here mentions hash ids, and the
    /// select DTO's Value gets them — which is how a framework's two kinds of rule have to meet.
    /// </summary>
    [Fact]
    public void A_convention_composes_with_a_global_conversion()
    {
        GeneratorRun run = RunMapper(
            """CreateConversion<long, string>(id => "H" + id, id => "H" + id);""");

        run.Compiles()
           .Emits("Value = Customizations.Conversion<long, string>()(source.BrandId)")
           .DoesNotEmit("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)");
    }

    // -----------------------------------------------------------------
    // NARROWING, AND MORE THAN ONE RULE.
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>WhenDestinationIs</c> narrows a rule to maps whose DESTINATION fits, so a framework's rule
    /// cannot reach into an application's unrelated types that happen to use the same member type.
    /// </summary>
    [Fact]
    public void A_destination_filter_keeps_the_rule_off_other_maps()
    {
        GeneratorRun run = Run(
            """
            public class ListDtoBase { }
            public class NarrowedDto : ListDtoBase { public SelectDTO Brand { get; set; } = new(); }
            public class OtherDto { public SelectDTO Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<SelectDTO>()
                        .WhenDestinationIs<ListDtoBase>()
                        .NameFrom<KeyAndNameAttribute>("Text")
                        .Fill(d => d.Value, "{Member}ID");

                    CreateMap<Product, NarrowedDto>();
                    CreateMap<Product, OtherDto>();
                }
            }
            """);

        run.Compiles()
           // The narrowed one is filled...
           .Emits("global::NarrowedDto")
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)");

        // ... and the other one is NOT. The rule declined it, so the member fell through to
        // ordinary name matching, which saw two class types and asked for a nested map nobody
        // declared - SM0011. That it is reported AT ALL is the point: a narrowed rule leaves the
        // member to the normal machinery rather than half-filling it.
        Assert.Contains("SM0011", run.Ids());
    }

    /// <summary>Two conventions coexist, each claiming its own member type.</summary>
    [Fact]
    public void Two_conventions_can_coexist()
    {
        GeneratorRun run = Run(
            """
            public class CodeDTO { public string Code { get; set; } = ""; }

            public class TwoDto
            {
                public SelectDTO Brand { get; set; } = new();
                public CodeDTO Other { get; set; } = new();
            }

            public class TwoSource
            {
                public long BrandId { get; set; }
                public Brand Brand { get; set; } = new();
                public long OtherId { get; set; }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<SelectDTO>()
                        .NameFrom<KeyAndNameAttribute>("Text")
                        .Fill(d => d.Value, "{Member}ID");

                    CreateMemberConvention<CodeDTO>()
                        .Fill(d => d.Code, "{Member}ID");

                    CreateMap<TwoSource, TwoDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("new global::SelectDTO {")
           .Emits("new global::CodeDTO {")
           .Emits("source.OtherId");

        run.None("SM0034");
    }

    // -----------------------------------------------------------------
    // WHEN IT CANNOT.
    // -----------------------------------------------------------------

    /// <summary>SM0034 — the convention claimed the member and the path did not resolve.</summary>
    [Fact]
    public void An_unresolvable_path_is_reported_and_the_member_left_unmapped()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class SelectDTO { public string Value { get; set; } = ""; }

            public class Product { public long Id { get; set; } }
            public class ProductListDto { public SelectDTO Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMemberConvention<SelectDTO>().Fill(d => d.Value, "{Member}Code");
                    CreateMap<Product, ProductListDto>();
                }
            }
            """);

        run.Compiles();

        // Product has no BrandCode - and the message names the EXPANDED path, which is the one
        // somebody can look for.
        Assert.Contains("BrandCode", run.Single("SM0034").GetMessage());
        run.DoesNotEmit("new global::SelectDTO {");
    }

    // -----------------------------------------------------------------
    // ONE RULE, TWO SHAPES — FillIfPossible.
    // -----------------------------------------------------------------

    private const string OptionalConvention =
        """
                CreateMemberConvention<SelectDTO>()
                    .NameFrom<KeyAndNameAttribute>("Text")
                    .Fill(d => d.Value, "{Member}ID")
                    .FillIfPossible(d => d.Text, "{Member}.{NameOf}");
        """;

    /// <summary>
    /// THE ID-ONLY SHAPE, from the SAME rule. The source has a foreign key and no navigation beside
    /// it — a request body, or a response whose label the client already holds — so the optional
    /// entry is dropped and the id is still set.
    ///
    /// <para>With a required <c>Fill</c> this is SM0034 and an unmapped member, and a framework
    /// needs a SECOND rule for every entity that leaves its label to the UI. That is the thing
    /// conventions exist to avoid, which is why the entry can be optional at all.</para>
    /// </summary>
    [Fact]
    public void An_optional_entry_is_dropped_when_the_source_has_only_the_key()
    {
        GeneratorRun run = GeneratorHarness.Run(
            Types + "\n" + $$"""
            // No Stock navigation: the key and nothing else.
            public class Order
            {
                public long Id { get; set; }
                public long StockID { get; set; }
            }

            public class OrderDto
            {
                public SelectDTO Stock { get; set; } = new();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
            {{OptionalConvention}}
                    CreateMap<Order, OrderDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.StockID)");

        // The member IS built — it is not left unmapped — and it carries no Text.
        Assert.Contains("new global::SelectDTO {", run.Generated);
        run.DoesNotEmit("Text =");

        // And it skips QUIETLY. Writing FillIfPossible IS the acknowledgement, as Ignore is.
        run.None("SM0034");
        run.None("SM0001");
    }

    /// <summary>
    /// The same source with a REQUIRED <c>Fill</c>: reported, and the member left unmapped. The
    /// difference between the two is the whole reason <c>FillIfPossible</c> is a separate method
    /// rather than a flag — silence has to be asked for.
    /// </summary>
    [Fact]
    public void A_required_entry_in_the_same_position_is_still_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(
            Types + "\n" + $$"""
            public class Order
            {
                public long Id { get; set; }
                public long StockID { get; set; }
            }

            public class OrderDto
            {
                public SelectDTO Stock { get; set; } = new();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
            {{Convention}}
                    CreateMap<Order, OrderDto>();
                }
            }
            """);

        run.Compiles();

        string message = run.Single("SM0034").GetMessage();

        // The expanded path, and the type it did not resolve on. {NameOf} stays unexpanded here
        // because the walk failed at the first segment and never reached a type to read it from.
        Assert.Contains("Stock.{NameOf}", message);
        Assert.Contains("Order", message);

        run.DoesNotEmit("new global::SelectDTO {");
    }

    /// <summary>
    /// ONE RULE SERVING BOTH SHAPES IN ONE MAP, which is what the sample shows: the destination has
    /// two shaped members, one whose source offers a name and one whose source does not, and neither
    /// needs anything said about it.
    /// </summary>
    [Fact]
    public void One_rule_serves_both_shapes_in_the_same_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            Types + "\n" + $$"""
            // Stock nominates NO display member, so {NameOf} has nothing to resolve on it.
            public class Stock
            {
                public long Id { get; set; }
                public string Name { get; set; } = "";
            }

            public class Order
            {
                public long Id { get; set; }
                public long BrandID { get; set; }
                public Brand Brand { get; set; } = new();
                public long StockID { get; set; }
                public Stock Stock { get; set; } = new();
            }

            public class OrderDto
            {
                public SelectDTO Brand { get; set; } = new();
                public SelectDTO Stock { get; set; } = new();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
            {{OptionalConvention}}
                    CreateMap<Order, OrderDto>();
                }
            }
            """);

        run.Compiles()
           // The full shape: id and name.
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandID)")
           .Emits("source.Brand.Name")
           // The id-only shape, from the same rule and with nothing added.
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.StockID)");

        run.DoesNotEmit("source.Stock.Name");
        run.None("SM0034");
        run.None("SM0001");
    }

    /// <summary>
    /// The optional flag SURVIVES THE ASSEMBLY BOUNDARY. A package's id-only rule has to stay
    /// id-only in a consumer — if the marker were lost it would arrive as a hard requirement, and
    /// every consumer whose entity nominates no name would get SM0034 from a rule it never wrote.
    /// </summary>
    [Fact]
    public void An_optional_entry_stays_optional_across_an_assembly()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            packageSource:
            """
            using ShiftMapper;
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class KeyAndNameAttribute : Attribute
            {
                public KeyAndNameAttribute(string value, string text) { Value = value; Text = text; }
                public string Value { get; }
                public string Text { get; }
            }

            public class SelectDTO
            {
                public string Value { get; set; } = "";
                public string Text { get; set; } = "";
            }

            public sealed class FrameworkProfile : ShiftMapperProfile
            {
                public FrameworkProfile()
                {
                    CreateMemberConvention<SelectDTO>()
                        .NameFrom<KeyAndNameAttribute>("Text")
                        .Fill(d => d.Value, "{Member}ID")
                        .FillIfPossible(d => d.Text, "{Member}.{NameOf}");
                }
            }
            """,
            applicationSource:
            """
            using ShiftMapper;

            public class Order
            {
                public long Id { get; set; }
                public long StockID { get; set; }
            }

            public class OrderDto
            {
                public SelectDTO Stock { get; set; } = new();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<FrameworkProfile>();
                    CreateMap<Order, OrderDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.StockID)");

        run.DoesNotEmit("Text =");
        run.None("SM0034");
    }

    /// <summary>
    /// A CONVENTION DECLARED IN A PACKAGE, which is the shape ShiftFramework needs: the framework
    /// ships the rule, an application adds the profile, and its own DTOs are filled by a rule that
    /// names none of its types.
    ///
    /// <para>It rides Step 14's metadata like everything else, so what comes back is the same
    /// convention the source path builds - not a weaker kind.</para>
    /// </summary>
    [Fact]
    public void A_convention_declared_in_a_package_reaches_the_application()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            packageSource:
            """
            using ShiftMapper;
            using System;

            namespace Framework;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class KeyAndNameAttribute : Attribute
            {
                public KeyAndNameAttribute(string value, string text) { Value = value; Text = text; }
                public string Value { get; }
                public string Text { get; }
            }

            public class SelectDTO
            {
                public string Value { get; set; } = "";
                public string Text { get; set; } = "";
            }

            public class FrameworkProfile : ShiftMapperProfile
            {
                public FrameworkProfile() =>
                    CreateMemberConvention<SelectDTO>()
                        .NameFrom<KeyAndNameAttribute>("Text")
                        .Fill(d => d.Value, "{Member}ID")
                        .Fill(d => d.Text, "{Member}.{NameOf}");
            }
            """,
            applicationSource:
            """
            using ShiftMapper;
            using Framework;

            // The application's OWN entity, which the framework has never seen.
            [KeyAndName("Id", "Name")]
            public class Brand
            {
                public long Id { get; set; }
                public string Name { get; set; } = "";
            }

            public class Product
            {
                public long BrandId { get; set; }
                public Brand Brand { get; set; } = new();
            }

            public class ProductListDto { public SelectDTO Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddProfile<FrameworkProfile>();
                    CreateMap<Product, ProductListDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("new global::Framework.SelectDTO {")
           .Emits("Value = global::ShiftMapper.ValueConverter.ToInvariantString(source.BrandId)")
           .Emits("source.Brand.Name");

        run.None("SM0034");
    }

    /// <summary>A project that declares no conventions is completely unaffected.</summary>
    [Fact]
    public void A_project_without_conventions_is_unchanged()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Product { public long BrandId { get; set; } }
            public class ProductDto { public string BrandId { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Product, ProductDto>();
            }
            """);

        run.Compiles();
        run.None("SM0034");
    }
}
