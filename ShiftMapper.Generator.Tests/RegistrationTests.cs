using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// THE REGISTRATION, read at compile time.
///
/// <c>AddShiftMapper(o =&gt; ...)</c> is the one place outside a mapper class where declarations are
/// written, and the generator reads it exactly as it reads a constructor: what a mapper is given
/// there is baked into it, a mapper from a referenced assembly gets an ADAPTER here, and anything
/// the generator cannot follow is reported rather than half-applied.
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

        public class Stock { public string Name { get; set; } = ""; }
        public class StockDto { public string Name { get; set; } = ""; }
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Types + "\n" + body);

    // -----------------------------------------------------------------
    // WHAT THE LAMBDA SAYS IS BAKED.
    // -----------------------------------------------------------------

    /// <summary>An include written at registration reaches the mapper's generated code.</summary>
    [Fact]
    public void An_include_written_at_registration_is_baked_into_the_mapper()
    {
        GeneratorRun run = Run(
            """
            public partial class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<TestMapper>(m => m.IncludeMapper<StockMapper>()));
            }
            """);

        run.Compiles();

        string testMapperFile = run.GeneratedFiles.Single(file => file.Contains("partial class TestMapper"));
        Assert.Contains("MapToStockDto", testMapperFile);
    }

    /// <summary>A pack given to one mapper at registration is that mapper's, and no other's.</summary>
    [Fact]
    public void A_pack_written_for_one_mapper_at_registration_reaches_only_that_mapper()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<Money, string>(m => "R", m => "R");
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class OtherMapper : ShiftMapperBase
            {
                public OtherMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<TestMapper>(m => m.AddConversions<Rules>());
                        o.AddMapper<OtherMapper>();
                    });
            }
            """);

        run.Compiles();

        string testMapperFile = run.GeneratedFiles.Single(file => file.Contains("partial class TestMapper"));
        string otherMapperFile = run.GeneratedFiles.Single(file => file.Contains("partial class OtherMapper"));

        Assert.Contains("typeof(global::Rules)", testMapperFile);
        Assert.DoesNotContain("typeof(global::Rules)", otherMapperFile);
    }

    /// <summary>The short form registers one mapper and is read like the long one — a package mapper gets its adapter.</summary>
    [Fact]
    public void The_generic_short_form_is_read_as_a_registration()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package.Replace("public class PackageMapper", "public partial class PackageMapper"),
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;
            using Framework;

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper<PackageMapper>();
            }
            """);

        run.Compiles()
           .Emits("public sealed class Framework_PackageMapper_Adapter : global::Framework.PackageMapper");
    }

    /// <summary>SM0040 — two mappers in ONE call declaring the same pair.</summary>
    [Fact]
    public void Two_mappers_in_one_call_declaring_a_pair_is_reported()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public partial class OtherMapper : ShiftMapperBase
            {
                public OtherMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<TestMapper>();
                        o.AddMapper<OtherMapper>();
                    });
            }
            """);

        run.Compiles();

        Assert.Contains("OtherMapper", run.Single("SM0040").GetMessage());
    }

    /// <summary>
    /// SM0041 — one mapper, two calls, different packs. The generated code holds the UNION, and
    /// its metadata says so, which is what the runtime applies in both calls.
    /// </summary>
    [Fact]
    public void A_mapper_registered_differently_in_two_calls_is_reported_and_gets_the_union()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<Money, string>(m => "R", m => "R");
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void ConfigureApp(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<TestMapper>();
                        o.AddConversions<Rules>();
                    });

                public static void ConfigureTests(IServiceCollection services) =>
                    services.AddShiftMapper<TestMapper>();
            }
            """);

        run.Compiles()
           .Emits("Customizations.Conversion<global::Money, string>(typeof(global::Rules))")
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredComposition(typeof(global::TestMapper), typeof(global::Rules))]");

        Assert.Contains("Rules", run.Single("SM0041").GetMessage());
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
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) => services.AddShiftMapper(Options);

                private static void Options(ShiftMapperOptions o) => o.AddMapper<TestMapper>();
            }
            """);

        run.Compiles();

        Assert.Contains("inline lambda", run.Single("SM0035").GetMessage());
    }

    /// <summary>SM0035 — a registration behind an <c>if</c>.</summary>
    [Fact]
    public void A_conditional_registration_is_reported()
    {
        GeneratorRun run = Run(
            """
            public class Rules : ShiftMapperConversions
            {
                public Rules() => CreateConversion<Money, string>(m => "R", m => "R");
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static bool Flag;

                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<TestMapper>();

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

    /// <summary>SM0040 — the same mapper registered twice.</summary>
    [Fact]
    public void A_mapper_registered_twice_is_reported()
    {
        GeneratorRun run = Run(
            """
            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Brand, BrandDto>();
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<TestMapper>();
                        o.AddMapper<TestMapper>();
                    });
            }
            """);

        run.Compiles();

        Assert.Contains("registered more than once", run.Single("SM0040").GetMessage());
    }

    // -----------------------------------------------------------------
    // ADAPTERS — a mapper from a referenced assembly.
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

    private const string SealedPackage =
        """
        using ShiftMapper;

        namespace Framework;

        public class FileDto { public string Name { get; set; } = ""; }

        public class FileSummary { public string Name { get; set; } = ""; }

        public sealed partial class PackageMapper : ShiftMapperBase
        {
            public PackageMapper() => CreateMap<FileDto, FileSummary>();
        }
        """;

    /// <summary>
    /// THE ADAPTER: a package mapper registered directly gets a subclass here, overriding the
    /// package's virtual members, mirroring its constructor, re-implementing the interface, and
    /// announced with the assembly attribute the runtime reads.
    /// </summary>
    [Fact]
    public void A_package_mapper_registered_directly_gets_an_adapter()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package.Replace("public class PackageMapper", "public partial class PackageMapper"),
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;
            using Framework;

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<PackageMapper>());
            }
            """);

        run.Compiles()
           .Emits("[assembly: global::ShiftMapper.ShiftMapperAdapter(typeof(global::Framework.PackageMapper), typeof(global::ShiftMapper.Generated.Framework_PackageMapper_Adapter))]")
           .Emits("public sealed class Framework_PackageMapper_Adapter : global::Framework.PackageMapper, global::ShiftMapper.IShiftMapper")
           .Emits("public Framework_PackageMapper_Adapter(global::Framework.IClock @clock) : base(@clock)")
           .Emits("protected override global::System.Type DeclaringType => typeof(global::Framework.PackageMapper);")
           .Emits("public override global::Framework.FileSummary MapToFileSummary(global::Framework.FileDto source)")
           // The package's MapFrom still arrives — as the same lookup the package's own half uses.
           .Emits("Customizations.Value<global::Framework.FileDto, global::Framework.FileSummary, string>(\"Name\")")
           // And no second extension class: the base's dispatch virtually into the overrides.
           .DoesNotEmit("Framework_PackageMapper_Adapter_ShiftMapperExtensions");
    }

    /// <summary>
    /// THE POINT OF THE ADAPTER: this project's pack, given to every mapper, reaches the package
    /// mapper's maps — which its own compiled code never could.
    /// </summary>
    [Fact]
    public void A_registration_wide_pack_reaches_an_adapted_package_mapper()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package.Replace("public class PackageMapper", "public partial class PackageMapper"),
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;
            using Framework;

            public class Global : ShiftMapperConversions
            {
                public Global() => CreateConversion<long, string>(id => "H" + id, id => "H" + id);
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<PackageMapper>();
                        o.AddConversions<Global>();
                    });
            }
            """);

        run.Compiles()
           .Emits("Size = Customizations.Conversion<long, string>(typeof(global::Global))(source.Size)");
    }

    /// <summary>SM0039 — a sealed package mapper has nothing to override.</summary>
    [Fact]
    public void A_sealed_package_mapper_cannot_be_adapted()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            SealedPackage,
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;
            using Framework;

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<PackageMapper>());
            }
            """);

        Diagnostic problem = run.Single("SM0039");

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("sealed", problem.GetMessage());
        run.DoesNotEmit("_Adapter");
    }

    /// <summary>SM0028 — a package built without the generator carries nothing to adapt.</summary>
    [Fact]
    public void A_package_mapper_without_metadata_cannot_be_adapted()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Package,
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;
            using Framework;

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<PackageMapper>());
            }
            """,
            runGeneratorOnPackage: false);

        Diagnostic problem = run.Single("SM0028");

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("PackageMapper", problem.GetMessage());
    }
}
