using System.Globalization;

namespace ShiftMapper;

/// <summary>
/// The value conversions the generated mapping code calls into.
///
/// WHY THIS EXISTS. When a source property and a destination property have the same name
/// but different types, ShiftMapper converts instead of giving up — <c>int</c> to
/// <c>string</c>, <c>string</c> back to <c>int</c>, <c>bool</c> to <c>string</c>, one number
/// type to another, and so on. The generator could write those conversions out inline, but
/// three of them need a decision made consistently every single time:
///
///   * WHICH CULTURE formats and parses the text,
///   * WHAT A NULL OR EMPTY source means,
///   * WHAT THE ERROR SAYS when the text is not a number after all.
///
/// Making those decisions in one place — here — is what keeps every generated map answering
/// them the same way, and is what lets a failure name the exact map it came from instead of
/// throwing a bare <c>FormatException</c> from somewhere inside the BCL.
///
/// UNLIKE THE REST OF THIS LIBRARY, THIS CLASS REALLY RUNS. <see cref="ShiftMapperBase.CreateMap"/>,
/// <see cref="MapExpression{TSource, TDestination}"/> and <see cref="MapOptions"/> are all
/// compile-time markers that do nothing at runtime. These methods are the opposite: the
/// generator emits calls to them, and they execute every time a map runs — once for each
/// property whose type had to be converted. Properties whose types already match are still
/// copied straight across and never come near this class.
///
/// You can call them yourself — they are ordinary public static methods — but you are not
/// meant to have to.
///
/// <para><b>THE THREE RULES</b></para>
///
/// <list type="number">
/// <item>
/// <description>
/// <b>Text never depends on the machine.</b> Every conversion to and from <c>string</c> is
/// culture-independent: the ones that take a format provider are handed
/// <see cref="CultureInfo.InvariantCulture"/> rather than the thread's current culture, and
/// the rest — <c>bool</c>, <c>char</c> and enum member names — do not vary by culture in the
/// first place. A DTO is something you serialise, store, and send to another machine; if the
/// text depended on the server's locale then <c>1234.5</c> would travel as <c>"1234,5"</c> in
/// Berlin and fail to parse in London. Dates and times use the round-trip format
/// (<c>"O"</c>), and <see cref="TimeSpan"/> uses the constant format (<c>"c"</c>), so what is
/// written can always be read back.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Nothing means default; nonsense throws.</b> A null, empty or whitespace-only source
/// string is ABSENCE, so it converts to <c>default</c> (or to <c>null</c> for a nullable
/// destination). A source string with something in it that does not parse is BAD DATA, so it
/// throws — silently turning <c>"abc"</c> into <c>0</c> would hide the problem in your
/// database rather than in your logs.
///
/// <c>char</c> is the one exception, and deliberately so: see <see cref="ParseChar"/>. For a
/// character, <c>" "</c> IS a value, so only null and empty count as absence there.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Failures name the map.</b> Every parse method takes a <c>mapping</c> string that the
/// generator fills in with the property pair being mapped, e.g.
/// <c>"Product.Sku -&gt; ProductDto.Sku"</c>, so the exception message points straight at the
/// two properties involved.
/// </description>
/// </item>
/// </list>
/// </summary>
public static class ValueConverter
{
    /// <summary>
    /// Which set of conversions this class contains, so the generator can tell whether the
    /// runtime underneath it is new enough to call.
    ///
    /// The generator and this library are two SEPARATE references, and nothing forces their
    /// versions to agree. Before emitting a call to anything here, the generator checks this
    /// number; if the referenced ShiftMapper is too old it emits no conversion at all and
    /// reports SM0002, which is a build warning the developer can act on rather than a CS0117
    /// inside a generated file they cannot edit.
    ///
    /// RAISE IT whenever a method is added here AND the generator starts emitting calls to
    /// it — in both places, or the check protects nothing.
    ///
    ///   1 — numbers, bool, char, enums, Guid and the date/time types, to and from text;
    ///       DateOnly/TimeOnly to DateTime/TimeSpan.
    ///   2 — collections of those types: ToArray, ToList and ToHashSet.
    ///   3 — dictionaries (ToDictionary), and the OrEmpty twin of every collection builder,
    ///       which is what the null-collection policy is generated against.
    /// </summary>
    public const int ConverterApiVersion = 3;

