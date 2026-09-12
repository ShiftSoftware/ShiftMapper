using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// CAN ANOTHER PACKAGE'S GENERATOR CONTRIBUTE MAPS TO THIS ONE?
///
/// The question decides how a framework extends an application's mapper, so the answer is pinned
/// here rather than assumed. It is a fact about ROSLYN, not about ShiftMapper, and it is not the
/// one most people expect:
///
/// <list type="bullet">
/// <item>Sources added in POST-INITIALIZATION are visible to every other generator — they go into
/// the compilation before any generation pass runs. But that context has no compilation to look
/// at, so what it adds cannot depend on the application's types. It is a constant.</item>
/// <item>Sources added in the ordinary generation pass are NOT visible to other generators. Every
/// generator sees the compilation as it was BEFORE any generator ran, whatever the order.</item>
/// </list>
///
/// So a framework generator that scans an application's entities and writes CreateMap calls for
/// them cannot work: the scanning requires the compilation, and needing the compilation puts it in
/// the pass whose output nobody else can see. That is what the metadata contract is for.
/// </summary>
public class CrossGeneratorTests
{
    /// <summary>What a framework would want to contribute — a second part carrying maps.</summary>
    private const string FrameworkPart =
        """
        public partial class TestMapper
        {
            private void FrameworkMaps()
            {
                CreateMap<Brand, BrandDto>();
            }
        }
        """;

    private sealed class PostInitGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterPostInitializationOutput(
                ctx => ctx.AddSource("FrameworkMaps.g.cs", FrameworkPart));
    }

    private sealed class SourceOutputGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterSourceOutput(
                context.CompilationProvider,
                (ctx, _) => ctx.AddSource("FrameworkMaps.g.cs", FrameworkPart));
    }

    private const string Types =
        """
        using ShiftMapper;

        public class Brand { public string Name { get; set; } = ""; }
        public class BrandDto { public string Name { get; set; } = ""; }
        """;

    private const string EmptyMapper =
        """

        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper() { }
        }
        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

    /// <summary>
    /// From TRUSTED_PLATFORM_ASSEMBLIES rather than the loaded set: ShiftMapper.dll loads lazily,
    /// and without it <c>ShiftMapperBase</c> does not resolve and the generator does nothing —
    /// which would make every assertion below pass for the wrong reason.
    /// </summary>
    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string platform = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;

        foreach (string path in platform.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && seen.Add(Path.GetFileNameWithoutExtension(path)))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return references.ToImmutable();
    }

    private static Compilation Compile(string source) =>
        CSharpCompilation.Create(
            "CrossGenerator",
            new[] { CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), ParseOptions, path: "Mapper.cs") },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

    private static bool MappedBrand(Compilation compilation, params IIncrementalGenerator[] generators)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators.Select(g => g.AsSourceGenerator()).ToArray(),
            parseOptions: ParseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult().GeneratedTrees
            .Any(tree => tree.ToString().Contains("MapToBrandDto", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE CONTROL. Without it the two results below would be indistinguishable from a probe that
    /// simply never ran the generator properly — which is how this test first came out.
    /// </summary>
    [Fact]
    public void A_hand_written_second_part_is_read()
    {
        const string mapperWithMap =
            """

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() { CreateMap<Brand, BrandDto>(); }
            }
            """;

        Assert.True(MappedBrand(Compile(Types + mapperWithMap), new ShiftMapperGenerator()),
            "the ordinary case must work, or nothing below means anything");

        Assert.True(MappedBrand(Compile(Types + EmptyMapper + "\n" + FrameworkPart), new ShiftMapperGenerator()),
            "a second hand-written part is ordinary source and must be read");

        // And the empty mapper on its own maps nothing — so a True below really did come from the
        // contributed part rather than from anywhere else.
        Assert.False(MappedBrand(Compile(Types + EmptyMapper), new ShiftMapperGenerator()),
            "the baseline must be empty");
    }

    /// <summary>
    /// POST-INITIALIZATION OUTPUT IS SHARED. Those sources enter the compilation before any
    /// generation pass, so order does not matter and ShiftMapper reads them like hand-written code.
    ///
    /// <para>The catch is in what such a generator can say: its context has no compilation, so the
    /// text is fixed at build time and cannot name a type the application declared. It is a route
    /// for a framework's OWN maps, not for the application's entities.</para>
    /// </summary>
    [Fact]
    public void Post_initialization_output_from_another_generator_is_visible()
    {
        Compilation compilation = Compile(Types + EmptyMapper);

        Assert.True(MappedBrand(compilation, new PostInitGenerator(), new ShiftMapperGenerator()));
        Assert.True(MappedBrand(compilation, new ShiftMapperGenerator(), new PostInitGenerator()));
    }

    /// <summary>
    /// AND ORDINARY GENERATED SOURCE IS NOT. Every generator sees the compilation as it was before
    /// any of them ran; there is no ordering, no chaining and no way to ask for one.
    ///
    /// <para>This is the result that shapes the extension contract. A framework generator that walks
    /// the application's entities to write CreateMap calls needs the compilation, and needing the
    /// compilation puts it in exactly the pass whose output ShiftMapper cannot see. So the
    /// extension route cannot be "another generator writes CreateMap calls" — it has to be
    /// something that survives into METADATA and is read from the referenced assembly.</para>
    /// </summary>
    [Fact]
    public void Ordinary_generated_source_from_another_generator_is_not_visible()
    {
        Compilation compilation = Compile(Types + EmptyMapper);

        Assert.False(MappedBrand(compilation, new SourceOutputGenerator(), new ShiftMapperGenerator()));
        Assert.False(MappedBrand(compilation, new ShiftMapperGenerator(), new SourceOutputGenerator()));
    }
}
