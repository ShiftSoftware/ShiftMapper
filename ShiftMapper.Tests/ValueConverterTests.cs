using System.Globalization;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// <see cref="ValueConverter"/> — the one piece of ShiftMapper that runs on the hot path and can
/// fail on DATA rather than on types.
/// </summary>
public class ValueConverterTests
{
    private const string Mapping = "Source.Value -> Destination.Value";

    /// <summary>
    /// A culture whose separators are the opposite of the invariant one's, used to prove that
    /// nothing here reads <see cref="CultureInfo.CurrentCulture"/>.
    ///
    /// This is the failure mode ShiftMapper exists to prevent: a decimal written on a machine set
    /// to de-DE comes out "1234,56", and every consumer that reads it back — a JSON client, a
    /// second service, the same service after a server rebuild — gets a different number or an
    /// exception. Pinning the culture makes what a map produces a property of the two types
    /// rather than of the machine it ran on.
    /// </summary>
    private static IDisposable HostileCulture() => new CultureSwap(new CultureInfo("de-DE"));

    private sealed class CultureSwap : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureSwap(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }

    // -----------------------------------------------------------------
    // TO TEXT
    // -----------------------------------------------------------------

    [Fact]
    public void Numbers_are_written_with_the_invariant_separators()
    {
        using (HostileCulture())
        {
            // de-DE would render this "1234,56".
            Assert.Equal("1234.56", ValueConverter.ToInvariantString(1234.56m));
            Assert.Equal("2.5", ValueConverter.ToInvariantString(2.5d));
            Assert.Equal("42", ValueConverter.ToInvariantString(42));
        }
    }

    [Fact]
    public void Bool_is_written_the_way_bool_Parse_reads_it_back()
    {
        Assert.Equal("True", ValueConverter.ToInvariantString(true));
        Assert.Equal("False", ValueConverter.ToInvariantString(false));
    }

    /// <summary>
    /// The date and time types get their own overloads because their DEFAULT rendering throws
    /// information away — the sub-second digits, the Kind, and for TimeOnly the seconds outright.
    /// </summary>
    [Fact]
    public void Date_and_time_types_are_written_round_trippable()
    {
        using (HostileCulture())
        {
            var moment = new DateTime(2026, 8, 21, 14, 30, 15, 500, DateTimeKind.Utc);

            string text = ValueConverter.ToInvariantString(moment);

            Assert.Equal("2026-08-21T14:30:15.5000000Z", text);
            Assert.Equal("2026-08-21", ValueConverter.ToInvariantString(new DateOnly(2026, 8, 21)));
            Assert.Equal("14:30:15.0000000", ValueConverter.ToInvariantString(new TimeOnly(14, 30, 15)));
        }
    }

    [Fact]
    public void A_null_source_stays_null_rather_than_becoming_text()
    {
        Assert.Null(ValueConverter.ToInvariantString((int?)null));
        Assert.Null(ValueConverter.ToInvariantString((bool?)null));
        Assert.Null(ValueConverter.ToInvariantString((DateTime?)null));
        Assert.Equal("7", ValueConverter.ToInvariantString((int?)7));
    }

    // -----------------------------------------------------------------
    // FROM TEXT
    // -----------------------------------------------------------------

    [Fact]
    public void Text_is_read_with_the_invariant_separators()
    {
        using (HostileCulture())
        {
            // Under de-DE this text would either parse as 123456 or throw.
            Assert.Equal(1234.56m, ValueConverter.Parse<decimal>("1234.56", Mapping));
            Assert.Equal(42, ValueConverter.Parse<int>("42", Mapping));
            Assert.True(ValueConverter.Parse<bool>("True", Mapping));
        }
    }

    /// <summary>
    /// Absence is the empty column a client POSTs when it has no id yet, and it becomes the
    /// destination's default rather than an exception.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_text_becomes_the_destinations_default(string? value)
    {
        Assert.Equal(0, ValueConverter.Parse<int>(value, Mapping));
        Assert.Equal(Guid.Empty, ValueConverter.Parse<Guid>(value, Mapping));
        Assert.False(ValueConverter.Parse<bool>(value, Mapping));
    }

    /// <summary>
    /// The difference a nullable destination makes. An empty cell becoming <c>0</c> is a real
    /// quantity; becoming null still says nobody filled it in.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_text_stays_absent_for_a_nullable_destination(string? value)
    {
        Assert.Null(ValueConverter.ParseOrNull<int>(value, Mapping));
        Assert.Null(ValueConverter.ParseDateTimeOrNull(value, Mapping));
        Assert.Null(ValueConverter.ParseEnumOrNull<Weekday>(value, Mapping));
    }

