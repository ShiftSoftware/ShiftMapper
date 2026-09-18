using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ShiftMapper;

/// <summary>
/// Marks an open generic class, or an attribute class, as a MAP SOURCE: every type that closes it
/// — by deriving from the class, or by carrying the attribute — declares a map between the type
/// arguments named here, exactly as if a mapper class in that project had written
/// <c>CreateMap&lt;A, B&gt;()</c>.
///
/// <code>
/// // in a framework package, once
/// [ShiftMapperDeclaresMap("TEntity", "TView", Reverse = true, Nested = 10, Flattening = DeclaredOption.False,
///                         Rules = typeof(PlatformConversions))]
/// [ShiftMapperDeclaresMap("TEntity", "TList", Nested = 10, Flattening = DeclaredOption.False,
///                         Rules = typeof(PlatformConversions))]
/// public abstract class Repository&lt;TEntity, TList, TView&gt; { }
///
/// // in an application — nothing else is written
/// public class InvoiceRepository : Repository&lt;Invoice, InvoiceListDto, InvoiceDto&gt; { }
/// </code>
///
/// <para>The generator compiling the application sees <c>InvoiceRepository</c>, reads the marker
/// from the base class's metadata, substitutes the type arguments, and declares
/// <c>Invoice → InvoiceDto</c>, <c>InvoiceDto → Invoice</c> and <c>Invoice → InvoiceListDto</c> in
/// the application's generated mapper. They are IMPLICIT maps: a <c>CreateMap</c> the application
/// writes for the same pair replaces one, silently (SM0047, an informational note), which is how
/// an implicit map is customized in full.</para>
///
/// <para><b>The programmer of the application writes no attribute.</b> This one lives on the
/// framework's base type; nobody applying it is expected to be anyone but the framework's author.
/// On an attribute class, <c>"this"</c> names the type the attribute is applied to.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ShiftMapperDeclaresMapAttribute : Attribute
{
    /// <summary>The name a type parameter is written as when the marker means the closing type itself.</summary>
    public const string This = "this";

    /// <param name="source">The name of the type parameter that is the map's source, or <see cref="This"/>.</param>
    /// <param name="destination">The name of the type parameter that is the map's destination, or <see cref="This"/>.</param>
    public ShiftMapperDeclaresMapAttribute(string source, string destination)
    {
        Source = source;
        Destination = destination;
    }

    public string Source { get; }

    public string Destination { get; }

    /// <summary>Declare the opposite direction too, as <c>.ReverseMap()</c> would.</summary>
    public bool Reverse { get; set; }

    /// <summary>
    /// How many levels of nested class members get an implicit map of their own, below this one.
    /// Zero (the default) nests nothing: a nested member whose pair has no map is SM0011, as for any
    /// map. Ten covers any DTO graph a person would write by hand.
    ///
    /// <para>A nested pair that already has a map — declared anywhere — is used as it is, with its
    /// own customizations; a member an explicit <c>ForMember</c> or a member convention claims is
    /// not nested; a cycle stops at the member that would close it (SM0048, informational).</para>
    /// </summary>
    public int Nested { get; set; }

    /// <summary>
    /// Whether the implicit maps flatten (<c>OrderDto.CustomerName</c> from <c>Order.Customer.Name</c>).
    /// Not declared means ShiftMapper's default, which is on. A framework whose DTOs must never reach
    /// two levels into an entity by name alone sets <see cref="DeclaredOption.False"/>.
    /// </summary>
    public DeclaredOption Flattening { get; set; }

    /// <summary>
    /// A <see cref="ShiftMapperConversions"/> pack the implicit maps take their rules from, at the
    /// level a mapper class's own <c>AddConversions&lt;T&gt;()</c> would put it — nearer than the
    /// registration's packs, so the framework's rules answer for the framework's maps.
    /// </summary>
    public Type? Rules { get; set; }
}

/// <summary>Which side of a map a member rule applies to.</summary>
public enum MemberRole
{
    /// <summary>The member is neither read as a source nor written as a destination.</summary>
    Both = 0,

