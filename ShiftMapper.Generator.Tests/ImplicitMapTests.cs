using System.Reflection;
using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// IMPLICIT MAPS — maps a FRAMEWORK's marked generic type declares for every type that closes it.
///
/// <para>The framework writes <c>[ShiftMapperDeclaresMap("TEntity", "TView", …)]</c> on its base
/// class once; an application writes <c>class InvoiceRepository : Repository&lt;Invoice, InvoiceListDto,
/// InvoiceDto&gt;</c> and nothing else, and the three maps exist. These tests pin what "nothing else"
/// means: no attribute, no class, no registration — and what still wins over it.</para>
/// </summary>
public class ImplicitMapTests
{
    /// <summary>A framework package: the marked base class, a marked attribute, and the rules pack the markers name.</summary>
    private const string Framework =
        """
        using System;
        using System.Collections.Generic;
        using ShiftMapper;

        namespace Framework
        {
            public class PlatformConversions : ShiftMapperConversions
            {
                public PlatformConversions()
                {
                    CreateConversion<long, string>(id => "H" + id, id => "H" + id);
                }
            }

            [ShiftMapperDeclaresMap("TEntity", "TView", Reverse = true, Nested = 10, Flattening = DeclaredOption.False, Rules = typeof(PlatformConversions))]
            [ShiftMapperDeclaresMap("TEntity", "TList", Nested = 10, Flattening = DeclaredOption.False, Rules = typeof(PlatformConversions))]
            [ShiftMapperDeclaresMap("TEntity", "TEntity", Rules = typeof(PlatformConversions))]
            public abstract class Repository<TEntity, TList, TView>
            {
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
            [ShiftMapperDeclaresMap("this", "TView", Reverse = true, Nested = 10, Flattening = DeclaredOption.False)]
            [ShiftMapperDeclaresMap("this", "TList", Nested = 10, Flattening = DeclaredOption.False)]
            public sealed class EndpointAttribute<TList, TView> : Attribute
            {
                public EndpointAttribute(string route) { Route = route; }
                public string Route { get; }
            }
        }
        """;

    /// <summary>An application: an entity graph two levels deep, its DTOs, and one repository. No mapper class.</summary>
    private const string Application =
        """
        using System;
        using System.Collections.Generic;
        using ShiftMapper;
        using Framework;

        namespace App
        {
            public class Invoice
            {
                public long Id { get; set; }
                public string Number { get; set; } = "";
                public Customer Customer { get; set; } = new();
                public List<InvoiceLine> Lines { get; set; } = new();
            }

            public class Customer { public string Name { get; set; } = ""; }

            public class InvoiceLine
            {
                public long Id { get; set; }
                public string Description { get; set; } = "";
                public Invoice Invoice { get; set; } = null!;
            }

            public class InvoiceDto
            {
                public long Id { get; set; }
                public string Number { get; set; } = "";
                public string CustomerName { get; set; } = "";
                public List<InvoiceLineDto> Lines { get; set; } = new();
            }

            public class InvoiceLineDto
            {
                public long Id { get; set; }
                public string Description { get; set; } = "";
            }

            public class InvoiceListDto
            {
                public string Id { get; set; } = "";
                public string Number { get; set; } = "";
            }

            public class InvoiceRepository : Repository<Invoice, InvoiceListDto, InvoiceDto>
            {
            }
        }
        """;

    private const string ImplicitMapper = "global::ShiftMapper.Generated.ShiftMapperSnippet.ImplicitMapper";

    /// <summary>
    /// Framework and application in ONE compilation, for the tests that load and run the generated
    /// code: the harness's package assemblies are metadata-only and cannot be loaded at run time. The
    /// marker is read the same way from a source symbol as from a metadata one.
    /// </summary>
    private static string SingleFile(string application)
    {
        // Raw string literals keep this file's line endings; normalise before cutting the usings out.
        string framework = Framework.Replace("\r\n", "\n");
        string app = application.Replace("\r\n", "\n");

        return framework.Replace("using ShiftMapper;\n", "using ShiftMapper;\nusing Framework;\n")
            + "\n"
            + app.Replace("using System;\n", "").Replace("using System.Collections.Generic;\n", "")
                 .Replace("using ShiftMapper;\n", "").Replace("using Framework;\n", "");
    }

    [Fact]
    public void A_class_closing_a_marked_base_declares_the_marked_maps_with_nothing_else_written()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(Framework, Application);