    // -----------------------------------------------------------------
    // TO TEXT
    // -----------------------------------------------------------------
    //
    // These cannot fail, so none of them takes a `mapping` argument.
    //
    // Most types are handled by the two GENERIC overloads at the bottom, which work for
    // anything implementing IFormattable — every numeric type, every enum, Guid, and any
    // type of your own that implements it. The concrete overloads above them exist for three
    // different reasons, worth keeping straight:
    //
    //   bool           — the only one here that does NOT implement IFormattable at all, so
    //                    without its own overload it would not convert to text.
    //   DateTime,      — DO implement it, but their DEFAULT format is the human-readable one,
    //   DateTimeOffset   which throws information away: "08/21/2026 14:30:00" has lost the
    //   DateOnly,        sub-second digits AND the Kind, and TimeOnly's default has lost the
    //   TimeOnly         seconds outright. They need "O" to round-trip.
    //   char, TimeSpan — DO implement it, and their default rendering already happens to be
    //                    the right one. They have overloads anyway, so that what ShiftMapper
    //                    writes is pinned by ShiftMapper rather than inherited from an
    //                    interface implementation that could be tuned in a future .NET.
    //
    // A concrete overload always beats a generic one in C# overload resolution, so passing
    // a DateTime lands on the DateTime overload and passing an int lands on the generic —
    // without either of them having to know the other exists.

    /// <summary>
    /// <c>true</c> / <c>false</c> as <c>"True"</c> / <c>"False"</c>.
    ///
    /// That capitalisation is <see cref="bool.TrueString"/>, and it is what
    /// <see cref="bool.Parse(string)"/> reads back, so a bool that goes out as text and comes
    /// back returns the value it started with. It is NOT the lowercase spelling JSON uses —
    /// if you need that, the destination property wants to stay a <c>bool</c> and let your
    /// serialiser write it.
    /// </summary>
    public static string ToInvariantString(bool value) => value.ToString();

    /// <inheritdoc cref="ToInvariantString(bool)"/>
    public static string? ToInvariantString(bool? value) => value?.ToString();

    /// <summary>A single character as a one-character string.</summary>
    public static string ToInvariantString(char value) => value.ToString();

    /// <inheritdoc cref="ToInvariantString(char)"/>
    public static string? ToInvariantString(char? value) => value?.ToString();

