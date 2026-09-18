using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SM0049 — the update overload replaces a nested collection with new objects.
///
/// <para>Harmless for a DTO; for rows with an identity of their own it duplicates or orphans them,
/// and nothing in the types can tell the two apart. So it is an informational note, once per map,
/// naming the members — the reader who owns tracked rows knows which maps those are.</para>
/// </summary>
public class UpdateRebuildTests
{
    private const string Types =
        """
        using System.Collections.Generic;
        using ShiftMapper;

        public class Invoice { public string Number { get; set; } = ""; public List<Line> Lines { get; set; } = new(); public Customer Customer { get; set; } = new(); }
        public class Line { public string Description { get; set; } = ""; }
        public class Customer { public string Name { get; set; } = ""; }

        public class InvoiceDto { public string Number { get; set; } = ""; public List<LineDto> Lines { get; set; } = new(); public CustomerDto Customer { get; set; } = new(); }
        public class LineDto { public string Description { get; set; } = ""; }
        public class CustomerDto { public string Name { get; set; } = ""; }
        """;

    [Fact]
    public void A_map_with_a_nested_collection_gets_one_note_naming_the_member()
    {
        GeneratorRun run = GeneratorHarness.Run(Types +
            """
            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
                    CreateMap<Invoice, InvoiceDto>();
                    CreateMap<Line, LineDto>();
                    CreateMap<Customer, CustomerDto>();
                }
            }
            """);

        Diagnostic note = run.Single("SM0049");

        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Contains("'Lines'", note.GetMessage());
        Assert.DoesNotContain("Customer", note.GetMessage());   // a nested OBJECT is mapped in place; only collections are rebuilt
    }

    [Fact]
    public void A_map_with_no_nested_collection_gets_none()
    {
        GeneratorRun run = GeneratorHarness.Run(Types +
            """
            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
                    CreateMap<Line, LineDto>();
                    CreateMap<Customer, CustomerDto>();
                }
            }
            """);

        run.None("SM0049");
    }
}
