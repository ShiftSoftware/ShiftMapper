using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ShiftMapper;

/// <summary>
/// Base class for a mapper.
///
/// You write a small partial class that derives from this and declares its maps in the
/// CONSTRUCTOR; the ShiftMapper source generator writes the other half of that same class,
/// containing the real Map methods.
///
/// <code>
/// public partial class AppMapper : ShiftMapperBase
/// {
///     private readonly ICurrencyService _currency;
///
///     public AppMapper(ICurrencyService currency)
///     {
///         _currency = currency;           // ordinary constructor injection
///
///         CreateMap&lt;Brand, BrandDto&gt;();   // declare your maps here
///         CreateMap&lt;Stock, StockDto&gt;()     // chain ReverseMap for both directions
///             .ReverseMap();
///     }
/// }
/// </code>
///
/// Register it with <c>builder.Services.AddShiftMapper&lt;AppMapper&gt;();</c>.
///
/// The generator writes the real mapping code as INSTANCE methods on your class, plus
/// EXTENSION methods that forward to them. Both spellings do the same work, so use
/// whichever reads better where you are:
/// <code>
/// // instance
/// var dto = mapper.Map&lt;BrandDto&gt;(brand);
/// mapper.Map(brand, existingDto);
///
/// // extension — the mapper comes last
/// var dto = brand.Map&lt;BrandDto&gt;(mapper);
/// brand.Map(existingDto, mapper);
/// </code>
/// </summary>
public abstract class ShiftMapperBase
{
    private IServiceProvider? _services;

    /// <summary>
    /// The application's service provider. You never set this — <c>AddShiftMapper</c>
    /// assigns it when the mapper is resolved from DI, so custom mappings (and the
    /// generated extension methods that run through this mapper) can resolve whatever
    /// services they need.
    ///
    /// <code>var formatter = mapper.Services.GetRequiredService&lt;ICurrencyFormatter&gt;();</code>
    ///
    /// Prefer plain constructor injection when you know the dependency up front; this is
    /// for the cases where you only discover it while mapping.
    ///
    /// TIMING: your constructor runs BEFORE this is assigned, because the object has to
    /// exist before anything can be set on it. So do not touch Services from the
    /// constructor — inject what you need there instead. It is ready everywhere else.
    /// </summary>
    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException(
            $"ShiftMapper: no service provider has been set on '{GetType().Name}'. That happens " +
            "when the mapper is constructed directly instead of resolved from DI. Register it " +
            $"with services.AddShiftMapper<{GetType().Name}>() and inject it.");

    /// <summary>
    /// Called by <c>AddShiftMapper</c> when the mapper is created. Internal on purpose:
    /// this is the library's job, never the developer's.
    /// </summary>
    internal void SetServices(IServiceProvider services) => _services = services;

    /// <summary>
    /// The expressions handed to <c>opt.MapFrom</c>, kept so they can be used at runtime.
    ///
    /// Almost all of the declaration API is compile-time only — the generator reads your
    /// <c>CreateMap</c> chain and then the calls do nothing. <c>opt.MapFrom</c> cannot work that
    /// way: it is given an <c>Expression&lt;&gt;</c> tree that the compiler built in YOUR file,
    /// with your fields captured and your usings resolved, and reusing that tree is far more
    /// robust than trying to copy your code into the generated file as text. So it survives
    /// here, and the generated code reads it from this property.
    ///
    /// Protected because the generated half of your mapper is the only thing that should touch
    /// it; that code lives in the same partial class, so protected is enough.
    /// </summary>
    protected MapCustomizations Customizations
    {
        get
        {
            EnsureProfiles();
            return _customizations;
        }
    }

    /// <summary>
    /// The same store, reached WITHOUT materialising profiles.
    ///
    /// <para>The distinction is the whole of the profile timing problem. <c>CreateMap</c> runs
    /// inside your constructor and must not trigger profile construction, because a profile may
    /// need services and <see cref="Services"/> is not assigned until after your constructor
    /// returns. The generated code, which reads <see cref="Customizations"/>, only ever runs when
    /// something is actually mapped — by which time everything is in place.</para>
    /// </summary>
    private readonly MapCustomizations _customizations;

    /// <summary>The profile types <see cref="AddProfile{TProfile}"/> recorded, in order.</summary>
    private List<Type>? _profileTypes;

    /// <summary>Set once the profiles have been constructed and folded in.</summary>
    private bool _profilesMaterialised;

