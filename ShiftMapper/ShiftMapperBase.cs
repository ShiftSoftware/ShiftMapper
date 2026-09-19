using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftMapper;

/// <summary>
/// Base class for a mapper — a class whose CONSTRUCTOR declares maps.
///
/// <code>
/// public class CatalogMapper : ShiftMapperBase
/// {
///     private readonly ICurrencyService _currency;
///
///     public CatalogMapper(ICurrencyService currency)
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
/// <para><b>NOTHING IS GENERATED ONTO THIS CLASS, AND NOTHING INJECTS IT.</b> It is a place to
/// write declarations. The ShiftMapper generator reads every such class in a project — and every
/// one declared by the packages the project references — and writes ONE generated class per
/// assembly holding all of their maps, reached through <see cref="Mapper"/>: inject that, call
/// <c>mapper.Map&lt;BrandDto&gt;(brand)</c>. Write as many mapper classes as read well; none of them
/// needs to be partial, registered, or named anywhere.</para>
///
/// <para>A mapper class is constructed the first time anything is mapped, with its dependencies
/// injected, so what its constructor registers at run time — the <c>MapFrom</c> trees, the hooks,
/// the factories — is where the generated code looks for it. The rest of the declaration API
/// (<c>CreateMap</c> and its chain) does nothing at run time: it is read at compile time.</para>
///
/// <para>Register once, in the application: <c>builder.Services.AddShiftMapper();</c>.</para>
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
            $"ShiftMapper: no service provider has been set on '{DeclaringType.Name}'. That happens " +
            "when the mapper is constructed directly instead of resolved from DI. Register with " +
            "services.AddShiftMapper() and inject Mapper.");

    /// <summary>
    /// Called by <c>AddShiftMapper</c> when the mapper is created. Internal on purpose:
    /// this is the library's job, never the developer's.
    /// </summary>
    internal void SetServices(IServiceProvider services)
    {
        _services = services;

        // The pull for a customized implicit map used before its configuring type ran: ask the
        // container for that type (or whatever an IShiftMapperConfiguratorResolver says applies its
        // configuration). Constructing it runs the lambda, which calls IMapper.Configure, which lands
        // in this store — so the retry that follows finds the expression.
        _customizations.ConfiguratorPull = configurator =>
        {
            if (services.GetService(typeof(IShiftMapperConfiguratorResolver)) is IShiftMapperConfiguratorResolver resolver)
                return resolver.TryApply(configurator, services);

            return services.GetService(configurator) is not null;
        };
    }

    /// <summary>
    /// Folds a configuration surface's expressions into this mapper's store. What the surface says
    /// WINS over what is already there: an implicit map has no customizations of its own, and the
    /// surface is the one place its members are customized.
    /// </summary>
    protected internal void ApplyConfiguration(ShiftMapperConfigurationSurface surface) =>
        _customizations.MergeFrom(surface.Customizations, overriding: true);

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
    /// Protected because the generated mapper — which derives from this class — is the only
    /// thing that should touch it.
    /// </summary>
    protected MapCustomizations Customizations
    {
        get
        {
            EnsureIncluded(visiting: null);
            return _customizations;
        }
    }

    /// <summary>
    /// The same store, reached WITHOUT materialising included mappers and packs.
    ///
    /// <para>The distinction is the whole of the timing problem. <c>CreateMap</c> runs inside your
    /// constructor and must not trigger construction of an included mapper, because that mapper
    /// may need services and <see cref="Services"/> is not assigned until after your constructor
    /// returns. The generated code, which reads <see cref="Customizations"/>, only ever runs when
    /// something is actually mapped — by which time everything is in place.</para>
    /// </summary>
    private readonly MapCustomizations _customizations;

    /// <summary>The mapper types this one includes, in order — what the generated mapper's metadata says, plus what a registration added.</summary>
    private List<Type>? _includedTypes;

    /// <summary>The pack types <see cref="AddConversions{TPack}"/> recorded, in order.</summary>
    private List<Type>? _packTypes;

    /// <summary>Set once the included mappers and packs have been constructed and folded in.</summary>
    private bool _materialised;

    /// <summary>
    /// Hands the customization store the mapper CLASS it belongs to, which is what lets a
    /// compiled customization be reused by every later instance of the same mapper instead of
    /// being compiled again per request. <c>GetType()</c> is the runtime type, so a mapper that
    /// derives from another gets its own entries rather than sharing its base's.
    /// </summary>
    protected ShiftMapperBase() => _customizations = new MapCustomizations(GetType());

    /// <summary>
    /// The type whose DECLARATIONS this instance's constructor registers — the key every
    /// conversion it declares is stored under, and the one the generated code looks it up by.
    ///
    /// <para>The runtime type. Virtual so a derived class that runs another mapper's constructor
    /// could say whose declarations those are; nothing ShiftMapper generates does.</para>
    /// </summary>
    protected virtual Type DeclaringType => GetType();

    /// <summary>
    /// Registers a conversion for a TYPE PAIR, applied to every member of those types in every
    /// map THIS MAPPER DECLARES — directly, as the element type of a collection, as the value type
    /// of a dictionary, or inside a nested map.
    ///
    /// <code>
    /// CreateConversion&lt;string, List&lt;FileDTO&gt;&gt;(
    ///     memory: json =&gt; FileHelpers.Parse(json),
    ///     query:  json =&gt; ShiftJson.Files(json));
    /// </code>
    ///
    /// <para><b>THIS IS THE ONE THAT SCALES.</b> A <c>ForMember</c> is written per member per map;
    /// this is written once and answers wherever the pair appears. It is consulted by the same
    /// resolver that handles <c>int</c> to <c>string</c>, just before it would have given up and
    /// reported SM0002, so it EXTENDS the built-in table rather than replacing it. A
    /// <c>ForMember</c> on a particular member still wins over both.</para>
    ///
    /// <para><b>ITS REACH IS THIS MAPPER'S MAPS.</b> A rule written here does not leak into the
    /// maps of a mapper that includes this one, and a rule written in a mapper this one includes
    /// does not reach the maps written here. A rule several mappers should share belongs in a
    /// <see cref="ShiftMapperConversions"/> pack, added with <see cref="AddConversions{TPack}"/>
    /// or at registration. Where more than one rule could answer, the nearest wins: this mapper's
    /// own, then a pack it added, then a pack the registration gave every mapper, then the
    /// built-in table.</para>
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

        _customizations.RegisterConversion(DeclaringType, typeof(TSource), typeof(TDestination), memory, query);
    }

    /// <summary>
    /// <see cref="CreateConversion{TSource, TDestination}(Func{TSource, TDestination}, Expression{Func{TSource, TDestination}})"/>
    /// for a conversion that needs to know WHAT it is converting: the second argument is the
    /// property pair being mapped, as the generator writes it — <c>"ProductDto.Sku -&gt; Product.Sku"</c>,
    /// or <c>"ProductDto.Brand.Value -&gt; Product.BrandID"</c> from inside a shaped member.
    ///
    /// <para>For the conversion that REFUSES. A framework that turns blank text into an error
    /// rather than a default has to say which field was blank, and the value alone cannot tell it;
    /// the mapping can. The query form is the same as ever — a database has no message to write.</para>
    /// </summary>
    protected void CreateConversion<TSource, TDestination>(
        Func<TSource, string, TDestination> memory,
        Expression<Func<TSource, TDestination>>? query = null)
    {
        if (memory is null)
            throw new ArgumentNullException(nameof(memory));

        _customizations.RegisterConversion(DeclaringType, typeof(TSource), typeof(TDestination), memory, query);
    }

    /// <summary>
    /// Declares a MEMBER-SHAPED rule: how to fill any destination member of
    /// <typeparamref name="TMember"/>, from source members the destination member's own NAME picks
    /// out.
    ///
    /// <code>
    /// CreateMemberConvention&lt;SelectDto&gt;()
    ///     .NameFrom&lt;KeyAndNameAttribute&gt;("Text")
    ///     .Fill(d =&gt; d.Value, "{Member}ID")
    ///     .Fill(d =&gt; d.Text,  "{Member}.{NameOf}");
    /// </code>
    ///
    /// <para>It answers a question <c>CreateConversion</c> cannot. A conversion is handed ONE value;
    /// this needs two source members, and which two depends on the member's name. See
    /// <see cref="MemberConventionExpression{TMember}"/> for what the vocabulary is and why it is
    /// deliberately small.</para>
    ///
    /// <para>Declare it here, where it applies to this mapper's maps, or in a
    /// <see cref="ShiftMapperConversions"/> pack to share it. An explicit <c>ForMember</c> always
    /// wins over it.</para>
    ///
    /// <para>Compile-time only, like <c>CreateMap</c> — the generator reads the chain and the
    /// calls do nothing.</para>
    /// </summary>
    protected MemberConventionExpression<TMember> CreateMemberConvention<TMember>() => new();

    /// <summary>
    /// Declares that a member is never mapped — not read as a source, not written as a
    /// destination, or neither — on EVERY map whose source or destination type declares it,
    /// inherits it, or implements the interface that declares it.
    ///
    /// <code>
    /// IgnoreMember&lt;EntityBase&gt;(e =&gt; e.Id, MemberRole.Destination);      // never written from a request
    /// IgnoreMember&lt;ITaggable&gt;(e =&gt; e.Tags, MemberRole.Destination);      // owned by a pipeline
    /// IgnoreMember(typeof(Entity&lt;&gt;), "ReloadAfterSave");                  // an open generic base: by name
    /// </code>
    ///
    /// <para>This is a framework's way of saying "this member is mine" ONCE, as code in a pack,
    /// instead of an attribute on every type or an <c>Ignore</c> on every map. An ignored
    /// destination member is treated exactly as <c>opt.Ignore()</c> would treat it — omitted, and
    /// not reported as unmapped; an ignored source member is simply not a candidate. Compile-time
    /// only, like the rest of the declaration API; it reaches maps by the same distance rule a
    /// conversion does.</para>
    /// </summary>
    protected void IgnoreMember<TDeclaring>(Expression<Func<TDeclaring, object?>> member, MemberRole role = MemberRole.Both)
    {
        _ = member;
        _ = role;
    }

    /// <inheritdoc cref="IgnoreMember{TDeclaring}(Expression{Func{TDeclaring, object}}, MemberRole)"/>
    /// <param name="declaring">The type that declares the member — an open generic (<c>typeof(Entity&lt;&gt;)</c>) is allowed.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="role">Which side of a map the rule applies to.</param>
    protected void IgnoreMember(Type declaring, string member, MemberRole role = MemberRole.Both)
    {
        _ = declaring;
        _ = member;
        _ = role;
    }


    /// <summary>
    /// Gives this mapper the rules of a <see cref="ShiftMapperConversions"/> pack. Call it from your
    /// constructor, like <c>CreateMap</c>.
    ///
    /// <code>
    /// public AppMapper()
    /// {
    ///     AddConversions&lt;PlatformConversions&gt;();
    ///     CreateMap&lt;Brand, BrandDto&gt;();          // may now use the pack's long -&gt; string
    /// }
    /// </code>
    ///
    /// <para>The pack's conversions and member conventions apply to every map THIS mapper declares.
    /// A rule this mapper wrote itself still wins over the pack's, and a pack added here wins over
    /// one the registration gave every mapper.</para>
    ///
    /// <para>Unlike <c>CreateMap</c> this does something at run time as well: it records the type,
    /// so the pack can be constructed on first use — through DI when there is a provider, so it
    /// may take dependencies.</para>
    /// </summary>
    protected void AddConversions<TPack>() where TPack : ShiftMapperConversions => AddConversions(typeof(TPack));

    /// <summary>
    /// Records a mapper to include — what the GENERATED mapper's metadata lists, applied by
    /// <see cref="EnsureIncluded"/>, and what a registration adds. Internal: nobody writes an
    /// include by hand any more, because the generator includes everything it can see.
    /// </summary>
    internal void IncludeMapper(Type mapper)
    {
        _includedTypes ??= new List<Type>();

        // Once. A mapper the constructor includes and the registration names as well is still one
        // include; building it twice would merge the same store twice for nothing.
        if (_includedTypes.Contains(mapper))
            return;

        _includedTypes.Add(mapper);

        // A mapper included after something has already been mapped would otherwise be ignored in
        // silence. It cannot happen from a constructor or from AddShiftMapper, which are the only
        // supported places, but this makes the unsupported one loud instead of subtle.
        _materialised = false;
    }

    /// <inheritdoc cref="IncludeMapper(Type)"/>
    internal void AddConversions(Type pack)
    {
        _packTypes ??= new List<Type>();

        if (_packTypes.Contains(pack))
            return;

        _packTypes.Add(pack);
        _materialised = false;
    }

    /// <summary>
    /// The store with everything included and added, for an INCLUDING mapper that is materialising
    /// this one — carrying the set of mappers already on the include path so a cycle stops.
    /// </summary>
    internal MapCustomizations Materialised(HashSet<Type> visiting)
    {
        EnsureIncluded(visiting);
        return _customizations;
    }

    /// <summary>
    /// Builds each included mapper and pack once and folds its registrations into this mapper's
    /// store.
    ///
    /// <para>They are resolved from <see cref="Services"/> when there is one — as a registered
    /// service if there is a registration, else constructed with their dependencies injected — so
    /// an included mapper can take the same constructor dependencies a mapper can. When the mapper
    /// was built by hand rather than by DI there is no provider to ask, and a parameterless one
    /// still works, which keeps a plain <c>new AppMapper()</c> usable in a test.</para>
    ///
    /// <para><paramref name="visiting"/> is the include path so far. Two mappers may include each
    /// other — the generator maps the union of what they declare, which is what someone writing
    /// it would expect — and without the path the runtime would construct them alternately
    /// forever.</para>
    /// </summary>
    private void EnsureIncluded(HashSet<Type>? visiting)
    {
        if (_materialised)
            return;

        // WHAT THE GENERATED MAPPER COMPOSES — every mapper and pack the generator folded into it —
        // is written into its assembly as metadata, and read from there: one list, whether the
        // instance came from DI, from Mapper.Create, or from a bare new. Before the flag below,
        // because recording an include clears it.
        foreach (Type composed in Composition.For(GetType()))
        {
            if (typeof(ShiftMapperConversions).IsAssignableFrom(composed))
                AddConversions(composed);
            else
                IncludeMapper(composed);
        }

        // Set FIRST. A constructor that reached back into this mapper would otherwise re-enter
        // here and build the same set again, forever.
        _materialised = true;

        visiting ??= new HashSet<Type>();
        visiting.Add(GetType());
        visiting.Add(DeclaringType);

        if (_includedTypes is not null)
        {
            foreach (Type includedType in _includedTypes)
            {
                if (visiting.Contains(includedType))
                    continue;

                var included = (ShiftMapperBase)Construct(includedType, "mapper");

                // Hand the provider down so what IT includes can be resolved the same way.
                if (included._services is null && _services is not null)
                    included.SetServices(_services);

                _customizations.MergeFrom(included.Materialised(visiting));
            }
        }

        if (_packTypes is not null)
        {
            foreach (Type packType in _packTypes)
                _customizations.MergeFrom(((ShiftMapperConversions)Construct(packType, "pack")).Customizations);
        }

        // AFTER everything else, so a conversion a constructor registered keeps its place:
        // RegisterQueryConversion does not overwrite, and the generator resolves the pair by the
        // same precedence. Near beats far, in both halves.
        RegisterDeclaredConversions(_customizations);
    }

    /// <summary>
    /// Hands the store the QUERY forms of conversions declared by REFERENCED ASSEMBLIES whose
    /// memory forms were lifted into named methods — overridden by the generated half of the
    /// mapper, and empty here.
    ///
    /// <para>Only the query forms. A lifted conversion has its memory form called DIRECTLY by the
    /// generated code, by name, because the generator read that name out of metadata at compile
    /// time. An expression tree is the one thing a name cannot stand in for, so it is the one thing
    /// that has to arrive here.</para>
    /// </summary>
    protected virtual void RegisterDeclaredConversions(MapCustomizations customizations)
    {
    }

    /// <summary>
    /// What a generated mapper composes, read once per type from the
    /// <see cref="ShiftMapperDeclaredCompositionAttribute"/>s its assembly carries: every mapper
    /// class and every pack the generator folded into it. A hand-written mapper class composes
    /// nothing this way — its assembly lists nothing under its name — so the read costs it one
    /// empty lookup.
    /// </summary>
    private static class Composition
    {
        private static readonly ConcurrentDictionary<Type, Type[]> ByType = new();

        public static Type[] For(Type generated) =>
            ByType.GetOrAdd(generated, static type =>
                type.Assembly.GetCustomAttributes<ShiftMapperDeclaredCompositionAttribute>()
                    .Where(attribute => attribute.Mapper == type)
                    .Select(attribute => attribute.Composed)
                    .ToArray());
    }

    private object Construct(Type type, string kind)
    {
        if (_services is not null)
        {
            // Registered wins, because a registration may carry a lifetime the developer chose;
            // otherwise build it here with its dependencies injected, so nobody has to register a
            // mapper only to be able to include it.
            return _services.GetService(type)
                ?? ActivatorUtilities.CreateInstance(_services, type);
        }

        try
        {
            return Activator.CreateInstance(type)!;
        }
        catch (MissingMethodException error)
        {
            throw new InvalidOperationException(
                $"ShiftMapper: the {kind} '{type.Name}' takes constructor arguments, so it has to " +
                "come from DI, but the mapper was not resolved from a service provider. " +
                "Resolve Mapper through AddShiftMapper(), or give the " + kind + " a " +
                "parameterless constructor.",
                error);
        }
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
