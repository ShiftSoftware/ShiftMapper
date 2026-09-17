using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// DISCOVERY — which mapper classes the generated mapper is built from, chosen per project with
/// <c>o.Discovery</c> in the <c>AddShiftMapper</c> lambda.
///
/// <para><c>All</c> (the default) takes every class in sight and needs nothing named.
/// <c>LocalAndRegistered</c> takes every local class, and the package classes named with
/// <c>AddMapper</c> or shared by their package. <c>Registered</c> takes only what is named.</para>
/// </summary>
public class DiscoveryTests
{
    private const string Package =
        """
        using ShiftMapper;
        using Microsoft.Extensions.DependencyInjection;

        namespace Framework;

        public class FileDto { public string Name { get; set; } = ""; }
        public class FileSummary { public string Name { get; set; } = ""; }

        public class Tag { public string Label { get; set; } = ""; }
        public class TagDto { public string Label { get; set; } = ""; }

        public class FileMapper : ShiftMapperBase
        {
            public FileMapper() => CreateMap<FileDto, FileSummary>();
        }

        public class TagMapper : ShiftMapperBase
        {
            public TagMapper() => CreateMap<Tag, TagDto>();
        }
        """;

    /// <summary>The same package, sharing one of its two mappers with every referencing project.</summary>
    private const string SharingPackage = Package +
        """

        public static class Startup
        {
            public static IServiceCollection AddFramework(this IServiceCollection services) =>
                services.AddShiftMapper(o =>
                {
                    o.ShareMapper<FileMapper>();
                });
        }
        """;

    private const string Application =
        """
        using ShiftMapper;
        using Microsoft.Extensions.DependencyInjection;
        using Framework;

        public class Brand { public string Name { get; set; } = ""; }
        public class BrandDto { public string Name { get; set; } = ""; }

        public class Stock { public string Name { get; set; } = ""; }
        public class StockDto { public string Name { get; set; } = ""; }

        public class BrandMapper : ShiftMapperBase
        {
            public BrandMapper() => CreateMap<Brand, BrandDto>();
        }

        public class StockMapper : ShiftMapperBase
        {
            public StockMapper() => CreateMap<Stock, StockDto>();
        }
        """;

    private static GeneratorRun Run(string registration, string package = Package) =>
        GeneratorHarness.RunWithPackage(package, Application + "\n" + registration);

    // -----------------------------------------------------------------
    // ALL — the default.
    // -----------------------------------------------------------------