    /// <summary>
    /// Text that is not empty and does not parse THROWS. Turning "abc" into 0 would put bad data
    /// quietly into the model, and finding it later costs far more than failing here.
    /// </summary>
    [Fact]
    public void Text_that_does_not_parse_throws_naming_both_properties()
    {
        FormatException error = Assert.Throws<FormatException>(
            () => ValueConverter.Parse<int>("abc", Mapping));

        Assert.Contains("\"abc\"", error.Message);
        Assert.Contains("Int32", error.Message);
        Assert.Contains(Mapping, error.Message);
        Assert.NotNull(error.InnerException);
    }

    /// <summary>
    /// A FormatException whatever actually went wrong, so there is ONE type to catch around a
    /// mapping call — with the real reason kept as the inner exception.
    /// </summary>
    [Fact]
    public void An_overflow_is_reported_as_a_format_failure_with_the_real_cause_inside()
    {
        FormatException error = Assert.Throws<FormatException>(
            () => ValueConverter.Parse<int>("99999999999999999999", Mapping));

        Assert.IsType<OverflowException>(error.InnerException);
    }

    /// <summary>An exception message can end up in a log aggregator; a 2 MB column value must not.</summary>
    [Fact]
    public void A_long_value_is_truncated_in_the_message()
    {
        string huge = new('x', 5000);

        FormatException error = Assert.Throws<FormatException>(
            () => ValueConverter.Parse<int>(huge, Mapping));

        Assert.Contains("...", error.Message);
        Assert.DoesNotContain(huge, error.Message);
    }

    /// <summary>
    /// <c>char</c> is the one type for which whitespace is a VALUE rather than an absence, so it
    /// does not go through <see cref="ValueConverter.Parse{T}"/>.
    /// </summary>
    [Fact]
    public void A_space_is_a_character_rather_than_an_absence()
    {
        Assert.Equal(' ', ValueConverter.ParseChar(" ", Mapping));
        Assert.Equal(' ', ValueConverter.ParseCharOrNull(" ", Mapping));

        Assert.Equal('\0', ValueConverter.ParseChar("", Mapping));
        Assert.Null(ValueConverter.ParseCharOrNull(null, Mapping));
    }

    /// <summary>Two characters in a one-character column is bad data, not a value to trim down.</summary>
    [Fact]
    public void Text_longer_than_one_character_is_rejected_rather_than_truncated()
    {
        Assert.Throws<FormatException>(() => ValueConverter.ParseChar("ab", Mapping));
    }

    /// <summary>
    /// The reason DateTime has its own reader: the plain parse rewrites what it reads into LOCAL
    /// time, so a UTC timestamp would come back shifted by the server's offset.
    /// </summary>
    [Fact]
    public void A_DateTime_keeps_its_Kind_through_a_round_trip()
    {
        var utc = new DateTime(2026, 8, 21, 14, 30, 0, DateTimeKind.Utc);

        using (HostileCulture())
        {
            DateTime back = ValueConverter.ParseDateTime(ValueConverter.ToInvariantString(utc), Mapping);

            Assert.Equal(utc, back);
            Assert.Equal(DateTimeKind.Utc, back.Kind);
        }
    }

    /// <summary>
    /// DateTimeOffset deliberately goes through the ordinary parse: it carries its offset IN the
    /// value, so there is no Kind to lose.
    /// </summary>
    [Fact]
    public void A_DateTimeOffset_keeps_its_offset_through_a_round_trip()
    {
        var moment = new DateTimeOffset(2026, 8, 21, 14, 30, 0, TimeSpan.FromHours(3));

        using (HostileCulture())
        {
            DateTimeOffset back = ValueConverter.Parse<DateTimeOffset>(
                ValueConverter.ToInvariantString(moment), Mapping);

            Assert.Equal(moment, back);
            Assert.Equal(TimeSpan.FromHours(3), back.Offset);
        }
    }

    /// <summary>
    /// Enum text usually came from a database column or a query string rather than from C#, so
    /// the name is matched ignoring case — and the underlying number is read too.
    /// </summary>
    [Theory]
    [InlineData("Tuesday")]
    [InlineData("tuesday")]
    [InlineData("TUESDAY")]
    [InlineData("1")]
    public void An_enum_is_read_by_name_ignoring_case_or_by_number(string text)
    {
        Assert.Equal(Weekday.Tuesday, ValueConverter.ParseEnum<Weekday>(text, Mapping));
    }

    [Fact]
    public void A_name_the_enum_does_not_declare_throws()
    {
        Assert.Throws<FormatException>(() => ValueConverter.ParseEnum<Weekday>("Caturday", Mapping));
    }

    [Fact]
    public void Enums_round_trip_through_text()
    {
        string text = ValueConverter.ToInvariantString(Weekday.Tuesday);

        Assert.Equal("Tuesday", text);
        Assert.Equal(Weekday.Tuesday, ValueConverter.ParseEnum<Weekday>(text, Mapping));
    }

