using Microsoft.Extensions.Logging;
using ShiftMapper.Sample.Dtos;
using ShiftMapper.Sample.Entities;
using ShiftMapper.Sample.Services;

namespace ShiftMapper.Sample.Mapping;

/// <summary>
/// The mapper for this application — and the only half of it written by hand.
///
/// Three things make this shape useful:
///
/// 1. Maps are declared in the CONSTRUCTOR, the same way an AutoMapper Profile does it.
///    Those CreateMap calls never run; the source generator reads them at compile time.
///
/// 2. It is an ORDINARY DI SERVICE. The constructor takes an ILogger purely to prove the
///    point: anything you can inject anywhere, you can inject here.
///
/// 3. <see cref="ShiftMapperBase.Services"/> is filled in for you by AddShiftMapper, so a
///    custom mapping can resolve a service it only discovers it needs while mapping.
///
/// It is also PARTIAL — the generator writes the other half, the real Map methods.
///
/// Registered in Program.cs with <c>builder.Services.AddShiftMapper&lt;AppMapper&gt;();</c>.
///
/// There are two ways to call the generated maps, and they do the same work — the
/// extension methods simply forward to the instance methods. The sample shows both:
/// BrandEndpoints uses the extension form, StockEndpoints calls the mapper directly.
/// <code>
/// var dto = mapper.Map&lt;BrandDto&gt;(brand);   // instance
/// var dto = brand.Map&lt;BrandDto&gt;(mapper);   // extension
/// </code>
///
/// Chaining <c>.ReverseMap()</c> onto a CreateMap registers the opposite direction too, so
/// one line gives you entity-to-DTO and DTO-to-entity. StockEndpoints uses both.
/// </summary>
public partial class AppMapper : ShiftMapperBase
{
    private readonly ILogger<AppMapper> _logger;

    private readonly IInvoiceNumbering _numbering;

