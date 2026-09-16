using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// One test per diagnostic, SM0001 to SM0012.
///
/// Each asserts three things, and the third is the one nothing else in the suite covers: the ID
/// (what a NoWarn entry names), the SEVERITY (whether the build stops, warns, or only whispers —
/// a deliberate decision for every one of these, argued at length in DiagnosticDescriptors), and
/// the LOCATION (which line the developer is sent to).
/// </summary>
public class DiagnosticTests
{
    // -----------------------------------------------------------------
    // SM0001 — the source type has nothing with that name.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0001_is_a_warning_on_the_CreateMap_that_asked_for_the_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0001");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("'Destination.Name' is not mapped", diagnostic.GetMessage());
        run.Compiles();
    }

    [Fact]
    public void Sm0001_is_silenced_by_ignoring_the_property()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } public string Name { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() =>
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Name, opt => opt.Ignore());
            }
            """);

        run.None("SM0001");
        run.Compiles().DoesNotEmit("Name =");
    }

    // -----------------------------------------------------------------
    // SM0002 — the names line up and no conversion bridges the types.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0002_is_a_warning_on_the_CreateMap_that_asked_for_the_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public bool Flag { get; set; } }
            public class Destination { public int Flag { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0002");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("does not convert 'bool' to 'int'", diagnostic.GetMessage());
        run.Compiles().DoesNotEmit("Flag =");
    }

    // -----------------------------------------------------------------
    // SM0003 — assignable-looking, and the setter cannot be called.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0003_is_a_warning_on_the_CreateMap_that_asked_for_the_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; internal set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0003");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("its setter is not public", diagnostic.GetMessage());
        run.Compiles();
    }

    /// <summary>
    /// The line SM0003 draws. A property with NO setter is a deliberate choice by whoever wrote
    /// the DTO, so nothing is said about it; only one that LOOKS assignable and is not gets a word.
    /// </summary>
    [Fact]
    public void A_get_only_property_is_skipped_without_a_word()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Assert.Empty(run.Ids());
        run.Compiles();
    }

    // -----------------------------------------------------------------
    // SM0004 — there is no constructor ShiftMapper can call AT ALL.
    //
    // It used to mean "no parameterless constructor", which stopped being
    // the interesting case once records and primary constructors became
    // mappable. A constructor that merely cannot be FILLED is SM0013,
    // which can name the parameter; this is what is left.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0004_is_a_warning_on_the_CreateMap_that_asked_for_the_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }

            public abstract class Destination
            {
                public int Id { get; set; }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0004");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("no constructor ShiftMapper can call", diagnostic.GetMessage());

        // The create half is gone. The overload that copies onto an object it was HANDED is
        // still perfectly possible, and is still emitted.
        run.Compiles()
           .DoesNotEmit("new global::Destination")
           .Emits("public virtual global::Destination Map(global::Source source, global::Destination destination)");
    }

    // -----------------------------------------------------------------
    // SM0005 — the whole mapper produced nothing. Three ways in.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0005_reports_a_mapper_that_is_not_partial_at_the_class_declaration()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0005");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.StartsWith("public class TestMapper : ShiftMapperBase", run.CodeUnder(diagnostic));
        Assert.Contains("it is not declared partial", diagnostic.GetMessage());

        // Nothing was generated, so nothing was said about the map inside it either.
        Assert.Empty(run.GeneratedFiles);
    }

    [Fact]
    public void Sm0005_reports_a_mapper_nested_in_a_type_that_is_not_partial()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public class Outer
            {
                public partial class TestMapper : ShiftMapperBase
                {
                    public TestMapper() => CreateMap<Source, Destination>();
                }
            }
            """);

        Diagnostic diagnostic = run.Single("SM0005");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.StartsWith("public partial class TestMapper : ShiftMapperBase", run.CodeUnder(diagnostic));
        Assert.Contains("a type it is nested inside is not declared partial", diagnostic.GetMessage());
        Assert.Empty(run.GeneratedFiles);
    }

    [Fact]
    public void Sm0005_reports_a_generic_mapper()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public partial class TestMapper<T> : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0005");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.StartsWith("public partial class TestMapper<T> : ShiftMapperBase", run.CodeUnder(diagnostic));
        Assert.Contains("generic mapper classes are not supported", diagnostic.GetMessage());
        Assert.Empty(run.GeneratedFiles);
    }

    /// <summary>
    /// A SUB-PATH OF THE NESTING RULE: the OUTERMOST container is the one that is not partial, two
    /// levels up. The check has to walk the whole chain of containers, not just the immediate one
    /// — and a test that nests only one deep cannot tell the difference.
    /// </summary>
    [Fact]
    public void Sm0005_reports_a_mapper_whose_outermost_container_is_not_partial()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public class Outermost
            {
                public partial class Middle
                {
                    public partial class TestMapper : ShiftMapperBase
                    {
                        public TestMapper() => CreateMap<Source, Destination>();
                    }
                }
            }
            """);

        Diagnostic diagnostic = run.Single("SM0005");

        Assert.Contains("a type it is nested inside is not declared partial", diagnostic.GetMessage());
        Assert.Empty(run.GeneratedFiles);
    }

    /// <summary>
    /// AND THE GENERIC CONTAINER. A mapper inside <c>Outer&lt;T&gt;</c> is as unsupported as a generic
    /// mapper — the emitted extension methods would need the container's type argument, which the
    /// call site has no way to supply — so it has to be reported rather than emitted wrong.
    /// </summary>
    [Fact]
    public void Sm0005_reports_a_mapper_nested_in_a_generic_container()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Destination { public int Id { get; set; } }

            public partial class Outer<T>
            {
                public partial class TestMapper : ShiftMapperBase
                {
                    public TestMapper() => CreateMap<Source, Destination>();
                }
            }
            """);

        Diagnostic diagnostic = run.Single("SM0005");

        Assert.Contains("generic mapper classes are not supported", diagnostic.GetMessage());
        Assert.Empty(run.GeneratedFiles);
    }

    // -----------------------------------------------------------------
    // SM0006 — SM0001's quieter twin, for the map ReverseMap added.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0006_is_informational_and_points_at_ReverseMap_rather_than_CreateMap()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } public string Only { get; set; } = ""; }
            public class Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>().ReverseMap();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0006");

        // INFO, not a warning: a DTO being a subset of its entity is the usual reason to reverse
        // a map at all, so this must not count against a warnings-as-errors build.
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal("ReverseMap", run.CodeUnder(diagnostic));
        Assert.Contains("the reverse map leaves 'Source.Only' unmapped", diagnostic.GetMessage());

        // And the forward direction stays quiet — SM0001 is for what the developer typed.
        run.None("SM0001");
        run.Compiles();
    }

    // -----------------------------------------------------------------
    // SM0007 — the case-insensitive fallback found several candidates.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0007_is_a_warning_naming_every_candidate()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string Sku { get; set; } = ""; public string SKU { get; set; } = ""; }
            public class Destination { public string sku { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0007");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("(SKU, Sku)", diagnostic.GetMessage());
        run.Compiles().DoesNotEmit("sku =");
    }

    /// <summary>
    /// Why SM0007 needs its "and no exact match" qualifier: with one, there is nothing ambiguous
    /// left to report — each destination property finds its own counterpart, and the two can
    /// never be swapped.
    /// </summary>
    [Fact]
    public void An_exact_match_settles_what_would_otherwise_be_ambiguous()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string Sku { get; set; } = ""; public string SKU { get; set; } = ""; }
            public class Destination { public string Sku { get; set; } = ""; public string SKU { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.None("SM0007");
        run.Compiles()
           .Emits("Sku = source.Sku,")
           .Emits("SKU = source.SKU,");
    }

    // -----------------------------------------------------------------
    // SM0008 — mapped, and something is dropped BY DESIGN.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0008_is_informational_on_the_CreateMap_that_asked_for_the_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System;

            public class Source { public DateTime Moment { get; set; } }
            public class Destination { public DateOnly Moment { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0008");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("the time of day is discarded", diagnostic.GetMessage());
        run.Compiles().Emits("global::ShiftMapper.ValueConverter.ToDateOnly(source.Moment)");
    }

    // -----------------------------------------------------------------
    // SM0009 — filled by reading text, so it can throw on DATA.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0009_is_informational_on_the_CreateMap_that_asked_for_the_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public string Count { get; set; } = ""; }
            public class Destination { public int Count { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0009");

        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("filled by parsing text when the map runs", diagnostic.GetMessage());

        // The mapping literal baked into the call is what lets a runtime failure name the two
        // properties it was working on.
        run.Compiles()
           .Emits("global::ShiftMapper.ValueConverter.Parse<int>(source.Count, \"Source.Count -> Destination.Count\")");
    }

    // -----------------------------------------------------------------
    // SM0010 — mapped, and an ORDINARY value can come out different.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0010_is_a_warning_on_the_CreateMap_that_asked_for_the_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public long Big { get; set; } }
            public class Destination { public int Big { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0010");

        // A WARNING where SM0008 is a note: this is the loss the developer did not ask for.
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("wraps round rather than being rejected", diagnostic.GetMessage());
        run.Compiles().Emits("Big = unchecked((int)source.Big)");
    }

    // -----------------------------------------------------------------
    // SM0011 — a nested object, and no map for it. An ERROR.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0011_is_an_error_on_the_CreateMap_that_reaches_the_nested_property()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Child { public int Id { get; set; } }
            public class ChildDto { public int Id { get; set; } }

            public class Source { public Child Item { get; set; } = new(); }
            public class Destination { public ChildDto Item { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        Diagnostic diagnostic = run.Single("SM0011");

        // The only ERROR ShiftMapper reports about a single property. A null nested object in a
        // response is indistinguishable from a null in the database, so the build stops.
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("CreateMap<Source, Destination>", run.CodeUnder(diagnostic));
        Assert.Contains("needs a map from 'Child' to 'ChildDto'", diagnostic.GetMessage());

        // Even while failing, what it DID write has to compile — otherwise the real message ends
        // up buried under CS errors from a file nobody can edit.
        run.Compiles().DoesNotEmit("Item =");
    }

    [Fact]
    public void Declaring_the_nested_map_settles_Sm0011()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Child { public int Id { get; set; } }
            public class ChildDto { public int Id { get; set; } }

            public class Source { public Child Item { get; set; } = new(); }
            public class Destination { public ChildDto Item { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                    CreateMap<Child, ChildDto>();
                }
            }
            """);

        run.None("SM0011");
        // The DIRECT method, not the generic dispatcher — nested mapping does not pay for a
        // typeof chain it can settle at compile time.
        run.Compiles().Emits("Item = MapToChildDto(source.Item)");
    }

    // -----------------------------------------------------------------
    // SM0012 — the nested maps form a LOOP. Also an error.
    // -----------------------------------------------------------------

    [Fact]
    public void Sm0012_is_an_error_naming_the_loop_it_found()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Brand { public List<Product> Products { get; set; } = new(); }
            public class Product { public Brand Brand { get; set; } = new(); }

            public class BrandDto { public List<ProductDto> Products { get; set; } = new(); }
            public class ProductDto { public BrandDto Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    CreateMap<Product, ProductDto>();
                }
            }
            """);

        Diagnostic diagnostic = run.Single("SM0012");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.StartsWith("CreateMap<", run.CodeUnder(diagnostic));

        // The message spells the loop out as the PROPERTIES it is made of, because the fix is to
        // ignore one of them and a list of type names would not say which.
        Assert.Contains("BrandDto.Products -> ProductDto.Brand -> BrandDto", diagnostic.GetMessage());

        // The edge that closes the loop is cut, so what was emitted still compiles rather than
        // recursing until the stack runs out.
        run.Compiles();
    }

    [Fact]
    public void Ignoring_the_back_reference_settles_Sm0012()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Brand { public List<Product> Products { get; set; } = new(); }
            public class Product { public Brand Brand { get; set; } = new(); }

            public class BrandDto { public List<ProductDto> Products { get; set; } = new(); }
            public class ProductDto { public BrandDto Brand { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Brand, BrandDto>();
                    CreateMap<Product, ProductDto>()
                        .ForMember(d => d.Brand, opt => opt.Ignore());
                }
            }
            """);

        run.None("SM0012");
        run.None("SM0011");
        run.Compiles();
    }
}
