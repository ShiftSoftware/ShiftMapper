using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// THE REGISTRATION, read at compile time.
///
/// <c>AddShiftMapper(o =&gt; ...)</c> is the one place outside a mapper class where declarations are
/// written, and the generator reads it exactly as it reads a constructor: a pack added there is
/// baked into every map of the project's generated mapper, and anything the generator cannot
/// follow is reported rather than half-applied. Nothing names a mapper: the call registers the
/// assembly's generated mapper, whatever it holds.
/// </summary>
public class RegistrationTests
{
    private const string Types =
        """
        using ShiftMapper;
        using Microsoft.Extensions.DependencyInjection;
        using System;
        using System.Collections.Generic;

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

        public class Stock { public string Name { get; set; } = ""; public Money Fee { get; set; } = new(); }
        public class StockDto { public string Name { get; set; } = ""; public string Fee { get; set; } = ""; }
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Types + "\n" + body);

    // -----------------------------------------------------------------
    // WHAT THE LAMBDA SAYS IS BAKED.
    // -----------------------------------------------------------------

    /// <summary>A pack added at registration reaches EVERY map in the project, whichever class declared it.</summary>
    [Fact]
    public void A_pack_added_at_registration_reaches_every_map()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<Money, string>(m => "$" + m.Amount, m => "$" + m.Amount);
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddConversions<Rules>());
            }
            """);

        run.Compiles()
           .Emits("Price = Customizations.Conversion<global::Money, string>(typeof(global::Rules))(source.Price)")
           .Emits("Fee = Customizations.Conversion<global::Money, string>(typeof(global::Rules))(source.Fee)")
           // Recorded as composition, so the runtime applies the pack from this assembly's metadata alone.
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredComposition(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper), typeof(global::Rules))]");

        run.None("SM0002");
    }

    /// <summary>The parameterless form registers, and reads nothing.</summary>
    [Fact]
    public void The_short_form_is_a_registration_with_nothing_to_read()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) => services.AddShiftMapper();
            }
            """);

        run.Compiles();
        run.None("SM0035");

        // Money -> string has no rule anywhere, so it is SM0002 as it would be without the call.
        run.Single("SM0002");
    }

    /// <summary>
    /// NEAREST WINS. A pack a class added itself sits before the registration's for that class's
    /// maps, and the registration's answers for the classes that added nothing.
    /// </summary>
    [Fact]
    public void A_classs_own_pack_beats_the_registrations_for_its_maps()
    {
        GeneratorRun run = Run(
            """
            public class Own : ShiftMapperConversions
            {
                public Own() => CreateConversion<Money, string>(m => "own", m => "own");
            }

            public class Shared : ShiftMapperConversions
            {
                public Shared() => CreateConversion<Money, string>(m => "shared", m => "shared");
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper()
                {
                    AddConversions<Own>();
                    CreateMap<Brand, BrandDto>();
                }
            }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddConversions<Shared>());
            }
            """);

        run.Compiles()
           .Emits("Price = Customizations.Conversion<global::Money, string>(typeof(global::Own))(source.Price)")
           .Emits("Fee = Customizations.Conversion<global::Money, string>(typeof(global::Shared))(source.Fee)");

        // Different distances, so not a conflict.
        run.None("SM0031");
    }

    /// <summary>Two calls in one project add up: the generated mapper gets the union of their packs.</summary>
    [Fact]
    public void Two_calls_add_up()
    {
        GeneratorRun run = Run(
            """
            public class Prices : ShiftMapperConversions
            {
                public Prices() => CreateConversion<Money, string>(m => "$" + m.Amount, m => "$" + m.Amount);
            }

            public class Ids : ShiftMapperConversions
            {
                public Ids() => CreateConversion<long, string>(id => "#" + id, id => "#" + id);
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void ConfigureApi(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddConversions<Prices>());

                public static void ConfigureJobs(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddConversions<Ids>());
            }
            """);

        run.Compiles()
           .Emits("Price = Customizations.Conversion<global::Money, string>(typeof(global::Prices))(source.Price)")
           .Emits("Id = Customizations.Conversion<long, string>(typeof(global::Ids))(source.Id)");
    }

    // -----------------------------------------------------------------
    // WHAT THE GENERATOR CANNOT FOLLOW.
    // -----------------------------------------------------------------

    /// <summary>SM0035 — a method group instead of a lambda.</summary>
    [Fact]
    public void A_method_group_configuration_is_reported()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<Money, string>(m => "R", m => "R");
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) => services.AddShiftMapper(Options);

                private static void Options(ShiftMapperOptions o) => o.AddConversions<Rules>();
            }
            """);

        run.Compiles();

        Assert.Contains("inline lambda", run.Single("SM0035").GetMessage());
    }

    /// <summary>SM0035 — a pack behind an <c>if</c>.</summary>
    [Fact]
    public void A_conditional_registration_is_reported()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<Money, string>(m => "R", m => "R");
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static bool Flag;

                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        if (Flag)
                            o.AddConversions<Rules>();
                    });
            }
            """);

        run.Compiles();

        Diagnostic problem = run.Single("SM0035");

        Assert.Contains("AddConversions", problem.GetMessage());
        Assert.Contains("inside an 'if'", problem.GetMessage());
    }

    // -----------------------------------------------------------------
    // A MAPPER FROM A REFERENCED ASSEMBLY.
    // -----------------------------------------------------------------

    private const string Package =
        """
        using ShiftMapper;
        using System;
        using System.Collections.Generic;

        namespace Framework;

        public class FileDto { public string Name { get; set; } = ""; public long Size { get; set; } }

        public class FileSummary { public string Name { get; set; } = ""; public string Size { get; set; } = ""; }

        public interface IClock { DateTime Now { get; } }

        public class PackageMapper : ShiftMapperBase
        {
            public PackageMapper(IClock clock) =>
                CreateMap<FileDto, FileSummary>()
                    .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
        }
        """;

    /// <summary>
    /// A package's map is generated into the application — re-baked with the application's own
    /// packs — with nothing written to ask for it. The package mapper's MapFrom still arrives, as
    /// the same lookup the package's own generated mapper uses; its constructor dependency is the
    /// runtime's business, on first use.
    /// </summary>
    [Fact]
    public void A_package_mapper_is_generated_into_the_application_with_its_packs()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package,
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;
            using Framework;

            public class Ids : ShiftMapperConversions
            {
                public Ids() => CreateConversion<long, string>(id => "#" + id, id => "#" + id);
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddConversions<Ids>());
            }
            """);

        run.Compiles()
           .Emits("public global::Framework.FileSummary MapToFileSummary(global::Framework.FileDto source)")
           .Emits("Customizations.Value<global::Framework.FileDto, global::Framework.FileSummary, string>(\"Name\")")
           .Emits("Size = Customizations.Conversion<long, string>(typeof(global::Ids))(source.Size)")
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredComposition(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper), typeof(global::Framework.PackageMapper))]")
           // The application's extension methods cover the package's pair too.
           .Emits("MapToFileSummary(this global::ShiftMapper.Mapper mapper, global::Framework.FileDto source)");

        run.None("SM0028");
    }

    /// <summary>A pair the application declares itself wins over the package's, and the build says so (SM0027).</summary>
    [Fact]
    public void An_applications_own_declaration_wins_over_a_packages()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package,
            """
            using ShiftMapper;
            using Framework;

            public class FileMapper : ShiftMapperBase
            {
                public FileMapper() =>
                    CreateMap<FileDto, FileSummary>()
                        .ForMember(d => d.Name, opt => opt.MapFrom(s => "app:" + s.Name));
            }
            """);

        run.Compiles();

        Diagnostic notice = run.Single("SM0027");

        Assert.Equal(DiagnosticSeverity.Warning, notice.Severity);
        Assert.Contains("FileMapper", notice.GetMessage());
        Assert.Contains("PackageMapper", notice.GetMessage());
        Assert.Equal("CreateMap<FileDto, FileSummary>", run.CodeUnder(notice));

        // ONE map for the pair, and it is the application's.
        run.DoesNotEmit("typeof(global::Framework.PackageMapper), typeof(global::Framework.FileDto), typeof(global::Framework.FileSummary)");
        run.None("SM0042");
    }

    /// <summary>Two PACKAGES each declaring their own map for one pair: nothing nearer settles it (SM0042).</summary>
    [Fact]
    public void Two_packages_declaring_one_pair_is_an_error()
    {
        string second = Package
            .Replace("namespace Framework;", "namespace Other;")
            .Replace("public class FileDto", "public class OtherDto")
            .Replace("public class FileSummary", "public class OtherSummary")
            .Replace("PackageMapper", "OtherMapper")
            .Replace("CreateMap<FileDto, FileSummary>()", "CreateMap<Framework.FileDto, Framework.FileSummary>()")
            .Replace("using System;", "using System;\n        using Framework;");

        // The second package REFERENCES the first, so it can name the first's types — which the
        // harness models by compiling the first as a reference of the second.
        GeneratorRun run = GeneratorHarness.RunWithPackages(
            new[] { Package, second },
            """
            using ShiftMapper;
            """,
            chained: true);

        Diagnostic problem = run.Single("SM0042");

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("PackageMapper", problem.GetMessage());
        Assert.Contains("OtherMapper", problem.GetMessage());
    }

    /// <summary>
    /// A package built WITHOUT the generator announces no mapper, so there is nothing to generate
    /// and nothing to report about it — until something in this project asks for one of its
    /// packs, which is SM0028.
    /// </summary>
    [Fact]
    public void A_package_built_without_the_generator_declares_nothing()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package,
            """
            using ShiftMapper;
            using Framework;

            public class Brand { public string Name { get; set; } = ""; }
            public class BrandDto { public string Name { get; set; } = ""; }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }
            """,
            runGeneratorOnPackage: false);

        run.Compiles()
           .Emits("MapToBrandDto(global::Brand source)")
           .DoesNotEmit("MapToFileSummary");

        run.None("SM0028");
    }
}
