using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// Dictionaries — step 2b of the conversion table, and until Step 5 a flat SM0002.
///
/// A dictionary is a collection like any other as far as ShiftMapper is concerned: it is COPIED
/// rather than shared, its keys and values convert by the ordinary rules, and it answers the
/// null-collection policy. What it needs of its own is a second element type, which is the whole
/// reason it could not fit into the list builders.
/// </summary>
public class DictionaryConversionTests
{
    private static GeneratorRun Pair(string sourceType, string destinationType, string? options = null) =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;
            using System;
            using System.Collections.Generic;

            public class Child { public int Id { get; set; } }
            public class ChildDto { public int Id { get; set; } }

            public class Source { public {{sourceType}} Value { get; set; } = default!; }
            public class Destination { public {{destinationType}} Value { get; set; } = default!; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>({{options}});
            }
            """);

    private const string Converter = "global::ShiftMapper.ValueConverter";
    private const string Linq = "global::System.Linq.Enumerable";

    // -----------------------------------------------------------------
    // THE SHAPES. Read from anything that is an IEnumerable<KeyValuePair>,
    // built as a Dictionary — which is what the two interfaces are for.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("Dictionary<string, int>", "Dictionary<string, int>")]
    [InlineData("Dictionary<string, int>", "IDictionary<string, int>")]
    [InlineData("Dictionary<string, int>", "IReadOnlyDictionary<string, int>")]
    [InlineData("IReadOnlyDictionary<string, int>", "Dictionary<string, int>")]
    [InlineData("SortedDictionary<string, int>", "Dictionary<string, int>")]
    public void Shape_only(string source, string destination)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Empty(run.Ids());
        run.Compiles().Emits($"Value = {Converter}.ToDictionaryOrEmpty(source.Value),");
    }

    /// <summary>
    /// Copied even when both sides are already the same type, exactly as a <c>List</c> is: the
    /// DTO ends up owning its own dictionary rather than a second reference to the entity's,
    /// which EF is still tracking and which is still very much mutable through the entity.
    /// </summary>
    [Fact]
    public void An_identical_dictionary_is_still_copied()
    {
        GeneratorRun run = Pair("Dictionary<string, int>", "Dictionary<string, int>");

        // In memory. The PROJECTION assigns it across, for the reason the collection tests give:
        // there is no entity to share with, since EF builds a fresh one per row either way.
        run.Compiles().Emits($"Value = {Converter}.ToDictionaryOrEmpty(source.Value),");
    }

    // -----------------------------------------------------------------
    // THE KEYS AND VALUES CONVERT, INDEPENDENTLY.
    // -----------------------------------------------------------------

    /// <summary>
    /// The identity <c>key =&gt; key</c> is not dead code. Generics are invariant, so a
    /// <c>Dictionary&lt;string, int&gt;</c> is not a <c>Dictionary&lt;string, string&gt;</c>
    /// however freely an int becomes a string — the whole dictionary is rebuilt, and the key
    /// lambda is what settles the destination's key type for the compiler rather than for type
    /// inference.
    /// </summary>
    [Fact]
    public void The_values_convert()
    {
        GeneratorRun run = Pair("Dictionary<string, int>", "Dictionary<string, string>");

        Assert.Empty(run.Ids());
        run.Compiles().Emits(
            $"Value = {Converter}.ToDictionaryOrEmpty<string, int, string, string>(source.Value, " +
            $"static key => key, static value => {Converter}.ToInvariantString(value)),");
    }

    [Fact]
    public void The_keys_convert()
    {
        GeneratorRun run = Pair("Dictionary<int, string>", "Dictionary<string, string>");

        run.Compiles().Emits(
            $"Value = {Converter}.ToDictionaryOrEmpty<int, string, string, string>(source.Value, " +
            $"static key => {Converter}.ToInvariantString(key), static value => value),");
    }

    /// <summary>
    /// CONVERTING THE KEYS IS WHAT CAN LOSE AN ENTRY. Two keys that were distinct in the source
    /// can arrive as one — two longs narrowing onto the same int — and the later one wins, which
    /// is the answer <c>ToHashSet</c> already gives for the same situation. Hence a note on any
    /// map whose keys are converted, whether or not the conversion itself carries one.
    /// </summary>
    [Fact]
    public void Converting_the_keys_is_reported_as_lossy()
    {
        GeneratorRun run = Pair("Dictionary<int, string>", "Dictionary<string, string>");

        Assert.Equal(new[] { "SM0008" }, run.Ids());
        Assert.Contains(
            "two source keys that convert to the same destination key collapse into one",
            run.Single("SM0008").GetMessage());
    }

    /// <summary>Leaving the keys alone says nothing: a dictionary cannot collide with itself.</summary>
    [Fact]
    public void Converting_only_the_values_says_nothing_about_keys()
    {
        GeneratorRun run = Pair("Dictionary<string, int>", "Dictionary<string, long>");

        Assert.Empty(run.Ids());
    }

    /// <summary>A dictionary carries whatever its keys and values carry, and the louder wins.</summary>
    [Fact]
    public void A_narrowed_key_reports_both_facts()
    {
        GeneratorRun run = Pair("Dictionary<long, string>", "Dictionary<int, string>");

        Assert.Equal(new[] { "SM0010" }, run.Ids());

        string message = run.Single("SM0010").GetMessage();
        Assert.Contains("collapse into one", message);
        Assert.Contains("for each key", message);
    }

    /// <summary>Reading text can fail on data, in a dictionary exactly as in a list.</summary>
    [Fact]
    public void A_parsed_value_is_reported_as_SM0009()
    {
        GeneratorRun run = Pair("Dictionary<string, string>", "Dictionary<string, int>");

        Assert.Equal(new[] { "SM0009" }, run.Ids());
        run.Compiles();
    }

    // -----------------------------------------------------------------
    // WHAT IS STILL REFUSED.
    // -----------------------------------------------------------------

    /// <summary>
    /// A dictionary of OBJECTS is not a conversion at all: filling it means MAPPING each value,
    /// and mapping does not run through this table. Half converting it — a fresh dictionary
    /// holding the entity's own Child instances — is precisely the outcome the simple-element
    /// test exists to prevent, so it stays SM0002 until nested mapping reaches dictionaries.
    /// </summary>
    [Fact]
    public void A_dictionary_of_objects_is_still_SM0002()
    {
        GeneratorRun run = Pair("Dictionary<string, Child>", "Dictionary<string, ChildDto>");

        Assert.Equal(new[] { "SM0002" }, run.Ids());
        run.Compiles().DoesNotEmit("Value =");
    }

    /// <summary>
    /// A dictionary shape we can READ but not BUILD, which is the same asymmetry the list
    /// builders have and for the same reason.
    /// </summary>
    [Fact]
    public void A_dictionary_shape_we_cannot_construct_is_SM0002()
    {
        GeneratorRun run = Pair("Dictionary<string, int>", "SortedDictionary<string, int>");

        Assert.Equal(new[] { "SM0002" }, run.Ids());
    }

    // -----------------------------------------------------------------
    // THE QUERY SPELLING AND THE NULL POLICY.
    // -----------------------------------------------------------------

    /// <summary>
    /// Same types and assignable: the projection hands the value straight across, and a NULLABLE
    /// source picks up the coalesce that answers the null-collection policy. A non-nullable one
    /// does not — see
    /// <see cref="CollectionConversionTests.A_non_nullable_source_is_left_unguarded_so_EF_can_still_see_it"/>
    /// for what that guard costs where it is not needed.
    /// </summary>
    [Fact]
    public void An_assignable_dictionary_is_not_rebuilt_in_a_projection()
    {
        Pair("Dictionary<string, int>", "IReadOnlyDictionary<string, int>")
            .Compiles()
            .Emits("Value = source.Value,");

        Pair("Dictionary<string, int>?", "IReadOnlyDictionary<string, int>")
            .Compiles()
            .Emits(
                "Value = (source.Value ?? (global::System.Collections.Generic.IReadOnlyDictionary<string, int>)" +
                "new global::System.Collections.Generic.Dictionary<string, int>()),");
    }

    /// <summary>
    /// Otherwise it is <c>Enumerable.ToDictionary</c> over the pairs, which is the ordinary BCL
    /// shape a developer would have hand-written. Whether a given provider can turn that into SQL
    /// is EF's answer to give, not ShiftMapper's to predict.
    /// </summary>
    [Fact]
    public void A_converting_dictionary_is_Enumerable_ToDictionary_in_a_projection()
    {
        GeneratorRun run = Pair("Dictionary<string, int>", "Dictionary<string, string>");

        run.Compiles().Emits(
            $"Value = {Linq}.ToDictionary<global::System.Collections.Generic.KeyValuePair<string, int>, string, string>(" +
            "source.Value, item => item.Key, item => item.Value.ToString()),");
    }

    [Fact]
    public void The_null_collection_policy_picks_the_builder()
    {
        GeneratorRun run = Pair("Dictionary<string, int>", "Dictionary<string, int>", "o => o.AllowNullCollections = true");

        run.Compiles().Emits($"Value = {Converter}.ToDictionary(source.Value),");
    }
}
