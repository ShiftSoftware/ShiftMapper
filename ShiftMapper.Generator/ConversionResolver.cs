using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ShiftMapper.Generator;

/// <summary>
/// Decides whether a source property's type can be turned into a destination property's
/// type, and if so writes down the exact C# that does it.
///
/// This is the whole of ShiftMapper's answer to "the names line up but the types do not".
/// Before it existed the rule was simply <c>types must be identical</c>; now a
/// <c>decimal</c> can fill a <c>string</c>, a <c>string</c> can fill an <c>int</c>, and a
/// <c>Product</c> still cannot fill a <c>ProductDto</c> — with a warning saying so.
///
/// <para><b>WHY IT IS ORDERED THE WAY IT IS</b></para>
///
/// The steps in <see cref="Resolve"/> are tried in a fixed order, and in two places the order
/// is load bearing rather than incidental:
///
/// <list type="bullet">
/// <item><description>
/// The REFUSALS come before everything, so a pair we have decided not to convert cannot be
/// picked up again by a later step that only looks at part of the picture.
/// </description></item>
/// <item><description>
/// Unwrapping a nullable comes BEFORE the plain cast, so <c>int?</c> to <c>int</c> emits
/// <c>GetValueOrDefault()</c> instead of <c>(int)</c> — the cast is a perfectly good C#
/// conversion that throws on every null, which is the last thing a mapper should do.
/// </description></item>
/// </list>
///
/// <para><b>WHAT IT DELIBERATELY REFUSES</b></para>
///
/// Not everything C# will let you write is a mapping, and the line is drawn at ONE rule:
/// <b>a conversion's answer must come from the two types alone.</b> That rules out three
/// families —
///
/// <list type="bullet">
/// <item><description>
/// conversions that just move a REFERENCE around — a downcast (<c>object</c> to
/// <c>Product</c>) or an unboxing, which throw when the value is not really that type; and
/// an up-cast or boxing (<c>Product</c> to <c>object</c>, <c>List&lt;T&gt;</c> to
/// <c>IEnumerable&lt;T&gt;</c>), which compile and hand the DTO a reference to the very entity
/// it was meant to be a copy of. These are the nested-object and collection cases, which
/// ShiftMapper does not map yet and reports as SM0002;
/// </description></item>
/// <item><description>
/// conversions whose result depends on something OUTSIDE the two types — the machine's time
/// zone, or the numeric order somebody happened to declare an enum's members in. See
/// <see cref="RefusedNote"/> for the four named pairs;
/// </description></item>
/// <item><description>
/// user-defined EXPLICIT operators, because <c>explicit</c> is the author's own way of saying
/// this needs a decision. Their <c>implicit</c> operators are honoured.
/// </description></item>
/// </list>
///
/// <para><b>NO SYMBOLS COME OUT OF HERE</b></para>
///
/// Everything this class returns is plain strings and enums, because the result is stored in
/// a <see cref="MapModel"/> that the compiler caches between keystrokes. Holding a Roslyn
/// symbol there would pin an entire compilation in memory.
/// </summary>
internal static class ConversionResolver
{
    /// <summary>How the generated code spells the runtime helper.</summary>
    private const string ConverterType = "global::ShiftMapper.ValueConverter";

    /// <summary>How the compiler spells it when we ask the USER's compilation whether it is there.</summary>
    private const string ConverterMetadataName = "ShiftMapper.ValueConverter";

    /// <summary>The const on that class that says which conversions it contains.</summary>
    private const string ApiVersionFieldName = "ConverterApiVersion";

    /// <summary>
    /// The version this generator writes calls for. Raise it in BOTH places whenever a new
    /// method is added to <c>ValueConverter</c> and the generator starts calling it, so an
    /// older runtime degrades to SM0002 instead of failing to compile.
    /// </summary>
    private const int RequiredApiVersion = 1;