    // -----------------------------------------------------------------
    // BETWEEN THE DATE AND TIME TYPES
    // -----------------------------------------------------------------

    [Fact]
    public void A_date_becomes_midnight_and_a_timestamp_loses_its_time()
    {
        Assert.Equal(
            new DateTime(2026, 8, 21, 0, 0, 0),
            ValueConverter.ToDateTime(new DateOnly(2026, 8, 21)));

        Assert.Equal(
            new DateOnly(2026, 8, 21),
            ValueConverter.ToDateOnly(new DateTime(2026, 8, 21, 14, 30, 0)));

        Assert.Equal(
            new TimeSpan(14, 30, 0),
            ValueConverter.ToTimeSpan(new TimeOnly(14, 30, 0)));
    }

    [Fact]
    public void The_date_and_time_helpers_keep_an_absent_value_absent()
    {
        Assert.Null(ValueConverter.ToDateTime((DateOnly?)null));
        Assert.Null(ValueConverter.ToDateOnly((DateTime?)null));
        Assert.Null(ValueConverter.ToTimeSpan((TimeOnly?)null));
    }

    // -----------------------------------------------------------------
    // COLLECTIONS
    // -----------------------------------------------------------------

    /// <summary>
    /// Always a NEW collection, even when the source could have been assigned across. Sharing the
    /// entity's list means adding to the DTO quietly adds to the entity EF is tracking.
    /// </summary>
    [Fact]
    public void A_collection_is_copied_rather_than_shared()
    {
        var source = new List<int> { 1, 2, 3 };

        List<int>? copy = ValueConverter.ToList(source);

        Assert.Equal(source, copy);
        Assert.NotSame(source, copy);

        copy!.Add(4);
        Assert.Equal(3, source.Count);
    }

    /// <summary>
    /// An IEnumerable may not be a collection at all — it can be an unevaluated query that runs
    /// again on every enumeration and throws once its DbContext is gone. Materialising here means
    /// the DTO leaves the mapper holding data rather than a promise.
    /// </summary>
    [Fact]
    public void A_lazy_sequence_is_materialised_once()
    {
        int enumerations = 0;

        IEnumerable<int> Lazy()
        {
            enumerations++;
            yield return 1;
            yield return 2;
        }

        List<int>? copied = ValueConverter.ToList(Lazy());

        Assert.Equal(1, enumerations);
        Assert.Equal(new[] { 1, 2 }, copied);

        // Enumerating the copy again does not run the source again.
        foreach (int _ in copied!) { }
        foreach (int _ in copied) { }

        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void Elements_are_converted_one_at_a_time()
    {
        Assert.Equal(
            new[] { "1", "2" },
            ValueConverter.ToArray<int, string>(new[] { 1, 2 }, item => ValueConverter.ToInvariantString(item)));

        Assert.Equal(
            new List<int> { 1, 2 },
            ValueConverter.ToList<string, int>(new[] { "1", "2" }, item => ValueConverter.Parse<int>(item, Mapping)));
    }

    /// <summary>A set discards duplicates. That is what a set is, and it is why SM0008 says so.</summary>
    [Fact]
    public void A_set_discards_duplicates()
    {
        HashSet<int>? set = ValueConverter.ToHashSet(new[] { 1, 1, 2 });

        Assert.Equal(2, set!.Count);
    }

    /// <summary>Absence is copied rather than invented — a null source does not become an empty list.</summary>
    [Fact]
    public void A_null_collection_stays_null()
    {
        Assert.Null(ValueConverter.ToList((IEnumerable<int>?)null));
        Assert.Null(ValueConverter.ToArray((IEnumerable<int>?)null));
        Assert.Null(ValueConverter.ToHashSet((IEnumerable<int>?)null));
        Assert.Null(ValueConverter.ToList<int, string>(null, item => ValueConverter.ToInvariantString(item)));
        Assert.Null(ValueConverter.ToArray<int, string>(null, item => ValueConverter.ToInvariantString(item)));
        Assert.Null(ValueConverter.ToHashSet<int, string>(null, item => ValueConverter.ToInvariantString(item)));
    }

    /// <summary>
    /// The generator checks this number before emitting a call to anything here, so an old
    /// ShiftMapper.dll under a new generator reports SM0002 rather than producing a CS0117 inside
    /// a file the developer cannot edit. It must never go DOWN.
    /// </summary>
    [Fact]
    public void The_converter_api_version_covers_the_collection_helpers()
    {
        Assert.True(ValueConverter.ConverterApiVersion >= 2);
    }

    public enum Weekday
    {
        Monday,
        Tuesday,
        Wednesday,
    }
}
