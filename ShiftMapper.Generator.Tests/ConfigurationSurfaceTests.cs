using System.Reflection;
using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// CONFIGURATION SURFACES — customizing an implicit map where the framework's user configures
/// everything else, in ShiftMapper's own vocabulary.
///
/// <para>The framework hands its user an object with one <c>MapExpression</c> per implicit map;
/// the user writes <c>o.Mapping(m =&gt; m.List.ForMember(…))</c> in the repository. The generator
/// reads the lambda for its SHAPE at build time, exactly as it reads a mapper class's constructor;
/// the lambda runs at run time wherever the framework runs it, and the framework hands the object
/// to the mapper with <c>IMapper.Configure</c>. These tests pin both halves and the three rules
/// around them: inline and unconditional (SM0035), one configurator per pair (SM0050), a mapper
/// class wins (SM0051).</para>
/// </summary>
public class ConfigurationSurfaceTests
{
    /// <summary>The framework: a surface with one handle per map, options that run the lambda, and the marked base.</summary>
    private const string Framework =
        """
        using System;
        using System.Collections.Generic;
        using ShiftMapper;
        using Framework;

        namespace Framework
        {
            public sealed class RepositoryMapping<TEntity, TList, TView> : ShiftMapperConfigurationSurface
            {
                public RepositoryMapping()
                {
                    View = Map<TEntity, TView>();
                    Entity = Map<TView, TEntity>();
                    List = Map<TEntity, TList>();
                }

                public MapExpression<TEntity, TView> View { get; }
                public MapExpression<TView, TEntity> Entity { get; }
                public MapExpression<TEntity, TList> List { get; }
            }

            public sealed class RepositoryOptions<TEntity, TList, TView>
            {
                public RepositoryMapping<TEntity, TList, TView>? Surface { get; private set; }

                public void Mapping(Action<RepositoryMapping<TEntity, TList, TView>> configure)
                {
                    var surface = new RepositoryMapping<TEntity, TList, TView>();
                    configure(surface);
                    Surface = surface;
                }
            }

            [ShiftMapperDeclaresMap("TEntity", "TView", Reverse = true, Nested = 10, Flattening = DeclaredOption.False)]
            [ShiftMapperDeclaresMap("TEntity", "TList", Nested = 10, Flattening = DeclaredOption.False)]
            public abstract class Repository<TEntity, TList, TView>
            {
                protected Repository(Action<RepositoryOptions<TEntity, TList, TView>>? configure = null) =>
                    configure?.Invoke(Options);

                public RepositoryOptions<TEntity, TList, TView> Options { get; } = new();
            }
        }
        """;

    private const string Types =
        """

        namespace App
        {
            public class Invoice
            {
                public long Id { get; set; }
                public string Number { get; set; } = "";
                public string Secret { get; set; } = "";
                public List<InvoiceLine> Lines { get; set; } = new();
            }

            public class InvoiceLine { public string Description { get; set; } = ""; }

            public class InvoiceDto
            {
                public long Id { get; set; }
                public string Number { get; set; } = "";
                public string Secret { get; set; } = "";
                public List<InvoiceLineDto> Lines { get; set; } = new();
            }

            public class InvoiceLineDto { public string Description { get; set; } = ""; }

            public class InvoiceListDto
            {
                public long Id { get; set; }
                public string Number { get; set; } = "";
                public int Total { get; set; }
            }
        }
        """;

    private const string ConfiguredRepository =
        """

        namespace App
        {
            public class InvoiceRepository : Repository<Invoice, InvoiceListDto, InvoiceDto>
            {
                public InvoiceRepository() : base(o => o.Mapping(m =>
                {
                    m.List.ForMember(d => d.Total, opt => opt.MapFrom(e => e.Lines.Count));
                    m.View.ForMember(d => d.Secret, opt => opt.Ignore());
                    m.Entity.ForMember(e => e.Secret, opt => opt.Ignore())
                            .AfterMap((dto, entity) => entity.Number = entity.Number.Trim());
                }))
                {
                }
            }
        }
        """;