    /// <summary>
    /// Hands the customization store the mapper CLASS it belongs to, which is what lets a
    /// compiled customization be reused by every later instance of the same mapper instead of
    /// being compiled again per request. <c>GetType()</c> is the runtime type, so a mapper that
    /// derives from another gets its own entries rather than sharing its base's.
    /// </summary>
    protected ShiftMapperBase() => _customizations = new MapCustomizations(GetType());

    /// <summary>
    /// Registers a conversion for a TYPE PAIR, applied to every member of those types in every
    /// map — including maps written in code that has never heard of it.
    ///
    /// <code>
    /// CreateConversion&lt;string, List&lt;FileDTO&gt;&gt;(
    ///     memory: json =&gt; FileHelpers.Parse(json),
    ///     query:  json =&gt; ShiftJson.Files(json));
    /// </code>
    ///
    /// <para><b>THIS IS THE ONE THAT SCALES.</b> A <c>ForMember</c> is written per member per map;
    /// this is written once and answers wherever the pair appears — directly, as the element type
    /// of a collection, as the value type of a dictionary, or inside a nested map. It is consulted
    /// by the same resolver that handles <c>int</c> to <c>string</c>, just before it would have
    /// given up and reported SM0002, so it EXTENDS the built-in table rather than replacing it. A
    /// <c>ForMember</c> on a particular member still wins over both.</para>
    ///
    /// <para><b>TWO FORMS, BECAUSE THERE ARE TWO BACKENDS.</b> <paramref name="memory"/> is a
    /// delegate the <c>Map</c> methods call, and it may do anything C# can do.
    /// <paramref name="query"/> is an expression tree spliced into the projection, so it must be
    /// something a database can run — which usually means writing the same conversion a second
    /// way, in the vocabulary EF understands.</para>
    ///
    /// <para><b>THE QUERY FORM IS OPTIONAL, AND OMITTING IT IS A DECISION.</b> It says the pair
    /// cannot be projected. Any map that uses the pair then loses its projection, and the BUILD
    /// says so (SM0030) naming the pair and the member — which is the whole reason to do this at
    /// compile time rather than discover it when a query runs.</para>
    ///
    /// <para><b>ASSIGNABILITY, NOT IDENTITY.</b> A conversion registered for a base type answers
    /// for everything that derives from it, so one rule covers an entity hierarchy. Where two
    /// registrations could both answer, the nearest by inheritance wins, so a general rule can
    /// always be narrowed for a particular type.</para>
    ///
    /// <para>Declare it in a mapper's constructor or, better, in a
    /// <see cref="ShiftMapperProfile"/> that several mappers add.</para>
    /// </summary>
    /// <param name="memory">The conversion the in-memory maps run.</param>
    /// <param name="query">
    /// The same conversion as an expression tree, for projections. Omit it to declare that this
    /// pair cannot be projected.
    /// </param>
    protected void CreateConversion<TSource, TDestination>(
        Func<TSource, TDestination> memory,
        Expression<Func<TSource, TDestination>>? query = null)
    {
        if (memory is null)
            throw new ArgumentNullException(nameof(memory));

        _customizations.RegisterConversion(typeof(TSource), typeof(TDestination), memory, query);
    }

    /// <summary>
    /// Declares a MEMBER-SHAPED rule: how to fill any destination member of
    /// <typeparamref name="TMember"/>, from source members the destination member's own NAME picks
    /// out.
    ///
    /// <code>
    /// CreateMemberConvention&lt;ShiftEntitySelectDTO&gt;()
    ///     .NameFrom&lt;ShiftEntityKeyAndNameAttribute&gt;("Text")
    ///     .Fill(d =&gt; d.Value, "{Member}ID")
    ///     .Fill(d =&gt; d.Text,  "{Member}.{NameOf}");
    /// </code>
    ///
    /// <para>It answers a question <c>CreateConversion</c> cannot. A conversion is handed ONE value;
    /// this needs two source members, and which two depends on the member's name. See
    /// <see cref="MemberConventionExpression{TMember}"/> for what the vocabulary is and why it is
    /// deliberately small.</para>
    ///
    /// <para>Declare it here or in a <see cref="ShiftMapperProfile"/>, which carries it across an
    /// assembly like everything else. An explicit <c>ForMember</c> always wins over it.</para>
    ///
    /// <para>Compile-time only, like <c>CreateMap</c> — the generator reads the chain and the
    /// calls do nothing.</para>
    /// </summary>
    protected MemberConventionExpression<TMember> CreateMemberConvention<TMember>() => new();

