using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The conversion table, pair by pair.
///
/// Every case runs through the same one-property map, so the only variable is the pair of types,
/// and each asserts BOTH halves of the answer: the C# emitted for the in-memory maps, and which
/// diagnostic (if any) the developer hears about it.
///
/// The refused pairs matter as much as the accepted ones. Four of them are conversions C# would
/// perform quite happily, and the whole argument for refusing them is that the developer is TOLD
/// — a silent skip would be indistinguishable from a typo in a property name.
/// </summary>
public class ConversionMatrixTests
{
    /// <summary>
    /// One property, two types, one map. The extra types in the preamble are the cases that need
    /// a type of the developer's own: an enum, a second enum, a struct with an implicit operator,
    /// a struct with an explicit one.
    /// </summary>
    private static GeneratorRun Pair(string sourceType, string destinationType) =>
        GeneratorHarness.Run(
            $$"""
            using ShiftMapper;
            using System;
            using System.Collections.Generic;

            public enum Status { Draft, Sent, Paid }
            public enum Other { A, B, C }

            public struct Money
            {
                public decimal Amount { get; set; }
                public static implicit operator decimal(Money value) => value.Amount;
                public static implicit operator Money(decimal value) => new Money { Amount = value };
            }

            public struct Guarded
            {
                public int Amount { get; set; }
                public static explicit operator int(Guarded value) => value.Amount;
            }

            public class Child { public int Id { get; set; } }
            public class ChildDto { public int Id { get; set; } }

            public class Source { public {{sourceType}} Value { get; set; } = default!; }
            public class Destination { public {{destinationType}} Value { get; set; } = default!; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

    private const string Converter = "global::ShiftMapper.ValueConverter";
    private const string Mapping = "\"Source.Value -> Destination.Value\"";

    // -----------------------------------------------------------------
    // ASSIGNED STRAIGHT ACROSS — C# already converts, so nothing is emitted
    // and nothing is said.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("int", "int")]                 // identity
    [InlineData("string", "string")]           // identity, and NOT read as a collection of chars
    [InlineData("Status", "Status")]           // identity, enum
    [InlineData("int", "long")]                // widening
    [InlineData("int", "decimal")]
    [InlineData("int", "double")]
    [InlineData("float", "double")]
    [InlineData("char", "int")]                // char to a number IS implicit in C#
    [InlineData("int", "int?")]                // lifting
    [InlineData("int?", "long?")]
    [InlineData("Money", "decimal")]           // a user-defined IMPLICIT operator is honoured
    [InlineData("decimal", "Money")]
    public void Assigned_straight_across(string source, string destination)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Empty(run.Ids());
        run.Compiles().Emits("Value = source.Value,");
    }

    // -----------------------------------------------------------------
    // A CAST. Explicit between two numbers means the destination cannot hold
    // everything the source can, so these are narrowing by construction —
    // except the enum cases, which are a by-design reinterpretation.
    // -----------------------------------------------------------------

    [Theory]
    // Whole number to whole number WRAPS, and `unchecked` pins that so a csproj setting cannot
    // change what the generated line means.
    [InlineData("long", "int", "unchecked((int)source.Value)", "SM0010")]
    [InlineData("int", "byte", "unchecked((byte)source.Value)", "SM0010")]
    [InlineData("int", "char", "unchecked((char)source.Value)", "SM0010")]
    // Floating point to whole number is unspecified rather than wrapping, but still unchecked.
    [InlineData("double", "int", "unchecked((int)source.Value)", "SM0010")]
    // The decimal conversions ignore the checked context entirely, so `unchecked` would promise
    // a truncation that never happens and is deliberately left off.
    [InlineData("decimal", "int", "(int)source.Value", "SM0010")]
    [InlineData("double", "decimal", "(decimal)source.Value", "SM0010")]
    // An enum and a number convert by VALUE. That is a note, not a warning.
    [InlineData("Status", "int", "unchecked((int)source.Value)", "SM0008")]
    [InlineData("int", "Status", "unchecked((global::Status)source.Value)", "SM0008")]
    public void Cast(string source, string destination, string expected, string diagnostic)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Equal(new[] { diagnostic }, run.Ids());
        run.Compiles().Emits("Value = " + expected + ",");
    }

    // -----------------------------------------------------------------
    // TO TEXT. Anything that can format itself fills a string property, and
    // it cannot fail, so nothing is reported.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("decimal")]
    [InlineData("double")]
    [InlineData("bool")]            // the one common type that does NOT implement IFormattable
    [InlineData("char")]
    [InlineData("Status")]
    [InlineData("Guid")]
    [InlineData("DateTime")]
    [InlineData("DateTimeOffset")]
    [InlineData("DateOnly")]
    [InlineData("TimeOnly")]
    [InlineData("TimeSpan")]
    [InlineData("int?")]            // the nullable overload keeps an absent value absent
    public void To_text(string source)
    {
        GeneratorRun run = Pair(source, "string");

        Assert.Empty(run.Ids());
        run.Compiles().Emits($"Value = {Converter}.ToInvariantString(source.Value),");
    }

    // -----------------------------------------------------------------
    // FROM TEXT. The mirror of the above, and the ONE direction that can fail
    // on data rather than on types — hence SM0009 on every row.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("int", "Parse<int>")]
    [InlineData("long", "Parse<long>")]
    [InlineData("decimal", "Parse<decimal>")]
    [InlineData("double", "Parse<double>")]
    [InlineData("bool", "Parse<bool>")]
    [InlineData("Guid", "Parse<global::System.Guid>")]
    [InlineData("DateTimeOffset", "Parse<global::System.DateTimeOffset>")]
    [InlineData("DateOnly", "Parse<global::System.DateOnly>")]
    [InlineData("TimeOnly", "Parse<global::System.TimeOnly>")]
    [InlineData("TimeSpan", "Parse<global::System.TimeSpan>")]
    [InlineData("int?", "ParseOrNull<int>")]
    // char is routed away from Parse<T> because it is the one type for which whitespace is a
    // VALUE rather than an absence.
    [InlineData("char", "ParseChar")]
    [InlineData("char?", "ParseCharOrNull")]
    // DateTime is routed away because the plain parse rewrites what it reads into local time.
    [InlineData("DateTime", "ParseDateTime")]
    [InlineData("DateTime?", "ParseDateTimeOrNull")]
    // Enums do not implement IParsable at all.
    [InlineData("Status", "ParseEnum<global::Status>")]
    [InlineData("Status?", "ParseEnumOrNull<global::Status>")]
    public void From_text(string destination, string method)
    {
        GeneratorRun run = Pair("string", destination);

        Assert.Equal(new[] { "SM0009" }, run.Ids());
        run.Compiles().Emits($"Value = {Converter}.{method}(source.Value, {Mapping}),");
    }

    // -----------------------------------------------------------------
    // THE DATE AND TIME PAIRS C# HAS NO CONVERSION FOR — the three with
    // exactly one sensible answer.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("DateOnly", "DateTime", "ToDateTime", null)]
    [InlineData("TimeOnly", "TimeSpan", "ToTimeSpan", null)]
    // The only one of the three that throws information away.
    [InlineData("DateTime", "DateOnly", "ToDateOnly", "SM0008")]
    public void Between_date_and_time_types(string source, string destination, string method, string? diagnostic)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Equal(diagnostic is null ? Array.Empty<string>() : new[] { diagnostic }, run.Ids());
        run.Compiles().Emits($"Value = {Converter}.{method}(source.Value),");
    }

    // -----------------------------------------------------------------
    // A NULLABLE SOURCE FILLING A NON-NULLABLE DESTINATION. Unwrapped first,
    // then whatever conversion the unwrapped pair needs — so every rule works
    // lifted without being written twice.
    // -----------------------------------------------------------------

    [Theory]
    // Only the null-becomes-default part: a note.
    [InlineData("int?", "int", "source.Value.GetValueOrDefault()", "SM0008")]
    [InlineData("int?", "long", "source.Value.GetValueOrDefault()", "SM0008")]
    [InlineData("DateTime?", "DateTime", "source.Value.GetValueOrDefault()", "SM0008")]
    // Null becomes default AND the rest narrows. The narrowing is the half worth a warning.
    [InlineData("long?", "int", "unchecked((int)source.Value.GetValueOrDefault())", "SM0010")]
    public void A_nullable_source_filling_a_non_nullable_destination(
        string source, string destination, string expected, string diagnostic)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Equal(new[] { diagnostic }, run.Ids());
        run.Compiles().Emits("Value = " + expected + ",");
    }

    // -----------------------------------------------------------------
    // THE REFUSALS. Every one of these is reported as SM0002 rather than
    // skipped in silence, which is the whole point of having the list.
    // -----------------------------------------------------------------

    [Theory]
    // The four pairs C# WOULD convert, refused because the answer would not come from the two
    // types alone.
    [InlineData("DateTime", "DateTimeOffset")]
    [InlineData("DateTimeOffset", "DateTime")]
    [InlineData("Status", "Other")]
    [InlineData("TimeSpan", "TimeOnly")]
    // A user-defined EXPLICIT operator: its author chose the keyword that says stop and think.
    [InlineData("Guarded", "int")]
    // No conversion at all.
    [InlineData("bool", "int")]
    [InlineData("int", "bool")]
    // Moving a REFERENCE around, by boxing or by up-casting, is not a mapping.
    [InlineData("int", "object")]
    [InlineData("Child", "object")]
    // A string is an IEnumerable<char>, and must never be treated as one here — in either
    // direction.
    [InlineData("string", "char[]")]
    [InlineData("char[]", "string")]
    // A dictionary of OBJECTS: the values would have to be mapped, and mapping does not run
    // through the conversion table. (A dictionary of values is step 2b and maps — see
    // DictionaryConversionTests.)
    [InlineData("Dictionary<string, Child>", "Dictionary<string, ChildDto>")]
    // A collection shape we can read but not build.
    [InlineData("List<int>", "Stack<int>")]
    public void Refused_as_Sm0002(string source, string destination)
    {
        GeneratorRun run = Pair(source, destination);

        Assert.Equal(new[] { "SM0002" }, run.Ids());
        run.Compiles().DoesNotEmit("Value =");
    }

    /// <summary>
    /// <c>dynamic</c> has to be refused FIRST, before anything else looks at it. The compiler
    /// reports an implicit conversion to it from every type in existence, so one dynamic
    /// property left in the running would swallow whatever it was pointed at.
    /// </summary>
    [Fact]
    public void Dynamic_is_refused_rather_than_swallowing_anything()
    {
        GeneratorRun run = Pair("Child", "dynamic");

        Assert.Equal(new[] { "SM0002" }, run.Ids());
        run.Compiles().DoesNotEmit("Value =");
    }
}
