using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SHARED PACKS — <c>o.ShareConversions&lt;T&gt;()</c>: a package registering a pack on behalf of
/// every project that references it.
///
/// <para>The one declaration that reaches a project without the project naming the type. The
/// package's build writes it down (<c>ShiftMapperDeclaredSharedPack</c>); the referencing
/// project's generator reads it and treats it as an <c>AddConversions</c> appended to every
/// <c>AddShiftMapper</c> call there — the furthest level, so anything the project wrote itself
/// still wins — and says so (SM0043). The runtime follows from the composition attribute the
/// referencing project's own build emits, so the two halves cannot disagree.</para>
/// </summary>
public class SharedPackTests
{
    /// <summary>A package whose own registration shares its pack — what a framework's AddXxx does.</summary>
    private const string SharingPackage =
        """
        using ShiftMapper;
        using Microsoft.Extensions.DependencyInjection;

        namespace Framework;

        public class FileDto { public string Name { get; set; } = ""; public long Size { get; set; } }

        public class FileSummary { public string Name { get; set; } = ""; public string Size { get; set; } = ""; }

        public partial class PlatformMapper : ShiftMapperBase
        {
            public PlatformMapper() => CreateMap<FileDto, FileSummary>();
        }

        public class PlatformConversions : ShiftMapperConversions
        {
            public PlatformConversions() => CreateConversion<long, string>(id => "H" + id, id => "H" + id);
        }

        public static class Startup
        {
            public static IServiceCollection AddPlatform(this IServiceCollection services) =>
                services.AddShiftMapper(o =>
                {
                    o.AddMapper<PlatformMapper>();
                    o.ShareConversions<PlatformConversions>();
                });
        }
        """;

    /// <summary>An application with a mapper of its own that names nothing of the package's.</summary>
    private const string Application =
        """
        using ShiftMapper;
        using Microsoft.Extensions.DependencyInjection;

        public class Brand { public long Id { get; set; } }

        public class BrandDto { public string Id { get; set; } = ""; }

        public partial class AppMapper : ShiftMapperBase
        {
            public AppMapper() => CreateMap<Brand, BrandDto>();
        }
        """;

    // -----------------------------------------------------------------
    // THE SHARING SIDE.
    // -----------------------------------------------------------------

    /// <summary>The package's build writes the shared pack down, and applies it to its own call too.</summary>
    [Fact]
    public void A_shared_pack_is_written_into_the_packages_metadata_and_applied_to_its_own_call()
    {
        GeneratorRun run = GeneratorHarness.Run(SharingPackage);

        run.Compiles()
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredSharedPack(typeof(global::Framework.PlatformConversions))]")
           // ShareConversions is AddConversions for the call that wrote it.
           .Emits("Size = Customizations.Conversion<long, string>(typeof(global::Framework.PlatformConversions))(source.Size)");

