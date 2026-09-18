using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace ShiftMapper.Generator;

/// <summary>
/// The warnings ShiftMapper can report. A <see cref="DiagnosticDescriptor"/> is the
/// *definition* of a warning (its id, wording and severity); a <see cref="Diagnostic"/> is
/// one actual occurrence of it, attached to a location in your code.
///
/// The ids are what you use to retune a rule. In .editorconfig, which is the usual place and
/// works per folder:
/// <code>
/// [*.cs]
/// dotnet_diagnostic.SM0010.severity = error
///
/// [tests/**.cs]
/// dotnet_diagnostic.SM0001.severity = none
/// </code>
/// or in a .csproj, for the whole project at once:
///   &lt;NoWarn&gt;$(NoWarn);SM0001&lt;/NoWarn&gt;
///   &lt;WarningsAsErrors&gt;$(WarningsAsErrors);SM0002&lt;/WarningsAsErrors&gt;
///
/// That .editorconfig support is why these are reported by <see cref="ShiftMapperAnalyzer"/>
/// and not by the generator: a diagnostic a source generator reports is treated by the
/// compiler like one of its own CS ones, which honours NoWarn and ignores analyzer config
/// entirely.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "ShiftMapper";

    /// <summary>SM0001 — the source type simply has nothing with that name.</summary>
    public static readonly DiagnosticDescriptor NoSourceProperty = new(
        id: "SM0001",
        title: "Destination property is not mapped",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because '{2}' has no readable property named '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The destination property keeps its default value. Add a matching property " +
                     "to the source, or remove it from the destination.");

    /// <summary>
    /// SM0002 — the names line up, and ShiftMapper will not bridge the two types.
    ///
    /// Note the wording: "does not convert", not "cannot be converted". Some of these pairs
    /// really have no conversion at all (Product to ProductDto, bool to int); others C# would
    /// convert quite happily and ShiftMapper still refuses, because the answer would depend on
    /// something other than the two types. Claiming they are impossible would be a lie the
    /// developer could disprove in one line.
    /// </summary>
    public static readonly DiagnosticDescriptor NotConvertible = new(
        id: "SM0002",
        title: "Destination property is not mapped because ShiftMapper does not convert between the two types",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because ShiftMapper does not convert '{2}' to '{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ShiftMapper converts between the simple types — numbers, text, bool, char, enums, " +
                     "Guid and the date and time types — honours implicit conversion operators the " +
                     "types themselves declare, and copies COLLECTIONS of those simple types between " +
                     "the shapes it can build (arrays, List, HashSet, and the interfaces those satisfy). " +
                     "A nested object, or a collection of them, is mapped through its OWN map and " +
                     "reported as SM0011 when that map is missing, not here. It will not move a " +
                     "reference around by up-casting, down-casting or boxing; and it refuses " +
                     "four pairs on purpose, because their answer would not come from the types alone: " +
                     "DateTime to DateTimeOffset (the offset would come from the machine's time zone), " +
                     "DateTimeOffset to DateTime (dropping the offset and converting to UTC are equally " +
                     "defensible), one enum to a different enum (a cast maps them by number, so " +
                     "reordering either would silently change the meaning), and TimeSpan to TimeOnly " +
                     "(a negative duration, or one of a day or more, has no time of day). A user-defined " +
                     "EXPLICIT operator is refused too: its author chose the keyword that says stop and " +
                     "think. Give the destination property the source's type, or fill it in yourself " +
                     "after mapping.");

    /// <summary>SM0003 — it looks mappable but the setter cannot be called.</summary>
    public static readonly DiagnosticDescriptor SetterNotAccessible = new(
        id: "SM0003",
        title: "Destination property is not mapped because its setter is not public",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because its setter is not public",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Generated code lives outside your type, so it can only assign properties with " +
                     "a public setter. Get-only and computed properties are skipped silently; this " +
                     "warning is for the ones that look assignable but are not.");

    /// <summary>
    /// SM0004 — there is no constructor ShiftMapper can call at all.
    ///
    /// It used to mean "no public parameterless constructor", which stopped being the interesting
    /// case once records and primary constructors became mappable. What is left is the type that
    /// cannot be built by anybody from outside: an abstract class, an interface, a type whose
    /// every constructor is private. A type whose constructor merely cannot be FILLED gets
    /// SM0013 instead, which can name the parameter.
    /// </summary>
    public static readonly DiagnosticDescriptor CannotConstructDestination = new(
        id: "SM0004",
        title: "Destination type cannot be created by ShiftMapper",
        messageFormat: "ShiftMapper: no Map method was generated to create '{0}' because it has no constructor ShiftMapper can call",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ShiftMapper builds a destination with a public constructor, matching its " +
                     "parameters to source properties by name where it has to. An abstract type, an " +
                     "interface, or a type with no public constructor cannot be created at all.");

    /// <summary>
    /// SM0006 — the SM0001 case, but for the map that <c>ReverseMap()</c> added.
    ///
    /// Same situation, deliberately quieter. A DTO is normally a SUBSET of its entity, so
    /// mapping back always leaves entity-only properties untouched — navigation collections,
    /// audit columns, keys the client never sends. Reporting each of those as a warning would
    /// make ReverseMap unusable on exactly the shape it exists to serve, so this is
    /// informational: visible in the IDE and under `dotnet build -v d`, silent in a normal build.
    ///
    /// If you want these enforced, raise it where it matters:
    /// <c>dotnet_diagnostic.SM0006.severity = warning</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor NoSourcePropertyInReverseMap = new(
        id: "SM0006",
        title: "Destination property is not mapped by the reverse map",
        messageFormat: "ShiftMapper: the reverse map leaves '{0}.{1}' unmapped because '{2}' has no readable property named '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The property keeps its default value when mapping back. This is expected when " +
                     "the destination is richer than the source, which is the usual reason to call " +
                     "ReverseMap in the first place. Being informational, it shows in the IDE and in a " +
                     "detailed build log (-v d), and is not counted as a build warning.");

    /// <summary>
    /// SM0007 — the case-insensitive fallback found several candidates.
    ///
    /// Only reachable under <c>PropertyMatching.CaseInsensitive</c>, and only when there was
    /// no exact match to settle it: the source carries two properties whose names differ
    /// only by case, so either could be meant. Picking one silently would be a coin toss
    /// with the developer's data, so nothing is mapped and the choice is handed back.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousCaseInsensitiveMatch = new(
        id: "SM0007",
        title: "Destination property matches more than one source property when case is ignored",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because '{2}' has several properties matching '{1}' when case is ignored ({3})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Rename one of the source properties, give the destination the exact name of " +
                     "the one you want, or pass PropertyMatching.CaseSensitive to turn the " +
                     "fallback off for this map.");

    /// <summary>
    /// SM0008 — the property IS mapped, and something is dropped on the way BY DESIGN.
    ///
    /// A null becoming the destination's default, an enum member becoming its number, a
    /// HashSet discarding duplicates, a <c>DateTime</c> losing its time of day: each of those
    /// is the conversion doing exactly what it says on the tin, for every value it is given.
    ///
    /// Contrast SM0010, which is the case where an ORDINARY value comes out wrong. That one is
    /// a warning; this one is a note.
    ///
    /// INFORMATIONAL on purpose, for the same reason as SM0006. These conversions are the
    /// feature working as intended — the developer wrote two types that do not match and
    /// asked ShiftMapper to cope — so a warning on every one of them would train people to
    /// ignore ShiftMapper's warnings, which is worse than saying nothing. It shows in the
    /// IDE and under <c>dotnet build -v d</c>, and does not count as a build warning.
    /// </summary>
    public static readonly DiagnosticDescriptor LossyConversion = new(
        id: "SM0008",
        title: "Destination property is mapped through a conversion that can lose information",
        messageFormat: "ShiftMapper: '{0}.{1}' is mapped by converting '{2}' to '{3}', which can lose information ({4})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The map is generated and works. This is here so the narrowing is a decision you " +
                     "have seen rather than one you inherit. Give the two properties the same type if " +
                     "you would rather it did not happen.");

    /// <summary>
    /// SM0009 — the property is filled by READING TEXT, so it can throw on data rather than
    /// on types.
    ///
    /// Every other conversion is settled at compile time; this one is not. A destination
    /// <c>int</c> fed by a source <c>string</c> compiles perfectly and then throws the first
    /// time the column contains something that is not a number.
    ///
    /// Informational for the same reason as SM0008 — this is the feature doing what it was
    /// asked to do — but worth surfacing, because it is the only place a ShiftMapper map can
    /// fail at runtime for reasons the build could not see.
    /// </summary>
    public static readonly DiagnosticDescriptor ParsedConversion = new(
        id: "SM0009",
        title: "Destination property is mapped by parsing text at runtime",
        messageFormat: "ShiftMapper: '{0}.{1}' is filled by parsing text when the map runs, so source text that does not parse throws",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Null, empty and whitespace-only text converts to the destination's default value " +
                     "(or to null, when the destination is nullable). Anything else that does not parse " +
                     "throws a FormatException naming the two properties, rather than quietly mapping a " +
                     "zero. See ShiftMapper.ValueConverter for the exact rules.");

    /// <summary>
    /// SM0010 — the property IS mapped, and the conversion can hand back a DIFFERENT VALUE
    /// from the one it was given.
    ///
    /// A <c>long</c> of 9,000,000,000 arrives in an <c>int</c> as 410,065,408. A
    /// <c>decimal</c> price arrives in a <c>double</c> having quietly lost digits. Nothing in
    /// the code says so and no exception marks it when it happens — which is exactly why this
    /// one is a WARNING where SM0008 is a note. SM0008 is for the losses you asked for (a null
    /// becoming a default, a set discarding duplicates, a DateTime dropping its time); this is
    /// for the ones you did not.
    ///
    /// It fires on collections too, once for the property rather than once per element:
    /// mapping a <c>List&lt;long&gt;</c> onto a <c>List&lt;int&gt;</c> narrows every item in it.
    ///
    /// Silence it where you have decided the range is safe with
    /// <c>dotnet_diagnostic.SM0010.severity = none</c>, or make it impossible to ignore with
    /// <c>= error</c> — per folder, in .editorconfig.
    /// The real fix is usually to give the destination property the source's type.
    /// </summary>
    public static readonly DiagnosticDescriptor NarrowingConversion = new(
        id: "SM0010",
        title: "Destination property is mapped through a conversion that can change the value",
        messageFormat: "ShiftMapper: '{0}.{1}' is mapped by converting '{2}' to '{3}', which cannot hold every value the source can ({4})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The map is generated and works, and for values inside the destination's range it " +
                     "is exact. Outside that range the result is silently wrong rather than rejected, " +
                     "so this is a warning rather than a note: give the destination the source's type, " +
                     "or NoWarn it once you have decided the range is safe.");

    /// <summary>
    /// SM0011 — a nested object, and no map for it.
    ///
    /// The ONLY ERROR in ShiftMapper, and the severity is the point. Every other report here
    /// describes a property left unmapped, which is a decision the developer may well have made
    /// on purpose. This one describes a property that cannot be mapped YET — the types line up,
    /// the intent is obvious, and the only thing missing is one line:
    ///
    /// <code>CreateMap&lt;Product, ProductDto&gt;();</code>
    ///
    /// A warning would let the build through with the object silently null, and a null nested
    /// object in a response looks exactly like a null in the database. So it stops the build,
    /// and there are only ever two ways forward, both of them one line:
    ///
    ///   * declare the map, and the property is filled;
    ///   * <c>.ForMember(d =&gt; d.Product, opt =&gt; opt.Ignore())</c>, and it is deliberately
    ///     left alone.
    ///
    /// Either way, what the map does with that property is written down somewhere a reader can
    /// find it, which is exactly what a silent skip fails to do.
    ///
    /// DECLARING IT MEANS EITHER DIRECTION. A map is a map however it was registered, so a pair
    /// that exists only because some other map chained <c>ReverseMap()</c> satisfies this just as
    /// well as its own <c>CreateMap</c>, and no error is reported. The message names both,
    /// because which one reads better depends on what is already there: a nested
    /// <c>BrandDto</c> to <c>Brand</c> is usually best filled by adding <c>.ReverseMap()</c> to
    /// the <c>CreateMap&lt;Brand, BrandDto&gt;</c> already in the file, not by declaring a second
    /// map pointing the other way.
    /// </summary>
    public static readonly DiagnosticDescriptor NoMapForNestedProperty = new(
        id: "SM0011",
        title: "Nested object property has no map",
        messageFormat: "ShiftMapper: '{0}.{1}' needs a map from '{2}' to '{3}'. Add CreateMap<{2}, {3}>(), or CreateMap<{3}, {2}>().ReverseMap(), or .ForMember(d => d.{1}, opt => opt.Ignore()) to leave it unmapped on purpose.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The names match and both sides are objects ShiftMapper could map, but nothing in " +
                     "this mapper declares the pair — in either direction, since a map registered by " +
                     "ReverseMap counts the same as one registered by CreateMap. Generating a null here " +
                     "would be indistinguishable from a null in the data, so the build stops until the " +
                     "map is declared or the property is explicitly ignored.");

    /// <summary>
    /// SM0012 — the nested maps form a LOOP, so following them would never finish.
    ///
    /// A <c>BrandDto</c> holding <c>ProductDto</c>s that each hold a <c>BrandDto</c> describes an
    /// object graph with no bottom. There is no depth at which it is complete, and nothing the
    /// generator can quietly pick is the right answer.
    ///
    /// AN ERROR, and specifically an error rather than a truncation, because of what the
    /// alternatives cost. Generated code that follows the loop calls itself forever, and infinite
    /// recursion in .NET is a <c>StackOverflowException</c> — which cannot be caught and takes the
    /// process down rather than failing one request. Cutting the loop at some arbitrary depth
    /// avoids the crash but replaces it with a response whose shape depends on a number nobody
    /// chose, which is its own kind of bug.
    ///
    /// So the loop is refused, and breaking it is one line: <c>opt.Ignore()</c> on whichever side
    /// is the back-reference. That is a decision only the developer can make — which of the two types
    /// is the view and which is the thing being viewed — and writing it down is worth more than
    /// any default.
    /// </summary>
    public static readonly DiagnosticDescriptor CircularNesting = new(
        id: "SM0012",
        title: "Nested object mapping is circular",
        messageFormat: "ShiftMapper: nested mapping never finishes — {0}. Break the loop with .ForMember(d => d.{1}, opt => opt.Ignore()) on whichever side is the back-reference.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "These maps nest each other in a loop, so there is no depth at which the graph is " +
                     "complete. Generated code that followed it would recurse until the stack ran out, " +
                     "so the build stops instead. Ignore the property that points back and the rest of " +
                     "the graph maps as normal.");

    /// <summary>
    /// SM0013 — the constructor exists and one of its parameters has nothing to fill it.
    ///
    /// The message names the PARAMETER, which is the whole reason this is not SM0004. A record
    /// whose <c>createdAt</c> has no counterpart on the entity is one <c>ForMember</c> away from
    /// working, and "BrandDto cannot be constructed" would not have said which one.
    ///
    /// Where several constructors are offered, this describes the one that came CLOSEST — fewest
    /// unfillable parameters — because that is the one the developer is most likely to have
    /// meant.
    /// </summary>
    public static readonly DiagnosticDescriptor ConstructorParameterNotFilled = new(
        id: "SM0013",
        title: "Destination cannot be created because a constructor parameter cannot be filled",
        messageFormat: "ShiftMapper: no Map method was generated to create '{0}' because its constructor parameter '{1}' ({2}) cannot be filled from '{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ShiftMapper matches constructor parameters to source properties by name and " +
                     "converts them the same way it converts members. Give the source a property of " +
                     "that name, or supply the value with ForMember on the destination member the " +
                     "parameter stands for — on a positional record they are the same name.");

    /// <summary>
    /// SM0014 — a <c>required</c> member nothing fills.
    ///
    /// This one is not a property left empty: C# REFUSES an object initializer that leaves a
    /// required member out, so the destination cannot be built at all. Saying so here is the
    /// difference between one sentence and a CS9035 inside a generated file the developer cannot
    /// open.
    ///
    /// A constructor carrying <c>[SetsRequiredMembers]</c> is the author's promise to fill them
    /// itself, and silences this for every member of that type.
    /// </summary>
    public static readonly DiagnosticDescriptor RequiredMemberNotFilled = new(
        id: "SM0014",
        title: "Destination cannot be created because a required member is not mapped",
        messageFormat: "ShiftMapper: no Map method was generated to create '{0}' because its required member '{1}' ({2}) is not mapped",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "C# refuses an object initializer that leaves a required member unset, so an " +
                     "unmapped one stops the whole destination rather than just itself. Map it, fill " +
                     "it with ForMember, or drop `required` if the member is not really required.");

    /// <summary>
    /// SM0015 — the map builds its destination with <c>ConstructUsing</c>, so it cannot be
    /// projected.
    ///
    /// <c>Map</c> works and is unaffected. <c>ProjectTo</c> cannot: a projection has to reach EF
    /// as one expression it can read all the way down, and there is no general way to graft the
    /// mapped properties onto an object a delegate returned.
    ///
    /// INFORMATIONAL, because this is the feature doing what it says rather than a mistake — and
    /// because the alternative reading, that every ConstructUsing is suspect, is not true. It is
    /// here so that "why does ProjectTo throw for this one map" is answered at build time instead
    /// of at run time.
    /// </summary>
    public static readonly DiagnosticDescriptor ConstructUsingIsNotProjectable = new(
        id: "SM0015",
        title: "Map cannot be projected because it builds its destination with ConstructUsing",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' builds its destination with ConstructUsing, so ProjectTo cannot use it; Map is unaffected",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A projection is handed to the database as one expression, and a factory " +
                     "delegate is opaque to it. Records and primary constructors project fine when " +
                     "ShiftMapper picks the constructor itself, so ForMember on the members the " +
                     "arguments stand for is usually the projectable way to say the same thing.");

    /// <summary>
    /// SM0016 — a <c>Condition</c> on a member whose value is settled while the object is being
    /// CREATED, so there is no assignment to guard.
    ///
    /// An ERROR, and the severity is the argument. Both ways to ignore it diverge silently: emit
    /// the assignment anyway and the condition never fires on a create; omit it and the member is
    /// never mapped. That is the shape of SM0011 and SM0012 — a configuration that cannot mean
    /// what it says — rather than of a property left unmapped.
    ///
    /// The generator still emits the member UNCONDITIONED, so the generated file compiles while
    /// the build fails. A project with analyzers turned off then gets the member mapped rather
    /// than a CS error in a file it cannot edit.
    /// </summary>
    public static readonly DiagnosticDescriptor MemberCannotBeConditioned = new(
        id: "SM0016",
        title: "Member cannot be given a Condition",
        messageFormat: "ShiftMapper: '{0}.{1}' cannot be given a Condition because {2}, so there is nothing to leave untouched",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A Condition guards an ASSIGNMENT, and an init-only member, a required member " +
                     "in an object initializer, and a constructor argument are all settled while the " +
                     "object is being built. Drop the Condition, or give the member an ordinary " +
                     "public setter.");

    /// <summary>
    /// SM0017 — the map carries a <c>Condition</c>, so it cannot be projected.
    ///
    /// The same shape as SM0015, one severity louder, and the difference is what the two failures
    /// look like. A <c>ConstructUsing</c> map's projection THROWS the moment it is asked for. A
    /// conditioned one would not: <c>Compose</c> never sees a condition, so the projection would
    /// bind the member unconditionally and quietly hand back different data from <c>Map</c> —
    /// per row, in a list endpoint, with no exception anywhere. Silent divergence is the one
    /// outcome this library's whole design is against, so it is a warning rather than a note.
    /// </summary>
    public static readonly DiagnosticDescriptor ConditionIsNotProjectable = new(
        id: "SM0017",
        title: "Map cannot be projected because a member carries a Condition",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' assigns '{2}' behind a Condition, so ProjectTo cannot use it; Map is unaffected",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A projection is one member initializer handed to the database, and there is no " +
                     "way to leave one binding out per row. Asking for the projection throws a " +
                     "message naming this map rather than returning data that disagrees with Map.");

    /// <summary>
    /// SM0018 — the map carries a <c>BeforeMap</c> or <c>AfterMap</c>, so it cannot be projected.
    ///
    /// A WARNING, on the same reasoning as SM0017 and not SM0015's note. <c>ConstructUsing</c>'s
    /// projection throws the moment it is asked for, so nobody can be misled by it; a hook's would
    /// not. <c>Compose</c> has no idea a hook exists, so the projection would be built, run, and
    /// hand back rows the hook never touched — silently, per row, in a list endpoint, with
    /// <c>Map</c> and <c>ProjectTo</c> disagreeing about the same map.
    ///
    /// THE USUAL FIX IS NOT TO SILENCE IT. A value worked out from the SOURCE belongs in a
    /// <c>ForMember</c>, which projects; <c>AfterMap</c> earns its place only where the finished
    /// DESTINATION is genuinely needed, and those maps are the ones you <c>Map</c> rather than
    /// project.
    /// </summary>
    public static readonly DiagnosticDescriptor HookIsNotProjectable = new(
        id: "SM0018",
        title: "Map cannot be projected because it runs an in-memory hook",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' runs {2} over its destination, so ProjectTo cannot use it; Map is unaffected",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A projection is one expression handed to the database, and there is no " +
                     "statement in it for your code to be. Where the value can be worked out from " +
                     "the source, a ForMember says the same thing and keeps the projection.");

    /// <summary>
    /// SM0019 — configuration on a <c>ConvertUsing</c> map, which replaces the whole map and so
    /// ignores it.
    ///
    /// It matters because it LOOKS configured. A <c>ForMember</c> written above a
    /// <c>ConvertUsing</c> reads as though it refines the map, and refines nothing — and unlike
    /// most mistakes this one leaves no trace at runtime to work backwards from, because the
    /// member it names is simply never assigned by anything.
    /// </summary>
    public static readonly DiagnosticDescriptor ConvertUsingIgnoresConfiguration = new(
        id: "SM0019",
        title: "Configuration has no effect because ConvertUsing replaces the whole map",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' uses ConvertUsing, which replaces the whole map, so its {2} does nothing",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ConvertUsing is the whole map: no member is matched, converted or assigned " +
                     "afterwards. Delete the configuration it ignores, or fold what it does into " +
                     "the ConvertUsing expression.");

    /// <summary>
    /// SM0020 — a member FLATTENING filled, and the path it walked.
    ///
    /// INFORMATIONAL, and it is the price of the feature rather than a complaint about it.
    /// Flattening is a GUESS: nothing in <c>CustomerName</c> says it means <c>Customer.Name</c>
    /// rather than a column somebody has not added yet. The developer asked for the guess by
    /// turning it on; this is how they read back which guesses were made, in the IDE and under
    /// <c>dotnet build -v d</c>, without a normal build filling up with them.
    ///
    /// Raise it where flattening is on and the maps matter:
    /// <c>dotnet_diagnostic.SM0020.severity = warning</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor FlattenedMember = new(
        id: "SM0020",
        title: "Destination property is filled by flattening",
        messageFormat: "ShiftMapper: '{0}.{1}' is filled by flattening, from '{2}.{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Flattening walks into the source when no property carries the destination " +
                     "member's own name. It is on by default and can be turned off per map or for a " +
                     "whole mapper, and every member it fills is named here so the conventions can " +
                     "be checked rather than trusted.");

    /// <summary>
    /// SM0021 — more than one path flattens to the same member, so none is taken.
    ///
    /// The member is left unmapped and reported. A source carrying both <c>Order</c> (with a
    /// <c>CustomerName</c>) and <c>OrderCustomer</c> (with a <c>Name</c>) makes
    /// <c>OrderCustomerName</c> a genuine question, and picking the first would be exactly the
    /// silent guess this library exists not to make — the same answer SM0007 gives two source
    /// names differing only by case.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousFlattening = new(
        id: "SM0021",
        title: "Destination property could be flattened more than one way",
        messageFormat: "ShiftMapper: '{0}.{1}' is not mapped because flattening resolves it more than one way: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two source paths lead to the same destination member, and only you can say " +
                     "which was meant. Name it with ForMember, or rename one of the source " +
                     "properties so the split is unambiguous.");

    /// <summary>
    /// SM0022 — an <c>IncludeBase</c> naming a map this mapper does not declare.
    ///
    /// Worth reporting louder than it looks, because nothing else goes wrong: the derived map
    /// keeps doing exactly what it did before, so a base map that was renamed or never written
    /// takes its whole configuration with it in silence. Nothing is inherited, and every member
    /// the base was going to speak for falls back to the conventions.
    /// </summary>
    public static readonly DiagnosticDescriptor UnresolvedBaseMap = new(
        id: "SM0022",
        title: "IncludeBase names a map that does not exist",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' includes a base map from '{2}', which this mapper does not declare, so nothing is inherited",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "IncludeBase takes over the ForMember configuration of a map between the base " +
                     "types, so that map has to exist somewhere in this mapper. Add the CreateMap, " +
                     "or correct the type arguments.");

    /// <summary>
    /// SM0023 — an <c>Include</c> that cannot dispatch: the types do not line up, or the derived
    /// pair has no map of its own.
    ///
    /// The branch is simply not emitted, which is the quiet failure this exists to make loud —
    /// the base map goes on mapping a derived value as though it were the base, and everything the
    /// derived type knows is dropped without a word.
    /// </summary>
    public static readonly DiagnosticDescriptor DerivedPairCannotDispatch = new(
        id: "SM0023",
        title: "Include cannot dispatch to the derived pair",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' cannot dispatch to '{2}' to '{3}' because {4}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Include needs the derived source to derive from this map's source, the derived " +
                     "destination to derive from its destination, and the derived pair to have its " +
                     "own CreateMap. Without all three there is nothing to dispatch to.");

    /// <summary>
    /// SM0024 — a map with <c>Include</c> cannot be projected.
    ///
    /// A projection has ONE element type, fixed when the query is written, and no per-row type
    /// test a provider could translate. A warning rather than a note for the reason SM0017 and
    /// SM0018 are: the failure would otherwise be silent — the projection would build every row
    /// as the BASE destination and quietly disagree with <c>Map</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor IncludeIsNotProjectable = new(
        id: "SM0024",
        title: "Map cannot be projected because it dispatches on the runtime type",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' dispatches on the source's runtime type through Include, so ProjectTo cannot use it; Map is unaffected",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Project the derived type directly instead — OfType<TDerived>() then " +
                     "ProjectTo<TDerivedDestination>() — which is one query and says which shape " +
                     "you meant.");

    /// <summary>
    /// SM0025 — an <c>As</c> that cannot stand in: the concrete type is not assignable to the
    /// destination, or has no map of its own.
    ///
    /// The first case is not merely wrong but unemittable — the generated method returns the
    /// destination type — so the <c>As</c> is ignored entirely and the destination goes back to
    /// being whatever it was, usually SM0004.
    /// </summary>
    public static readonly DiagnosticDescriptor ConcreteTypeCannotStandIn = new(
        id: "SM0025",
        title: "As names a type that cannot stand in for the destination",
        messageFormat: "ShiftMapper: the map from '{0}' to '{1}' cannot be built as '{2}' because {3}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "As names the concrete type to build for an interface or abstract destination. " +
                     "It has to be assignable to that destination and needs its own CreateMap, or " +
                     "there is nothing to redirect to.");

    /// <summary>
    /// SM0026 — an open generic map the generator will not close.
    ///
    /// One type parameter on each side is the only shape with a single obvious pairing. With two
    /// there is no answer to choose, only a combinatorial one, so the declaration is refused and
    /// says so rather than quietly producing nothing.
    /// </summary>
    public static readonly DiagnosticDescriptor OpenGenericNotClosed = new(
        id: "SM0026",
        title: "Open generic map was not closed",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)) is closed for every pair " +
                     "you already map, which needs exactly one type parameter on each side. Write " +
                     "the closed CreateMap calls out instead.");

    /// <summary>
    /// SM0027 — one type pair declared by a mapper class in THIS project AND by a referenced
    /// package's.
    ///
    /// A warning rather than an error because there IS a defined answer, and both halves of the
    /// library give the same one: this project's declaration wins, in the generated code and in
    /// the runtime store alike — overriding a package's map is a thing to do on purpose. What it
    /// cannot be is silent.
    /// </summary>
    public static readonly DiagnosticDescriptor IncludedMapDeclaredTwice = new(
        id: "SM0027",
        title: "A map is declared both in this project and by a referenced package",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "This project's declaration is the one that runs. Delete it if you did not " +
                     "mean to override the package's map.");

    /// <summary>
    /// SM0028 — a mapper or pack from a referenced assembly that carries no declaration metadata.
    ///
    /// AN ERROR: a package that says nothing would otherwise be included, registered or added and
    /// contribute nothing, in silence — the exact outcome this library exists to prevent. The one
    /// failure with no consuming-side workaround, so the message says what the limitation IS.
    /// </summary>
    public static readonly DiagnosticDescriptor DeclarationsNotInMetadata = new(
        id: "SM0028",
        title: "A referenced assembly carries no ShiftMapper declaration metadata",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A package whose mappers or packs are used from another project must be " +
                     "built with the ShiftMapper generator referenced as an analyzer, which is what " +
                     "writes its declarations into the assembly where a consuming generator can " +
                     "read them.");

    /// <summary>
    /// SM0030 — a map uses a global conversion that has no query form.
    ///
    /// A WARNING, on the same terms as SM0017 and SM0024 rather than the Info SM0015 gets: the
    /// person who loses the projection is not the person who chose to. Whoever wrote the
    /// CreateConversion made a decision about a type pair, possibly in a framework; whoever writes
    /// a map that happens to touch that pair inherits the consequence without having asked for it,
    /// and is the one who needs to be told.
    /// </summary>
    public static readonly DiagnosticDescriptor ConversionHasNoQueryForm = new(
        id: "SM0030",
        title: "Map cannot be projected because a conversion has no query form",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CreateConversion was given a memory form but no query form, which declares " +
                     "that the pair cannot be translated to SQL. Supply a query expression, or use " +
                     "Map rather than ProjectTo for maps that touch the pair.");

    /// <summary>
    /// SM0031 — two packs at the same distance from a map declaring a conversion for one pair.
    ///
    /// AN ERROR, unlike almost everything else here, because there is no answer to pick. Whichever
    /// won, half the maps in the application would convert the other way and nobody reading either
    /// pack could see why. The fix is a decision, and it has to be made by a person — a
    /// declaration nearer to the map settles it.
    /// </summary>
    public static readonly DiagnosticDescriptor DeclaredConversionConflict = new(
        id: "SM0031",
        title: "Two packs declare a conversion for the same type pair",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Remove one of the packs, or declare the pair on the mapper with " +
                     "CreateConversion, which wins over both.");

    /// <summary>
    /// SM0032 — a declaration a referenced assembly got wrong.
    ///
    /// A WARNING rather than an error: the assembly compiled, so the mistake belongs to the package
    /// author rather than to whoever is building now, and failing their build over it would leave
    /// them with nothing to do but wait. It is loud enough to report upstream.
    /// </summary>
    public static readonly DiagnosticDescriptor DeclaredConversionMalformed = new(
        id: "SM0032",
        title: "A declared conversion could not be read",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A package's build records each CreateConversion as a " +
                     "ShiftMapperDeclaredConversion attribute naming the declaring type, the source " +
                     "type and the destination type. One that does not have that shape was written " +
                     "by a different version of the generator, and the pair it described is not " +
                     "applied.");

    /// <summary>
    /// SM0033 — a package built against a DIFFERENT contract than this generator reads.
    ///
    /// Its declarations are ignored rather than half-read. A generator that guessed at a shape it
    /// does not know would emit code that fails to compile in a file the developer cannot edit,
    /// which is the worst outcome available.
    /// </summary>
    public static readonly DiagnosticDescriptor DeclaredContractMismatch = new(
        id: "SM0033",
        title: "A referenced assembly declares a different ShiftMapper contract",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Build the referenced package and this project against the same ShiftMapper " +
                     "version.");

    /// <summary>
    /// SM0034 — a member convention claimed a member and could not fill it.
    ///
    /// The member is left UNMAPPED rather than filled some other way. Falling back to name matching
    /// would quietly map it to the very thing the convention was written to override, which is the
    /// failure a convention exists to prevent.
    /// </summary>
    public static readonly DiagnosticDescriptor MemberConventionFailed = new(
        id: "SM0034",
        title: "A member convention could not fill the member it claimed",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Check the convention's Fill paths against the source type, or write a " +
                     "ForMember for this member, which always wins over a convention.");

    /// <summary>
    /// SM0038 — a member convention that fills nothing.
    ///
    /// <para>A WARNING that exists to stop a LIE. A convention with no readable <c>Fill</c> does
    /// nothing, the members it was written for fall through to ordinary name matching, and the build
    /// then reports SM0001 — "'Source' has no readable property named 'Brand'" — about the very
    /// member somebody wrote a convention for. Naming the real cause is also what makes SM0001's
    /// code fix safe: without it, one click would Ignore the member and cement the wrong answer.</para>
    /// </summary>
    public static readonly DiagnosticDescriptor MemberConventionIsEmpty = new(
        id: "SM0038",
        title: "This member convention fills nothing",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A member convention is a rule about how to fill a member. One with no Fill " +
                     "entries has nothing to say, so the members it claims are matched by name " +
                     "instead - which is what the convention existed to override.");


    /// <summary>
    /// SM0037 — ProjectTo called on a pair that cannot be projected.
    ///
    /// <para>A WARNING rather than an error, because the call is not wrong in itself — it is a
    /// query that will throw when it runs. Reported at the CALL, which is the one place the other
    /// projection rules cannot reach: they describe a mapper where it is declared, and whoever
    /// writes the query is usually looking at a different file.</para>
    ///
    /// <para>It reasons from positive evidence only. A pair naming a type PARAMETER says nothing
    /// about which map is meant, so nothing is said about it — which is what keeps a generic
    /// repository from being accused of a mistake it has not made.</para>
    /// </summary>
    public static readonly DiagnosticDescriptor ProjectToIsNotSupported = new(
        id: "SM0037",
        title: "ProjectTo cannot be used for this pair",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The map exists and Map works, but it needs a statement that no projection can " +
                     "hold, so this query would throw when it runs.");


    /// <summary>
    /// SM0036 — a map that cannot be projected because something it NESTS cannot.
    ///
    /// <para>A warning, like the other projection refusals, and worded to name the CHILD: that is
    /// the map somebody has to go and fix, and naming the parent would describe the symptom. Until
    /// this existed a parent inherited the hole in silence, emitted a projection anyway, and the
    /// query failed at run time naming a pair nobody had asked about.</para>
    /// </summary>
    public static readonly DiagnosticDescriptor NestedMapIsNotProjectable = new(
        id: "SM0036",
        title: "Map cannot be projected because a map it nests cannot",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A projection is one expression, built from the projections of the maps it " +
                     "nests. If one of those cannot be expressed as an expression, neither can this " +
                     "one. Map is unaffected.");


    /// <summary>
    /// SM0035 — a declaration written somewhere its position cannot be honoured.
    ///
    /// <para>AN ERROR, and the only sensible severity. The generator reads declarations from syntax
    /// and bakes them once; a declaration inside an <c>if</c>, a loop, a <c>switch</c>, a lambda or a
    /// local function is therefore applied UNCONDITIONALLY, discarding the very thing the developer
    /// wrote. Emitting a mapper that does not do what the source says, and saying nothing, is the
    /// exact failure this library refuses to have — so the build stops instead.</para>
    ///
    /// <para>It keys on statement POSITION, never on reachability: a call in a helper method the
    /// constructor calls is fine, and proving a helper is never called is not decidable from one
    /// file.</para>
    /// </summary>
    public static readonly DiagnosticDescriptor DeclarationNotBakeable = new(
        id: "SM0035",
        title: "This declaration cannot be honoured where it is written",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Declarations are read at compile time and baked into the generated mapper, so " +
                     "the code around one cannot decide whether it applies. Written inside a " +
                     "condition, a loop or a lambda, it would be applied unconditionally and the " +
                     "surrounding code silently ignored.");


    /// <summary>SM0005 — a mapper class the generated mapper cannot include, so nothing it declares is generated.</summary>
    public static readonly DiagnosticDescriptor MapperSkipped = new(
        id: "SM0005",
        title: "No mapping code was generated for this mapper",
        messageFormat: "ShiftMapper: no mapping code was generated for '{0}' because {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The class derives from ShiftMapperBase, so it was clearly meant to be a mapper, " +
                     "but the generated mapper cannot construct it.");





    /// <summary>
    /// SM0042 — one pair declared twice, with nothing to choose between the two: two included
    /// mappers that each wrote it, or two CreateMap calls in one mapper.
    ///
    /// AN ERROR, where SM0027 is a warning: there the mapper's own declaration is nearer and wins,
    /// here neither declaration is nearer than the other, and picking by order would make a map
    /// silently depend on which include was written first.
    /// </summary>
    public static readonly DiagnosticDescriptor MapDeclaredTwice = new(
        id: "SM0042",
        title: "A map is declared twice with nothing to choose between the two",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Declare the pair once. When two included mappers both declare it, keep it in " +
                     "one of them, or declare it on the including mapper, whose own declaration wins.");

    /// <summary>
    /// SM0043 — a referenced package shared a pack with this project, and this registration got it.
    ///
    /// INFO, and deliberately not silent: a shared pack is the one declaration that reaches a
    /// project without the project naming the type, so the build says which packs arrived and from
    /// where. It sits at the furthest level; anything the registration wrote itself still wins.
    /// </summary>
    public static readonly DiagnosticDescriptor SharedPackApplied = new(
        id: "SM0043",
        title: "A referenced package shared a pack or a mapper class with this project",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A package wrote o.ShareConversions<T>() in its own registration, so the pack " +
                     "is applied to every mapper this project registers, after everything the " +
                     "project wrote itself. Nothing to do; a rule of your own for the same pair " +
                     "wins.");

    /// <summary>
    /// SM0044 — a pack shared with referencing projects that they could not name.
    ///
    /// AN ERROR in the sharing project's build: the referencing project's generated code names the
    /// pack in an assembly attribute and in every conversion call, and a non-public type there is a
    /// compile error in a file nobody can edit. Reported where it can be fixed.
    /// </summary>
    public static readonly DiagnosticDescriptor SharedPackNotPublic = new(
        id: "SM0044",
        title: "A shared pack or mapper class must be public",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Make the pack public, or add it with AddConversions for this project's " +
                     "mappers alone.");


    /// <summary>
    /// SM0046 — a registration line that changes nothing: an <c>AddMapper</c> under
    /// <c>MapperDiscovery.All</c>, or a <c>Discovery</c> set differently in two calls.
    ///
    /// <para>A warning: nothing is wrong with the generated mapper, but the line says the developer
    /// expected something to happen, and the something is a discovery mode away.</para>
    /// </summary>
    public static readonly DiagnosticDescriptor RegistrationHasNoEffect = new(
        id: "SM0046",
        title: "This registration line has no effect",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "AddMapper decides which mapper classes are generated only under " +
                     "MapperDiscovery.LocalAndRegistered or MapperDiscovery.Registered; under All, the " +
                     "default, every mapper class the project can see is generated already. The " +
                     "discovery mode is one setting for the whole project.");

    /// <summary>
    /// SM0047 — a map a framework's marker declared IMPLICITLY for a closing type is replaced by a
    /// <c>CreateMap</c> for the same pair. Informational: that is the customization path, and the
    /// note is here so the replacement is visible in the build rather than silent.
    /// </summary>
    public static readonly DiagnosticDescriptor ImplicitMapReplaced = new(
        id: "SM0047",
        title: "An implicit map is replaced by an explicit declaration",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A marked framework type declares maps for every type that closes it. A CreateMap " +
                     "for the same pair, anywhere the project can see, takes the pair over in full — " +
                     "the implicit map, and any configuration surface that customized it, no longer apply.");

    /// <summary>
    /// SM0048 — automatic nesting met a member whose pair is an ancestor of the map being built,
    /// and left it at its default rather than map a type inside itself.
    /// </summary>
    public static readonly DiagnosticDescriptor ImplicitNestingCycle = new(
        id: "SM0048",
        title: "Automatic nesting stopped at a cycle",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A marker's Nested depth declares implicit maps for nested members. A member whose pair " +
                     "is already being mapped above it would nest the map inside itself, so it is left " +
                     "unmapped; a ForMember maps it if it is wanted.");

    /// <summary>
    /// SM0049 — the update overload rebuilds every nested collection with new objects. Harmless for
    /// a DTO; for tracked rows with an identity of their own it means duplicated or orphaned rows,
    /// which is what the note is for.
    /// </summary>
    public static readonly DiagnosticDescriptor UpdateRebuildsNestedCollection = new(
        id: "SM0049",
        title: "The update overload replaces a nested collection with new objects",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Map(source, destination) assigns a nested collection member a NEW collection of newly " +
                     "mapped objects; nothing is matched against what the destination already held. Where the " +
                     "elements are rows with their own identity, reconcile them in an AfterMap or in the caller " +
                     "and Ignore the member.");

    /// <summary>SM0050 — two types configure the same implicit map through configuration surfaces.</summary>
    public static readonly DiagnosticDescriptor PairConfiguredTwice = new(
        id: "SM0050",
        title: "A pair is configured by two configuration surfaces",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An implicit map is one map per assembly. Two Mapping(m => ...) lambdas customizing the same " +
                     "pair from two types have nothing to choose between them; which one ran would depend on " +
                     "construction order.");

    /// <summary>SM0051 — a configuration surface customizes a pair a mapper class declares; the class wins.</summary>
    public static readonly DiagnosticDescriptor SurfaceConfigurationIgnored = new(
        id: "SM0051",
        title: "A configuration surface is ignored because a mapper class declares the pair",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A CreateMap for a pair replaces the implicit map in full, including whatever a " +
                     "Mapping(m => ...) lambda said about it. The lambda's lines for that pair do nothing.");

    /// <summary>SM0052 — a configuration surface customizes a pair for which no map is declared at all.</summary>
    public static readonly DiagnosticDescriptor SurfaceConfiguresNoMap = new(
        id: "SM0052",
        title: "A configuration surface configures a pair nothing declares",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A Mapping(m => ...) lambda names a pair that no marker the enclosing type closes declares, " +
                     "and no CreateMap declares either. Nothing is generated for it, so the configuration does nothing.");

    /// <summary>SM0053 — a marker could not be applied to a closing type.</summary>
    public static readonly DiagnosticDescriptor ImplicitMarkerNotApplied = new(
        id: "SM0053",
        title: "An implicit map marker could not be applied",
        messageFormat: "ShiftMapper: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A ShiftMapperDeclaresMap marker names its source and destination by the marked type's type " +
                     "parameter names, or \"this\" for the closing type. A name that resolves to nothing " +
                     "concrete on a closing type declares no map for it.");

    public static readonly ImmutableArray<DiagnosticDescriptor> All = ImmutableArray.Create(
        NoSourceProperty,
        NotConvertible,
        SetterNotAccessible,
        CannotConstructDestination,
        MapperSkipped,
        NoSourcePropertyInReverseMap,
        AmbiguousCaseInsensitiveMatch,
        LossyConversion,
        ParsedConversion,
        NarrowingConversion,
        NoMapForNestedProperty,
        CircularNesting,
        ConstructorParameterNotFilled,
        RequiredMemberNotFilled,
        ConstructUsingIsNotProjectable,
        MemberCannotBeConditioned,
        ConditionIsNotProjectable,
        HookIsNotProjectable,
        ConvertUsingIgnoresConfiguration,
        FlattenedMember,
        AmbiguousFlattening,
        UnresolvedBaseMap,
        DerivedPairCannotDispatch,
        IncludeIsNotProjectable,
        ConcreteTypeCannotStandIn,
        OpenGenericNotClosed,
        IncludedMapDeclaredTwice,
        DeclarationsNotInMetadata,
        ConversionHasNoQueryForm,
        DeclaredConversionConflict,
        DeclaredConversionMalformed,
        DeclaredContractMismatch,
        MemberConventionFailed,
        DeclarationNotBakeable,
        NestedMapIsNotProjectable,
        ProjectToIsNotSupported,
        MemberConventionIsEmpty,
        MapDeclaredTwice,
        SharedPackApplied,
        SharedPackNotPublic,
        RegistrationHasNoEffect,
        ImplicitMapReplaced,
        ImplicitNestingCycle,
        UpdateRebuildsNestedCollection,
        PairConfiguredTwice,
        SurfaceConfigurationIgnored,
        SurfaceConfiguresNoMap,
        ImplicitMarkerNotApplied);
}

