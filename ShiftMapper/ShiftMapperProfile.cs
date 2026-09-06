namespace ShiftMapper;

/// <summary>
/// A place to write maps OUTSIDE the mapper class.
///
/// <code>
/// public class CatalogProfile : ShiftMapperProfile
/// {
///     public CatalogProfile()
///     {
///         CreateMap&lt;CatalogItem, CatalogItemDto&gt;()
///             .ForMember(d =&gt; d.Sku, opt =&gt; opt.MapFrom(s =&gt; s.Sku.ToUpper()));
///
///         CreateMap&lt;PhysicalItem, PhysicalItemDto&gt;().IncludeBase&lt;CatalogItem, CatalogItemDto&gt;();
///     }
/// }
///
/// public partial class AppMapper : ShiftMapperBase
/// {
///     public AppMapper() =&gt; AddProfile&lt;CatalogProfile&gt;();
/// }
/// </code>
///
/// <para><b>A PROFILE IS A PLACE TO WRITE DECLARATIONS, NOT A SECOND MAPPER.</b> Nothing is
/// generated onto it — no <c>Map</c> methods, no projections, no extension methods. Its maps become
/// the maps of every mapper that adds it, and they are called through that mapper exactly as if the
/// <c>CreateMap</c> had been written in its own constructor. There is no <c>CatalogProfile.Map</c>
/// to find, and looking for one is the sign of a mental model worth correcting early.</para>
///
/// <para><b>WHY IT EXISTS.</b> One constructor is a fine place for a dozen maps and a poor place
/// for fifty. Splitting by area — catalogue, invoicing, reporting — puts each map next to the ones
/// it is read with, and lets two people change different areas without meeting in the same file.
/// It is also the shape everyone arriving from AutoMapper already has in mind.</para>
///
/// <para><b>THE WHOLE SURFACE IS INHERITED.</b> Everything <see cref="ShiftMapperBase"/> offers a
/// mapper — <c>CreateMap</c>, open generic <c>CreateMap</c>, and every refinement chained onto them
/// — means the same thing here, because it IS the same method. There is no second API to learn and
/// no subset to remember.</para>
///
/// <para><b>DEPENDENCIES WORK, and are resolved late.</b> A profile may take constructor arguments
/// like any service:</para>
///
/// <code>
/// public class InvoiceProfile : ShiftMapperProfile
/// {
///     public InvoiceProfile(IInvoiceNumbering numbering) =&gt;
///         CreateMap&lt;Invoice, InvoiceLabelDto&gt;()
///             .ConstructUsing(s =&gt; new InvoiceLabelDto(numbering.Prefix + s.Number));
/// }
/// </code>
///
/// Register it (<c>services.AddTransient&lt;InvoiceProfile&gt;()</c>) and it is resolved from the
/// mapper's <see cref="ShiftMapperBase.Services"/> the first time anything is mapped — not while
/// the mapper's constructor is running, because that provider does not exist yet. A profile with a
/// parameterless constructor needs no registration at all and works in a plain <c>new AppMapper()</c>.
///
/// <para><b>AND THE SHARP EDGE THAT FOLLOWS: a profile taking dependencies makes the whole mapper
/// DI-ONLY.</b> All of a mapper's profiles are built together on first use, so one that cannot be
/// built fails the mapper's first map — including maps that have nothing to do with it. The
/// alternative would be to skip the profile and carry on, which would leave its <c>MapFrom</c>
/// members quietly unfilled: the exact silent divergence this library exists to prevent. So it
/// throws, naming the profile and what to register. If you construct mappers by hand in tests,
/// keep their profiles parameterless.</para>
///
/// <para><b>SAME COMPILATION ONLY.</b> The generator reads a profile the way it reads the mapper:
/// as SOURCE. A profile compiled into a referenced package cannot be read — a generator sees a
/// referenced assembly as metadata, and metadata has no method bodies, so the <c>CreateMap</c>
/// calls inside it simply are not there to find. That case is reported (SM0028) rather than
/// silently mapping nothing.</para>
/// </summary>
public abstract class ShiftMapperProfile : ShiftMapperBase
{
}