    /// <summary>
    /// Works out how to get a <paramref name="sourceType"/> value into a
    /// <paramref name="destinationType"/> property.
    /// Returns null when there is no conversion ShiftMapper is willing to make.
    /// </summary>
    /// <param name="mapping">
    /// The property pair in the form <c>"Brand.Code -&gt; BrandDto.Code"</c>. It is baked into
    /// the generated call as a string literal so a conversion that fails at runtime can name
    /// the two properties it was working on. Only the text conversions use it.
    /// </param>
    public static ValueConversion? Resolve(
        Compilation compilation,
        ITypeSymbol sourceType,
        ITypeSymbol destinationType,
        string mapping)
    {
        // 1. THE SAME TYPE. Note this comparison ignores nullable reference ANNOTATIONS, so
        //    `string?` to `string` is still a plain copy — exactly as it was before
        //    conversions existed, and the reason the generated file disables CS8601.
        if (SymbolEqualityComparer.Default.Equals(sourceType, destinationType))
            return ValueConversion.Direct;

        // 2. THE TYPES WE WILL NOT REASON ABOUT AT ALL. `dynamic` has to go first: the
        //    compiler reports an implicit conversion to it from EVERYTHING, so leaving it in
        //    would let a single `dynamic` property swallow every source property and silence
        //    the SM0002 that should have been reported. Pointers and unresolved types are
        //    here for the ordinary reason — there is nothing sensible to emit.
        if (IsUnreasonable(sourceType) || IsUnreasonable(destinationType))
            return null;

        ITypeSymbol sourceCore = Unwrap(sourceType, out bool sourceIsNullableValue);
        ITypeSymbol destinationCore = Unwrap(destinationType, out bool destinationIsNullableValue);

        // 3. THE PAIRS WE REFUSE ON PURPOSE, even though C# would convert them.
        //    Each one is a conversion whose ANSWER IS NOT DETERMINED BY THE TWO TYPES ALONE,
        //    which is the one thing a compile-time mapper must never emit. See RefusedNote.
        if (RefusedNote(compilation, sourceCore, destinationCore) is not null)
            return null;

        // 4. C# ALREADY DOES IT IMPLICITLY — int to long, int to int?, or an
        //    `implicit operator` somebody wrote by hand. An implicit conversion is the
        //    language's own promise that nothing goes wrong, so we assign and say nothing.
        //
        //    This includes the few implicit conversions that DO lose precision (long to
        //    double, int to float). Flagging those would mean warning about code the
        //    developer could have written by hand without a squeak from the compiler, so we
        //    follow the language rather than second-guess it.
        //
        //    But NOT the implicit conversions that merely move a REFERENCE around —
        //    `Product` to `object`, `Product` to a base class, `List<T>` to `IEnumerable<T>`.
        //    Those compile and do nothing useful: the destination ends up pointing at the
        //    very entity the DTO was supposed to be a copy OF, complete with its lazy-loading
        //    proxy and its cycles. They are also exactly the nested-object and collection
        //    cases SM0002 exists to report, so accepting them here would quietly contradict
        //    the promise that ShiftMapper does not map them.
        //
        //    ClassifyConversion is declared on CSharpCompilation rather than on the base
        //    Compilation, and a `default` conversion is simply one that does not Exist — so
        //    a non-C# compilation, which a C# generator never sees anyway, falls through
        //    harmlessly instead of needing a special case.
        Conversion conversion = compilation is CSharpCompilation csharp
            ? csharp.ClassifyConversion(sourceType, destinationType)
            : default;

        if (conversion.Exists && conversion.IsImplicit && !conversion.IsReference && !conversion.IsBoxing)
            return ValueConversion.Direct;

        // 5. TO TEXT. Anything that can format itself can fill a string property, and a
        //    nullable one needs no special handling on the way: ToInvariantString has its own
        //    nullable overloads, so an absent value stays absent instead of becoming "0".
        if (destinationCore.SpecialType == SpecialType.System_String)
        {
            return CanFormat(compilation, sourceCore) && HasConverter(compilation)
                ? ValueConversion.Call($"{ConverterType}.ToInvariantString({{0}})", ConversionRisk.None)
                : null;
        }

        // 6. FROM TEXT. The mirror of step 5, and the one direction that can fail on data
        //    rather than on types — hence ConversionRisk.Parsed, which becomes SM0009.
        if (sourceCore.SpecialType == SpecialType.System_String)
        {
            return HasConverter(compilation)
                ? ResolveParse(compilation, destinationCore, destinationIsNullableValue, mapping)
                : null;
        }

        // 7. A NULLABLE SOURCE FILLING A NON-NULLABLE DESTINATION. There is no value to
        //    copy when the source is null, so the destination gets its default. Working out
        //    the rest by recursion means every conversion below is written once and works
        //    lifted for free: `long?` to `int` becomes
        //    `unchecked((int)source.X.GetValueOrDefault())`.
        if (sourceIsNullableValue && !destinationIsNullableValue && destinationType.IsValueType)
        {
            ValueConversion? inner = Resolve(compilation, sourceCore, destinationType, mapping);
            if (inner is null)
                return null;

            const string NullNote = "a null source becomes the destination type's default value";

            return new ValueConversion(
                inner.Apply("{0}.GetValueOrDefault()"),
                ConversionRisk.Lossy,
                inner.Note is null ? NullNote : $"{NullNote}, and {inner.Note}");
        }

        // 8. A CAST, between numbers and enums. Reference downcasts, unboxing and
        //    user-defined explicit operators are all excluded in IsCastable.
        if (IsCastable(conversion))
            return Cast(destinationType, sourceCore, destinationCore);

        // 9. BETWEEN THE DATE AND TIME TYPES, which C# gives no conversions of its own.
        return HasConverter(compilation) ? ResolveDateAndTime(sourceCore, destinationCore) : null;
    }