    private static GeneratorRun Run(string application) => GeneratorHarness.Run(Framework + Types + application);

    [Fact]
    public void The_lambdas_shape_is_baked_into_the_implicit_maps()
    {
        GeneratorRun run = Run(ConfiguredRepository);

        run.Compiles()
           // MapFrom: the lookup names the configuring type, so a map used before it ran can have it built.
           .Emits("Customizations.Value<global::App.Invoice, global::App.InvoiceListDto, int>(\"Total\", typeof(global::App.InvoiceRepository)))(source)")
           // Ignore: the member is omitted from the view map...
           .DoesNotEmit("Secret = source.Secret,")
           // ...and the write map's hook runs.
           .Emits("Customizations.RunAfter(source, destination);");

        // An ignored member is not an unmapped one.
        run.None("SM0001");
        run.None("SM0052");
    }

    [Fact]
    public void The_configuration_applies_once_the_framework_hands_the_surface_to_the_mapper()
    {
        GeneratorRun run = Run(ConfiguredRepository +
            """
            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new ShiftMapper.Mapper();
                    var repository = new App.InvoiceRepository();          // runs the lambda into the surface
                    ((ShiftMapper.IMapper)mapper).Configure(repository.Options.Surface!);

                    var invoice = new App.Invoice { Id = 1, Number = " INV ", Secret = "s", Lines = { new(), new(), new() } };

                    App.InvoiceListDto row = mapper.MapToInvoiceListDto(invoice);
                    App.InvoiceDto dto = mapper.MapToInvoiceDto(invoice);
                    App.Invoice back = mapper.MapToInvoice(new App.InvoiceDto { Number = " x ", Secret = "leak" });

                    return row.Total + "|" + dto.Secret + "|" + back.Number + "|" + back.Secret;
                }
            }
            """);

        Assembly assembly = run.Load();

        object result = assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null)!;

