using System.Collections.Generic;
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
/// <c>decimal</c> can fill a <c>string</c>, a <c>string</c> can fill an <c>int</c>, a
/// <c>List&lt;int&gt;</c> can fill an <c>IReadOnlyList&lt;string&gt;</c>, and a
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
/// an up-cast or boxing (<c>Product</c> to <c>object</c>), which compile and hand the DTO a
/// reference to the very entity it was meant to be a copy of. These are the nested-object
/// cases, which ShiftMapper does not map yet and reports as SM0002. Note that a
/// <c>List&lt;int&gt;</c> to <c>IEnumerable&lt;int&gt;</c> is NOT among them: that pair is an
/// up-cast too, but step 2 catches it first and COPIES rather than shares;
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

    /// <summary>Where the LINQ operators used by the query spelling of a conversion live.</summary>
    private const string LinqType = "global::System.Linq.Enumerable";

    /// <summary>How the compiler spells it when we ask the USER's compilation whether it is there.</summary>
    private const string ConverterMetadataName = "ShiftMapper.ValueConverter";

    /// <summary>The const on that class that says which conversions it contains.</summary>
    private const string ApiVersionFieldName = "ConverterApiVersion";

    /// <summary>
    /// The <c>ValueConverter</c> version each family of conversions needs. Raise the const on
    /// <c>ValueConverter</c> and add a number here whenever a new method is added and the
    /// generator starts calling it, so a project sitting on an older ShiftMapper runtime loses
    /// only the conversions that runtime cannot serve — and gets SM0002 for them — instead of
    /// failing to compile.
    /// </summary>
    private const int ScalarConversionApi = 1;

    /// <inheritdoc cref="ScalarConversionApi"/>
    private const int CollectionConversionApi = 2;

    /// <inheritdoc cref="ScalarConversionApi"/>
    private const int DictionaryConversionApi = 3;

    /// <summary>
    /// The version that added the <c>OrEmpty</c> twin of every collection builder, which is what
    /// <c>MapOptions.AllowNullCollections = false</c> — the DEFAULT — is generated against.
    ///
    /// A project whose runtime predates it keeps the null-stays-null behaviour that runtime
    /// implements, rather than getting a call to a method it does not have. That is the same
    /// bargain every number here makes: an older ShiftMapper loses what it cannot serve instead
    /// of failing to compile.
    /// </summary>
    private const int NullCollectionPolicyApi = 3;

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
    /// <param name="allowNullCollections">
    /// The map's <c>MapOptions.AllowNullCollections</c>: whether a null SOURCE collection is
    /// carried across as a null, or becomes an empty destination collection. Only the collection
    /// and dictionary steps read it — a scalar has no such question — which is why the
    /// recursive calls below leave it at its default.
    /// </param>
    public static ValueConversion? Resolve(
        Compilation compilation,
        ITypeSymbol sourceType,
        ITypeSymbol destinationType,
        string mapping,
        bool allowNullCollections = false,
        ConversionTable? globals = null)
    {
        // 1. THE TYPES WE WILL NOT REASON ABOUT AT ALL. `dynamic` has to go first: the
        //    compiler reports an implicit conversion to it from EVERYTHING, so leaving it in
        //    would let a single `dynamic` property swallow every source property and silence
        //    the SM0002 that should have been reported. Pointers and unresolved types are
        //    here for the ordinary reason — there is nothing sensible to emit.
        if (IsUnreasonable(sourceType) || IsUnreasonable(destinationType))
            return null;

        // 1b. A GLOBAL CONVERSION THE DEVELOPER REGISTERED, ahead of everything below.
        //
        //     AHEAD, and that is a deliberate departure from "extend the built-in table rather
        //     than replace it". The case that settles it is ShiftFramework's hash ids: `long` to
        //     `string` ALREADY converts, so a rule registered for that pair would be silently
        //     ignored under the other ordering — and silently ignoring an explicit declaration
        //     is the one behaviour this library is arranged never to have. Registering a pair is a
        //     specific statement about those two types; the built-in table is the general one, and
        //     the specific wins.
        //
        //     Everything you did NOT register is untouched, which is the sense in which the table
        //     is still extended rather than replaced.
        if (globals is { IsEmpty: false }
            && globals.Find(sourceType, destinationType) is { } registered)
        {
            string source = FullName(sourceType);
            string destination = FullName(destinationType);

            return new ValueConversion(
                template: $"Customizations.Conversion<{source}, {destination}>()({{0}})",
                risk: ConversionRisk.None,
                note: null,
                // A MARKER, not a call. The projection is an expression tree EF reads, and the
                // conversion it needs is a tree registered at run time; Compose replaces this with
                // that tree, inlined. See MapCustomizations.Splice.
                queryTemplate: $"global::ShiftMapper.MapCustomizations.Splice<{source}, {destination}>({{0}})",
                projectionRefusal: registered.HasQueryForm
                    ? null
                    : $"the conversion from '{sourceType.Name}' to '{destinationType.Name}' was " +
                      "registered without a query form",
                capturesMapper: true);
        }

        // 2. COLLECTIONS OF SIMPLE VALUES, ahead of the identity test on purpose.
        //    A collection is copied into whatever shape the destination asks for, and that
        //    INCLUDES the case where both sides are already the same shape: mapping
        //    `List<int>` onto `List<int>` gives the destination its own list rather than a
        //    second reference to the source's. Letting identity win there would make one
        //    collection mapping behave differently from all the others for no reason the
        //    developer could see from the code.
        //
        //    Everything this step does not recognise — a collection of Products, a Dictionary,
        //    a shape we cannot construct, a string (which is an IEnumerable<char> and must
        //    never be treated as one here) — returns null and carries on down the scalar path
        //    unchanged. `List<Product>` to `List<Product>` is still the plain assignment it
        //    always was, until nested mapping exists to do better.
        if (ResolveCollection(compilation, sourceType, destinationType, mapping, allowNullCollections, globals) is { } collection)
            return collection;

        //    2b. DICTIONARIES, on the same terms and for the same reason. A Dictionary is not an
        //    IEnumerable<T> of anything step 2 can build, so it lands here rather than there, and
        //    it too is COPIED even when both sides are already the same type.
        if (ResolveDictionary(compilation, sourceType, destinationType, mapping, allowNullCollections, globals) is { } dictionary)
            return dictionary;

        // 3. THE SAME TYPE. Note this comparison ignores nullable reference ANNOTATIONS, so
        //    `string?` to `string` is still a plain copy — exactly as it was before
        //    conversions existed, and the reason the generated file disables CS8601.
        if (SymbolEqualityComparer.Default.Equals(sourceType, destinationType))
            return ValueConversion.Direct;

        ITypeSymbol sourceCore = Unwrap(sourceType, out bool sourceIsNullableValue);
        ITypeSymbol destinationCore = Unwrap(destinationType, out bool destinationIsNullableValue);

        // 4. THE PAIRS WE REFUSE ON PURPOSE, even though C# would convert them.
        //    Each one is a conversion whose ANSWER IS NOT DETERMINED BY THE TWO TYPES ALONE,
        //    which is the one thing a compile-time mapper must never emit. See RefusedNote.
        if (RefusedNote(compilation, sourceCore, destinationCore) is not null)
            return null;

        // 5. C# ALREADY DOES IT IMPLICITLY — int to long, int to int?, or an
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

        // 6. TO TEXT. Anything that can format itself can fill a string property, and a
        //    nullable one needs no special handling on the way: ToInvariantString has its own
        //    nullable overloads, so an absent value stays absent instead of becoming "0".
        if (destinationCore.SpecialType == SpecialType.System_String)
        {
            return CanFormat(compilation, sourceCore) && HasConverter(compilation, ScalarConversionApi)
                ? ValueConversion.Call(
                    $"{ConverterType}.ToInvariantString({{0}})",
                    ConversionRisk.None,
                    // SQL decides the text format, so this is not the invariant-culture
                    // guarantee its in-memory twin makes — it is the database's own CONVERT.
                    queryTemplate: "{0}.ToString()")
                : null;
        }

        // 7. FROM TEXT. The mirror of step 6, and the one direction that can fail on data
        //    rather than on types — hence ConversionRisk.Parsed, which becomes SM0009.
        if (sourceCore.SpecialType == SpecialType.System_String)
        {
            return HasConverter(compilation, ScalarConversionApi)
                ? ResolveParse(compilation, destinationCore, destinationIsNullableValue, mapping)
                : null;
        }

        // 8. A NULLABLE SOURCE FILLING A NON-NULLABLE DESTINATION. There is no value to
        //    copy when the source is null, so the destination gets its default. Working out
        //    the rest by recursion means every conversion below is written once and works
        //    lifted for free: `long?` to `int` becomes
        //    `unchecked((int)source.X.GetValueOrDefault())`.
        if (sourceIsNullableValue && !destinationIsNullableValue && destinationType.IsValueType)
        {
            ValueConversion? inner = Resolve(
                compilation, sourceCore, destinationType, mapping, globals: globals);
            if (inner is null)
                return null;

            const string NullNote = "a null source becomes the destination type's default value";

            // Unwrapping is only a note, but whatever is wrapped inside it may be more than
            // that: `long?` to `int` both defaults the nulls AND narrows the rest, and the
            // narrowing is the half worth a warning.
            return new ValueConversion(
                inner.Apply("{0}.GetValueOrDefault()"),
                inner.Risk == ConversionRisk.Narrowing ? ConversionRisk.Narrowing : ConversionRisk.Lossy,
                inner.Note is null ? NullNote : $"{NullNote}, and {inner.Note}",
                queryTemplate: inner.ApplyQuery("{0}.GetValueOrDefault()"),
                projectionRefusal: inner.ProjectionRefusal,
                capturesMapper: inner.CapturesMapper);
        }

        // 9. A CAST, between numbers and enums. Reference downcasts, unboxing and
        //    user-defined explicit operators are all excluded in IsCastable.
        if (IsCastable(conversion))
            return Cast(destinationType, sourceCore, destinationCore);

        // 10. BETWEEN THE DATE AND TIME TYPES, which C# gives no conversions of its own.
        return HasConverter(compilation, ScalarConversionApi) ? ResolveDateAndTime(sourceCore, destinationCore) : null;
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
    /// <summary>
    /// Describes a pair of properties whose values are OBJECTS ShiftMapper maps, rather than
    /// values it converts — <c>Product</c> to <c>ProductDto</c>, or <c>List&lt;Product&gt;</c> to
    /// <c>IReadOnlyList&lt;ProductDto&gt;</c>.
    ///
    /// This runs only after <see cref="Resolve"/> has declined, so anything a conversion could
    /// have handled has already been handled. What is left is the case the conversion rules
    /// deliberately refuse: two class types that are not the same and have no conversion between
    /// them, which is exactly the shape of an entity and its DTO.
    ///
    /// Returning a description rather than performing anything is the point. Whether the pair can
    /// actually be mapped depends on whether a <c>CreateMap</c> for it exists somewhere in the
    /// mapper, and that is not known until every part of the class has been read. So this answers
    /// the question it CAN answer — are these two objects, and what shape is the collection — and
    /// leaves the verdict to the resolve pass.
    ///
    /// Returns null when the pair is not object-to-object, in which case the caller reports
    /// SM0002 exactly as before.
    /// </summary>
    public static ComplexPair? DescribeComplex(
        Compilation compilation,
        ITypeSymbol sourceType,
        ITypeSymbol destinationType)
    {
        // A single object on both sides.
        if (IsMappableObject(sourceType) && IsMappableObject(destinationType))
            return new ComplexPair(sourceType, destinationType, builder: null);

        // A collection on both sides, whose ELEMENTS are objects. The shape is settled by the
        // same helpers a collection of ints uses, so `List<Product>` fills an
        // `IReadOnlyList<ProductDto>` for the same reason `List<int>` fills an
        // `IReadOnlyList<string>` — only the per-element step is different.
        if (sourceType.SpecialType == SpecialType.System_String
            || destinationType.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (GetElementType(compilation, sourceType) is not ITypeSymbol sourceElement)
            return null;

        if (GetDestinationShape(compilation, destinationType) is not (string method, ITypeSymbol destinationElement))
            return null;

        return IsMappableObject(sourceElement) && IsMappableObject(destinationElement)
            ? new ComplexPair(sourceElement, destinationElement, method)
            : null;
    }

    /// <summary>
    /// Whether a type is the kind of thing a <c>CreateMap</c> could be written for: an ordinary
    /// class or struct of the developer's own.
    ///
    /// The exclusions are what stop this swallowing cases that belong elsewhere. <c>string</c> is
    /// a class and is emphatically a value here. <c>object</c> and interfaces have no properties
    /// worth mapping and no single type to construct. Anything in the BCL is out because a
    /// mapper is for YOUR types — a <c>Uri</c> or a <c>Stream</c> appearing on both sides is a
    /// reference to carry across, not a graph to copy, and SM0002 says so far more usefully than
    /// SM0011 demanding a CreateMap for it would.
    /// </summary>
    private static bool IsMappableObject(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
            return false;

        if (type is not INamedTypeSymbol named || named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return false;

        if (named.IsAbstract || named.IsAnonymousType || named.IsTupleType)
            return false;

        // Nullable<T> is a struct, but the thing worth mapping is whatever is inside it, and that
        // has already been unwrapped and offered to the conversion rules.
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return false;

        return !IsFrameworkType(named);
    }

    /// <summary>Whether a type ships with .NET rather than with the developer's project.</summary>
    private static bool IsFrameworkType(INamedTypeSymbol type)
    {
        for (INamespaceSymbol? ns = type.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            if (ns.ContainingNamespace is { IsGlobalNamespace: true })
                return ns.Name is "System" or "Microsoft";
        }

        return false;
    }

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

        // Anything reaching here is a conversion C# itself classes as EXPLICIT, and between
        // two numbers that means one thing: the destination cannot hold everything the source
        // can. (An int to a long is implicit and was taken four steps ago.) So the numeric
        // cases are narrowing by construction — no range table needed — while the enum cases
        // are a by-design reinterpretation and stay a note.
        bool enumInvolved = sourceCore.TypeKind == TypeKind.Enum || destinationCore.TypeKind == TypeKind.Enum;

        return new ValueConversion(
            template,
            enumInvolved ? ConversionRisk.Lossy : ConversionRisk.Narrowing,
            DescribeCast(sourceCore, destinationCore));
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
    /// Step 2 — one collection of simple values into another.
    ///
    /// <c>List&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>,
    /// <c>IReadOnlyList&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c> and arrays all describe the same
    /// idea, and an entity and its DTO rarely spell it the same way. This is the step that
    /// gets from any of them to any other:
    ///
    /// <code>
    /// // List&lt;int&gt; -> IReadOnlyList&lt;int&gt;
    /// Tags = ValueConverter.ToList(source.Tags)
    ///
    /// // List&lt;int&gt; -> string[]     — the elements convert too
    /// Tags = ValueConverter.ToArray(source.Tags, static item =&gt; ValueConverter.ToInvariantString(item))
    /// </code>
    ///
    /// <para><b>THE SOURCE</b> is anything that is an <c>IEnumerable&lt;T&gt;</c>, which
    /// includes arrays and every collection in the BCL. <b>THE DESTINATION</b> has to be a
    /// shape we know how to BUILD, which is a shorter list — the three concrete ones
    /// (<c>T[]</c>, <c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c>) and the interfaces one of
    /// those satisfies. A destination we cannot construct is reported as SM0002 rather than
    /// guessed at, the same as anywhere else.</para>
    ///
    /// <para><b>SIMPLE ELEMENTS ONLY, for now.</b> The elements have to be value types or
    /// strings, and they have to convert to each other by the ordinary rules — which is why
    /// this method recurses into <see cref="Resolve"/> rather than having a table of its own.
    /// A <c>List&lt;Product&gt;</c> is left alone: turning it into a
    /// <c>List&lt;ProductDto&gt;</c> means mapping each element, and mapping nested objects is
    /// a feature that does not exist yet. It falls through to the scalar path, where an
    /// identical pair is still assigned across and anything else is still SM0002.</para>
    ///
    /// <para><b>WHY STRING IS EXCLUDED EXPLICITLY.</b> <c>string</c> is an
    /// <c>IEnumerable&lt;char&gt;</c>. Without the guard, a <c>string</c> source would be read
    /// as a collection of characters and a <c>char[]</c> would happily fill a <c>string</c>
    /// property with something nobody asked for.</para>
    /// </summary>
    private static ValueConversion? ResolveCollection(
        Compilation compilation,
        ITypeSymbol sourceType,
        ITypeSymbol destinationType,
        string mapping,
        bool allowNullCollections,
        ConversionTable? globals = null)
    {
        if (sourceType.SpecialType == SpecialType.System_String
            || destinationType.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (!HasConverter(compilation, CollectionConversionApi))
            return null;

        if (GetElementType(compilation, sourceType) is not ITypeSymbol sourceElement)
            return null;

        if (GetDestinationShape(compilation, destinationType) is not var (method, destinationElement))
            return null;

        // Complex elements are the next feature, not this one.
        // A REGISTERED PAIR COUNTS AS SIMPLE. IsSimpleElement asks "is this a value the resolver
        // knows how to convert", and a global conversion is exactly a developer answering yes for
        // two types it did not already know about. Without this a rule for Money → string would
        // fill a member and not a List of them, which is not a distinction anyone declaring one
        // would expect to have made.
        bool registeredElement = globals is { IsEmpty: false }
            && globals.Find(sourceElement, destinationElement) is not null;

        if (!registeredElement
            && (!IsSimpleElement(sourceElement) || !IsSimpleElement(destinationElement)))
        {
            return null;
        }

        ValueConversion? element = Resolve(
            compilation, sourceElement, destinationElement, mapping, globals: globals);
        if (element is null)
            return null;

        // THE TEST IS "SAME TYPE", NOT "NEEDS NO CONVERSION CODE", and the difference is the
        // whole reason this line is careful.
        //
        // For a single value the two are interchangeable: an int can be assigned to a long
        // with no code at all, because C# converts it implicitly. For a COLLECTION of them
        // they are nothing alike — generics are INVARIANT, so a List<int> is not a List<long>
        // and never converts into one however willing the compiler is about the elements.
        // Copying `List<int>` into `List<long>` with the element-free overload produces a
        // List<int>, and the generated file does not compile.
        //
        // So the elements go through the converting overload whenever their types differ at
        // all — even when the conversion itself is the empty `item => item`, whose entire job
        // is to let the implicit conversion happen one element at a time.
        bool sameElement = SymbolEqualityComparer.Default.Equals(sourceElement, destinationElement);

        // `static` on the lambda is not a style choice: it forbids capturing, which is what
        // lets the compiler cache the delegate in a static field instead of allocating one
        // every time the map runs.
        //
        // The type arguments are written out for the same reason the test above is careful.
        // Inference reads TDestination from what the LAMBDA returns, so `item => item` over a
        // List<int> would infer List<int> again and quietly rebuild the bug this comment is
        // about. Stating both types makes the destination the compiler's problem rather than
        // the inference algorithm's — and makes the generated line say what it produces.
        // THE NULL-COLLECTION POLICY, which is one method name. `ToList` copies a null source
        // to a null destination; `ToListOrEmpty` copies it to an empty collection, and is what a
        // map uses unless it asked for the other answer. See MapOptions.AllowNullCollections for
        // why empty is the default.
        string builder = NullPolicySuffix(compilation, method, allowNullCollections);

        // `static` is dropped exactly where the element conversion reaches the mapper's own
        // Customizations, because static forbids capturing and the generated lambda would not
        // compile. Everywhere else it stays, and keeps the cached-delegate optimisation.
        string elementLambda = element.CapturesMapper ? "item" : "static item";

        string template = sameElement
            ? $"{ConverterType}.{builder}({{0}})"
            : $"{ConverterType}.{builder}<{FullName(sourceElement)}, {FullName(destinationElement)}>" +
              $"({{0}}, {elementLambda} => {element.Apply("item")})";

        // The query spelling is the same shape written with LINQ, because ValueConverter's
        // overloads are ShiftMapper's own methods and no database can run them.
        //
        // Called as plain static methods rather than as extensions — the generated file has no
        // using directives, on purpose — which builds the identical expression tree either way.
        // `static` comes off the lambda too: it is meaningless in a tree, which is data rather
        // than a delegate to cache.
        // The type arguments are stated for exactly the reason they are stated above, and leaving
        // them off is a real bug rather than a tidier line. Select infers TResult from what the
        // LAMBDA returns, so the identity conversion `item => item` over a List<int> filling a
        // List<long> infers List<int> and the generated file does not compile. Naming both types
        // makes the destination the compiler's problem instead of the inference algorithm's.
        // A projection that only needs the SHAPE changed assigns straight across instead of
        // copying. In memory the copy is the point — the DTO owns its own list rather than a
        // second reference to the entity's — but a projection has no entity to share with, since
        // EF reads the column and builds a fresh collection per row either way.
        //
        // It is also the difference between working and not. EF cannot shape a primitive
        // collection through Enumerable.ToList in a projection; it throws a NullReferenceException
        // out of its own shaper, and does so for hand-written LINQ exactly as readily. Assigning
        // directly is both the cheaper spelling and the one that survives.
        bool assignable = compilation.ClassifyConversion(sourceType, destinationType) is
            { Exists: true, IsImplicit: true, IsBoxing: false, IsUserDefined: false };

        // THE POLICY AGAIN, and in a projection it is a real question rather than a method name.
        // A collection of VALUES lives in a column, and a NULLABLE column can be null — so the
        // projection has to answer it too, or the same map would hand back an empty list in
        // memory and a null out of the database.
        //
        // Only where a null can actually arrive, though: see GuardsNullInQuery for why wrapping
        // a column that was never nullable costs a translation rather than nothing.
        //
        // (The collections of OBJECTS handled by NestedBinding need no guard at all: they are
        // navigation collections, and EF materialises no rows as an empty collection rather than
        // as a null. See MapOptions.AllowNullCollections.)
        bool guard = GuardsNullInQuery(sourceType, allowNullCollections);

        string emptySource = guard
            ? $"({{0}} ?? {LinqType}.Empty<{FullName(sourceElement)}>())"
            : "{0}";

        // The assignable case cannot use that spelling: it assigns the source across untouched,
        // so there is no Enumerable call to put an IEnumerable<T> inside. It coalesces to an
        // empty of the shape the destination actually asked for instead, cast to the destination
        // type so the two arms of the ?? have a type in common whatever the source is spelled as.
        string assignedEmpty = guard
            ? $"({{0}} ?? ({FullName(destinationType)}){EmptyCollection(method, FullName(destinationElement))})"
            : "{0}";

        string queryTemplate = sameElement
            ? (assignable ? assignedEmpty : $"{LinqType}.{method}({emptySource})")
            : $"{LinqType}.{method}<{FullName(destinationElement)}>(" +
              $"{LinqType}.Select<{FullName(sourceElement)}, {FullName(destinationElement)}>" +
              $"({emptySource}, item => {element.ApplyQuery("item")}))";

        return new ValueConversion(
            template, CollectionRisk(method, element), CollectionNote(method, element), queryTemplate,
            // Both travel outward. A conversion that cannot be translated does not become
            // translatable by being done once per element, and one that reaches the mapper still
            // reaches it from inside a lambda.
            projectionRefusal: element.ProjectionRefusal,
            capturesMapper: element.CapturesMapper);
    }

    /// <summary>
    /// Whether the referenced ShiftMapper runtime carries the <c>OrEmpty</c> collection builders,
    /// and so whether "a null source collection becomes an empty one" can be generated at all.
    ///
    /// Asked once per map, by the generator, so that a project sitting on an older runtime is
    /// analysed as though it had asked for <c>AllowNullCollections</c> — which leaves it with
    /// exactly the behaviour that runtime implements, rather than with a call to a method that is
    /// not there.
    /// </summary>
    public static bool SupportsNullCollectionPolicy(Compilation compilation) =>
        HasConverter(compilation, NullCollectionPolicyApi);

    /// <summary>
    /// The builder to call for a given null-collection policy — <c>ToList</c> or
    /// <c>ToListOrEmpty</c>.
    ///
    /// Falls back to the plain builder when the referenced runtime predates the OrEmpty family,
    /// which leaves such a project with exactly the behaviour the ShiftMapper it references
    /// implements rather than a call to a method that is not there.
    /// </summary>
    private static string NullPolicySuffix(Compilation compilation, string method, bool allowNullCollections) =>
        allowNullCollections || !HasConverter(compilation, NullCollectionPolicyApi)
            ? method
            : method + "OrEmpty";

    /// <summary>
    /// Whether the PROJECTION guards this collection against a null source.
    ///
    /// Two things have to be true. The map has to want the empty-collection policy at all — and
    /// the source property has to be DECLARED NULLABLE, which is the part worth explaining.
    ///
    /// A coalesce is not free in a projection. EF recognises a primitive collection by the shape
    /// of the expression around it, and <c>x ?? empty</c> is a shape it does not see through: the
    /// same <c>Select</c> over a JSON column that translates perfectly on its own stops
    /// translating once the column is wrapped, and the developer gets a runtime "could not be
    /// translated" for a guard they never asked for. So the guard is written only where a null can
    /// actually arrive.
    ///
    /// The nullable ANNOTATION is the right test because it is the developer's own statement about
    /// the column, and it is the same test <c>NestedProperty.SourceIsNullable</c> already applies
    /// to nested objects for exactly this reason. The in-memory maps guard either way — there
    /// the OrEmpty builder costs one null check and hides nothing from anybody.
    /// </summary>
    private static bool GuardsNullInQuery(ITypeSymbol sourceType, bool allowNullCollections) =>
        !allowNullCollections && sourceType.NullableAnnotation == NullableAnnotation.Annotated;

    /// <summary>
    /// An empty collection of the shape a given builder produces, written out so a projection can
    /// coalesce onto it. It is the CONCRETE type in each case, because that is what the
    /// destination interface was going to be filled with anyway.
    /// </summary>
    private static string EmptyCollection(string method, params string[] elements) => method switch
    {
        "ToArray" => $"global::System.Array.Empty<{elements[0]}>()",
        "ToHashSet" => $"new global::System.Collections.Generic.HashSet<{elements[0]}>()",
        "ToDictionary" => $"new global::System.Collections.Generic.Dictionary<{elements[0]}, {elements[1]}>()",
        _ => $"new global::System.Collections.Generic.List<{elements[0]}>()",
    };

    /// <summary>
    /// Step 2b — one dictionary into another.
    ///
    /// <code>
    /// // Dictionary&lt;string, int&gt; -> IReadOnlyDictionary&lt;string, int&gt;
    /// Ratings = ValueConverter.ToDictionaryOrEmpty(source.Ratings)
    ///
    /// // Dictionary&lt;string, int&gt; -> Dictionary&lt;string, string&gt;   — the values convert too
    /// Ratings = ValueConverter.ToDictionaryOrEmpty&lt;string, int, string, string&gt;(
    ///               source.Ratings, static key =&gt; key, static value =&gt; ValueConverter.ToInvariantString(value))
    /// </code>
    ///
    /// <para><b>THE SOURCE</b> is anything that is an
    /// <c>IEnumerable&lt;KeyValuePair&lt;K, V&gt;&gt;</c>, which every dictionary in the BCL is.
    /// <b>THE DESTINATION</b> is a <c>Dictionary&lt;K, V&gt;</c> or one of the two interfaces a
    /// Dictionary satisfies. A <c>SortedDictionary</c> or a <c>ConcurrentDictionary</c> can be
    /// READ from and not written to, the same asymmetry the list builders have, and for the same
    /// reason: we can enumerate anything and we can only construct what we know how to.</para>
    ///
    /// <para><b>KEYS AND VALUES CONVERT INDEPENDENTLY</b>, by the ordinary rules — so a
    /// <c>Dictionary&lt;int, decimal&gt;</c> fills a <c>Dictionary&lt;string, string&gt;</c>, and
    /// a pair with no conversion for either half is SM0002 exactly as it was before.</para>
    ///
    /// <para><b>SIMPLE KEYS AND VALUES ONLY</b>, the same restriction the list builders carry: a
    /// <c>Dictionary&lt;string, Product&gt;</c> is left alone rather than half converted, because
    /// mapping the values means mapping objects and that path does not run through here.</para>
    /// </summary>
    private static ValueConversion? ResolveDictionary(
        Compilation compilation,
        ITypeSymbol sourceType,
        ITypeSymbol destinationType,
        string mapping,
        bool allowNullCollections,
        ConversionTable? globals = null)
    {
        if (!HasConverter(compilation, DictionaryConversionApi))
            return null;

        if (GetDictionaryShape(compilation, destinationType) is not var (destinationKey, destinationValue))
            return null;

        if (GetPairTypes(compilation, sourceType) is not var (sourceKey, sourceValue))
            return null;

        // Objects on either side belong to the nested-mapping path, which does not handle
        // dictionaries yet. Half converting one — a fresh dictionary holding the entity's own
        // Products — is the outcome IsSimpleElement exists to prevent.
        // Same rule as the collection builder: a registered pair is one the developer has told the
        // resolver how to convert, so it counts as simple for the shape it appears in.
        bool registeredKey = globals is { IsEmpty: false }
            && globals.Find(sourceKey, destinationKey) is not null;

        bool registeredValue = globals is { IsEmpty: false }
            && globals.Find(sourceValue, destinationValue) is not null;

        if ((!registeredKey && (!IsSimpleElement(sourceKey) || !IsSimpleElement(destinationKey)))
            || (!registeredValue && (!IsSimpleElement(sourceValue) || !IsSimpleElement(destinationValue))))
        {
            return null;
        }

        ValueConversion? key = Resolve(
            compilation, sourceKey, destinationKey, mapping, globals: globals);
        if (key is null)
            return null;

        ValueConversion? value = Resolve(
            compilation, sourceValue, destinationValue, mapping, globals: globals);
        if (value is null)
            return null;

        // Same test, same reason as the collection builders: generics are invariant, so a
        // Dictionary<string, int> is not a Dictionary<string, long> however freely an int becomes
        // a long. Anything but an exact match on BOTH halves goes through the converting overload.
        bool same = SymbolEqualityComparer.Default.Equals(sourceKey, destinationKey)
            && SymbolEqualityComparer.Default.Equals(sourceValue, destinationValue);

        string builder = NullPolicySuffix(compilation, "ToDictionary", allowNullCollections);

        string keyLambda = key.CapturesMapper ? "key" : "static key";
        string valueLambda = value.CapturesMapper ? "value" : "static value";

        string template = same
            ? $"{ConverterType}.{builder}({{0}})"
            : $"{ConverterType}.{builder}<{FullName(sourceKey)}, {FullName(sourceValue)}, " +
              $"{FullName(destinationKey)}, {FullName(destinationValue)}>" +
              $"({{0}}, {keyLambda} => {key.Apply("key")}, {valueLambda} => {value.Apply("value")})";

        bool assignable = compilation.ClassifyConversion(sourceType, destinationType) is
            { Exists: true, IsImplicit: true, IsBoxing: false, IsUserDefined: false };

        string pair = $"global::System.Collections.Generic.KeyValuePair<{FullName(sourceKey)}, {FullName(sourceValue)}>";

        bool guard = GuardsNullInQuery(sourceType, allowNullCollections);

        string assignedEmpty = guard
            ? $"({{0}} ?? ({FullName(destinationType)})" +
              $"{EmptyCollection("ToDictionary", FullName(destinationKey), FullName(destinationValue))})"
            : "{0}";

        string emptySource = guard
            ? $"({{0}} ?? {LinqType}.Empty<{pair}>())"
            : "{0}";

        string queryTemplate = same && assignable
            ? assignedEmpty
            : $"{LinqType}.ToDictionary<{pair}, {FullName(destinationKey)}, {FullName(destinationValue)}>(" +
              $"{emptySource}, item => {key.ApplyQuery("item.Key")}, item => {value.ApplyQuery("item.Value")})";

        return new ValueConversion(
            template, DictionaryRisk(same, key, value), DictionaryNote(same, key, value), queryTemplate,
            projectionRefusal: key.ProjectionRefusal ?? value.ProjectionRefusal,
            capturesMapper: key.CapturesMapper || value.CapturesMapper);
    }

    /// <summary>
    /// What a dictionary conversion carries: whatever its keys and values carry, plus the one
    /// thing the dictionary itself can lose.
    /// </summary>
    private static ConversionRisk DictionaryRisk(bool sameKeyAndValue, ValueConversion key, ValueConversion value)
    {
        ConversionRisk worst = key.Risk > value.Risk ? key.Risk : value.Risk;

        // Converting the KEYS is what can collide two entries that were distinct in the source.
        // Leaving them alone cannot: a dictionary has already made that guarantee about itself.
        bool keysConverted = !sameKeyAndValue && key.Template is not null;

        return keysConverted && worst == ConversionRisk.None ? ConversionRisk.Lossy : worst;
    }

    /// <inheritdoc cref="DictionaryRisk"/>
    private static string? DictionaryNote(bool sameKeyAndValue, ValueConversion key, ValueConversion value)
    {
        const string KeyNote = "two source keys that convert to the same destination key collapse into one, " +
                               "so the destination can hold fewer entries than the source";

        var parts = new List<string>();

        if (!sameKeyAndValue && key.Template is not null)
            parts.Add(KeyNote);

        if (key.Note is not null)
            parts.Add($"for each key, {key.Note}");

        if (value.Note is not null)
            parts.Add($"for each value, {value.Note}");

        return parts.Count == 0 ? null : string.Join(", and ", parts);
    }

    /// <summary>
    /// The dictionary shapes we can BUILD, and their key and value types. All three are filled
    /// with a <c>Dictionary&lt;K, V&gt;</c>, which is what the two interfaces are here for.
    /// </summary>
    private static readonly string[] DictionaryShapes =
    {
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2",
    };

    /// <inheritdoc cref="DictionaryShapes"/>
    private static (ITypeSymbol Key, ITypeSymbol Value)? GetDictionaryShape(Compilation compilation, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named || named.TypeArguments.Length != 2)
            return null;

        foreach (string metadata in DictionaryShapes)
        {
            if (compilation.GetTypeByMetadataName(metadata) is INamedTypeSymbol shape
                && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, shape))
            {
                return (named.TypeArguments[0], named.TypeArguments[1]);
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>K</c> and <c>V</c> of the <c>IEnumerable&lt;KeyValuePair&lt;K, V&gt;&gt;</c> this
    /// type is, or null when it is not one. Reading the source through the pair rather than
    /// through <c>IDictionary</c> is what lets a <c>SortedDictionary</c>, a
    /// <c>ConcurrentDictionary</c> or a plain list of pairs feed a dictionary property.
    /// </summary>
    private static (ITypeSymbol Key, ITypeSymbol Value)? GetPairTypes(Compilation compilation, ITypeSymbol type)
    {
        if (GetElementType(compilation, type) is not INamedTypeSymbol { IsGenericType: true } element
            || element.TypeArguments.Length != 2)
        {
            return null;
        }

        return compilation.GetTypeByMetadataName("System.Collections.Generic.KeyValuePair`2") is INamedTypeSymbol pair
            && SymbolEqualityComparer.Default.Equals(element.OriginalDefinition, pair)
                ? (element.TypeArguments[0], element.TypeArguments[1])
                : null;
    }

    /// <summary>
    /// What to tell the developer about a collection conversion: whatever its ELEMENTS carry,
    /// plus the one thing the collection itself can lose.
    /// </summary>
    private static ConversionRisk CollectionRisk(string method, ValueConversion element) =>
        method == "ToHashSet" && element.Risk == ConversionRisk.None
            ? ConversionRisk.Lossy
            : element.Risk;   // a List<long> to List<int> is narrowing, one element at a time

    /// <inheritdoc cref="CollectionRisk"/>
    private static string? CollectionNote(string method, ValueConversion element)
    {
        // A set is not a shorter word for a list. Whatever feeds it, equal values collapse
        // into one, so a destination declared as a HashSet can come out shorter than the
        // source that filled it — quietly, and only for the data that happens to repeat.
        const string SetNote = "duplicate values are discarded, so the destination can hold fewer items than the source";

        if (method != "ToHashSet")
            return element.Note is null ? null : $"for each element, {element.Note}";

        return element.Note is null ? SetNote : $"{SetNote}, and for each element, {element.Note}";
    }

    /// <summary>
    /// The <c>T</c> in the <c>IEnumerable&lt;T&gt;</c> this type is, or null when it is not
    /// one. A type implementing it for two different <c>T</c> is treated as not one at all —
    /// there would be no way to know which sequence was meant.
    /// </summary>
    private static ITypeSymbol? GetElementType(Compilation compilation, ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return array.Rank == 1 ? array.ElementType : null;

        if (compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1") is not INamedTypeSymbol enumerable)
            return null;

        ITypeSymbol? found = null;

        // The type may BE IEnumerable<T> rather than merely implement it, so it is considered
        // alongside its interfaces.
        if (type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, enumerable))
            found = named.TypeArguments[0];

        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, enumerable))
                continue;

            if (found is not null && !SymbolEqualityComparer.Default.Equals(found, candidate.TypeArguments[0]))
                return null;

            found = candidate.TypeArguments[0];
        }

        return found;
    }

    /// <summary>
    /// The collection shapes we can BUILD, and the helper that builds each one.
    ///
    /// Deliberately shorter than the list of shapes we can READ. The interfaces are here
    /// because the concrete type we would build satisfies them — a <c>List&lt;T&gt;</c> is an
    /// <c>IList&lt;T&gt;</c>, an <c>ICollection&lt;T&gt;</c>, an
    /// <c>IReadOnlyList&lt;T&gt;</c> and an <c>IEnumerable&lt;T&gt;</c> all at once, so a
    /// destination declared as any of them can be filled with one.
    ///
    /// Anything not on this list falls through to the next step: a <c>Dictionary&lt;K,V&gt;</c>
    /// is a different shape entirely and is handled by <see cref="ResolveDictionary"/>, and a
    /// collection type of your own — which could need anything at all to construct — is
    /// SM0002.
    /// </summary>
    private static readonly (string Metadata, string Method)[] DestinationShapes =
    {
        ("System.Collections.Generic.List`1", "ToList"),
        ("System.Collections.Generic.IList`1", "ToList"),
        ("System.Collections.Generic.ICollection`1", "ToList"),
        ("System.Collections.Generic.IEnumerable`1", "ToList"),
        ("System.Collections.Generic.IReadOnlyList`1", "ToList"),
        ("System.Collections.Generic.IReadOnlyCollection`1", "ToList"),
        ("System.Collections.Generic.HashSet`1", "ToHashSet"),
        ("System.Collections.Generic.ISet`1", "ToHashSet"),
        ("System.Collections.Generic.IReadOnlySet`1", "ToHashSet"),
    };

    /// <summary>
    /// Which <c>ValueConverter</c> method builds this destination, and what its elements are.
    /// Returns null when the destination is not a shape we can construct.
    /// </summary>
    private static (string Method, ITypeSymbol Element)? GetDestinationShape(Compilation compilation, ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return array.Rank == 1 ? ("ToArray", array.ElementType) : null;

        if (type is not INamedTypeSymbol { IsGenericType: true } named || named.TypeArguments.Length != 1)
            return null;

        foreach ((string metadata, string method) in DestinationShapes)
        {
            if (compilation.GetTypeByMetadataName(metadata) is INamedTypeSymbol shape
                && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, shape))
            {
                return (method, named.TypeArguments[0]);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an element is simple enough for this first version: a value type (an
    /// <c>int</c>, an <c>int?</c>, an enum, a <c>DateTime</c>, a struct of your own) or a
    /// <c>string</c>.
    ///
    /// Note what the test rules OUT and why it is a TYPE test rather than just letting
    /// <see cref="Resolve"/> decide. Two <c>Product</c> elements would resolve perfectly well
    /// — as an identity — and the collection would be copied with both sides pointing at the
    /// same Product instances. That is a half-done job: the shape is a copy and the contents
    /// are shared. Nested mapping is the feature that finishes it, and until it exists these
    /// are left alone rather than half converted.
    /// </summary>
    private static bool IsSimpleElement(ITypeSymbol type) =>
        type.IsValueType || type.SpecialType == SpecialType.System_String;

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
    private static bool HasConverter(Compilation compilation, int minimumVersion)
    {
        if (compilation.GetTypeByMetadataName(ConverterMetadataName) is not INamedTypeSymbol converter)
            return false;

        foreach (ISymbol member in converter.GetMembers(ApiVersionFieldName))
        {
            if (member is IFieldSymbol { HasConstantValue: true, ConstantValue: int version })
                return version >= minimumVersion;
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

    /// <summary>
    /// How a type is spelled in emitted code — fully qualified, so nothing depends on which
    /// usings happen to be in scope where the generated file lands.
    /// </summary>
    private static string FullName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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

    /// <summary>
    /// Something is dropped on the way, BY DESIGN and predictably — a null becomes a default,
    /// an enum member becomes its number, a set discards duplicates, a DateTime loses its time
    /// of day. Reported as SM0008, informational, because it is the conversion doing exactly
    /// what it says.
    /// </summary>
    Lossy,

    /// <summary>
    /// The destination CANNOT HOLD every value the source can, so an ordinary value can come
    /// out as a different one — <c>long</c> 9,000,000,000 arriving as <c>int</c> 410,065,408.
    /// Reported as SM0010, and a real warning rather than a note, because nothing about the
    /// code says it is happening and no exception marks it when it does.
    /// </summary>
    Narrowing,

    /// <summary>Text is read at runtime, so bad data throws — reported as SM0009.</summary>
    Parsed,
}

/// <summary>
/// Two properties that hold OBJECTS ShiftMapper maps, as opposed to values it converts.
///
/// Carries symbols rather than strings because, unlike <see cref="ValueConversion"/>, this never
/// reaches a cached model — the caller turns it into a <c>NestedProperty</c> immediately.
/// </summary>
internal sealed class ComplexPair
{
    public ComplexPair(ITypeSymbol source, ITypeSymbol destination, string? builder)
    {
        Source = source;
        Destination = destination;
        Builder = builder;
    }

    /// <summary>The object type on the source side — the ELEMENT type for a collection.</summary>
    public ITypeSymbol Source { get; }

    /// <summary>The object type on the destination side.</summary>
    public ITypeSymbol Destination { get; }

    /// <summary>
    /// Which <c>ValueConverter</c> method builds the destination collection, or null when the
    /// property holds a single object.
    /// </summary>
    public string? Builder { get; }
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

    public ValueConversion(
        string? template,
        ConversionRisk risk,
        string? note,
        string? queryTemplate = null,
        string? projectionRefusal = null,
        bool capturesMapper = false)
    {
        Template = template;
        QueryTemplate = queryTemplate ?? template;
        Risk = risk;
        Note = note;
        ProjectionRefusal = projectionRefusal;
        CapturesMapper = capturesMapper;
    }

    /// <summary>
    /// Why this conversion cannot appear in a PROJECTION, or null when it can.
    ///
    /// <para>Only a global <c>CreateConversion</c> declared without a query form sets this, and it
    /// is the developer's own decision rather than a limitation: they said the pair converts one
    /// way in C# and gave no way to say it in SQL. What it costs is the projection of every map
    /// that touches the pair, which the build states (SM0030) instead of leaving to be discovered
    /// when a query runs.</para>
    ///
    /// <para>It travels OUTWARD through every wrapper — a nullable lift, a collection of them, a
    /// dictionary value — because a conversion that cannot be translated does not become
    /// translatable by being done many times.</para>
    /// </summary>
    public string? ProjectionRefusal { get; }

    /// <summary>
    /// Whether the template names the mapper's own <c>Customizations</c>, which only a global
    /// conversion does.
    ///
    /// <para>It exists for one concrete reason: the collection and dictionary builders wrap the
    /// element conversion in a <c>static</c> lambda, and <c>static</c> forbids capturing. A global
    /// conversion reaches an INSTANCE member, so a <c>List&lt;string&gt;</c> whose elements convert
    /// through one would emit a lambda that does not compile. Where this is set the emitter drops
    /// the keyword — paying an allocation per map, only where one is unavoidable.</para>
    /// </summary>
    public bool CapturesMapper { get; }

    /// <summary>A conversion that needs code but has nothing extra to explain.</summary>
    public static ValueConversion Call(string template, ConversionRisk risk, string? queryTemplate = null) =>
        new(template, risk, note: null, queryTemplate);

    /// <summary>The C# to emit, with <c>{0}</c> for the source expression. Null means a plain copy.</summary>
    public string? Template { get; }

    /// <summary>
    /// The same conversion written for a QUERY PROJECTION, where the code does not run in C# —
    /// it is handed to Entity Framework to be turned into SQL.
    ///
    /// Most conversions need no second spelling and this is simply <see cref="Template"/>. The
    /// ones that do are the conversions that call into <c>ValueConverter</c>, and the reason is
    /// not that EF is limited: EF translates methods it has a translator for, and it can never
    /// have one for a static method in someone else's library. A database cannot run
    /// <c>ShiftMapper.ValueConverter.ToInvariantString</c>.
    ///
    /// So for those, the query spelling uses the ordinary BCL shape a developer would have
    /// hand-written — <c>{0}.ToString()</c>, <c>Enumerable.ToList({0})</c> — which is exactly what
    /// EF's translators are written against.
    ///
    /// This is NOT ShiftMapper predicting what EF can translate. It is choosing the standard
    /// spelling and leaving the verdict to EF, which is the only thing that actually knows, and
    /// which gets better at it with every release.
    /// </summary>
    public string? QueryTemplate { get; }

    public ConversionRisk Risk { get; }

    /// <summary>Why this conversion can lose or refuse a value — the tail of the SM0008 message.</summary>
    public string? Note { get; }

    /// <summary>
    /// Nests this conversion around another snippet, used when a nullable source is unwrapped
    /// first: the inner <c>{0}.GetValueOrDefault()</c> becomes the input to this conversion.
    /// </summary>
    public string Apply(string inner) =>
        Template is null ? inner : Template.Replace("{0}", inner);

    /// <summary><see cref="Apply"/> for the query spelling.</summary>
    public string ApplyQuery(string inner) =>
        QueryTemplate is null ? inner : QueryTemplate.Replace("{0}", inner);
}