    /// <summary>
    /// Writes the cast for step 8 — and wraps it in <c>unchecked</c> when that changes what
    /// it MEANS.
    ///
    /// This is not decoration. <c>(int)source.Big</c> truncates in an ordinary project and
    /// throws <c>OverflowException</c> in one built with
    /// <c>&lt;CheckForOverflowUnderflow&gt;true&lt;/CheckForOverflowUnderflow&gt;</c> — the
    /// same generated line, two different behaviours, decided by a setting in a csproj the
    /// generated file knows nothing about. Pinning it makes every ShiftMapper map behave the
    /// same everywhere, which is what lets SM0008 describe the behaviour at all.
    ///
    /// What that behaviour IS depends on the source, which is why
    /// <see cref="DescribeCast"/> splits the cases: whole number to whole number wraps
    /// round, while floating point to whole number is unspecified — <c>(int)1e20</c> lands on
    /// <c>int.MaxValue</c>, <c>(byte)300.0</c> lands on <c>0</c>, and NaN lands on zero.
    ///
    /// It is written ONLY where it has an effect. <c>unchecked((int)someDecimal)</c> still
    /// throws — the decimal conversions ignore the checked context entirely — and writing it
    /// there would promise a truncation that never happens.
    /// </summary>
    private static ValueConversion Cast(
        ITypeSymbol destinationType,
        ITypeSymbol sourceCore,
        ITypeSymbol destinationCore)
    {
        string destinationName = destinationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        bool pinChecking = IsIntegerLike(destinationCore) && sourceCore.SpecialType != SpecialType.System_Decimal;

        string template = pinChecking
            ? $"unchecked(({destinationName}){{0}})"
            : $"({destinationName}){{0}}";

        return new ValueConversion(template, ConversionRisk.Lossy, DescribeCast(sourceCore, destinationCore));
    }

    /// <summary>
    /// Step 4 — picks the reader for a destination that is being filled from text.
    ///
    /// Most types are covered by one generic method, because <c>IParsable&lt;T&gt;</c> is
    /// .NET's own answer to "this type can read itself from a string" and nearly every type
    /// worth converting to implements it. Two types are routed elsewhere, each for its own
    /// reason: <c>DateTime</c>, because the plain parse rewrites what it reads into LOCAL
    /// time (a UTC timestamp comes back three hours out on a UTC+3 server), and <c>char</c>,
    /// because it is the one type for which whitespace is a value rather than an absence.
    /// Enums are routed elsewhere because they do not implement the interface at all.
    ///
    /// <c>DateTimeOffset</c> is NOT one of the exceptions, tempting though the symmetry is:
    /// it carries its offset in the value, so the plain invariant parse already round-trips.
    /// </summary>
    private static ValueConversion? ResolveParse(
        Compilation compilation,
        ITypeSymbol destinationCore,
        bool destinationIsNullable,
        string mapping)
    {
        string suffix = destinationIsNullable ? "OrNull" : string.Empty;
        string literal = $"\"{mapping}\"";
        string core = destinationCore.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        string? call = destinationCore switch
        {
            { TypeKind: TypeKind.Enum } => $"{ConverterType}.ParseEnum{suffix}<{core}>({{0}}, {literal})",
            { SpecialType: SpecialType.System_Char } => $"{ConverterType}.ParseChar{suffix}({{0}}, {literal})",
            { SpecialType: SpecialType.System_DateTime } => $"{ConverterType}.ParseDateTime{suffix}({{0}}, {literal})",
            _ when IsParsable(compilation, destinationCore) =>
                $"{ConverterType}.Parse{suffix}<{core}>({{0}}, {literal})",
            _ => null,
        };

        return call is null ? null : ValueConversion.Call(call, ConversionRisk.Parsed);
    }

