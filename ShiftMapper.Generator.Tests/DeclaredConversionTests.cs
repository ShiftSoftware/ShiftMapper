using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// THE EXTENSION CONTRACT — a package writes ORDINARY maps and conversions, and an application gets
/// them with the ordinary <c>IncludeMapper</c> and <c>AddConversions</c>.
///
/// <para>Every test here compiles TWO assemblies, because nothing less would prove anything. The
/// whole difficulty is that a profile compiled into a package is METADATA by the time a consumer
/// sees it, with no method bodies; put the profile in the same snippet and the ordinary source path
/// handles it and the interesting code never runs.</para>
///
/// <para>What the package writes is the same API an application writes. There is no second
/// vocabulary, no hand-written attribute, and nothing in the application naming the package's
/// internals.</para>
/// </summary>
public class DeclaredConversionTests
{
    /// <summary>A package: ordinary profile, ordinary API. Its build emits the metadata.</summary>
    private const string Package =
        """
        using ShiftMapper;
        using System;
        using System.Collections.Generic;

        namespace Framework;

        public class FileDto { public string Name { get; set; } = ""; }

        public class FileSummary { public string Name { get; set; } = ""; }

        public class FrameworkPack : ShiftMapperConversions
        {
            public FrameworkPack() =>
                CreateConversion<long, string>(id => "H" + id, id => "H" + id);
        }

        public partial class FrameworkMapper : ShiftMapperBase
        {
            public FrameworkMapper() =>
                CreateMap<FileDto, FileSummary>()
                    .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
        }
        """;

    private const string Application =
        """
        using ShiftMapper;
        using System.Collections.Generic;
        using Framework;

        public class Entity { public long Id { get; set; } }
        public class EntityDto { public string Id { get; set; } = ""; }

        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper()
            {
                AddConversions<FrameworkPack>();
                CreateMap<Entity, EntityDto>();
            }
        }
        """;

    private static GeneratorRun Run(string application) =>
        GeneratorHarness.RunWithPackage(Package, application);

    // -----------------------------------------------------------------
    // THE CORE.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE TEST THAT MATTERS: a MAP declared in a package is generated into the application. This
    /// is the thing that could not be done at all before — a profile from a package compiled and
    /// contributed nothing.
    /// </summary>
    [Fact]
    public void A_map_declared_in_a_package_is_generated_into_the_application()
    {
        GeneratorRun run = Run(Application);

        run.Compiles()
           .Emits("global::Framework.FileSummary MapToFileSummary(global::Framework.FileDto source)");
    }

    /// <summary>
    /// AND ITS ForMember COMES WITH IT, as the identical runtime lookup an in-project MapFrom
    /// produces. The expression is not in the metadata and does not need to be: the profile's own
    /// constructor puts it in the store, which adding the pack already runs.
    /// </summary>
    [Fact]
    public void A_ForMember_declared_in_a_package_reaches_the_application()
    {
        Run(Application).Compiles()
           .Emits("Customizations.Value<global::Framework.FileDto, global::Framework.FileSummary, string>(\"Name\")");
    }

    /// <summary>A CONVERSION declared in a package applies to the application's own maps.</summary>
    [Fact]
    public void A_conversion_declared_in_a_package_applies_to_the_applications_maps()
    {
        GeneratorRun run = Run(Application);

        run.Compiles().Emits("Customizations.Conversion<long, string>(typeof(global::Framework.FrameworkPack))");
        run.None("SM0002");
    }

    /// <summary>
    /// It BEATS the built-in table, exactly as an in-project conversion does. <c>long</c> to
    /// <c>string</c> already converts, so a framework's hash-id rule would otherwise be ignored in
    /// silence.
    /// </summary>
    [Fact]
    public void A_packages_conversion_beats_the_built_in_one()
    {
        Run(Application).Compiles().DoesNotEmit("ToInvariantString(source.Id)");
    }

    // -----------------------------------------------------------------
    // OPT-IN, AND PRECEDENCE.
    // -----------------------------------------------------------------

