using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// Collections of simple values — the shapes, the per-element conversions, the null-collection
/// policy, and the second spelling every one of them needs for a projection.
///
/// The second spelling is not a detail. <c>ValueConverter</c> is ShiftMapper's own static class
/// and no database can run it, so the query form has to be the ordinary BCL shape a developer
/// would have hand-written. A test that only checked the in-memory text would pass on a map that
/// throws the moment it is projected.
/// </summary>
public class CollectionConversionTests
{
    private static GeneratorRun Pair(string sourceType, string destinationType, string? options = null) =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;
            using System;
            using System.Collections.Generic;

            public class Source { public {{sourceType}} Value { get; set; } = default!; }
            public class Destination { public {{destinationType}} Value { get; set; } = default!; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>({{options}});
            }
            """);

    /// <summary>The map that keeps a null source collection as a null, rather than emptying it.</summary>
    private const string AllowNull = "o => o.AllowNullCollections = true";

    private const string Converter = "global::ShiftMapper.ValueConverter";
    private const string Linq = "global::System.Linq.Enumerable";

    // -----------------------------------------------------------------
    // SHAPE ONLY. The elements already match, so the collection is simply
    // rebuilt in whatever shape the destination asks for.
    //
    // It is rebuilt even when both sides are the same shape, on purpose: the
    // destination gets its OWN list rather than a second reference to the
    // source's, and an IEnumerable that was really an unevaluated query
    // arrives as data rather than as a promise that re-runs later.
    //
    // The builder is the OrEmpty one because that is the DEFAULT null-collection
    // policy: a null source collection becomes an empty destination collection.
    // See The_null_collection_policy_picks_the_builder below.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("List<int>", "List<int>", "ToList")]
    [InlineData("List<int>", "IReadOnlyList<int>", "ToList")]
    [InlineData("List<int>", "IList<int>", "ToList")]
    [InlineData("List<int>", "ICollection<int>", "ToList")]
    [InlineData("List<int>", "IEnumerable<int>", "ToList")]
    [InlineData("List<int>", "int[]", "ToArray")]
    [InlineData("int[]", "List<int>", "ToList")]
    [InlineData("IEnumerable<int>", "List<int>", "ToList")]
    [InlineData("HashSet<int>", "List<int>", "ToList")]
    public void Shape_only(string source, string destination, string method)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Empty(run.Ids());
        run.Compiles().Emits($"Value = {Converter}.{method}OrEmpty(source.Value),");
    }

    /// <summary>
    /// A set is not a shorter word for a list: equal values collapse into one, so a destination
    /// declared as a HashSet can come out shorter than the source that filled it. Quietly, and
    /// only for the data that happens to repeat — hence a note.
    /// </summary>
    [Theory]
    [InlineData("List<int>", "HashSet<int>")]
    [InlineData("List<int>", "ISet<int>")]
    [InlineData("List<int>", "IReadOnlySet<int>")]
    public void Building_a_set_is_reported_as_lossy(string source, string destination)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Equal(new[] { "SM0008" }, run.Ids());
        Assert.Contains(
            "duplicate values are discarded",
            run.Single("SM0008").GetMessage());
        run.Compiles().Emits($"Value = {Converter}.ToHashSetOrEmpty(source.Value),");
    }

    // -----------------------------------------------------------------
    // THE ELEMENTS CONVERT TOO. Note the type arguments on every one of
    // these: generics are INVARIANT, so a List<int> is not a List<long>
    // however freely an int converts to a long, and inference reading the
    // destination from what the lambda returns would quietly rebuild the
    // source's own element type.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(
        "List<long>", "List<int>",
        "ToListOrEmpty<long, int>(source.Value, static item => unchecked((int)item))",
        "SM0010")]
    [InlineData(
        "List<int>", "List<string>",
        "ToListOrEmpty<int, string>(source.Value, static item => global::ShiftMapper.ValueConverter.ToInvariantString(item))",
        null)]
    [InlineData(
        "List<string>", "List<int>",
        "ToListOrEmpty<string, int>(source.Value, static item => global::ShiftMapper.ValueConverter.Parse<int>(item, \"Source.Value -> Destination.Value\"))",
        "SM0009")]
    [InlineData(
        "HashSet<int>", "string[]",
        "ToArrayOrEmpty<int, string>(source.Value, static item => global::ShiftMapper.ValueConverter.ToInvariantString(item))",
        null)]
    public void The_elements_convert(string source, string destination, string expected, string? diagnostic)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Equal(diagnostic is null ? Array.Empty<string>() : new[] { diagnostic }, run.Ids());
        run.Compiles().Emits($"Value = {Converter}.{expected},");
    }

    /// <summary>
    /// The case that looks like it needs no code and does. An <c>int</c> assigns to a
    /// <c>long</c> with nothing written at all — but a <c>List&lt;int&gt;</c> never becomes a
    /// <c>List&lt;long&gt;</c>, so the elements go through the converting overload anyway, with
    /// the empty <c>item =&gt; item</c> whose entire job is to let the implicit conversion happen
    /// one element at a time.
    /// </summary>
    [Fact]
    public void Elements_that_differ_at_all_go_through_the_converting_overload()
    {
        GeneratorRun run = Pair("List<int>", "IReadOnlyList<long>");

        Assert.Empty(run.Ids());
        run.Compiles().Emits($"Value = {Converter}.ToListOrEmpty<int, long>(source.Value, static item => item),");
    }

    /// <summary>A collection carries whatever its elements carry, plus what the shape itself loses.</summary>
    [Fact]
    public void A_set_of_narrowed_elements_reports_both()
    {
        GeneratorRun run = Pair("List<long>", "HashSet<int>");

        // The element narrowing is the louder of the two, so it decides the severity.
        Assert.Equal(new[] { "SM0010" }, run.Ids());

        string message = run.Single("SM0010").GetMessage();
        Assert.Contains("duplicate values are discarded", message);
        Assert.Contains("for each element", message);
        run.Compiles();
    }

    [Fact]
    public void Nullable_elements_are_unwrapped_one_at_a_time()
    {
        GeneratorRun run = Pair("List<int?>", "List<int>");

        Assert.Equal(new[] { "SM0008" }, run.Ids());
        run.Compiles()
           .Emits($"Value = {Converter}.ToListOrEmpty<int?, int>(source.Value, static item => item.GetValueOrDefault()),");
    }

    // -----------------------------------------------------------------
    // THE NULL-COLLECTION POLICY. One question — does a null source
    // collection produce null, or an empty destination collection — and
    // ShiftMapper's answer is EMPTY, because that is the answer that
    // removes a null check from every consumer of the DTO forever.
    // -----------------------------------------------------------------

    /// <summary>
    /// The policy is one method name in memory: <c>ToList</c> keeps the null, <c>ToListOrEmpty</c>
    /// replaces it. Nothing else about the map changes.
    /// </summary>
    [Theory]
    [InlineData(null, "ToListOrEmpty")]
    [InlineData(AllowNull, "ToList")]
    public void The_null_collection_policy_picks_the_builder(string? options, string expected)
    {
        GeneratorRun run = Pair("List<int>", "int[]", options);

        // Same pair either way; only the builder differs.
        run.Compiles().Emits($"Value = {Converter}.{expected.Replace("List", "Array")}(source.Value),");
    }

    /// <summary>
    /// The policy in a PROJECTION, which is the half that had to be argued for rather than
    /// assumed. A collection of values lives in a column, and a NULLABLE column can be null, so
    /// the projection guards it too — otherwise the same map would hand back an empty list in
    /// memory and a null out of the database.
    ///
    /// <c>?? Enumerable.Empty&lt;T&gt;()</c> is what EF turns into nothing at all: the column is
    /// read exactly as it was before and the coalesce becomes a COALESCE in the SQL.
    /// </summary>
    [Fact]
    public void The_null_collection_policy_guards_a_nullable_source_in_the_projection()
    {
        GeneratorRun run = Pair("List<long>?", "List<int>");

        run.Compiles().Emits(
            $"Value = {Linq}.ToList<int>({Linq}.Select<long, int>((source.Value ?? {Linq}.Empty<long>()), " +
            "item => unchecked((int)item))),");
    }

    /// <summary>
    /// AND ONLY A NULLABLE ONE. This is the rule that cost a working query to learn.
    ///
    /// EF recognises a primitive collection by the shape of the expression around it, and
    /// <c>x ?? empty</c> is a shape it does not see through: a <c>Select</c> over a JSON column
    /// that translates perfectly on its own stops translating once the column is wrapped, and the
    /// developer gets a runtime "could not be translated" for a guard they never asked for. The
    /// sample's <c>GET /api/products</c> is the query that proved it — it goes through
    /// <c>Brand.ExternalIds</c>, which is exactly this shape.
    ///
    /// So the guard is written only where a null can actually arrive, and the nullable ANNOTATION
    /// is the test because it is the developer's own statement about the column. It is the same
    /// test <c>NestedProperty.SourceIsNullable</c> already applies to nested objects, for the same
    /// reason. The in-memory maps guard either way: there it costs one null check and hides
    /// nothing from anybody.
    /// </summary>
    [Fact]
    public void A_non_nullable_source_is_left_unguarded_so_EF_can_still_see_it()
    {
        GeneratorRun run = Pair("List<long>", "List<int>");

        run.Compiles()
           // in memory the policy still applies, because there it costs nothing
           .Emits($"Value = {Converter}.ToListOrEmpty<long, int>(source.Value, static item => unchecked((int)item)),")
           // in the projection the column is handed to EF exactly as it was
           .Emits($"Value = {Linq}.ToList<int>({Linq}.Select<long, int>(source.Value, item => unchecked((int)item))),")
           .DoesNotEmit("?? global::System.Linq.Enumerable.Empty");
    }

    /// <summary>
    /// The assignable case cannot use that spelling — it assigns the source across untouched, so
    /// there is no Enumerable call to put an <c>IEnumerable&lt;T&gt;</c> inside. It coalesces onto
    /// an empty of the shape the destination asked for instead, cast to the destination type so
    /// the two arms of the <c>??</c> have a type in common whatever the source is spelled as.
    /// </summary>
    [Fact]
    public void An_assignable_shape_coalesces_onto_the_destinations_own_shape()
    {
        GeneratorRun run = Pair("int[]?", "IReadOnlyList<int>");

        run.Compiles().Emits(
            "Value = (source.Value ?? (global::System.Collections.Generic.IReadOnlyList<int>)" +
            "new global::System.Collections.Generic.List<int>()),");
    }

    /// <summary>Asking for the other policy leaves the projection exactly as it was.</summary>
    [Fact]
    public void Allowing_nulls_leaves_the_projection_unguarded()
    {
        GeneratorRun run = Pair("List<int>?", "IReadOnlyList<int>", AllowNull);

        run.Compiles()
           .Emits($"Value = {Converter}.ToList(source.Value),")
           .Emits("Value = source.Value,")
           // The lazy projection FIELD is initialised with ??=, so the fragment has to name the
           // assignment rather than the operator.
           .DoesNotEmit("Value = (source.Value ??");
    }

    /// <summary>
    /// The mapper-wide default reaches a map that says nothing, exactly as
    /// <c>Matching</c> does — the two options go through the same reader.
    /// </summary>
    [Fact]
    public void ConfigureDefaults_sets_the_policy_for_every_map()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;
            using System.Collections.Generic;

            public class Source { public List<int> Value { get; set; } = new(); }
            public class Destination { public int[] Value { get; set; } = default!; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();

                protected override void ConfigureDefaults(MapOptions options)
                    => options.AllowNullCollections = true;
            }
            """);

        run.Compiles().Emits("Value = global::ShiftMapper.ValueConverter.ToArray(source.Value),");
    }

    // -----------------------------------------------------------------
    // THE QUERY SPELLING. Same conversions, written the way EF can read.
    // -----------------------------------------------------------------

    /// <summary>
    /// A projection that only needs the SHAPE changed assigns straight across. In memory the copy
    /// is the point — the DTO owns its own list — but a projection has no entity to share with:
    /// EF reads the column and builds a fresh collection per row either way.
    /// </summary>
    [Theory]
    [InlineData("List<int>", "List<int>")]
    [InlineData("List<int>", "IReadOnlyList<int>")]
    [InlineData("List<int>", "IEnumerable<int>")]
    public void An_assignable_shape_is_not_rebuilt_in_a_projection(string source, string destination)
    {
        GeneratorRun run = Pair(source, destination);

        run.Compiles()
           // in memory, a copy
           .Emits($"Value = {Converter}.ToListOrEmpty(source.Value),")
           // in a projection, straight across
           .Emits("Value = source.Value,");
    }

    [Fact]
    public void A_shape_that_is_not_assignable_is_built_with_Linq_in_a_projection()
    {
        GeneratorRun run = Pair("List<int>", "int[]");

        run.Compiles()
           .Emits($"Value = {Converter}.ToArrayOrEmpty(source.Value),")
           .Emits($"Value = {Linq}.ToArray(source.Value),");
    }

    /// <summary>
    /// Converting elements in a projection is Select followed by the shape the destination wants
    /// — the same pair of calls the compiler emits for a hand-written correlated projection,
    /// which is exactly why EF recognises it. And the type arguments are stated for the same
    /// reason they are stated in memory.
    /// </summary>
    [Fact]
    public void Converting_elements_in_a_projection_is_Select_then_the_shape()
    {
        GeneratorRun run = Pair("List<long>", "List<int>");

        run.Compiles().Emits(
            $"Value = {Linq}.ToList<int>({Linq}.Select<long, int>(source.Value, item => unchecked((int)item))),");
    }

    /// <summary>
    /// The per-element conversion swaps its spelling too: no database can run
    /// <c>ValueConverter.ToInvariantString</c>, so the query form uses the ordinary
    /// <c>ToString()</c> EF's translators are written against.
    /// </summary>
    [Fact]
    public void A_ValueConverter_element_call_becomes_its_BCL_shape_in_a_projection()
    {
        GeneratorRun run = Pair("List<int>", "List<string>");

        run.Compiles().Emits(
            $"Value = {Linq}.ToList<string>({Linq}.Select<int, string>(source.Value, item => item.ToString())),");
    }
}