    /// <summary>
    /// Step 9 — the pairs among <c>DateOnly</c>, <c>TimeOnly</c>, <c>DateTime</c> and
    /// <c>TimeSpan</c> that have exactly one sensible answer. (<c>TimeSpan</c> to
    /// <c>TimeOnly</c> is NOT one of them; it is refused in <see cref="RefusedNote"/>.)
    ///
    /// Nothing here needs a null check of its own. Each helper comes in a nullable twin, and
    /// the ARGUMENT picks which one runs — pass a <c>DateOnly?</c> and absence survives the
    /// conversion. A nullable source with a NON-nullable destination never reaches here at
    /// all: step 5 has already unwrapped it.
    /// </summary>
    private static ValueConversion? ResolveDateAndTime(ITypeSymbol sourceCore, ITypeSymbol destinationCore)
    {
        string? method = (Name(sourceCore), Name(destinationCore)) switch
        {
            ("System.DateOnly", "System.DateTime") => "ToDateTime",
            ("System.DateTime", "System.DateOnly") => "ToDateOnly",
            ("System.TimeOnly", "System.TimeSpan") => "ToTimeSpan",
            _ => null,
        };

        if (method is null)
            return null;

        // Only the one that throws information away is worth a word. Turning a date into a
        // midnight timestamp, or a clock time into a duration, keeps everything it had.
        string? note = method == "ToDateOnly" ? "the time of day is discarded" : null;

        return new ValueConversion(
            $"{ConverterType}.{method}({{0}})",
            note is null ? ConversionRisk.None : ConversionRisk.Lossy,
            note);
    }

    /// <summary>
    /// THE PAIRS SHIFTMAPPER REFUSES ON PURPOSE — the ones C# is perfectly happy to convert
    /// and we still will not, each for the same underlying reason.
    ///
    /// THE RULE: <b>a conversion's answer must be determined by the two types alone.</b>
    /// It is the same rule that pins every text conversion to the invariant culture instead
    /// of the machine's, applied to conversions that are not about text:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <c>DateTime</c> to <c>DateTimeOffset</c> — implicit in C#, and it attaches
    /// <c>TimeZoneInfo.Local</c> to any DateTime whose Kind is Unspecified, which is what
    /// every DateTime read out of a database is. The map would then mean something different
    /// on the developer's laptop than on the server. (The reverse is refused too, for the
    /// plainer reason that "drop the offset" and "convert to UTC first" are equally
    /// defensible and only the developer knows which was meant.)
    /// </description></item>
    /// <item><description>
    /// One enum to a DIFFERENT enum — a cast maps them by NUMBER, not by name. Two enums
    /// that look like mirror images today start writing the wrong value into the database
    /// the day somebody inserts a member into the middle of one of them, with no compile
    /// error and no exception to notice. Mapping by NAME would be right, and is a feature
    /// with its own failure modes rather than a line in this table.
    /// </description></item>
    /// <item><description>
    /// <c>TimeSpan</c> to <c>TimeOnly</c> — a duration of a day or more, or a negative one, is
    /// ordinary data that no clock can hold, and the conversion throws
    /// <c>ArgumentOutOfRangeException</c> on it. That is a runtime failure outside the one
    /// documented place ShiftMapper is allowed to fail, which is parsing text.
    /// (<c>TimeOnly</c> to <c>TimeSpan</c> is kept: every time of day is a valid duration.)
    /// </description></item>
    /// </list>
    ///
    /// Returns the reason, or null when the pair is not refused. The reason is not reported
    /// anywhere yet — SM0002 names the two types and its description explains the rules — but
    /// keeping it as text rather than a bool is what stops this list turning back into a pile
    /// of unexplained conditions.
    /// </summary>
    private static string? RefusedNote(Compilation compilation, ITypeSymbol sourceCore, ITypeSymbol destinationCore)
    {
        if (sourceCore.SpecialType == SpecialType.System_DateTime && IsNamed(destinationCore, "System.DateTimeOffset"))
            return "the offset would come from the machine's time zone rather than from the data";

        if (IsNamed(sourceCore, "System.DateTimeOffset") && destinationCore.SpecialType == SpecialType.System_DateTime)
            return "dropping the offset and converting to UTC are both defensible, so neither is chosen";

        if (sourceCore.TypeKind == TypeKind.Enum
            && destinationCore.TypeKind == TypeKind.Enum
            && !SymbolEqualityComparer.Default.Equals(sourceCore, destinationCore))
        {
            return "one enum converts to another by number, so reordering either one would silently change the meaning";
        }

        if (IsNamed(sourceCore, "System.TimeSpan") && IsNamed(destinationCore, "System.TimeOnly"))
            return "a duration of a day or more, or a negative one, has no time of day";

        _ = compilation;
        return null;
    }