    /// <summary>
    /// A date and time in round-trip format, e.g. <c>"2026-08-21T14:30:00.0000000Z"</c>.
    ///
    /// <c>"O"</c> rather than the default format because it keeps the sub-second digits AND
    /// the <see cref="DateTimeKind"/> — the default would quietly turn a UTC timestamp into
    /// text that reads back as local time.
    /// </summary>
    public static string ToInvariantString(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ToInvariantString(DateTime)"/>
    public static string? ToInvariantString(DateTime? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// A date, time and UTC offset in round-trip format, e.g.
    /// <c>"2026-08-21T14:30:00.0000000+03:00"</c>.
    /// </summary>
    public static string ToInvariantString(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ToInvariantString(DateTimeOffset)"/>
    public static string? ToInvariantString(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A calendar date with no time, e.g. <c>"2026-08-21"</c>.</summary>
    public static string ToInvariantString(DateOnly value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ToInvariantString(DateOnly)"/>
    public static string? ToInvariantString(DateOnly? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A time of day with no date, e.g. <c>"14:30:00.0000000"</c>.</summary>
    public static string ToInvariantString(TimeOnly value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ToInvariantString(TimeOnly)"/>
    public static string? ToInvariantString(TimeOnly? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// A duration in constant format, e.g. <c>"1.02:30:00"</c> for one day, two and a half
    /// hours. This is what <see cref="TimeSpan"/> renders by default anyway; asking for
    /// <c>"c"</c> explicitly says so out loud, and <c>"c"</c> is culture-independent by
    /// definition, which is exactly what
    /// <see cref="TimeSpan.Parse(string, IFormatProvider)"/> expects to read back.
    /// </summary>
    public static string ToInvariantString(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ToInvariantString(TimeSpan)"/>
    public static string? ToInvariantString(TimeSpan? value) =>
        value?.ToString("c", CultureInfo.InvariantCulture);

    /// <summary>
    /// Anything that can format itself: every numeric type, every enum,
    /// <see cref="Guid"/>, and any STRUCT of your own implementing
    /// <see cref="IFormattable"/>.
    ///
    /// The format is left null, which asks the type for its own general format — the plain
    /// digits for a number, the member NAME for an enum, the dashed form for a Guid — and
    /// the culture is pinned to invariant so a decimal point stays a point.
    ///
    /// The <c>struct</c> constraint is what makes the return type non-nullable: a value type
    /// always has a value to render, so this can never hand back null, and the generated code
    /// is that much freer of nullable-assignment noise. Reference types are not converted to
    /// text at all — see the generator's CanFormat for why.
    /// </summary>
    public static string ToInvariantString<T>(T value) where T : struct, IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>
    /// The nullable-value-type twin of <see cref="ToInvariantString{T}(T)"/>: no value
    /// converts to null, not to <c>""</c> and not to <c>"0"</c>.
    /// </summary>
    public static string? ToInvariantString<T>(T? value) where T : struct, IFormattable =>
        value.HasValue ? value.GetValueOrDefault().ToString(null, CultureInfo.InvariantCulture) : null;

    // -----------------------------------------------------------------
    // FROM TEXT
    // -----------------------------------------------------------------
    //
    // These CAN fail, so every one of them takes `mapping` — the property pair the generator
    // was working on — purely so the exception can say which map produced it.
    //
    // <see cref="IParsable{TSelf}"/> is the .NET interface for "this type knows how to read
    // itself from a string", and nearly every type worth converting to implements it: all the
    // numeric types, bool, Guid, the date/time types. Constraining to it means one method
    // covers the lot, and covers YOUR types too if they implement it — instead of forty
    // near-identical overloads that would still miss something.
    //
    // Three cases still get their own method, each for a stated reason: enums do not
    // implement IParsable at all; DateTime and DateTimeOffset implement it in a way that
    // rewrites what it reads into local time; and char is the one type for which
    // whitespace is a value rather than an absence.
    //
    // The generator always writes the type argument out (`Parse<int>(...)`), so the
    // generated line says plainly what it is producing.

    /// <summary>
    /// Reads a <typeparamref name="T"/> out of text, e.g. <c>"42"</c> to <c>42</c>.
    ///
    /// A null, empty or whitespace-only string is treated as ABSENCE and returns
    /// <c>default</c> — <c>0</c>, <c>false</c>, <see cref="Guid.Empty"/>. A string that has
    /// content but does not parse THROWS: turning <c>"abc"</c> into <c>0</c> would put bad
    /// data quietly into your model, and finding it later is far more expensive than failing
    /// here.
    ///
    /// <typeparamref name="T"/> is a struct on purpose. Without that constraint a reference
    /// type would satisfy <see cref="IParsable{TSelf}"/> just as well, and <c>default</c>
    /// would quietly become NULL on a destination property declared non-nullable — an
    /// absence that turns into a <see cref="NullReferenceException"/> somewhere else
    /// entirely. Text converts into value types only.
    ///
    /// Use <see cref="ParseOrNull{T}"/> when the destination is nullable and you want
    /// absence to stay absent instead of becoming zero.
    /// </summary>
    /// <param name="value">The text to read.</param>
    /// <param name="mapping">
    /// The property pair being mapped, e.g. <c>"Product.Sku -&gt; ProductDto.Sku"</c>. Filled
    /// in by the generator; it appears in the exception message and nowhere else.
    /// </param>
    /// <exception cref="FormatException">
    /// The text is not empty and does not parse. The original BCL exception — which may be an
    /// <see cref="OverflowException"/> rather than a format problem — is kept as
    /// <see cref="Exception.InnerException"/>.
    /// </exception>
    public static T Parse<T>(string? value, string mapping) where T : struct, IParsable<T>
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        try
        {
            return T.Parse(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value, typeof(T), mapping, ex);
        }
    }

    /// <summary>
    /// <see cref="Parse{T}"/> for a nullable destination: absent text stays absent.
    ///
    /// The difference matters. Mapping a <c>string</c> column onto an <c>int</c> turns an
    /// empty cell into <c>0</c>, which is a real quantity; mapping it onto an <c>int?</c>
    /// leaves it null, which still says "nobody filled this in".
    /// </summary>
    /// <inheritdoc cref="Parse{T}"/>
    public static T? ParseOrNull<T>(string? value, string mapping) where T : struct, IParsable<T>
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return T.Parse(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value, typeof(T), mapping, ex);
        }
    }

    /// <summary>
    /// Reads a single character out of text.
    ///
    /// <see cref="char"/> implements <see cref="IParsable{TSelf}"/> and could have gone
    /// through <see cref="Parse{T}"/>, but it is the ONE type where that method's rule is
    /// wrong. Everywhere else whitespace-only text means "nothing was filled in"; for a
    /// character, <c>" "</c> IS the value, and collapsing it to <c>'\0'</c> would silently
    /// throw the developer's data away. So only null and empty count as absence here.
    ///
    /// Text of more than one character is rejected rather than truncated to its first
    /// character — a two-character value in a one-character column is bad data, not a value
    /// to be trimmed down without telling anybody.
    /// </summary>
    /// <inheritdoc cref="Parse{T}"/>
    public static char ParseChar(string? value, string mapping)
    {
        if (string.IsNullOrEmpty(value))
            return default;

        try
        {
            return char.Parse(value!);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value!, typeof(char), mapping, ex);
        }
    }

    /// <inheritdoc cref="ParseChar"/>
    public static char? ParseCharOrNull(string? value, string mapping)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        try
        {
            return char.Parse(value!);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value!, typeof(char), mapping, ex);
        }
    }

    /// <summary>
    /// Reads a date and time out of text.
    ///
    /// This does NOT go through <see cref="Parse{T}"/>, even though
    /// <see cref="DateTime"/> implements <see cref="IParsable{TSelf}"/>, because the plain
    /// parse converts whatever it reads into local time — so a UTC timestamp written by
    /// <see cref="ToInvariantString(DateTime)"/> would come back shifted by your server's
    /// offset. <see cref="DateTimeStyles.RoundtripKind"/> is what preserves the
    /// <see cref="DateTimeKind"/> that was written.
    /// </summary>
    /// <inheritdoc cref="Parse{T}"/>
    public static DateTime ParseDateTime(string? value, string mapping)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        try
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value, typeof(DateTime), mapping, ex);
        }
    }