    /// <summary>
    /// Declares that this mapper also uses the maps written in a
    /// <see cref="ShiftMapperProfile"/>. Call it from your constructor, like <c>CreateMap</c>.
    ///
    /// <code>
    /// public partial class AppMapper : ShiftMapperBase
    /// {
    ///     public AppMapper()
    ///     {
    ///         AddProfile&lt;CatalogProfile&gt;();
    ///         AddProfile&lt;InvoiceProfile&gt;();
    ///     }
    /// }
    /// </code>
    ///
    /// <para>Nothing about the generated code changes. The profile's maps become THIS mapper's
    /// maps — <c>mapper.Map&lt;BrandDto&gt;(brand)</c> and <c>ProjectTo</c> work exactly as if the
    /// <c>CreateMap</c> had been written here. A profile is a place to write declarations, not a
    /// second mapper, and it never gets Map methods of its own.</para>
    ///
    /// <para>UNLIKE the rest of the declaration API, this one does something at run time as well
    /// as at compile time. The generator reads it to find the maps; the call itself records the
    /// type so the profile can be CONSTRUCTED later — which is what puts its <c>MapFrom</c> trees
    /// where the generated code looks for them.</para>
    ///
    /// <para>"Later" rather than "now" is deliberate: a profile may take constructor dependencies,
    /// and this mapper's <see cref="Services"/> is not assigned until after its own constructor
    /// returns. So profiles are built on first use — through DI when the mapper came from DI, and
    /// through the parameterless constructor otherwise.</para>
    ///
    /// <para>A pair declared BOTH here and in a profile keeps the version written here, and the
    /// build reports the clash (SM0027) rather than leaving you to find out which won.</para>
    /// </summary>
    protected void AddProfile<TProfile>() where TProfile : ShiftMapperProfile
    {
        (_profileTypes ??= new List<Type>()).Add(typeof(TProfile));

        // A profile added after something has already been mapped would otherwise be ignored in
        // silence. It cannot happen from a constructor, which is the only supported place, but
        // this makes the unsupported one loud instead of subtle.
        _profilesMaterialised = false;
    }

    /// <summary>
    /// Builds each profile once and folds its registrations into this mapper's store.
    ///
    /// <para>Profiles are resolved from <see cref="Services"/> when there is one, so a profile can
    /// take the same constructor dependencies a mapper can. When the mapper was built by hand
    /// rather than by DI there is no provider to ask, and a parameterless profile still works —
    /// which keeps a plain <c>new AppMapper()</c> usable in a test.</para>
    /// </summary>
    private void EnsureProfiles()
    {
        if (_profilesMaterialised)
            return;

        // Set FIRST. A profile constructor that reached back into this mapper would otherwise
        // re-enter here and build the same profiles again, forever.
        _profilesMaterialised = true;

        if (_profileTypes is not null)
        {
            foreach (Type profileType in _profileTypes)
                _customizations.MergeFrom(CreateProfile(profileType).Customizations);
        }

        // AFTER the profiles, so a conversion the application declared in its own source keeps its
        // place: RegisterQueryConversion does not overwrite, and the generator resolves the pair by
        // the same precedence. Near beats far, in both halves.
        RegisterDeclaredConversions(_customizations);
    }

    /// <summary>
    /// Hands the store the QUERY forms of conversions declared by REFERENCED ASSEMBLIES —
    /// overridden by the generated half of the mapper, and empty here.
    ///
    /// <para>Only the query forms. A conversion declared through <c>[ShiftMapperConversions]</c>
    /// has its memory form called DIRECTLY by the generated code, by name, because the generator
    /// read that name out of metadata at compile time. An expression tree is the one thing a name
    /// cannot stand in for, so it is the one thing that has to arrive here.</para>
    /// </summary>
    protected virtual void RegisterDeclaredConversions(MapCustomizations customizations)
    {
    }

    private ShiftMapperProfile CreateProfile(Type profileType)
    {
        object? profile = _services?.GetService(profileType);

        if (profile is null)
        {
            try
            {
                profile = Activator.CreateInstance(profileType);
            }
            catch (MissingMethodException error)
            {
                throw new InvalidOperationException(
                    $"ShiftMapper: the profile '{profileType.Name}' takes constructor arguments, so " +
                    $"it has to come from DI, but '{GetType().Name}' was not resolved from a service " +
                    $"provider. Register the profile with services.AddTransient<{profileType.Name}>() " +
                    "and resolve the mapper through AddShiftMapper, or give the profile a " +
                    "parameterless constructor.",
                    error);
            }
        }

        return (ShiftMapperProfile)profile!;
    }