    /// <summary>
    /// Whether an explicit conversion is one we are willing to write as a cast.
    ///
    /// Numbers and enums are safe to write: they always produce a value, even if it is a
    /// truncated one, and that truncation is reported as SM0008.
    ///
    /// Three kinds are refused, and SM0002 reports them instead:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// REFERENCE conversions and UNBOXING. <c>(Product)source.Thing</c> compiles and then
    /// throws on any value that is not already a Product — a runtime coin toss, not a mapping.
    /// </description></item>
    /// <item><description>
    /// USER-DEFINED EXPLICIT operators. The one written by hand is <c>implicit</c> when the
    /// author means "this is always fine" and <c>explicit</c> when they mean "stop and think
    /// before doing this". Having a generator do it silently on their behalf inverts what the
    /// keyword says. Implicit operators ARE honoured, for the mirror-image reason.
    /// </description></item>
    /// </list>
    /// </summary>
    private static bool IsCastable(Conversion conversion)
    {
        if (!conversion.Exists || !conversion.IsExplicit)
            return false;

        if (conversion.IsReference || conversion.IsUnboxing || conversion.IsUserDefined)
            return false;

        return conversion.IsNumeric || conversion.IsEnumeration || conversion.IsNullable;
    }

    /// <summary>
    /// Wording for the SM0008 message, so it explains THIS cast rather than casts in general.
    /// The cases are ordered by which fact is the most surprising, not by type.
    /// </summary>
    private static string DescribeCast(ITypeSymbol sourceCore, ITypeSymbol destinationCore)
    {
        if (sourceCore.TypeKind == TypeKind.Enum || destinationCore.TypeKind == TypeKind.Enum)
            return "an enum and a number convert by value, and nothing checks that the result is a member the enum actually declares";

        // The decimal conversions are the ones the checked context cannot reach, so they are
        // the ones that really do throw rather than wrap. See Cast().
        if (sourceCore.SpecialType == SpecialType.System_Decimal && IsIntegerLike(destinationCore))
            return "a value too large for the destination throws OverflowException";

        if (destinationCore.SpecialType == SpecialType.System_Decimal)
            return "precision is lost, and a value outside decimal's range throws OverflowException";

        // Floating point to whole number is NOT the same story as whole number to whole
        // number, and saying so matters. `unchecked((int)1e20)` yields int.MaxValue,
        // `unchecked((byte)300.0)` yields 0, and NaN yields 0 — the language calls the result
        // unspecified, which is the honest word for it. Only integer-to-integer wraps.
        if (IsFloatingPoint(sourceCore) && IsIntegerLike(destinationCore))
            return "a value outside the destination type's range gives an unspecified result rather than an error, and NaN becomes zero";

        if (IsIntegerLike(destinationCore))
            return "a value outside the destination type's range wraps round rather than being rejected";

        return "precision is lost";
    }

