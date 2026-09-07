using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// THE COMPILE-TIME EXTENSION CONTRACT — conversions a package declares in metadata.
///
/// <para>The harness compiles one snippet, so these declare the contract in the snippet's own
/// assembly. That is the same code path a referenced assembly takes: the reader walks the
/// compilation's own assembly and its references together, and reads both as symbols. What it can
/// never do is read a method BODY, and nothing here asks it to — which is the whole design.</para>
/// </summary>
public class DeclaredConversionTests
{
    private const string Framework =
        """
        using ShiftMapper;
        using System;
        using System.Collections.Generic;
        using System.Linq.Expressions;

        [assembly: ShiftMapperContract(1)]
        [assembly: ShiftMapperConversions(typeof(FrameworkConversions))]

        public class FileDto { public string Name { get; set; } = ""; }

        [ShiftMapperConversions]
        public static class FrameworkConversions
        {
            public static List<FileDto> ToFiles(string json) => new();

            [ShiftMapperQueryForm]
            public static Expression<Func<string, List<FileDto>>> ToFilesQuery => json => new List<FileDto>();

            public static string ToHashId(long id) => "H" + id;

            [ShiftMapperQueryForm]
            public static Expression<Func<long, string>> ToHashIdQuery => id => "H" + id;
        }

        public class Entity { public string Files { get; set; } = ""; public long Id { get; set; } }
        public class EntityDto { public List<FileDto> Files { get; set; } = new(); public string Id { get; set; } = ""; }
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Framework + "\n" + body);

    private const string Mapper =
        """
        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper() => CreateMap<Entity, EntityDto>();
        }
        """;

    // -----------------------------------------------------------------
    // THE CORE.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE TEST THAT MATTERS: a declared conversion becomes a DIRECT CALL, not a lookup.
    ///
    /// That is what metadata buys over the in-source route. A <c>CreateConversion</c> lambda can
    /// only ever be looked up on the mapper at run time; a declared one has a NAME, and the
    /// generated code says it.
    /// </summary>
    [Fact]
    public void A_declared_conversion_becomes_a_direct_call()
    {
        GeneratorRun run = Run(Mapper);

        run.Compiles()
           .Emits("global::FrameworkConversions.ToFiles(source.Files)")
           .Emits("global::FrameworkConversions.ToHashId(source.Id)")
           // Nothing is looked up on the mapper for these.
           .DoesNotEmit("Customizations.Conversion<");

        run.None("SM0002");
    }

    /// <summary>
    /// A DECLARED PAIR BEATS THE BUILT-IN TABLE, exactly as an in-source one does. <c>long</c> to
    /// <c>string</c> already converts, so a framework's hash-id rule would be ignored in silence
    /// under any other ordering.
    /// </summary>
    [Fact]
    public void A_declared_pair_beats_the_built_in_conversion()
    {
        Run(Mapper).Compiles().DoesNotEmit("ToInvariantString(source.Id)");
    }

    /// <summary>
    /// The query forms are REGISTERED by the generated mapper, which is the one thing a name cannot
    /// stand in for: the projection needs a tree to splice.
    /// </summary>
    [Fact]
    public void The_query_forms_are_registered_for_the_projection()
    {
        GeneratorRun run = Run(Mapper);

        run.Compiles()
           .Emits("protected override void RegisterDeclaredConversions")
           .Emits("customizations.RegisterQueryConversion(typeof(string)")
           .Emits("global::FrameworkConversions.ToFilesQuery")
           .Emits("global::ShiftMapper.MapCustomizations.Splice<string,");
    }

    /// <summary>
    /// The element lambda of a collection stays <c>static</c>, because a declared conversion
    /// captures nothing. An in-source conversion cannot manage that — it has to reach the mapper
    /// instance — so this is a real difference rather than a cosmetic one.
    /// </summary>
    [Fact]
    public void A_declared_conversion_keeps_collection_lambdas_static()
    {
        GeneratorRun run = GeneratorHarness.Run(Framework +
            """

            public class Bag { public List<long> Ids { get; set; } = new(); }
            public class BagDto { public List<string> Ids { get; set; } = new(); }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Bag, BagDto>();
            }
            """);

        run.Compiles().Emits("static item => global::FrameworkConversions.ToHashId(item)");
    }

    /// <summary>A conversion the PROJECT declares wins over one a package declares.</summary>
    [Fact]
    public void A_source_declared_conversion_wins_over_a_declared_one()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<long, string>(id => "LOCAL" + id, id => "LOCAL" + id);
                    CreateMap<Entity, EntityDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Conversion<long, string>()")
           // The registration line still mentions ToHashIdQuery, which is why this names the CALL
           // rather than the method: query forms are registered whether or not anything uses them.
           .DoesNotEmit("global::FrameworkConversions.ToHashId(source.Id)");
    }

    // -----------------------------------------------------------------
    // THE DIAGNOSTICS.
    // -----------------------------------------------------------------

    /// <summary>
    /// SM0031 — two holders claiming one pair. An ERROR, because there is no answer to pick: half
    /// the maps in the application would convert the other way and nobody could see why.
    /// </summary>
    [Fact]
    public void Two_holders_claiming_one_pair_is_an_error()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            [assembly: ShiftMapperConversions(typeof(FirstConversions))]
            [assembly: ShiftMapperConversions(typeof(SecondConversions))]

            [ShiftMapperConversions]
            public static class FirstConversions { public static string ToText(long id) => "A" + id; }

            [ShiftMapperConversions]
            public static class SecondConversions { public static string ToText(long id) => "B" + id; }

            public class Entity { public long Id { get; set; } }
            public class EntityDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Entity, EntityDto>();
            }
            """);

        string message = run.Single("SM0031").GetMessage();

        Assert.Contains("FirstConversions", message);
        Assert.Contains("SecondConversions", message);
    }

    /// <summary>SM0032 — a query form whose pair nothing declares.</summary>
    [Fact]
    public void An_orphan_query_form_is_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System;
            using System.Linq.Expressions;

            [assembly: ShiftMapperConversions(typeof(FrameworkConversions))]

            [ShiftMapperConversions]
            public static class FrameworkConversions
            {
                public static string ToText(long id) => "H" + id;

                // For a pair nothing declares a memory form for.
                [ShiftMapperQueryForm]
                public static Expression<Func<int, string>> Orphan => id => "X" + id;
            }

            public class Entity { public long Id { get; set; } }
            public class EntityDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Entity, EntityDto>();
            }
            """);

        Assert.Contains("Orphan", run.Single("SM0032").GetMessage());
    }

    /// <summary>SM0033 — a package built against a contract this generator does not know.</summary>
    [Fact]
    public void A_newer_contract_is_refused_and_its_conversions_ignored()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            [assembly: ShiftMapperContract(99)]
            [assembly: ShiftMapperConversions(typeof(FrameworkConversions))]

            [ShiftMapperConversions]
            public static class FrameworkConversions { public static string ToText(long id) => "H" + id; }

            public class Entity { public long Id { get; set; } }
            public class EntityDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Entity, EntityDto>();
            }
            """);

        Assert.Contains("99", run.Single("SM0033").GetMessage());

        // IGNORED, not half-read: the member falls back to the built-in long -> string.
        run.Compiles().DoesNotEmit("global::FrameworkConversions.ToText");
    }

    /// <summary>A project referencing no such package is completely unaffected.</summary>
    [Fact]
    public void A_project_with_no_declared_conversions_is_unchanged()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Entity { public long Id { get; set; } }
            public class EntityDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Entity, EntityDto>();
            }
            """);

        run.Compiles().DoesNotEmit("RegisterDeclaredConversions");
        run.None("SM0031");
        run.None("SM0032");
        run.None("SM0033");
    }
}