    /// <summary>
    /// Declares that you want a map from <typeparamref name="TSource"/> to
    /// <typeparamref name="TDestination"/>. Call it from your constructor.
    ///
    /// IMPORTANT — this method does NOTHING at runtime. It exists so you can write a type
    /// pair down in ordinary C#. The ShiftMapper source generator READS these calls at
    /// COMPILE time and, for each one, writes a real mapping method into your class. So it
    /// is best understood as a marker the generator looks for.
    ///
    /// The returned <see cref="MapExpression{TSource, TDestination}"/> is what lets you chain
    /// <see cref="MapExpression{TSource, TDestination}.ReverseMap"/> to get the opposite
    /// direction as well. Ignoring the return value is perfectly normal.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;();                 // Brand -> BrandDto
    /// CreateMap&lt;Stock, StockDto&gt;().ReverseMap();     // Stock -> StockDto AND back
    /// </code>
    /// </summary>
    /// <param name="configure">
    /// Optional per-map settings. Anything you set here overrides
    /// <see cref="ConfigureDefaults"/> for this map only.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;();                                          // Sku finds SKU
    /// CreateMap&lt;Brand, BrandDto&gt;(o =&gt; o.Matching = PropertyMatching.CaseSensitive);
    /// </code>
    /// </param>
    protected MapExpression<TSource, TDestination> CreateMap<TSource, TDestination>(
        Action<MapOptions>? configure = null) => new(_customizations);

    /// <summary>
    /// Sets the defaults every map in THIS mapper starts from. Override it when a whole
    /// mapper wants different behaviour, instead of repeating the same option on every
    /// <c>CreateMap</c> call:
    ///
    /// <code>
    /// protected override void ConfigureDefaults(MapOptions options)
    ///     =&gt; options.Matching = PropertyMatching.CaseSensitive;
    /// </code>
    ///
    /// Precedence runs innermost-first: whatever a <c>CreateMap</c> (or <c>ReverseMap</c>)
    /// lambda sets wins, then this, then ShiftMapper's own defaults.
    ///
    /// Like the rest of the declaration API this never runs — the generator reads it at
    /// compile time. It is also read across ALL parts of a partial mapper, so it does not
    /// matter which file you put it in.
    /// </summary>
    protected virtual void ConfigureDefaults(MapOptions options)
    {
    }

    /// <summary>
    /// Declares an OPEN GENERIC map {D} one written once for <c>PagedResult&lt;&gt;</c> and closed
    /// by the generator for every element pair you already map.
    ///
    /// <code>
    /// CreateMap&lt;Brand, BrandDto&gt;();
    /// CreateMap&lt;Stock, StockDto&gt;();
    ///
    /// CreateMap(typeof(PagedResult&lt;&gt;), typeof(PagedResultDto&lt;&gt;));
    /// // gives you PagedResult&lt;Brand&gt; -&gt; PagedResultDto&lt;BrandDto&gt;
    /// //       and PagedResult&lt;Stock&gt; -&gt; PagedResultDto&lt;StockDto&gt;
    /// </code>
    ///
    /// <para><b>WHICH PAIRS IT CLOSES.</b> One per map you already declared: for every
    /// <c>CreateMap&lt;A, B&gt;</c>, the generator emits <c>Wrapper&lt;A&gt; -&gt; WrapperDto&lt;B&gt;</c>
    /// if both types can be constructed that way. That rule is the useful one and the only one
    /// that is decidable {D} a wrapper is closed over the things you map, and nothing else. The
    /// closed maps are ordinary maps in every other respect, projection included.</para>
    ///
    /// <para>ONE TYPE PARAMETER EACH, on both sides. Anything else is reported (SM0026) rather
    /// than guessed at: with two parameters there is no single pairing to choose, only a
    /// combinatorial one nobody asked for.</para>
    ///
    /// <para>Like the generic <c>CreateMap</c>, this does NOTHING at run time {D} the generator
    /// reads the two <c>typeof</c> expressions at compile time. It takes <c>Type</c> rather than
    /// type parameters because C# has no way to write an unbound generic as a type argument.</para>
    /// </summary>
    /// <param name="source">An unbound generic type, e.g. <c>typeof(PagedResult&lt;&gt;)</c>.</param>
    /// <param name="destination">The matching unbound generic destination.</param>
    protected void CreateMap(Type source, Type destination)
    {
        _ = source;
        _ = destination;
    }
}