        run.None("SM0043");
        run.None("SM0044");
    }

    /// <summary>SM0044 — a pack the referencing project's generated code could not name.</summary>
    [Fact]
    public void A_shared_pack_that_is_not_public_is_an_error()
    {
        GeneratorRun run = GeneratorHarness.Run(
            SharingPackage.Replace("public class PlatformConversions", "internal class PlatformConversions"));

        Diagnostic problem = run.Single("SM0044");

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("PlatformConversions", problem.GetMessage());
        Assert.Contains("ShareConversions<PlatformConversions>()", run.CodeUnder(problem));

        run.DoesNotEmit("ShiftMapperDeclaredSharedPack");
    }

    /// <summary>A shared pack behind an <c>if</c> is SM0035 like any other registration line.</summary>
    [Fact]
    public void A_conditional_share_is_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(
            SharingPackage.Replace(
                "o.ShareConversions<PlatformConversions>();",
                "if (System.Environment.MachineName.Length > 0) o.ShareConversions<PlatformConversions>();"));

        Assert.Contains("ShareConversions", run.Single("SM0035").GetMessage());
        run.DoesNotEmit("ShiftMapperDeclaredSharedPack");
    }

    // -----------------------------------------------------------------
    // THE RECEIVING SIDE.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE POINT: the application registers its own mapper, names nothing of the package's, and
    /// its long renders as the package's hash id — in the code, in the metadata the runtime reads,
    /// and in the build output.
    /// </summary>
    [Fact]
    public void A_shared_pack_reaches_every_mapper_a_referencing_project_registers()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            SharingPackage,
            Application +
            """

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<AppMapper>());
            }
            """);

        run.Compiles()
           .Emits("Id = Customizations.Conversion<long, string>(typeof(global::Framework.PlatformConversions))(source.Id)")
           // Recorded as composition, so the runtime applies the pack from this assembly's metadata alone.
           .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredComposition(typeof(global::AppMapper), typeof(global::Framework.PlatformConversions))]");

        Diagnostic notice = run.Single("SM0043");

        Assert.Equal(DiagnosticSeverity.Info, notice.Severity);
        Assert.Contains("PlatformConversions", notice.GetMessage());
        Assert.Contains("ShiftMapperPackage", notice.GetMessage());
        Assert.StartsWith("services.AddShiftMapper(", run.CodeUnder(notice));
    }

    /// <summary>The short form is a call like any other, and gets the pack.</summary>
    [Fact]
    public void The_generic_short_form_gets_a_shared_pack_too()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            SharingPackage,
            Application +
            """

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper<AppMapper>();
            }
            """);

        run.Compiles()
           .Emits("Id = Customizations.Conversion<long, string>(typeof(global::Framework.PlatformConversions))(source.Id)");

        run.Single("SM0043");
    }

    /// <summary>
    /// FURTHEST LEVEL. A rule the application wrote for the same pair — in the mapper, or in a
    /// pack of its own given to the call — wins over the shared one.
    /// </summary>
    [Fact]
    public void A_rule_the_project_wrote_itself_beats_a_shared_pack()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            SharingPackage,
            Application +
            """

            public class Own : ShiftMapperConversions
            {
                public Own() => CreateConversion<long, string>(id => "#" + id, id => "#" + id);
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<AppMapper>();
                        o.AddConversions<Own>();
                    });
            }
            """);

        run.Compiles()
           .Emits("Id = Customizations.Conversion<long, string>(typeof(global::Own))(source.Id)")
           .DoesNotEmit("typeof(global::Framework.PlatformConversions))(source.Id)");

        // Not SM0031: the two packs are at different distances, and the nearer one settles it.
        run.None("SM0031");
    }

    /// <summary>
    /// A shared pack is a REGISTRATION's pack. A mapper nothing registers is generated from its own
    /// constructor alone, exactly as it is with a call-wide pack.
    /// </summary>
    [Fact]
    public void A_mapper_nothing_registers_does_not_get_a_shared_pack()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(SharingPackage, Application);

        run.Compiles()
           .DoesNotEmit("typeof(global::Framework.PlatformConversions)")
           .DoesNotEmit("ShiftMapperDeclaredComposition");

        run.None("SM0043");
    }

    /// <summary>Naming the pack yourself as well is allowed, and changes nothing but the level.</summary>
    [Fact]
    public void Adding_a_shared_pack_yourself_as_well_is_harmless()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            SharingPackage,
            Application +
            """

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o =>
                    {
                        o.AddMapper<AppMapper>();
                        o.AddConversions<Framework.PlatformConversions>();
                    });
            }
            """);

        run.Compiles()
           .Emits("Id = Customizations.Conversion<long, string>(typeof(global::Framework.PlatformConversions))(source.Id)");

        run.None("SM0031");
    }

    /// <summary>
    /// A package mapper the application registers DIRECTLY — the adapter — gets the shared pack the
    /// same way, so the package's own long renders by the package's own rule in the application.
    /// </summary>
    [Fact]
    public void An_adapted_package_mapper_gets_the_shared_pack()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            SharingPackage,
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;
            using Framework;

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<PlatformMapper>());
            }
            """);

        run.Compiles()
           .Emits("Framework_PlatformMapper_Adapter")
           .Emits("Size = Customizations.Conversion<long, string>(typeof(global::Framework.PlatformConversions))(source.Size)");
    }

    /// <summary>
    /// A package with RULES AND NO MAPPER — a pack and the one call that shares it. Nothing to
    /// register on the package's side; the share is written down and the referencing project gets
    /// it exactly the same.
    /// </summary>
    [Fact]
    public void A_package_with_only_a_pack_can_share_it()
    {
        const string packOnly =
            """
            using ShiftMapper;
            using Microsoft.Extensions.DependencyInjection;

            namespace Framework;

            public class PlatformConversions : ShiftMapperConversions
            {
                public PlatformConversions() => CreateConversion<long, string>(id => "H" + id, id => "H" + id);
            }

            public static class Startup
            {
                public static IServiceCollection AddPlatform(this IServiceCollection services) =>
                    services.AddShiftMapper(o => o.ShareConversions<PlatformConversions>());
            }
            """;

        GeneratorHarness.Run(packOnly)
            .Compiles()
            .Emits("[assembly: global::ShiftMapper.ShiftMapperDeclaredSharedPack(typeof(global::Framework.PlatformConversions))]");

        GeneratorRun run = GeneratorHarness.RunWithPackage(
            packOnly,
            Application +
            """

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<AppMapper>());
            }
            """);

        run.Compiles()
           .Emits("Id = Customizations.Conversion<long, string>(typeof(global::Framework.PlatformConversions))(source.Id)");

        run.Single("SM0043");
    }

    /// <summary>Two packages each sharing a rule for one pair, at the same distance: SM0031, as for any two packs.</summary>
    [Fact]
    public void Two_packages_sharing_the_same_pair_is_reported()
    {
        string second = SharingPackage
            .Replace("namespace Framework;", "namespace Other;")
            .Replace("PlatformConversions", "OtherConversions")
            .Replace("\"H\" + id", "\"O\" + id");

        GeneratorRun run = GeneratorHarness.RunWithPackages(
            new[] { SharingPackage, second },
            Application +
            """

            public static class Startup
            {
                public static void Configure(IServiceCollection services) =>
                    services.AddShiftMapper(o => o.AddMapper<AppMapper>());
            }
            """);

        string message = run.Single("SM0031").GetMessage();

        Assert.Contains("PlatformConversions", message);
        Assert.Contains("OtherConversions", message);
    }
}