        run.Compiles()
           .Emits("MapToInvoiceDto(global::App.Invoice source)")       // TEntity -> TView
           .Emits("MapToInvoice(global::App.InvoiceDto source)")       // ...and Reverse = true
           .Emits("MapToInvoiceListDto(global::App.Invoice source)")   // TEntity -> TList
           .Emits("MapToInvoice(global::App.Invoice source)")          // TEntity -> TEntity
           .Emits($"public sealed class ImplicitMapper : global::ShiftMapper.ShiftMapperBase");

        run.None("SM0011");

        // The only warning is CustomerName, which the marker's Flattening = False leaves unmapped on
        // purpose (its own test); the reverse maps' unfilled entity members are SM0006 notes.
        Assert.Single(run.All("SM0001"));
        Assert.NotEmpty(run.All("SM0006"));
    }

    [Fact]
    public void Nested_members_get_implicit_maps_of_their_own_to_the_markers_depth()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(Framework, Application);

        // Invoice.Lines (List<InvoiceLine>) -> InvoiceDto.Lines (List<InvoiceLineDto>): a pair nobody
        // declared, mapped because the marker said Nested = 10. Both directions, since the root is reversed.
        run.Compiles()
           .Emits("MapToInvoiceLineDto(global::App.InvoiceLine source)")
           .Emits("MapToInvoiceLine(global::App.InvoiceLineDto source)")
           .Emits("Lines = global::ShiftMapper.ValueConverter.ToListOrEmpty<global::App.InvoiceLine, global::App.InvoiceLineDto>(source.Lines, item => MapToInvoiceLineDto(item))");
    }

    [Fact]
    public void The_maps_run()
    {
        GeneratorRun run = GeneratorHarness.Run(SingleFile(Application) +
            """
            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new ShiftMapper.Mapper();
                    var invoice = new App.Invoice
                    {
                        Id = 7, Number = "INV-1",
                        Lines = { new App.InvoiceLine { Id = 1, Description = "bolts" }, new App.InvoiceLine { Id = 2, Description = "nuts" } },
                    };

                    App.InvoiceDto dto = mapper.MapToInvoiceDto(invoice);
                    App.InvoiceListDto row = mapper.MapToInvoiceListDto(invoice);
                    App.Invoice back = mapper.MapToInvoice(dto);

                    return dto.Number + "|" + dto.Lines.Count + "|" + dto.Lines[1].Description + "|" + row.Id + "|" + back.Lines.Count;
                }
            }
            """);

        Assembly assembly = run.Load();

        object result = assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null)!;

        // row.Id is "H7": the pack the marker named (long -> string as a hash id) applied to the LIST map.
        Assert.Equal("INV-1|2|nuts|H7|2", result);
    }

    [Fact]
    public void The_markers_rules_pack_applies_to_the_implicit_maps()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(Framework, Application);

        run.Compiles()
           .Emits("Customizations.Conversion<long, string>(typeof(global::Framework.PlatformConversions))(source.Id)");
    }

    [Fact]
    public void Flattening_is_off_when_the_marker_says_so()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(Framework, Application);

        // InvoiceDto.CustomerName would flatten to Invoice.Customer.Name; the marker said not to, so
        // it is an ordinary unmapped member — reported at the repository, which is what declared the map.
        Diagnostic unmapped = run.Single("SM0001");

        Assert.Contains("CustomerName", unmapped.GetMessage());
        Assert.Contains("InvoiceRepository", run.CodeUnder(unmapped));
        run.None("SM0020");
    }

    [Fact]
    public void A_marked_attribute_declares_maps_for_the_type_it_is_applied_to()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Framework,
            """
            using Framework;

            namespace App
            {
                public class Country { public long Id { get; set; } public string Name { get; set; } = ""; }
                public class CountryDto { public long Id { get; set; } public string Name { get; set; } = ""; }

                [Endpoint<CountryDto, CountryDto>("api/country")]
                public class CountryRow : Country { }
            }
            """);

        run.Compiles()
           .Emits("MapToCountryDto(global::App.CountryRow source)")
           .Emits("MapToCountryRow(global::App.CountryDto source)");
    }

    [Fact]
    public void An_explicit_CreateMap_replaces_the_implicit_map_for_that_pair_only()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Framework,
            Application +
            """
            namespace App
            {
                public class InvoiceMapper : ShiftMapper.ShiftMapperBase
                {
                    public InvoiceMapper() =>
                        CreateMap<Invoice, InvoiceListDto>()
                            .ForMember(d => d.Number, opt => opt.MapFrom(i => "#" + i.Number));
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Value<global::App.Invoice, global::App.InvoiceListDto, string>(\"Number\"))(source)")
           .Emits("MapToInvoiceDto(global::App.Invoice source)");   // the other pairs stay implicit

        Diagnostic replaced = run.Single("SM0047");

        Assert.Equal(DiagnosticSeverity.Info, replaced.Severity);
        Assert.Contains("InvoiceMapper", replaced.GetMessage());
        run.None("SM0042");
        run.None("SM0027");
    }

    [Fact]
    public void A_cycle_is_cut_with_a_note_rather_than_an_error()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Framework,
            """
            using System.Collections.Generic;
            using Framework;

            namespace App
            {
                public class Node { public string Name { get; set; } = ""; public List<Node> Children { get; set; } = new(); public Node? Parent { get; set; } }
                public class NodeDto { public string Name { get; set; } = ""; public List<NodeDto> Children { get; set; } = new(); public NodeDto? Parent { get; set; } }
                public class NodeListDto { public string Name { get; set; } = ""; }

                public class NodeRepository : Repository<Node, NodeListDto, NodeDto> { }
            }
            """);

        // Node -> NodeDto nests Node -> NodeDto through Children and Parent: the very map being built.
        // The members are cut and said so; nothing recurses and nothing is an error.
        run.Compiles();

        Diagnostic[] cut = run.All("SM0048");

        Assert.NotEmpty(cut);
        Assert.All(cut, d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));
        run.None("SM0012");
        run.None("SM0011");
    }

    [Fact]
    public void Nesting_stops_at_the_depth_asked_for()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Framework.Replace("Nested = 10", "Nested = 0"),
            Application);

        // Nested = 0: the root maps exist, the line pair is not declared, and the member is simply
        // left out — not SM0011, because the framework asked for exactly this.
        run.Compiles()
           .Emits("MapToInvoiceDto(global::App.Invoice source)")
           .DoesNotEmit("MapToInvoiceLineDto(");

        run.None("SM0011");
    }

    [Fact]
    public void Implicit_maps_travel_to_a_referencing_project_like_a_package_mappers()
    {
        // Framework -> Data (closes the marker) -> Application (references both, declares nothing).
        GeneratorRun run = GeneratorHarness.RunWithPackages(
            new[] { Framework, Application },
            """
            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new ShiftMapper.Mapper();
                    var dto = mapper.MapToInvoiceDto(new App.Invoice { Number = "N", Lines = { new App.InvoiceLine { Description = "x" } } });
                    return dto.Number + dto.Lines.Count;
                }
            }
            """,
            chained: true);

        // The application's own generated mapper carries the maps, re-baked from Data's metadata,
        // and still knows they are implicit — its own CreateMap for a pair would replace one.
        run.Compiles()
           .Emits("MapToInvoiceDto(global::App.Invoice source)")
           .Emits("MapToInvoiceLineDto(global::App.InvoiceLine source)")
           .Emits("MapToInvoiceListDto(global::App.Invoice source)");

        Assert.Contains("typeof(global::ShiftMapper.Generated.ShiftMapperPackage2.ImplicitMapper)", run.Generated);
    }

    [Fact]
    public void The_implicit_maps_are_written_to_the_assemblys_metadata_as_implicit()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(Framework, Application);

        Assert.Contains($"ShiftMapperDeclaredMapper(typeof({ImplicitMapper}))", run.Metadata);
        Assert.Contains($"ShiftMapperDeclaredMap(typeof({ImplicitMapper}), typeof(global::App.Invoice), typeof(global::App.InvoiceDto), Flattening = global::ShiftMapper.DeclaredOption.False, Implicit = true)", run.Metadata);
        Assert.Contains($"ShiftMapperDeclaredComposition(typeof({ImplicitMapper}), typeof(global::Framework.PlatformConversions))", run.Metadata);
    }

    [Fact]
    public void A_marker_naming_nothing_concrete_is_reported_and_declares_nothing()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Framework.Replace("[ShiftMapperDeclaresMap(\"TEntity\", \"TEntity\", Rules = typeof(PlatformConversions))]",
                              "[ShiftMapperDeclaresMap(\"TEntity\", \"TNothing\", Rules = typeof(PlatformConversions))]"),
            Application);

        Diagnostic problem = run.Single("SM0053");

        Assert.Contains("TNothing", problem.GetMessage());
        run.Compiles().Emits("MapToInvoiceDto(global::App.Invoice source)");
    }

    [Fact]
    public void The_marker_works_in_the_same_compilation_too()
    {
        GeneratorRun run = GeneratorHarness.Run(SingleFile(Application));

        run.Compiles()
           .Emits("MapToInvoiceDto(global::App.Invoice source)")
           .Emits("MapToInvoiceLineDto(global::App.InvoiceLine source)");
    }
}