    /// <summary>
    /// Whole-number-shaped types — the ones whose casts the <c>checked</c> context governs,
    /// and so the ones <see cref="Cast"/> has to pin with <c>unchecked</c>. An enum counts:
    /// underneath it is one of these.
    /// </summary>
    /// <summary>
    /// The types whose casts to a whole number do NOT wrap. Kept separate from
    /// <see cref="IsIntegerLike"/> because <see cref="DescribeCast"/> has to tell the two
    /// stories apart, and telling a developer that a double "wraps round" would send them
    /// looking for the wrong bug.
    /// </summary>
    private static bool IsFloatingPoint(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_Single or SpecialType.System_Double;

    private static bool IsIntegerLike(ITypeSymbol type) =>
        type.TypeKind == TypeKind.Enum
        || type.SpecialType is SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Char
            or SpecialType.System_IntPtr
            or SpecialType.System_UIntPtr;

    /// <summary>
    /// Types there is no point reasoning about. <c>dynamic</c> is the dangerous one — the
    /// compiler reports an implicit conversion to it from every type in existence — and the
    /// rest are simply things no mapping can be written for.
    /// </summary>
    private static bool IsUnreasonable(ITypeSymbol type) =>
        type.TypeKind is TypeKind.Dynamic
            or TypeKind.Pointer
            or TypeKind.FunctionPointer
            or TypeKind.Error;

    /// <summary>
    /// Whether the ShiftMapper runtime this project references is new enough to contain the
    /// conversions we would emit calls to.
    ///
    /// The generator and the runtime library are two SEPARATE references — the sample wires
    /// them up as two independent ProjectReferences, and as NuGet packages nothing forces
    /// their versions to agree. An old <c>ShiftMapper.dll</c> underneath a new generator would
    /// otherwise produce CS0117 inside a generated file the developer cannot edit, which is
    /// the worst possible way to learn about a version mismatch.
    ///
    /// So the conversions that need the helper simply do not happen, and the property is
    /// reported as SM0002 exactly as it would have been before this feature existed. The
    /// conversions that are plain C# — widening, casts — keep working, because they call
    /// nothing. This is the same defence the generator already mounts around
    /// <c>MapExpression</c> for <c>ReverseMap</c>.
    /// </summary>
    private static bool HasConverter(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName(ConverterMetadataName) is not INamedTypeSymbol converter)
            return false;

        foreach (ISymbol member in converter.GetMembers(ApiVersionFieldName))
        {
            if (member is IFieldSymbol { HasConstantValue: true, ConstantValue: int version })
                return version >= RequiredApiVersion;
        }

        return false;
    }

    /// <summary>
    /// Whether a value of this type can be written as text.
    ///
    /// <c>IFormattable</c> covers every numeric type, every enum, <c>char</c>, <c>Guid</c>,
    /// the date and time types, and any STRUCT of your own that implements it. <c>bool</c> is
    /// named explicitly because it is the one common type that does not implement it;
    /// <c>char</c> is named alongside it only so that this test does not depend on char's
    /// numeric interfaces, which is where it picks IFormattable up.
    ///
    /// Everything else is refused, which is the important half, and it is refused twice over:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// Without the IFormattable test a destination <c>string</c> would swallow ANY source
    /// property by calling <c>ToString()</c> on it, and
    /// <c>"ShiftMapper.Sample.Entities.Product"</c> is not a mapping anybody asked for.
    /// </description></item>
    /// <item><description>
    /// Without the VALUE TYPE test, a reference type implementing IFormattable would convert
    /// too — and its mirror image, text back into that reference type, is where the real harm
    /// is: absent text has to become "no value", and for a reference type "no value" is null,
    /// landing on a property the developer declared non-nullable. Text converts to and from
    /// value types only, which is also exactly the "simple types" this feature is about.
    /// </description></item>
    /// </list>
    /// </summary>
    private static bool CanFormat(Compilation compilation, ITypeSymbol type) =>
        type.IsValueType
        && (type.SpecialType == SpecialType.System_Boolean
            || type.SpecialType == SpecialType.System_Char
            || type.TypeKind == TypeKind.Enum
            || Implements(type, compilation.GetTypeByMetadataName("System.IFormattable")));