    /// <summary>
    /// REFERENCING IS ENOUGH for the package's MAPS: its mapper is in the application's generated
    /// mapper without a line naming it. Its PACK is not — a conversion the package declared for its
    /// own maps does not reach the application's, unless the application adds it or the package
    /// shares it. That is the difference between a map you can call and a rule imposed on you.
    /// </summary>
    [Fact]
    public void A_referenced_packages_maps_are_generated_but_its_pack_stays_with_its_own_maps()
    {
        GeneratorRun run = Run(
            """
            using ShiftMapper;
            using Framework;

            public class Entity { public long Id { get; set; } }
            public class EntityDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                // No AddConversions: the package's pack says nothing about this map.
                public TestMapper() => CreateMap<Entity, EntityDto>();
            }
            """);

        run.Compiles()
           .Emits("MapToFileSummary(global::Framework.FileDto source)")
           .DoesNotEmit("Customizations.Conversion<long, string>(")
           // The built-in conversion is what fills it, because nothing overrode it.
           .Emits("ToInvariantString(source.Id)");

        run.None("SM0028");
    }

    /// <summary>A conversion the APPLICATION declares wins over the package's.</summary>
    [Fact]
    public void An_applications_own_conversion_wins_over_the_packages()
    {
        GeneratorRun run = Run(
            """
            using ShiftMapper;
            using Framework;

            public class Entity { public long Id { get; set; } }
            public class EntityDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    AddConversions<FrameworkPack>();
                    CreateConversion<long, string>(id => "LOCAL" + id, id => "LOCAL" + id);
                    CreateMap<Entity, EntityDto>();
                }
            }
            """);

        // The generated call names the APPLICATION's own scope, so the store it reads at run time
        // is the one the application's constructor filled.
        run.Compiles()
           .Emits("Customizations.Conversion<long, string>(typeof(global::TestMapper))")
           .DoesNotEmit("typeof(global::Framework.FrameworkPack)");
        run.None("SM0002");
    }

    /// <summary>
    /// A map the APPLICATION declares for the same pair wins over the package's, so an application
    /// can always override what a package said.
    /// </summary>
    [Fact]
    public void An_applications_own_map_wins_over_the_packages()
    {
        GeneratorRun run = Run(
            """
            using ShiftMapper;
            using Framework;

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<FileDto, FileSummary>().ForMember(d => d.Name, opt => opt.Ignore());
                }
            }
            """);

        // Ignored here, so the package's MapFrom lookup is not emitted for it.
        run.Compiles()
           .DoesNotEmit("Customizations.Value<global::Framework.FileDto, global::Framework.FileSummary, string>(\"Name\")");
    }

    // -----------------------------------------------------------------
    // WHEN THE PACKAGE SAID NOTHING.
    // -----------------------------------------------------------------

    /// <summary>
    /// SM0028 — a package built WITHOUT the ShiftMapper generator wrote no metadata, so its profile
    /// contributes nothing. That is a problem with an owner and a fix, and the message says so
    /// rather than mapping nothing in silence.
    /// </summary>
    [Fact]
    public void A_package_built_without_the_generator_is_reported()
    {
        // Compiled with NO generator, so no declarations were written down.
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            packageSource: Package,
            applicationSource: Application,
            runGeneratorOnPackage: false);

        // Once, for the pack the application adds — and an ERROR, because a package that says
        // nothing would otherwise map nothing in silence. Its MAPPER is not reported, because a
        // package built without the generator does not announce one: there is nothing to ask for.
        Microsoft.CodeAnalysis.Diagnostic[] reported = run.All("SM0028");

        Assert.Single(reported);
        Assert.All(reported, d => Assert.Contains("declaration metadata", d.GetMessage()));
        Assert.All(reported, d => Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d.Severity));
        run.DoesNotEmit("MapToFileSummary");
    }

    /// <summary>A project referencing no such package is completely unaffected.</summary>
    [Fact]
    public void A_project_with_no_packages_is_unchanged()
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

        run.Compiles().Emits("ToInvariantString(source.Id)");
        run.None("SM0028");
    }
}
