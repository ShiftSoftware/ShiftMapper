using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// Flattening — filling <c>OrderDto.CustomerName</c> from <c>Order.Customer.Name</c> — and the
/// naming conventions that widen a plain match.
///
/// It is ON by default, so the first tests are the ones that matter most: what it does without
/// being asked, and how to make it stop. It is the only convention in this library that GUESSES,
/// which is why every member it fills is reported with the path it chose (SM0020) and a name that
/// resolves more than one way is refused outright (SM0021).
/// </summary>
public class FlatteningTests
{
    private const string Converter = "global::ShiftMapper.ValueConverter";

    private const string Graph =
        """
        public class Customer { public string Name { get; set; } = ""; public int Age { get; set; } }

        public class Order
        {
            public int Id { get; set; }
            public Customer? Customer { get; set; }
        }

        public class OrderDto
        {
            public int Id { get; set; }
            public string CustomerName { get; set; } = "";
        }
        """;

    private static GeneratorRun Run(string types, string maps) =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;
            using System.Collections.Generic;

            {{types}}

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    {{maps}}
                }
            }
            """);

    // -----------------------------------------------------------------
    // THE DEFAULT.
    // -----------------------------------------------------------------

    /// <summary>
    /// ON BY DEFAULT, so a destination that reads like a path just works — and every member it
    /// fills is reported with the path it chose, which is what makes a guess auditable rather than
    /// merely silent.
    /// </summary>
    [Fact]
    public void Flattening_is_on_by_default()
    {
        GeneratorRun run = Run(Graph, "CreateMap<Order, OrderDto>();");

        run.None("SM0001");
        Assert.Contains("Customer.Name", run.Single("SM0020").GetMessage());
        run.Compiles().Emits("CustomerName = (source.Customer is null ? default(string)! : source.Customer.Name),");
    }

    /// <summary>
    /// And one option turns it off, which is how you get the stricter behaviour: a member that
    /// would have been walked is reported as SM0001 instead.
    /// </summary>
    [Fact]
    public void One_option_turns_it_off()
    {
        GeneratorRun run = Run(Graph, "CreateMap<Order, OrderDto>(o => o.Flattening = false);");

        Diagnostic diagnostic = run.Single("SM0001");

        Assert.Contains("'OrderDto.CustomerName'", diagnostic.GetMessage());
        run.Compiles().DoesNotEmit("CustomerName =");
    }

    /// <summary>A mapper can turn it off for every map it declares, like any other default.</summary>
    [Fact]
    public void ConfigureDefaults_turns_it_off_for_the_whole_mapper()
    {
        GeneratorRun run = GeneratorHarness.Run(
            $$"""
            using ShiftMapper;

            {{Graph}}

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Order, OrderDto>();

                protected override void ConfigureDefaults(MapOptions options) => options.Flattening = false;
            }
            """);

        run.Single("SM0001");
        run.Compiles().DoesNotEmit("source.Customer.Name");
    }

    /// <summary>And a map can opt back IN against a mapper that switched it off.</summary>
    [Fact]
    public void A_map_can_refuse_the_mapper_default()
    {
        GeneratorRun run = GeneratorHarness.Run(
            $$"""
            using ShiftMapper;

            {{Graph}}

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Order, OrderDto>(o => o.Flattening = true);

                protected override void ConfigureDefaults(MapOptions options) => options.Flattening = false;
            }
            """);

        run.None("SM0001");
        run.Compiles().Emits("source.Customer.Name");
    }

    /// <summary>
    /// It never competes with a real property. A source that carries the flat name outright is
    /// matched directly, so turning flattening on cannot change what an existing map does — it can
    /// only fill something that was SM0001 before.
    /// </summary>
    [Fact]
    public void A_direct_match_always_wins()
    {
        GeneratorRun run = Run(
            """
            public class Customer { public string Name { get; set; } = ""; }

            public class Order
            {
                public Customer Customer { get; set; } = new();
                public string CustomerName { get; set; } = "";
            }

            public class OrderDto { public string CustomerName { get; set; } = ""; }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.None("SM0020");
        run.Compiles()
           .Emits("CustomerName = source.CustomerName,")
           .DoesNotEmit("source.Customer.Name");
    }

    // -----------------------------------------------------------------
    // THE WALK.
    // -----------------------------------------------------------------

    /// <summary>Three segments, two steps: the search re-joins the split every way that resolves.</summary>
    [Fact]
    public void It_walks_more_than_one_level()
    {
        GeneratorRun run = Run(
            """
            public class Country { public string Name { get; set; } = ""; }
            public class Customer { public Country Country { get; set; } = new(); }
            public class Order { public Customer Customer { get; set; } = new(); }
            public class OrderDto { public string CustomerCountryName { get; set; } = ""; }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        Assert.Contains("Customer.Country.Name", run.Single("SM0020").GetMessage());
        run.Compiles().Emits("CustomerCountryName = source.Customer.Country.Name,");
    }

    /// <summary>The leaf converts exactly as a directly matched property would.</summary>
    [Fact]
    public void The_leaf_goes_through_the_conversion_table()
    {
        GeneratorRun run = Run(
            """
            public class Customer { public int Age { get; set; } }
            public class Order { public Customer Customer { get; set; } = new(); }
            public class OrderDto { public string CustomerAge { get; set; } = ""; }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.Compiles().Emits($"CustomerAge = {Converter}.ToInvariantString(source.Customer.Age),");
    }

    /// <summary>A leaf the table refuses is SM0002, not a silent skip.</summary>
    [Fact]
    public void A_leaf_the_table_refuses_is_Sm0002()
    {
        GeneratorRun run = Run(
            """
            public class Customer { public bool Active { get; set; } }
            public class Order { public Customer Customer { get; set; } = new(); }
            public class OrderDto { public int CustomerActive { get; set; } }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        Assert.Contains("does not convert 'bool' to 'int'", run.Single("SM0002").GetMessage());
    }

    // -----------------------------------------------------------------
    // NULLS.
    // -----------------------------------------------------------------

    /// <summary>
    /// A step the model declares NULLABLE is guarded; one it declares required is not. That is the
    /// rule the nested-object maps already follow — an unnecessary guard turns a required
    /// relationship's join into a CASE the provider has to reason about, for a null the type says
    /// cannot happen.
    /// </summary>
    [Fact]
    public void Only_a_nullable_step_is_guarded()
    {
        GeneratorRun run = Run(
            """
            public class Customer { public string Name { get; set; } = ""; }

            public class Order
            {
                public Customer? Maybe { get; set; }
                public Customer Always { get; set; } = new();
            }

            public class OrderDto
            {
                public string MaybeName { get; set; } = "";
                public string AlwaysName { get; set; } = "";
            }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.Compiles()
           .Emits("MaybeName = (source.Maybe is null ? default(string)! : source.Maybe.Name),")
           .Emits("AlwaysName = source.Always.Name,");
    }

    /// <summary>
    /// THE PROJECTION GUARD IS SPELLED DIFFERENTLY, and it has to be: an expression tree may not
    /// contain an <c>is</c> pattern at all (CS8122), so the query form uses <c>== null</c>.
    /// </summary>
    [Fact]
    public void The_projection_guards_with_an_equality_rather_than_a_pattern()
    {
        GeneratorRun run = Run(Graph, "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.Compiles()
           .Emits("CustomerName = (source.Customer == null ? default(string)! : source.Customer.Name),")
           .Emits("CustomerName = (source.Customer is null ? default(string)! : source.Customer.Name),");
    }

    /// <summary>
    /// A guarded VALUE leaf gets <c>default(T)</c> rather than null, because a conditional whose
    /// branches are <c>null</c> and <c>int</c> has no type. It is the same "absence becomes the
    /// default" rule ValueConverter applies to empty text.
    /// </summary>
    [Fact]
    public void A_guarded_value_leaf_falls_back_to_its_default()
    {
        GeneratorRun run = Run(
            """
            public class Customer { public int Age { get; set; } }
            public class Order { public Customer? Customer { get; set; } }
            public class OrderDto { public int CustomerAge { get; set; } }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.Compiles().Emits("CustomerAge = (source.Customer is null ? default(int)! : source.Customer.Age),");
    }

    // -----------------------------------------------------------------
    // WHAT IT WILL NOT WALK INTO.
    // -----------------------------------------------------------------

    /// <summary>
    /// NOT a string. <c>NameLength</c> quietly becoming <c>Name.Length</c> is the surprise every
    /// flattening mapper is remembered for, and a ForMember says it better.
    /// </summary>
    [Fact]
    public void It_does_not_walk_into_a_string()
    {
        GeneratorRun run = Run(
            """
            public class Order { public string Name { get; set; } = ""; }
            public class OrderDto { public int NameLength { get; set; } }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.Single("SM0001");
        run.None("SM0020");
    }

    /// <summary>NOT a collection: there is no single element to walk to.</summary>
    [Fact]
    public void It_does_not_walk_into_a_collection()
    {
        GeneratorRun run = Run(
            """
            public class Line { public int Quantity { get; set; } }
            public class Order { public List<Line> Lines { get; set; } = new(); }
            public class OrderDto { public int LinesQuantity { get; set; } }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.Single("SM0001");
    }

    /// <summary>
    /// A non-nullable value type IS walkable, so a date's parts are reachable and both backends
    /// can express them.
    /// </summary>
    [Fact]
    public void It_walks_into_a_value_type()
    {
        GeneratorRun run = Run(
            """
            using System;
            public class Order { public DateTime CreatedAt { get; set; } }
            public class OrderDto { public int CreatedAtYear { get; set; } }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.Compiles().Emits("CreatedAtYear = source.CreatedAt.Year,");
    }

    // -----------------------------------------------------------------
    // AMBIGUITY — SM0021.
    // -----------------------------------------------------------------

    /// <summary>
    /// Two paths is a question, not a tie to break. The member is left unmapped and both paths are
    /// named, on the same reasoning as SM0007's two source names differing only by case.
    /// </summary>
    [Fact]
    public void Two_paths_to_one_member_is_reported_rather_than_decided()
    {
        GeneratorRun run = Run(
            """
            public class Inner { public string Name { get; set; } = ""; }
            public class Outer { public string CustomerName { get; set; } = ""; }

            public class Order
            {
                public Outer Order2 { get; set; } = new();
                public Inner Order2Customer { get; set; } = new();
            }

            public class OrderDto { public string Order2CustomerName { get; set; } = ""; }
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        Diagnostic diagnostic = run.Single("SM0021");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Order2.CustomerName", diagnostic.GetMessage());
        Assert.Contains("Order2Customer.Name", diagnostic.GetMessage());

        // Left unmapped, and NOT also reported as SM0001 — this message replaces it.
        run.None("SM0001");
        run.Compiles().DoesNotEmit("Order2CustomerName =");
    }

    /// <summary>A ForMember settles it, and is the fix the message points at.</summary>
    [Fact]
    public void ForMember_settles_an_ambiguous_flattening()
    {
        GeneratorRun run = Run(
            """
            public class Inner { public string Name { get; set; } = ""; }
            public class Outer { public string CustomerName { get; set; } = ""; }

            public class Order
            {
                public Outer Order2 { get; set; } = new();
                public Inner Order2Customer { get; set; } = new();
            }

            public class OrderDto { public string Order2CustomerName { get; set; } = ""; }
            """,
            """
            CreateMap<Order, OrderDto>(o => o.Flattening = true)
                .ForMember(d => d.Order2CustomerName, opt => opt.MapFrom(s => s.Order2Customer.Name));
            """);

        run.None("SM0021");
        run.Compiles();
    }

    // -----------------------------------------------------------------
    // PREFIXES AND POSTFIXES.
    // -----------------------------------------------------------------

    [Fact]
    public void A_recognized_prefix_matches_a_plain_member()
    {
        GeneratorRun run = Run(
            """
            public class Row { public string DbName { get; set; } = ""; }
            public class RowDto { public string Name { get; set; } = ""; }
            """,
            "CreateMap<Row, RowDto>(o => o.RecognizePrefixes(\"Db\"));");

        run.None("SM0001");
        run.Compiles().Emits("Name = source.DbName,");
    }

    [Fact]
    public void A_recognized_postfix_matches_a_plain_member()
    {
        GeneratorRun run = Run(
            """
            public class Row { public int CustomerId { get; set; } }
            public class RowDto { public int Customer { get; set; } }
            """,
            "CreateMap<Row, RowDto>(o => o.RecognizePostfixes(\"Id\"));");

        run.None("SM0001");
        run.Compiles().Emits("Customer = source.CustomerId,");
    }

    /// <summary>
    /// The BARE NAME IS TRIED FIRST, which is what stops a prefix introducing an ambiguity: a
    /// source declaring both wins on the exact one, as everywhere else here.
    /// </summary>
    [Fact]
    public void The_unprefixed_name_wins()
    {
        GeneratorRun run = Run(
            """
            public class Row { public string Name { get; set; } = ""; public string DbName { get; set; } = ""; }
            public class RowDto { public string Name { get; set; } = ""; }
            """,
            "CreateMap<Row, RowDto>(o => o.RecognizePrefixes(\"Db\"));");

        run.Compiles().Emits("Name = source.Name,").DoesNotEmit("source.DbName");
    }

    /// <summary>Conventions apply to each step of a flattened path, not only to a plain member.</summary>
    [Fact]
    public void Conventions_apply_to_every_step_of_a_walk()
    {
        GeneratorRun run = Run(
            """
            public class Customer { public string DbName { get; set; } = ""; }
            public class Order { public Customer DbCustomer { get; set; } = new(); }
            public class OrderDto { public string CustomerName { get; set; } = ""; }
            """,
            "CreateMap<Order, OrderDto>(o => { o.Flattening = true; o.RecognizePrefixes(\"Db\"); });");

        run.Compiles().Emits("CustomerName = source.DbCustomer.DbName,");
    }

    /// <summary>They can be stated once for the mapper, like any other default.</summary>
    [Fact]
    public void ConfigureDefaults_can_state_the_conventions()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Row { public string DbName { get; set; } = ""; }
            public class RowDto { public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Row, RowDto>();

                protected override void ConfigureDefaults(MapOptions options) => options.RecognizePrefixes("Db");
            }
            """);

        run.None("SM0001");
        run.Compiles().Emits("Name = source.DbName,");
    }

    // -----------------------------------------------------------------
    // CONSTRUCTORS.
    // -----------------------------------------------------------------

    /// <summary>
    /// A constructor parameter is a destination member written inside the parentheses, so it
    /// flattens like one — which is what makes a record summary DTO work.
    /// </summary>
    [Fact]
    public void A_constructor_argument_flattens()
    {
        GeneratorRun run = Run(
            """
            public class Customer { public string Name { get; set; } = ""; }
            public class Order { public int Id { get; set; } public Customer Customer { get; set; } = new(); }
            public record OrderDto(int Id, string CustomerName);
            """,
            "CreateMap<Order, OrderDto>(o => o.Flattening = true);");

        run.None("SM0013");
        run.Compiles().Emits("source.Customer.Name);");
    }
}