    /// <summary>
    /// Whether a value of this type can be read back from text, i.e. it is a value type
    /// implementing <c>IParsable&lt;itself&gt;</c>. Asking the type system rather than listing
    /// the types means your own <c>IParsable</c> structs are converted too, with no list to
    /// maintain.
    ///
    /// The value-type half is not incidental. <c>System.Net.IPAddress</c> implements
    /// <c>IParsable&lt;IPAddress&gt;</c>, so without it a <c>string</c> would map onto a
    /// non-nullable <c>IPAddress</c> property — and an empty source column would fill that
    /// property with NULL, because null is the only "absent" a reference type has. The
    /// resulting NullReferenceException would surface far away from the map that caused it.
    /// </summary>
    private static bool IsParsable(Compilation compilation, ITypeSymbol type)
    {
        if (!type.IsValueType)
            return false;

        if (compilation.GetTypeByMetadataName("System.IParsable`1") is not INamedTypeSymbol parsable)
            return false;

        INamedTypeSymbol closed = parsable.Construct(type);
        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, closed));
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol? @interface) =>
        @interface is not null
        && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, @interface));

    /// <summary>
    /// <c>int?</c> becomes <c>int</c>; everything else is returned untouched. Working with
    /// the type INSIDE the nullable is what lets one rule cover both <c>int</c> and
    /// <c>int?</c> instead of every rule being written twice.
    /// </summary>
    private static ITypeSymbol Unwrap(ITypeSymbol type, out bool wasNullableValueType)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments.Length == 1)
        {
            wasNullableValueType = true;
            return nullable.TypeArguments[0];
        }

        wasNullableValueType = false;
        return type;
    }

    private static string Name(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
            SymbolDisplayGlobalNamespaceStyle.Omitted));

    private static bool IsNamed(ITypeSymbol type, string fullName) =>
        Name(type) == fullName;
}

/// <summary>How much a developer should know about a conversion that DID succeed.</summary>
internal enum ConversionRisk
{
    /// <summary>Nothing to report: the value survives the trip unchanged.</summary>
    None,

    /// <summary>Something can be lost or refused on the way — reported as SM0008.</summary>
    Lossy,

    /// <summary>Text is read at runtime, so bad data throws — reported as SM0009.</summary>
    Parsed,
}

/// <summary>
/// One resolved conversion: the C# that performs it, plus what the developer should be told.
///
/// <see cref="Template"/> is a snippet with <c>{0}</c> standing in for the source expression
/// (<c>source.Price</c>). It is deliberately a string rather than anything cleverer, so it
/// can sit inside a cached <see cref="MapModel"/> without dragging a Roslyn symbol along, and
/// so the emitter never has to know what kind of conversion it is writing.
///
/// Every template mentions <c>{0}</c> exactly ONCE. That is a rule, not a coincidence: a
/// template using it twice would read the source property twice per mapped value.
/// </summary>
internal sealed class ValueConversion
{
    /// <summary>Assign the value across unchanged — no conversion code at all.</summary>
    public static readonly ValueConversion Direct = new(template: null, ConversionRisk.None, note: null);

    public ValueConversion(string? template, ConversionRisk risk, string? note)
    {
        Template = template;
        Risk = risk;
        Note = note;
    }

    /// <summary>A conversion that needs code but has nothing extra to explain.</summary>
    public static ValueConversion Call(string template, ConversionRisk risk) => new(template, risk, note: null);

    /// <summary>The C# to emit, with <c>{0}</c> for the source expression. Null means a plain copy.</summary>
    public string? Template { get; }

    public ConversionRisk Risk { get; }

    /// <summary>Why this conversion can lose or refuse a value — the tail of the SM0008 message.</summary>
    public string? Note { get; }

    /// <summary>
    /// Nests this conversion around another snippet, used when a nullable source is unwrapped
    /// first: the inner <c>{0}.GetValueOrDefault()</c> becomes the input to this conversion.
    /// </summary>
    public string Apply(string inner) =>
        Template is null ? inner : Template.Replace("{0}", inner);
}