    /// <summary>With nothing named and nothing set, everything in sight is generated.</summary>
    [Fact]
    public void All_is_the_default_and_takes_everything()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) => services.AddShiftMapper();
            }
            """);

        run.Compiles()
           .Emits("MapToBrandDto(global::Brand source)")
           .Emits("MapToStockDto(global::Stock source)")
           .Emits("MapToFileSummary(global::Framework.FileDto source)")
           .Emits("MapToTagDto(global::Framework.Tag source)");

        run.None("SM0046");
    }

    /// <summary>An AddMapper under All changes nothing, and the build says so (SM0046).</summary>
    [Fact]
    public void AddMapper_under_All_has_no_effect_and_is_reported()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<FileMapper>());
            }
            """);

        run.Compiles()
           .Emits("MapToFileSummary(global::Framework.FileDto source)")
           .Emits("MapToTagDto(global::Framework.Tag source)");

        Diagnostic notice = run.Single("SM0046");

        Assert.Equal(DiagnosticSeverity.Warning, notice.Severity);
        Assert.Contains("AddMapper<FileMapper>()", notice.GetMessage());
        Assert.Contains("MapperDiscovery.All", notice.GetMessage());
        Assert.Equal("o.AddMapper<FileMapper>()", run.CodeUnder(notice));
    }

    // -----------------------------------------------------------------
    // LOCAL AND REGISTERED.
    // -----------------------------------------------------------------

    /// <summary>Every local class is in; a package class is in only when named.</summary>
    [Fact]
    public void LocalAndRegistered_takes_local_classes_and_named_package_classes()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.Discovery = MapperDiscovery.LocalAndRegistered;
                        o.AddMapper<FileMapper>();
                    });
            }
            """);

        run.Compiles()
           .Emits("MapToBrandDto(global::Brand source)")
           .Emits("MapToStockDto(global::Stock source)")
           .Emits("MapToFileSummary(global::Framework.FileDto source)")
           .DoesNotEmit("MapToTagDto")
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredComposition(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper), typeof(global::Framework.FileMapper))]")
           .DoesNotEmit("typeof(global::Framework.TagMapper)");

        run.None("SM0046");
        run.None("SM0005");
    }

    /// <summary>A package class the package SHARED is taken without being named, and announced (SM0043).</summary>
    [Fact]
    public void LocalAndRegistered_takes_a_shared_package_class()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.Discovery = MapperDiscovery.LocalAndRegistered);
            }
            """,
            SharingPackage);

        run.Compiles()
           .Emits("MapToFileSummary(global::Framework.FileDto source)")
           .DoesNotEmit("MapToTagDto");

        Diagnostic notice = run.Single("SM0043");

        Assert.Equal(DiagnosticSeverity.Info, notice.Severity);
        Assert.Contains("FileMapper", notice.GetMessage());
        Assert.Contains("ShiftMapperPackage", notice.GetMessage());
    }

    /// <summary>Naming a local class under LocalAndRegistered is allowed and changes nothing.</summary>
    [Fact]
    public void LocalAndRegistered_ignores_a_named_local_class_harmlessly()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.Discovery = MapperDiscovery.LocalAndRegistered;
                        o.AddMapper<BrandMapper>();
                    });
            }
            """);

        run.Compiles()
           .Emits("MapToBrandDto(global::Brand source)")
           .Emits("MapToStockDto(global::Stock source)");

        run.None("SM0046");
    }

    // -----------------------------------------------------------------
    // REGISTERED.
    // -----------------------------------------------------------------

    /// <summary>Only what is named — local or from a package — is generated; a local class nothing names is SM0005.</summary>
    [Fact]
    public void Registered_takes_only_named_classes()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.Discovery = MapperDiscovery.Registered;
                        o.AddMapper<BrandMapper>();
                        o.AddMapper<TagMapper>();
                    });
            }
            """);

        run.Compiles()
           .Emits("MapToBrandDto(global::Brand source)")
           .Emits("MapToTagDto(global::Framework.Tag source)")
           .DoesNotEmit("MapToStockDto")
           .DoesNotEmit("MapToFileSummary");

        Diagnostic skipped = run.Single("SM0005");

        Assert.Contains("StockMapper", skipped.GetMessage());
        Assert.Contains("MapperDiscovery.Registered", skipped.GetMessage());
        Assert.StartsWith("public class StockMapper : ShiftMapperBase", run.CodeUnder(skipped));

        // Nothing is said about the maps of a class that was not taken.
        run.None("SM0001");
    }

    /// <summary>Registered refuses a shared package class: it takes nothing it did not name.</summary>
    [Fact]
    public void Registered_does_not_take_a_shared_package_class()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.Discovery = MapperDiscovery.Registered;
                        o.AddMapper<BrandMapper>();
                        o.AddMapper<StockMapper>();
                    });
            }
            """,
            SharingPackage);

        run.Compiles()
           .DoesNotEmit("MapToFileSummary")
           .DoesNotEmit("MapToTagDto");

        run.None("SM0043");
    }

    /// <summary>A named package class whose package was built without the generator is SM0028.</summary>
    [Fact]
    public void A_named_package_class_without_metadata_is_reported()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package,
            Application +
            """

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.Discovery = MapperDiscovery.LocalAndRegistered;
                        o.AddMapper<FileMapper>();
                    });
            }
            """,
            runGeneratorOnPackage: false);

        Diagnostic problem = run.Single("SM0028");

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("FileMapper", problem.GetMessage());
    }

    // -----------------------------------------------------------------
    // THE SETTING ITSELF.
    // -----------------------------------------------------------------

    /// <summary>Two calls that set the mode differently: one project, one mode, and the build says so (SM0046).</summary>
    [Fact]
    public void Discovery_set_differently_in_two_calls_is_reported()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static void ConfigureApi(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.Discovery = MapperDiscovery.Registered);

                public static void ConfigureJobs(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.Discovery = MapperDiscovery.All);
            }
            """);

        run.Compiles();

        Assert.Contains("Discovery", run.Single("SM0046").GetMessage());
    }

    /// <summary>The setting behind an <c>if</c> is SM0035 like any other registration line.</summary>
    [Fact]
    public void A_conditional_discovery_setting_is_reported()
    {
        GeneratorRun run = Run(
            """
            public static class Startup
            {
                public static bool Flag;

                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        if (Flag)
                            o.Discovery = MapperDiscovery.Registered;
                    });
            }
            """);

        run.Compiles();

        Assert.Contains("Discovery", run.Single("SM0035").GetMessage());
    }

    /// <summary>A package's ShareMapper is written down as metadata; a non-public one is SM0044.</summary>
    [Fact]
    public void ShareMapper_is_written_into_the_packages_metadata()
    {
        GeneratorRun run = GeneratorHarness.Run(SharingPackage);

        run.Compiles()
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredSharedMapper(typeof(global::Framework.FileMapper))]");

        run.None("SM0044");

        GeneratorRun hidden = GeneratorHarness.Run(SharingPackage.Replace("public class FileMapper", "internal class FileMapper"));

        Assert.Contains("FileMapper", hidden.Single("SM0044").GetMessage());
        hidden.DoesNotEmit("ShiftMapperDeclaredSharedMapper");
    }
}