    /// <summary>The member is never read: it feeds no destination member.</summary>
    Source = 1,

    /// <summary>The member is never written: no map assigns it, and it is not reported as unmapped.</summary>
    Destination = 2,
}

/// <summary>
/// The base of a CONFIGURATION SURFACE: an object a framework hands its users so they can
/// customize implicit maps where they configure everything else, in ShiftMapper's own vocabulary.
///
/// <code>
/// // in the framework
/// public sealed class RepositoryMapping&lt;TEntity, TList, TView&gt; : ShiftMapperConfigurationSurface
/// {
///     public MapExpression&lt;TEntity, TView&gt; View   =&gt; Map&lt;TEntity, TView&gt;();
///     public MapExpression&lt;TView, TEntity&gt; Entity =&gt; Map&lt;TView, TEntity&gt;();
///     public MapExpression&lt;TEntity, TList&gt; List   =&gt; Map&lt;TEntity, TList&gt;();
/// }
///
/// // in the application's repository
/// options.Mapping(m =&gt;
/// {
///     m.List.ForMember(d =&gt; d.Total, opt =&gt; opt.MapFrom(e =&gt; e.Lines.Sum(l =&gt; l.Price)));
///     m.Entity.ForMember(e =&gt; e.Number, opt =&gt; opt.Ignore()).AfterMap((dto, e) =&gt; e.Touch());
///     m.Nested(2);
/// });
/// </code>
///
/// <para><b>Two halves, like a mapper class.</b> The generator reads the lambda at BUILD time for
/// its shape — which members are customized or ignored, whether there is a hook, the nesting depth
/// — and bakes it into the implicit maps of the type the lambda is written in, with every
/// diagnostic a mapper class gets. At RUN time the lambda runs wherever the framework runs it, its
/// expressions land in this object's store, and the framework hands the object to the mapper with
/// <see cref="IMapper.Configure"/>. The lambda must be inline and unconditional (SM0035), one type
/// per pair may configure it (SM0050), and a mapper class declaring the pair wins over it (SM0051).</para>
/// </summary>
public abstract class ShiftMapperConfigurationSurface
{
    private readonly MapCustomizations _customizations;

    protected ShiftMapperConfigurationSurface() => _customizations = new MapCustomizations(GetType());

    /// <summary>The expressions the lambda registered, read by the mapper the surface is applied to.</summary>
    internal MapCustomizations Customizations => _customizations;

    /// <summary>The nesting depth <see cref="Nested"/> set, or null when it was not called.</summary>
    internal int? NestedDepth { get; private set; }

    /// <summary>
    /// The handle a surface exposes for one implicit map, bound to this surface's store. A subclass
    /// exposes one per map, as a property, so the generator can read the pair off the property's type.
    /// </summary>
    protected MapExpression<TSource, TDestination> Map<TSource, TDestination>() => new(_customizations);

    /// <summary>
    /// Caps automatic nesting for the implicit maps of the type this surface configures — the
    /// per-type spelling of the marker's <see cref="ShiftMapperDeclaresMapAttribute.Nested"/>. Read at
    /// build time, so the argument must be a constant.
    /// </summary>
    public ShiftMapperConfigurationSurface Nested(int depth)
    {
        NestedDepth = depth;
        return this;
    }
}

/// <summary>
/// Resolves the type that CONFIGURES a pair — the repository whose <c>Mapping(m =&gt; …)</c> lambda
/// customized it — when a customized implicit map is used before that type has run in the current
/// scope. Optional: without a registration the mapper asks the service provider for the type itself.
///
/// <para>A framework registers one when the configuring type is not what the container knows the
/// configuration by — an entity that configures the built-in repository for its own triple, say —
/// so the mapper can still reach the object that applies the lambda.</para>
/// </summary>
public interface IShiftMapperConfiguratorResolver
{
    /// <summary>
    /// Constructs, or finds, whatever applies <paramref name="configurator"/>'s configuration, and
    /// returns true when it did. The construction is expected to have called
    /// <see cref="IMapper.Configure"/> by the time this returns.
    /// </summary>
    bool TryApply(Type configurator, IServiceProvider services);
}