    /// <inheritdoc cref="ParseDateTime"/>
    public static DateTime? ParseDateTimeOrNull(string? value, string mapping)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value, typeof(DateTime), mapping, ex);
        }
    }

    // There is deliberately no ParseDateTimeOffset. DateTimeOffset goes through Parse<T> with
    // everything else, because it carries its offset IN THE VALUE — there is no Kind to lose
    // and nothing for DateTimeStyles.RoundtripKind to preserve, so the plain invariant parse
    // already round-trips "+03:00", "Z" and "-05:00" exactly. DateTime is the one that needs
    // its own method: without RoundtripKind, "…T14:30:00Z" comes back as 17:30 LOCAL on a
    // UTC+3 server, and as 14:30 on a UTC one.

    /// <summary>
    /// Reads an enum member out of text, by NAME (<c>"Active"</c>) or by its underlying
    /// number written as text (<c>"1"</c>).
    ///
    /// Enums need their own method because they are the one common case that does not
    /// implement <see cref="IParsable{TSelf}"/> — the parsing lives on
    /// <see cref="Enum"/> instead.
    ///
    /// The name is matched IGNORING CASE, because the text usually came from a database
    /// column or a query string rather than from C#, and rejecting <c>"active"</c> for
    /// <c>Active</c> would be pedantry rather than safety.
    /// </summary>
    /// <inheritdoc cref="Parse{T}"/>
    public static TEnum ParseEnum<TEnum>(string? value, string mapping) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        try
        {
            return Enum.Parse<TEnum>(value, ignoreCase: true);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value, typeof(TEnum), mapping, ex);
        }
    }

    /// <inheritdoc cref="ParseEnum{TEnum}"/>
    public static TEnum? ParseEnumOrNull<TEnum>(string? value, string mapping) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return Enum.Parse<TEnum>(value, ignoreCase: true);
        }
        catch (Exception ex)
        {
            throw ConversionFailed(value, typeof(TEnum), mapping, ex);
        }
    }

    // -----------------------------------------------------------------
    // BETWEEN THE DATE AND TIME TYPES
    // -----------------------------------------------------------------
    //
    // DateOnly, TimeOnly, DateTime and TimeSpan describe overlapping things, and .NET
    // deliberately provides no conversion operators between them — the missing piece has to
    // be supplied by whoever knows what it should be. ShiftMapper supplies the only answer
    // that is not a guess, and refuses the pairs where there is more than one:
    // DateTimeOffset to DateTime is NOT converted, because "drop the offset" and "convert to
    // UTC first" are both defensible and picking either one silently would be wrong half the
    // time. That pair is reported as SM0002 so you can write the line you meant.
    //
    // Each one comes in a nullable twin so that a nullable source stays absent instead of
    // collapsing to DateTime.MinValue.

    /// <summary>A date at midnight, with <see cref="DateTimeKind.Unspecified"/>.</summary>
    public static DateTime ToDateTime(DateOnly value) => value.ToDateTime(TimeOnly.MinValue);

    /// <inheritdoc cref="ToDateTime(DateOnly)"/>
    public static DateTime? ToDateTime(DateOnly? value) => value?.ToDateTime(TimeOnly.MinValue);

    /// <summary>The date part only — the time of day is discarded.</summary>
    public static DateOnly ToDateOnly(DateTime value) => DateOnly.FromDateTime(value);

    /// <inheritdoc cref="ToDateOnly(DateTime)"/>
    public static DateOnly? ToDateOnly(DateTime? value) =>
        value.HasValue ? DateOnly.FromDateTime(value.GetValueOrDefault()) : null;

    /// <summary>The time of day as a duration since midnight.</summary>
    public static TimeSpan ToTimeSpan(TimeOnly value) => value.ToTimeSpan();

    /// <inheritdoc cref="ToTimeSpan(TimeOnly)"/>
    public static TimeSpan? ToTimeSpan(TimeOnly? value) => value?.ToTimeSpan();

    // There is deliberately no ToTimeOnly(TimeSpan). Every time of day is a valid duration,
    // which is why the direction above is safe, but the reverse is not: a TimeSpan can hold a
    // negative value or one of a day or more, both of which are ordinary data that no clock
    // can represent, and TimeOnly.FromTimeSpan throws ArgumentOutOfRangeException on them.
    // Parsing text is the one place ShiftMapper is allowed to fail on data; adding a second
    // one, with a different exception type, for a pair this rare is not worth it. The
    // generator refuses that pair and reports SM0002.

    // -----------------------------------------------------------------
    // COLLECTIONS
    // -----------------------------------------------------------------
    //
    // <c>List&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>,
    // <c>IReadOnlyList&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c> and arrays all describe the same
    // idea in slightly different words, and a DTO rarely spells it the same way its entity
    // does. These three methods are how the generator gets from any one of them to any other.
    //
    // THEY ALWAYS BUILD A NEW COLLECTION — even when the source could simply have been
    // assigned across, as a List can be to an IEnumerable. Two reasons, and the second is the
    // one that matters:
    //
    //   1. INDEPENDENCE. Assigning the entity's List straight onto the DTO does not copy it,
    //      it SHARES it. Add an item to the DTO afterwards and you have quietly added it to
    //      the entity that EF is tracking.
    //   2. AN IEnumerable MAY NOT BE A COLLECTION AT ALL. It can be an unevaluated LINQ query
    //      — which is exactly what an ORM hands you — and assigning that to a DTO means the
    //      DTO holds a live query that runs again every time somebody enumerates it, and
    //      throws once the DbContext behind it is disposed. Materialising here means the DTO
    //      leaves the mapper holding data rather than a promise.
    //
    // A null source stays null rather than becoming an empty collection: the same rule the
    // rest of ShiftMapper follows, where absence is copied rather than invented.

    /// <summary>Copies a sequence into a new array.</summary>
    public static T[]? ToArray<T>(IEnumerable<T>? source) => source?.ToArray();

    /// <summary>Copies a sequence into a new array, converting each element on the way.</summary>
    /// <param name="source">The sequence to copy. A null source stays null.</param>
    /// <param name="convert">
    /// Applied to every element. The generator passes a <c>static</c> lambda, so it is
    /// allocated once for the life of the process rather than once per mapped property.
    /// </param>
    public static TDestination[]? ToArray<TSource, TDestination>(
        IEnumerable<TSource>? source,
        Func<TSource, TDestination> convert) =>
        source is null ? null : ToList(source, convert)!.ToArray();

    /// <summary>Copies a sequence into a new list.</summary>
    public static List<T>? ToList<T>(IEnumerable<T>? source) => source?.ToList();

    /// <summary>Copies a sequence into a new list, converting each element on the way.</summary>
    /// <inheritdoc cref="ToArray{TSource, TDestination}"/>
    public static List<TDestination>? ToList<TSource, TDestination>(
        IEnumerable<TSource>? source,
        Func<TSource, TDestination> convert)
    {
        if (source is null)
            return null;

        // Sized up front when the source can say how many there are, which is the common
        // case — a list, an array, a HashSet. An unevaluated query cannot, and grows instead.
        var result = source is ICollection<TSource> known
            ? new List<TDestination>(known.Count)
            : new List<TDestination>();

        foreach (TSource item in source)
            result.Add(convert(item));

        return result;
    }

    /// <summary>
    /// Copies a sequence into a new set, which DISCARDS DUPLICATES — that is what a set is,
    /// and it is why a destination declared as a <see cref="HashSet{T}"/> can come out shorter
    /// than the list that fed it. The generator reports that as SM0008.
    /// </summary>
    public static HashSet<T>? ToHashSet<T>(IEnumerable<T>? source) => source?.ToHashSet();

    /// <inheritdoc cref="ToHashSet{T}(IEnumerable{T})"/>
    public static HashSet<TDestination>? ToHashSet<TSource, TDestination>(
        IEnumerable<TSource>? source,
        Func<TSource, TDestination> convert) =>
        source is null ? null : ToList(source, convert)!.ToHashSet();

    // -----------------------------------------------------------------
    // DICTIONARIES
    // -----------------------------------------------------------------
    //
    // A dictionary is a collection like any other as far as this class is concerned — it is
    // copied rather than shared, for the two reasons spelled out above the list builders — but
    // it has TWO element types rather than one, so it needs its own pair of methods instead of
    // fitting into ToList.
    //
    // THE SOURCE is any IEnumerable<KeyValuePair<K, V>>, which every dictionary in the BCL is:
    // Dictionary, IDictionary, IReadOnlyDictionary, SortedDictionary, ConcurrentDictionary.
    // THE DESTINATION is always a Dictionary<K, V>, which satisfies IDictionary<K, V> and
    // IReadOnlyDictionary<K, V> as well.
    //
    // DUPLICATE KEYS ARE DISCARDED, last one wins, exactly as ToHashSet discards duplicate
    // values — and for the same reason: converting the keys is what can create a collision that
    // did not exist in the source (two long keys narrowing to one int, "A" and "a" arriving as
    // the same text), and throwing halfway through building a DTO is a worse answer than the one
    // the set builder already gives. The generator reports it as SM0008 whenever the key type is
    // converted, and says nothing when it is not — a real dictionary source cannot collide with
    // itself.

    /// <summary>Copies key/value pairs into a new dictionary.</summary>
    public static Dictionary<TKey, TValue>? ToDictionary<TKey, TValue>(
        IEnumerable<KeyValuePair<TKey, TValue>>? source)
        where TKey : notnull
    {
        if (source is null)
            return null;

        // Sized up front when the source can say how many there are, which is the common case -
        // every dictionary in the BCL is an ICollection of its own pairs.
        var result = source is ICollection<KeyValuePair<TKey, TValue>> known
            ? new Dictionary<TKey, TValue>(known.Count)
            : new Dictionary<TKey, TValue>();

        foreach (KeyValuePair<TKey, TValue> pair in source)
            result[pair.Key] = pair.Value;

        return result;
    }

    /// <summary>
    /// Copies key/value pairs into a new dictionary, converting the keys AND the values on the
    /// way.
    /// </summary>
    /// <param name="source">The pairs to copy. A null source stays null.</param>
    /// <param name="key">
    /// Applied to every key. The generator passes a <c>static</c> lambda — often the identity
    /// <c>k =&gt; k</c>, which is there so the destination's key type is settled by the compiler
    /// rather than by type inference.
    /// </param>
    /// <param name="value">Applied to every value, on the same terms.</param>
    public static Dictionary<TDestinationKey, TDestinationValue>? ToDictionary<TSourceKey, TSourceValue, TDestinationKey, TDestinationValue>(
        IEnumerable<KeyValuePair<TSourceKey, TSourceValue>>? source,
        Func<TSourceKey, TDestinationKey> key,
        Func<TSourceValue, TDestinationValue> value)
        where TDestinationKey : notnull
    {
        if (source is null)
            return null;

        var result = source is ICollection<KeyValuePair<TSourceKey, TSourceValue>> known
            ? new Dictionary<TDestinationKey, TDestinationValue>(known.Count)
            : new Dictionary<TDestinationKey, TDestinationValue>();

        foreach (KeyValuePair<TSourceKey, TSourceValue> pair in source)
            result[key(pair.Key)] = value(pair.Value);

        return result;
    }

    // -----------------------------------------------------------------
    // THE SAME BUILDERS, WITH A NULL SOURCE TREATED AS AN EMPTY ONE
    // -----------------------------------------------------------------
    //
    // Every builder above copies a null source to a null destination. These are their twins for
    // the OTHER answer to that question, and which family the generated code calls is decided
    // per map by MapOptions.AllowNullCollections — which is false by default, so THESE are the
    // ones a map normally uses.
    //
    // Two families rather than a bool parameter, because the generated line then SAYS which
    // policy is in force. `ValueConverter.ToListOrEmpty(source.Tags)` needs no cross-reference
    // to read; `ValueConverter.ToList(source.Tags, false)` needs the signature open beside it.
    //
    // The return types are non-nullable, which is half the point of them: a DTO built this way
    // has no collection property that can be null, so nothing downstream has to test for one.

    /// <summary>Copies a sequence into a new array; a null source becomes an empty array.</summary>
    public static T[] ToArrayOrEmpty<T>(IEnumerable<T>? source) =>
        source is null ? Array.Empty<T>() : source.ToArray();

    /// <inheritdoc cref="ToArrayOrEmpty{T}(IEnumerable{T})"/>
    /// <inheritdoc cref="ToArray{TSource, TDestination}"/>
    public static TDestination[] ToArrayOrEmpty<TSource, TDestination>(
        IEnumerable<TSource>? source,
        Func<TSource, TDestination> convert) =>
        source is null ? Array.Empty<TDestination>() : ToList(source, convert)!.ToArray();

    /// <summary>Copies a sequence into a new list; a null source becomes an empty list.</summary>
    public static List<T> ToListOrEmpty<T>(IEnumerable<T>? source) =>
        source is null ? new List<T>() : source.ToList();

    /// <inheritdoc cref="ToListOrEmpty{T}(IEnumerable{T})"/>
    /// <inheritdoc cref="ToArray{TSource, TDestination}"/>
    public static List<TDestination> ToListOrEmpty<TSource, TDestination>(
        IEnumerable<TSource>? source,
        Func<TSource, TDestination> convert) =>
        source is null ? new List<TDestination>() : ToList(source, convert)!;

    /// <summary>Copies a sequence into a new set; a null source becomes an empty set.</summary>
    /// <inheritdoc cref="ToHashSet{T}(IEnumerable{T})"/>
    public static HashSet<T> ToHashSetOrEmpty<T>(IEnumerable<T>? source) =>
        source is null ? new HashSet<T>() : source.ToHashSet();

    /// <inheritdoc cref="ToHashSetOrEmpty{T}(IEnumerable{T})"/>
    /// <inheritdoc cref="ToArray{TSource, TDestination}"/>
    public static HashSet<TDestination> ToHashSetOrEmpty<TSource, TDestination>(
        IEnumerable<TSource>? source,
        Func<TSource, TDestination> convert) =>
        source is null ? new HashSet<TDestination>() : ToList(source, convert)!.ToHashSet();

    /// <summary>
    /// Copies key/value pairs into a new dictionary; a null source becomes an empty dictionary.
    /// </summary>
    /// <inheritdoc cref="ToDictionary{TKey, TValue}(IEnumerable{KeyValuePair{TKey, TValue}})"/>
    public static Dictionary<TKey, TValue> ToDictionaryOrEmpty<TKey, TValue>(
        IEnumerable<KeyValuePair<TKey, TValue>>? source)
        where TKey : notnull =>
        source is null ? new Dictionary<TKey, TValue>() : ToDictionary(source)!;

    /// <inheritdoc cref="ToDictionaryOrEmpty{TKey, TValue}(IEnumerable{KeyValuePair{TKey, TValue}})"/>
    /// <inheritdoc cref="ToDictionary{TSourceKey, TSourceValue, TDestinationKey, TDestinationValue}"/>
    public static Dictionary<TDestinationKey, TDestinationValue> ToDictionaryOrEmpty<TSourceKey, TSourceValue, TDestinationKey, TDestinationValue>(
        IEnumerable<KeyValuePair<TSourceKey, TSourceValue>>? source,
        Func<TSourceKey, TDestinationKey> key,
        Func<TSourceValue, TDestinationValue> value)
        where TDestinationKey : notnull =>
        source is null
            ? new Dictionary<TDestinationKey, TDestinationValue>()
            : ToDictionary(source, key, value)!;

    /// <summary>
    /// The one message every failed conversion produces.
    ///
    /// It is a <see cref="FormatException"/> whatever actually went wrong — a malformed
    /// number, a value too large for the type, a name that is not a member of the enum — so
    /// there is a single type to catch around a mapping call. The real exception is kept as
    /// the inner one for anybody who needs to tell those cases apart.
    ///
    /// The offending text is quoted and TRUNCATED. An exception message can end up in a log
    /// aggregator, and a runaway 2 MB column value in a message string helps nobody.
    /// </summary>
    private static FormatException ConversionFailed(string value, Type target, string mapping, Exception inner) =>
        new($"ShiftMapper: cannot convert \"{Truncate(value)}\" to {target.Name} while mapping {mapping}. " +
            $"See the inner {inner.GetType().Name} for the underlying reason. Either correct the source " +
            "data, or give the destination property a type ShiftMapper does not have to parse into.",
            inner);

    /// <summary>
    /// Caps quoted values in exception messages at a length a human can read.
    ///
    /// The surrogate check is not pedantry: an emoji or a CJK extension character is TWO
    /// chars, and cutting between them leaves a lone surrogate — an ill-formed string that
    /// some log pipelines reject outright and others render as a replacement glyph. Backing
    /// off by one character costs nothing and keeps the message well-formed text.
    /// </summary>
    private static string Truncate(string value)
    {
        const int Limit = 60;
        const int Kept = 57;

        if (value.Length <= Limit)
            return value;

        int end = char.IsHighSurrogate(value[Kept - 1]) ? Kept - 1 : Kept;

        return value.Substring(0, end) + "...";
    }
}