        // Total computed; Secret neither read into the DTO nor written onto the entity; AfterMap trimmed.
        Assert.Equal("3||x|", result);
    }

    [Fact]
    public void A_customized_map_used_before_its_configurator_ran_says_what_to_do()
    {
        GeneratorRun run = Run(ConfiguredRepository +
            """
            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new ShiftMapper.Mapper();   // no container: nothing can construct the repository

                    try
                    {
                        mapper.MapToInvoiceListDto(new App.Invoice());
                        return "mapped";
                    }
                    catch (System.InvalidOperationException error)
                    {
                        return error.Message;
                    }
                }
            }
            """);

        Assembly assembly = run.Load();

        string message = (string)assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null)!;

        Assert.Contains("InvoiceRepository", message);
        Assert.Contains("Total", message);
        Assert.Contains("mapper class", message);
    }

    [Fact]
    public void A_registered_mapper_constructs_the_configurator_from_the_container_on_first_use()
    {
        GeneratorRun run = Run(ConfiguredRepository +
            """
            public static class Probe
            {
                public static string Run()
                {
                    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
                    Microsoft.Extensions.DependencyInjection.ShiftMapperServiceCollectionExtensions.AddShiftMapper(services);

                    // The framework registers its repository; constructing it applies its surface.
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(services, provider =>
                    {
                        var repository = new App.InvoiceRepository();
                        var mapper = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ShiftMapper.IMapper>(provider);
                        mapper.Configure(repository.Options.Surface!);
                        return repository;
                    });

                    using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(
                        Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));

                    // The repository has NOT been resolved in this scope. The map pulls it in.
                    var mapper2 = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ShiftMapper.Mapper>(scope.ServiceProvider);

                    return mapper2.MapToInvoiceListDto(new App.Invoice { Lines = { new(), new() } }).Total.ToString();
                }
            }
            """);

        Assembly assembly = run.Load();

        Assert.Equal("2", assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void The_configuring_type_is_written_to_the_metadata()
    {
        GeneratorRun run = Run(ConfiguredRepository);

        Assert.Contains("ConfiguredBy = typeof(global::App.InvoiceRepository)", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredMember(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.ImplicitMapper), typeof(global::App.Invoice), typeof(global::App.InvoiceListDto), \"Total\"", run.Metadata);
    }

    [Fact]
    public void A_configuration_inside_an_if_is_an_error()
    {
        GeneratorRun run = Run(
            """
            namespace App
            {
                public class InvoiceRepository : Repository<Invoice, InvoiceListDto, InvoiceDto>
                {
                    public static bool Flag = true;

                    public InvoiceRepository() : base(o => o.Mapping(m =>
                    {
                        if (Flag)
                            m.List.ForMember(d => d.Total, opt => opt.MapFrom(e => e.Lines.Count));
                    }))
                    {
                    }
                }
            }
            """);

        Diagnostic error = run.Single("SM0035");

        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("inside an 'if'", error.GetMessage());
        Assert.Equal("m.List", run.CodeUnder(error));
    }

    [Fact]
    public void Two_types_configuring_one_pair_is_an_error()
    {
        GeneratorRun run = Run(ConfiguredRepository +
            """
            namespace App
            {
                public class ReportingRepository : Repository<Invoice, InvoiceListDto, InvoiceDto>
                {
                    public ReportingRepository() : base(o => o.Mapping(m =>
                        m.List.ForMember(d => d.Total, opt => opt.MapFrom(e => 0))))
                    {
                    }
                }
            }
            """);

        Diagnostic error = run.Single("SM0050");

        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("InvoiceRepository", error.GetMessage());
        Assert.Contains("ReportingRepository", error.GetMessage());
    }

    [Fact]
    public void A_mapper_class_declaring_the_pair_wins_and_the_surface_is_reported_dead()
    {
        GeneratorRun run = Run(ConfiguredRepository +
            """
            namespace App
            {
                public class InvoiceMapper : ShiftMapper.ShiftMapperBase
                {
                    public InvoiceMapper() =>
                        CreateMap<Invoice, InvoiceListDto>()
                            .ForMember(d => d.Total, opt => opt.MapFrom(e => 100));
                }
            }
            """);

        run.Compiles()
           .Emits("Customizations.Value<global::App.Invoice, global::App.InvoiceListDto, int>(\"Total\"))(source)")
           .DoesNotEmit("typeof(global::App.InvoiceRepository)))(source)");

        Diagnostic dead = run.Single("SM0051");

        Assert.Equal(DiagnosticSeverity.Warning, dead.Severity);
        Assert.Contains("InvoiceMapper", dead.GetMessage());
        Assert.Contains("InvoiceRepository", dead.GetMessage());
    }

    [Fact]
    public void A_surface_configuring_a_pair_nothing_declares_is_reported()
    {
        GeneratorRun run = Run(
            """
            namespace App
            {
                // Not a repository: closes no marker, so the pair it configures has no map.
                public class Nowhere
                {
                    public Nowhere()
                    {
                        var options = new RepositoryOptions<Invoice, InvoiceListDto, InvoiceDto>();
                        options.Mapping(m => m.List.ForMember(d => d.Total, opt => opt.MapFrom(e => 0)));
                    }
                }
            }
            """);

        Diagnostic orphan = run.Single("SM0052");

        Assert.Equal(DiagnosticSeverity.Warning, orphan.Severity);
        Assert.Contains("Nowhere", orphan.GetMessage());
    }

    [Fact]
    public void Nested_caps_the_depth_for_the_configuring_type()
    {
        GeneratorRun run = Run(
            """
            namespace App
            {
                public class InvoiceRepository : Repository<Invoice, InvoiceListDto, InvoiceDto>
                {
                    public InvoiceRepository() : base(o => o.Mapping(m => m.Nested(0)))
                    {
                    }
                }
            }
            """);

        run.Compiles()
           .Emits("MapToInvoiceDto(global::App.Invoice source)")
           .DoesNotEmit("MapToInvoiceLineDto(");

        run.None("SM0011");
    }
}
