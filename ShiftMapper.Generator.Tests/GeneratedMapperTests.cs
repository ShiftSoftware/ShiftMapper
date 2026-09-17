using System.Reflection;
using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// THE GENERATED MAPPER and the class that reaches it — <c>ShiftMapper.Mapper</c>.
///
/// One generated class per assembly, holding every map the assembly can see; extension methods on
/// <c>Mapper</c>, in both spellings, forwarding to it; and the run-time door underneath. Tested from
/// the outside in: what the file looks like, what the extension methods bind to, and — by loading
/// the compiled snippet — what actually happens when they run.
/// </summary>
public class GeneratedMapperTests
{
    private const string Types =
        """
        using ShiftMapper;
        using Microsoft.Extensions.DependencyInjection;
        using System;
        using System.Collections.Generic;

        public class Money { public decimal Amount { get; set; } }

        public class Brand { public string Name { get; set; } = ""; public int FoundedYear { get; set; } }
        public class BrandDto { public string Name { get; set; } = ""; public string FoundedYear { get; set; } = ""; }

        public class Stock { public string Name { get; set; } = ""; public Money Price { get; set; } = new(); }
        public class StockDto { public string Name { get; set; } = ""; public string Price { get; set; } = ""; }
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Types + "\n" + body);

    // -----------------------------------------------------------------
    // THE SHAPE.
    // -----------------------------------------------------------------

    /// <summary>The generated class: internal, sealed, in a namespace carrying the assembly's name, announced to the runtime.</summary>
    [Fact]
    public void One_generated_class_per_assembly_announced_to_the_runtime()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles()
           .Emits("global using ShiftMapper.Generated.ShiftMapperSnippet;")
           .Emits("[assembly: global::ShiftMapper.ShiftMapperGenerated(typeof(global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper))]")
           .Emits("namespace ShiftMapper.Generated.ShiftMapperSnippet")
           .Emits("internal sealed class GeneratedMapper : global::ShiftMapper.ShiftMapperBase, global::ShiftMapper.IMapper")
           .Emits("internal static class MapperExtensions");

        Assert.Single(run.GeneratedFiles);
    }

    /// <summary>
    /// The extension methods, both spellings, every shape — and the direct ones, so a call that
    /// knows both types pays for no typeof chain.
    /// </summary>
    [Fact]
    public void The_extension_methods_come_in_both_spellings()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        run.Compiles()
           // mapper first
           .Emits("public static global::BrandDto MapToBrandDto(this global::ShiftMapper.Mapper mapper, global::Brand source) =>")
           .Emits("MapToBrandDtoList(this global::ShiftMapper.Mapper mapper, global::System.Collections.Generic.IEnumerable<global::Brand>? source) =>")
           .Emits("MapToBrandDtoOrNull(this global::ShiftMapper.Mapper mapper, global::Brand? source) =>")
           .Emits("public static TDestination Map<TDestination>(this global::ShiftMapper.Mapper mapper, global::Brand source) =>")
           .Emits("public static TDestination? MapOrNull<TDestination>(this global::ShiftMapper.Mapper mapper, global::Brand? source)")
           .Emits("public static global::BrandDto Map(this global::ShiftMapper.Mapper mapper, global::Brand source, global::BrandDto destination) =>")
           .Emits("ProjectTo<TDestination>(this global::ShiftMapper.Mapper mapper, global::System.Linq.IQueryable<global::Brand> source) =>")
           // source first
           .Emits("public static TDestination Map<TDestination>(this global::Brand source, global::ShiftMapper.Mapper mapper) =>")
           .Emits("public static global::BrandDto Map(this global::Brand source, global::BrandDto destination, global::ShiftMapper.Mapper mapper) =>")
           .Emits("ProjectTo<TDestination>(this global::System.Linq.IQueryable<global::Brand> source, global::ShiftMapper.Mapper mapper) =>")
           // all through the one door to this assembly's generated class
           .Emits("return mapper.Root<global::ShiftMapper.Generated.ShiftMapperSnippet.GeneratedMapper>();");
    }

    /// <summary>Nothing is generated when nothing declares a map: no class, no attribute, no using.</summary>
    [Fact]
    public void A_project_declaring_nothing_generates_nothing()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Plain { }
            """);

        run.Compiles();
        Assert.Empty(run.GeneratedFiles);
    }

    // -----------------------------------------------------------------
    // RUNNING IT.
    // -----------------------------------------------------------------

    /// <summary>
    /// A BARE <c>new Mapper()</c> serves every typed extension method: the generated class is
    /// built on first use, and the mapper class it composes with it — so a MapFrom written in a
    /// mapper class runs.
    /// </summary>
    [Fact]
    public void A_bare_Mapper_runs_the_typed_extension_methods()
    {
        GeneratorRun run = Run(
            """
            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>()
                    .ForMember(d => d.Price, opt => opt.MapFrom(s => "price:" + s.Price.Amount));
            }

            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new Mapper();
                    var stock = new Stock { Name = "bolts", Price = new Money { Amount = 12.5m } };

                    StockDto direct = mapper.MapToStockDto(stock);           // mapper first, direct
                    StockDto generic = mapper.Map<StockDto>(stock);          // mapper first, by type argument
                    StockDto sourceFirst = stock.Map<StockDto>(mapper);      // source first

                    return direct.Price + "|" + generic.Name + "|" + sourceFirst.Price;
                }
            }
            """);

        Assembly assembly = run.Load();

        object result = assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null)!;

        Assert.Equal("price:12.5|bolts|price:12.5", result);
    }

    /// <summary>
    /// THE DI HALF: <c>AddShiftMapper()</c> from the snippet's own assembly registers its generated
    /// mapper; a mapper class with a constructor dependency is built from the container on first
    /// use; and the run-time door resolves to the same object as the class.
    /// </summary>
    [Fact]
    public void A_registered_Mapper_builds_a_mapper_class_with_its_dependency()
    {
        GeneratorRun run = Run(
            """
            public interface IPrefix { string Value { get; } }
            public sealed class Prefix : IPrefix { public string Value => "pfx:"; }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper(IPrefix prefix) => CreateMap<Stock, StockDto>()
                    .ForMember(d => d.Price, opt => opt.MapFrom(s => prefix.Value + s.Price.Amount));
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddSingleton<IPrefix, Prefix>();
                    services.AddShiftMapper();
                }
            }
            """);

        Assembly assembly = run.Load();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        assembly.GetType("Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        using Microsoft.Extensions.DependencyInjection.ServiceProvider provider =
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        using Microsoft.Extensions.DependencyInjection.IServiceScope scope =
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(provider);

        var mapper = (Mapper)scope.ServiceProvider.GetService(typeof(Mapper))!;
        var door = (IMapper)scope.ServiceProvider.GetService(typeof(IMapper))!;

        Assert.Same(mapper, door);
        Assert.Single(mapper.Registered);
        Assert.Equal("GeneratedMapper", mapper.Registered[0].GetType().Name);

        Type stockType = assembly.GetType("Stock")!;
        Type moneyType = assembly.GetType("Money")!;
        Type dtoType = assembly.GetType("StockDto")!;

        object money = Activator.CreateInstance(moneyType)!;
        moneyType.GetProperty("Amount")!.SetValue(money, 3m);

        object stock = Activator.CreateInstance(stockType)!;
        stockType.GetProperty("Price")!.SetValue(stock, money);

        Assert.True(door.CanMap(stockType, dtoType));

        object dto = typeof(IMapper).GetMethod(nameof(IMapper.Map), new[] { typeof(object) })!
            .MakeGenericMethod(dtoType)
            .Invoke(door, new[] { stock })!;

        Assert.Equal("pfx:3", dtoType.GetProperty("Price")!.GetValue(dto));
    }

    // -----------------------------------------------------------------
    // WHERE THE DIAGNOSTICS LAND.
    // -----------------------------------------------------------------

    /// <summary>A map's diagnostics land in the file that declared it, at its CreateMap, once.</summary>
    [Fact]
    public void A_maps_diagnostics_land_at_its_own_declaration()
    {
        GeneratorRun run = Run(
            """
            public class Widget { public string Name { get; set; } = ""; }
            public class WidgetDto { public string Name { get; set; } = ""; public string Missing { get; set; } = ""; }

            public class WidgetMapper : ShiftMapperBase
            {
                public WidgetMapper() => CreateMap<Widget, WidgetDto>();
            }

            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper() => CreateMap<Brand, BrandDto>();
            }
            """);

        Diagnostic unmapped = run.Single("SM0001");

        Assert.Contains("WidgetDto.Missing", unmapped.GetMessage());
        Assert.Equal("CreateMap<Widget, WidgetDto>", run.CodeUnder(unmapped));
    }

    /// <summary>A declaration written where it cannot be baked is that class's problem (SM0035), wherever the class is.</summary>
    [Fact]
    public void An_unbakeable_declaration_is_reported_in_its_class()
    {
        GeneratorRun run = Run(
            """
            public class BrandMapper : ShiftMapperBase
            {
                public BrandMapper(bool flag)
                {
                    if (flag)
                        CreateMap<Brand, BrandDto>();
                }
            }

            public class StockMapper : ShiftMapperBase
            {
                public StockMapper() => CreateMap<Stock, StockDto>();
            }
            """);

        Diagnostic problem = run.Single("SM0035");
        Assert.Equal("CreateMap<Brand, BrandDto>()", run.CodeUnder(problem));
    }
}