    public AppMapper(ILogger<AppMapper> logger, IInvoiceNumbering numbering)
    {
        _logger = logger;
        _numbering = numbering;

        // Maps cleanly, and demonstrates two things at once: CASE-INSENSITIVE MATCHING and
        // TYPE CONVERSION.
        //
        // TYPE CONVERSION first, in two flavours. Brand.FoundedYear is an int and
        // BrandDto.FoundedYear is a string; Brand.Tags is a List<string> and BrandDto.Tags is
        // an IReadOnlyList<string>. Both pairs line up by name, so the only question is
        // whether the types can be bridged — and both can:
        //
        //   FoundedYear = ValueConverter.ToInvariantString(source.FoundedYear)
        //   Tags        = ValueConverter.ToList(source.Tags)
        //
        // A third pair on this same map is deliberately WRONG, and is the live demonstration
        // of SM0010. Brand.ExternalIds is a List<long>; BrandDto.ExternalIds is a List<int>.
        // Same collection shape, different element type — so it maps, one element at a time:
        //
        //   ExternalIds = ValueConverter.ToList<long, int>(source.ExternalIds,
        //                     static item => unchecked((int)item))
        //
        // and that is the problem. Brand 1 is seeded with external id 4000000001, past
        // int.MaxValue, so GET /api/brands reports -294967295 instead — a different number,
        // entirely plausible-looking, with nothing in the code to say so and no exception to
        // mark it. Hence a WARNING rather than a note:
        //
        //   warning SM0010: 'BrandDto.ExternalIds' is mapped by converting 'List<long>' to
        //                   'List<int>', which cannot hold every value the source can
        //
        // That is the line SM0010 draws against SM0008. A null becoming zero, or a HashSet
        // dropping duplicates, is a loss you asked the conversion to perform; this is an
        // ordinary value coming out different. Declare the DTO property IReadOnlyList<long>
        // and the warning goes away — along with the demonstration.
        //
        // That is the whole feature in two lines. When names match but types do not,
        // ShiftMapper converts if it can and says so if it cannot, instead of silently
        // skipping the property. What it converts:
        //
        //   * anything C# already converts implicitly    int -> long, int -> decimal
        //   * between number types                       long -> int  (a cast; SM0008)
        //   * to and from text                           decimal <-> string, bool <-> string
        //   * enums and numbers, enums and text          Status <-> int, Status <-> string
        //   * Guid, TimeSpan and every date/time type   Guid <-> string, DateTime <-> string
        //     to and from text
        //   * the date/time pairs with one answer       DateOnly -> DateTime, DateTime -> DateOnly
        //   * your own IMPLICIT conversion operators
        //   * COLLECTIONS of all of the above           List<int> -> IReadOnlyList<int>,
        //                                               HashSet<int> -> string[]
        //
        // That last one is worth a second look, because it does not just cast. A collection is
        // COPIED into a new one, every time, even when the source could have been assigned
        // straight across — so the DTO owns its own list rather than a second reference to the
        // entity's, and an IEnumerable that was really an unevaluated EF query arrives as data
        // rather than as a promise that re-runs on a disposed DbContext.
        //
        // and what it refuses, reporting SM0002 rather than guessing: nested objects
        // (Product -> ProductDto) and collections OF them (List<Product> -> List<ProductDto>,
        // which needs nested mapping and is the next thing to build), moving a reference
        // around by up-casting or boxing, your own EXPLICIT operators — and four pairs C#
        // would convert quite happily, because their answer would not come from the two types
        // alone:
        //
        //   DateTime -> DateTimeOffset   the offset would come from the server's time zone
        //   DateTimeOffset -> DateTime   drop the offset, or convert to UTC? both defensible
        //   one enum -> a different enum a cast maps by NUMBER, so reordering either enum
        //                                would silently change what every map means
        //   TimeSpan -> TimeOnly         a negative duration, or one of a day or more, is
        //                                ordinary data that no clock can hold
        //
        // Brand carries the column as ISOCode — acronym casing, the way an EF entity
        // mirroring a database column usually looks. BrandDto spells it IsoCode. There is no
        // exact match for that name, so the default PropertyMatching.CaseInsensitive falls
        // back to ignoring case and generates:
        //
        //   IsoCode = source.ISOCode
        //
        // Every other property still matches exactly, and exact always wins first — so a
        // type carrying both Id and ID would map each to its own counterpart, never swap them.
        //
        // See the other mode: change this line to
        //     CreateMap<Brand, BrandDto>(o => o.Matching = PropertyMatching.CaseSensitive);
        // and the fallback switches off, so IsoCode stops mapping and the build reports
        //     SM0001: 'BrandDto.IsoCode' is not mapped because 'Brand' has no readable
        //             property named 'IsoCode'
        CreateMap<Brand, BrandDto>();

        // Same map, plus the way back — one line, both directions:
        //
        //   StockDto dto   = mapper.Map<StockDto>(stock);
        //   Stock    stock = mapper.Map<Stock>(dto);
        //
        // This is also where conversion has to work BOTH WAYS. Stock.Id is an int and
        // StockDto.Id is a string, so the two generated maps do opposite things:
        //
        //   Id = ValueConverter.ToInvariantString(source.Id)                       // out
        //   Id = ValueConverter.Parse<int>(source.Id, "StockDto.Id -> Stock.Id")   // back
        //
        // Stock.BayNumbers is the same idea one level up: a List<int> on the entity, an
        // IReadOnlyList<string> on the DTO, so the SHAPE and the ELEMENTS both differ and both
        // get bridged, in both directions:
        //
        //   BayNumbers = ValueConverter.ToList<int, string>(source.BayNumbers, item => ...)  // out
        //   BayNumbers = ValueConverter.ToList<string, int>(source.BayNumbers, item => ...)  // back
        //
        // The type arguments are load bearing. A List<int> is not a List<string> and never
        // converts into one, however freely an int converts to a string — generics are
        // invariant, so the elements are converted one at a time into a list built for the
        // destination's element type.
        //
        // Reading is the only one of the two that can fail on DATA rather than on types, so
        // the build reports it as SM0009 — informational, like SM0006 below. Empty text
        // (what arrives when a client POSTs a new location without an id) reads back as 0;
        // anything else that will not parse throws a FormatException naming both properties
        // rather than quietly mapping a zero.
        //
        // The reverse is not a mirror of the forward map; it is worked out on its own by
        // the same rule. Stock has a Products navigation list that StockDto does not, so
        // mapping back cannot fill it. That is reported as SM0006 — INFO, not a warning,
        // because a DTO being a subset of its entity is the normal reason to reverse a map
        // at all. See it with `dotnet build -v d`, or in the IDE's Error List with
        // informational messages shown:
        //
        //   AppMapper.cs(147,38): info SM0006: the reverse map leaves 'Stock.Products'
        //   unmapped because 'StockDto' has no readable property named 'Products'
        //
        // Note it points at .ReverseMap(), not at CreateMap — that is the code responsible.
        CreateMap<Stock, StockDto>().ReverseMap();

        // This one does NOT map cleanly, on purpose — it is the live demonstration of the
        // build-time warnings. Building produces exactly two, both pointing at this line:
        //
        //   SM0001  InvoiceLineDto.LineTotal  — InvoiceLine has no LineTotal; the DTO
        //                                       computes it, so there is nothing to copy.
        //   SM0002  InvoiceLineDto.Product    — both sides HAVE a Product, and no conversion
        //                                       turns a Product into a ProductDto. Nested
        //                                       objects are not mapped, and inventing a
        //                                       cast here would only move the failure to
        //                                       runtime.
        //
        // The map is still generated for the three properties that DO line up (Id,
        // Quantity, UnitPrice) — ShiftMapper does what it can and tells you the rest.
        // Delete this line and the warnings go away.
        CreateMap<InvoiceLine, InvoiceLineDto>();

        // ------------------------------------------------------------------
        // IGNORE and MAPFROM — telling ShiftMapper what the conventions cannot work out.
        // ------------------------------------------------------------------
        //
        // Invoice -> InvoiceDto does not map by name alone. Two of the DTO's properties have no
        // counterpart on the entity at all, and they need opposite answers:
        //
        //   Total   is a number the entity never stores — it is the lines added up.
        //   Lines   is a collection of a DIFFERENT type (InvoiceLine -> InvoiceLineDto), which is
        //           nested mapping, and ShiftMapper does not do that yet.
        //
        // Left alone, both would be reported as SM0001 and stay empty. So:
        //
        //   MapFrom  supplies a value for Total.
        //   Ignore   says Lines is deliberately not mapped — the caller fills it in.
        //
        // Ignore also SILENCES the report for that one property on this one map, which is the
        // point of it: SM0001 telling you a property you decided to leave alone is unmapped is
        // exactly the noise you wanted gone. Every other property keeps reporting.
        CreateMap<Invoice, InvoiceDto>()
            .Ignore(d => d.Lines)

            // THE VALUE YOU WRITE HERE IS AN EXPRESSION, NOT A METHOD.
            //
            // Declared Expression<Func<...>>, so the compiler does not compile this lambda — it
            // builds a TREE describing it, right here in this file, with _numbering captured and
            // every name already resolved. ShiftMapper keeps that tree and uses it two ways:
            //
            //   mapper.Map<InvoiceDto>(invoice)      compiles it once and calls it.
            //   db.Invoices.ProjectTo<InvoiceDto>()  splices it into the generated projection, so
            //                                        EF sees ONE expression and writes ONE query.
            //
            // Which is why this line becomes a correlated subquery rather than loading every line
            // of every invoice to add them up in C#:
            //
            //   SELECT (SELECT SUM([l].[Quantity] * [l].[UnitPrice]) FROM [InvoiceLines] AS [l]
            //           WHERE [i].[Id] = [l].[InvoiceId]), ...
            .MapFrom(d => d.Total, s => s.Lines.Sum(l => l.Quantity * l.UnitPrice))

            // A SERVICE, inside a custom mapping. Nothing special is needed: _numbering is the
            // field the constructor was handed, and the generated code lives in this same class.
            //
            // _numbering.Prefix never looks at the invoice, so EF works it out once before
            // running anything and sends the answer as a parameter. The value is genuinely IN
            // the SQL:
            //
            //   DECLARE @_numbering_Prefix nvarchar(4000) = N'IQ/';
            //   SELECT ..., @_numbering_Prefix + [i].[Number] AS [Number]
            //
            // Now swap it for the other member of that same service:
            //
            //   .MapFrom(d => d.Number, s => _numbering.Format(s.Number, s.IssuedAt))
            //
            // That one takes the ROW's data, and no database can run a C# method out of this
            // project. It still works — EF is allowed to evaluate a top-level projection on the
            // client, so it selects the COLUMNS the call needs and runs Format per row as the
            // results arrive. Still one query, still only the columns the DTO uses:
            //
            //   SELECT ..., [i].[Number], [i].[IssuedAt]     -- Format runs in C#
            //
            // The difference shows up when you ask the DATABASE about that property. Prefix is
            // in the SQL, so filtering on it is fine. Format is not, so this throws:
            //
            //   db.Invoices.ProjectTo<InvoiceDto>(mapper)
            //              .Where(i => i.Number.Contains("2026"))
            //
            //   InvalidOperationException: ... could not be translated. Translation of method
            //   'IInvoiceNumbering.Format' failed.
            //
            // A Where decides which rows come back, so it cannot wait until they have. That is
            // about databases rather than about ShiftMapper, which does not guess at any of it —
            // it generates the projection and lets EF answer, so you get what today's EF can do
            // rather than what ShiftMapper assumed when it was written.
            .MapFrom(d => d.Number, s => _numbering.Prefix + s.Number);
    }

    /// <summary>Proof that constructor injection works on this class.</summary>
    public string InjectedDependency => _logger.GetType().Name;

    /// <summary>
    /// Proof that AddShiftMapper filled in Services — we resolve something through it that
    /// was never passed to the constructor. This is the hook custom mappings will use.
    /// </summary>
    public bool CanResolveThroughServices =>
        Services.GetService(typeof(ILoggerFactory)) is not null;
}
