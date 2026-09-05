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
        // and that is the problem. Any id past int.MaxValue comes back a DIFFERENT number —
        // 4000000001 reads as -294967295 — entirely plausible-looking, with nothing in the code
        // to say so and no exception to mark it. Hence a WARNING rather than a note:
        //
        // The seeded ids stay inside int range, because BrandDto is now reached by the NESTED
        // projection four levels down (Invoice -> Lines -> Product -> Brand) and SQL refuses the
        // overflow rather than wrapping it: "Arithmetic overflow error converting expression to
        // data type int". Which is worth knowing on its own — the conversion that quietly hands
        // back the wrong number in C# is a hard error in the database.
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
        // and what it refuses, reporting SM0002 rather than guessing: moving a reference around
        // by up-casting or boxing, your own EXPLICIT operators — and four pairs C# would convert
        // quite happily, because their answer would not come from the two types alone:
        //
        // Nested objects (Product -> ProductDto) and collections of them are no longer on that
        // list — they are MAPPED now, as long as a CreateMap exists for the pair, and a build
        // ERROR when one does not. See CreateMap<Product, ProductDto>() below.
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
        // COLLECTIONS AND NULLS AT THE TOP LEVEL come with this one line too, and neither
        // needs a word of configuration:
        //
        //   mapper.Map<List<BrandDto>>(brands)          // and BrandDto[], HashSet<>, IReadOnlyList<>
        //   mapper.MapToBrandDtoList(brands)            // the typed route, no typeof chain
        //   mapper.MapOrNull<BrandDto>(maybeNull)       // Map throws on null, deliberately
        //
        // See BrandEndpoints for both, and BrandDto.Aliases for the NULL-COLLECTION POLICY —
        // the one decision here that had to be made rather than inherited. A null source
        // collection becomes an EMPTY destination collection, in memory AND inside the
        // projection, so a DTO ShiftMapper built has no collection property a caller has to
        // test for null. Ask for the other answer per map, or for the whole mapper:
        //
        //   CreateMap<Brand, BrandDto>(o => o.AllowNullCollections = true);
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
        CreateMap<Stock, StockDto>()
            .ReverseMap()

            // FORMEMBER, ON THE REVERSE MAP. Everything chained before .ReverseMap() configures the
            // forward direction and everything after it configures the way back, and the types
            // enforce that on their own: ReverseMap returns MapExpression<StockDto, Stock>, so
            // here `d` IS a Stock and naming a StockDto property would not compile.
            //
            // Stock has a Products collection that StockDto does not, which used to be reported
            // as SM0006 every build. It is not an oversight — a DTO being a subset of its entity
            // is the whole reason to reverse a map — so this says as much, once, and the message
            // stops. Delete the line and it comes back.
            .ForMember(d => d.Products, opt => opt.Ignore());

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
        CreateMap<InvoiceLine, InvoiceLineDto>()

            // LineTotal has no counterpart on the entity — it is quantity times price, worked out
            // rather than stored. This is also the customization that proves nesting composes:
            // this map is used NESTED inside Invoice -> InvoiceDto below, and this opt.MapFrom still
            // applies there, in memory AND in SQL, without the outer map knowing about it.
            .ForMember(d => d.LineTotal, opt => opt.MapFrom(s => s.Quantity * s.UnitPrice));

        // ------------------------------------------------------------------
        // NESTED OBJECTS — one line, and a whole graph maps.
        // ------------------------------------------------------------------
        //
        // ProductDto has a BrandDto and a StockDto on it, and this single CreateMap fills both,
        // because maps for them are declared above. That is the rule: a nested object is mapped
        // when a CreateMap exists for its pair, and it is a BUILD ERROR when one does not.
        //
        // Delete this line to see it. InvoiceLineDto.Product then has nowhere to go, and the
        // build stops with SM0011 rather than quietly handing back a null Product:
        //
        //   error SM0011: 'InvoiceLineDto.Product' needs a map from 'Product' to 'ProductDto'.
        //                 Add CreateMap<Product, ProductDto>() to this mapper, or
        //                 .ForMember(d => d.Product, opt => opt.Ignore()) to leave it unmapped
        //
        // An ERROR rather than a warning, and it is the only one ShiftMapper reports. Every other
        // message describes a property left unmapped, which may well be deliberate. This one
        // describes a property that CANNOT be mapped yet, where a null in the response would look
        // exactly like a null in the database. Both ways out are one line, and both leave the
        // decision written down where the next reader will find it.
        CreateMap<Product, ProductDto>();

        // ------------------------------------------------------------------
        // FORMEMBER — telling ShiftMapper what the conventions cannot work out.
        // ------------------------------------------------------------------
        //
        // Invoice -> InvoiceDto does not map by name alone. Two of the DTO's properties have no
        // counterpart on the entity at all, and they need opposite answers:
        //
        //   Total   is a number the entity never stores — it is the lines added up, so
        //           opt.MapFrom supplies it.
        //   Lines   is a COLLECTION OF OBJECTS: List<InvoiceLine> on the entity, and
        //           IReadOnlyList<InvoiceLineDto> on the DTO. Nothing here mentions it, and it maps
        //           anyway — the map for the two element types is declared above, which is all
        //           nesting needs.
        //
        // Note that the two collection SHAPES differ as well as the element types, and that is
        // handled by the same helpers a collection of ints goes through: List<InvoiceLine> fills an
        // IReadOnlyList<InvoiceLineDto> for exactly the reason List<int> fills an
        // IReadOnlyList<string>. Declare it as an InvoiceLineDto[] or a HashSet<InvoiceLineDto>
        // and it still maps; only the per-element step is different.
        //
        // The whole graph comes with it, all four levels, and there is nothing to configure:
        //
        //   InvoiceDto
        //     .Lines          InvoiceLine -> InvoiceLineDto
        //       .Product      Product     -> ProductDto
        //         .Brand      Brand       -> BrandDto
        //         .Stock      Stock       -> StockDto
        //
        // THE ONLY RULE IS THAT A MAP EXISTS. Mapping goes as deep as the maps you declared, and
        // stops there. No depth setting, no default to remember, nothing that quietly changes
        // what a response contains.
        //
        // The one graph that cannot work that way is a LOOP — a BrandDto holding ProductDtos
        // that each hold a BrandDto. There is no depth at which that is finished, and generated
        // code following it would call itself until the stack ran out, which in .NET takes the
        // whole process down rather than failing one request. So it is a build ERROR (SM0012)
        // naming the loop, and opt.Ignore() on the back-reference is the one-line fix — which also
        // records which of the two types is the view and which is the thing being viewed.
        CreateMap<Invoice, InvoiceDto>()

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
            .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Lines.Sum(l => l.Quantity * l.UnitPrice)))

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
            //   .ForMember(d => d.Number, opt => opt.MapFrom(s => _numbering.Format(s.Number, s.IssuedAt)))
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
            .ForMember(d => d.Number, opt => opt.MapFrom(s => _numbering.Prefix + s.Number));

        // ------------------------------------------------------------------
        // DESTINATIONS THAT ARE NOT `new T { }`.
        // ------------------------------------------------------------------
        //
        // A POSITIONAL RECORD, which until constructor support was SM0004 and nothing else.
        // Nothing is configured here: a constructor parameter is a destination member that
        // happens to be written inside the parentheses, so it matches a source property by name,
        // converts when the types differ, and maps a nested object when a CreateMap exists.
        //
        //   return new ProductSummaryDto(
        //       source.Id, source.Name, source.Sku,
        //       ValueConverter.ToInvariantString(source.Price),      // decimal -> string
        //       MapToBrandSummaryDto(source.Brand));                 // a record inside a record
        //
        // AND IT PROJECTS, which is the reason it is worth having: GET /api/products/summary
        // hands EF one `new` with real arguments. See ProductSummaryDto.
        CreateMap<Brand, BrandSummaryDto>();
        CreateMap<Product, ProductSummaryDto>();

        // REQUIRED MEMBERS. C# refuses an object initializer that leaves one out, so an unmapped
        // required member stops the whole destination rather than just itself — and the build
        // says which one (SM0014) instead of leaving a CS9035 inside a generated file.
        //
        // Total is the interesting one: required AND customized. A customized member is normally
        // absent from the generated projection, and a required one cannot be, so the projection
        // names it with a placeholder that Compose replaces. Delete this ForMember to watch
        // SM0014 arrive. See InvoiceReceiptDto.
        //
        // AND IT IS MAPFROMSOURCE, WHICH IS THE POINT. The sum is a decimal and Total is a string,
        // so plain MapFrom cannot express this at all — its expression must return the
        // DESTINATION member's type. Until MapFromSource existed this line read
        //
        //     .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Lines.Sum(...).ToString("0.00")))
        //
        // which compiled, worked, and was WRONG: ToString with no format provider reads the
        // machine's culture, so the total left a German server as "1596,00" — in a library whose
        // ValueConverter exists to make exactly that impossible, and which gets it right for
        // ProductSummaryDto.Price two maps up. Handing the decimal over and letting the conversion
        // table write the ToString is the whole feature.
        CreateMap<Invoice, InvoiceReceiptDto>()
            // Money stays a decimal. Converting a COMPUTED decimal to text is where the two
            // backends part company, and not because of anything ShiftMapper does: EF writes
            // CAST([Quantity] AS decimal(18,2)) * [UnitPrice], so SQL multiplies scale 2 by scale
            // 2 and gets 4 where C# gets 2 — "1596.0000" against "1596.00". See
            // InvoiceReceiptDto.Total, which records the whole finding.
            .ForMember(d => d.Total, opt => opt.MapFrom(s => s.Lines.Sum(l => l.Quantity * l.UnitPrice)))

            // MAPFROMSOURCE, on a conversion that cannot drift. Lines.Count is an int and
            // LineCount is text, so MapFrom cannot say this at all — its expression has to return
            // the destination member's type, which is why the old Total read
            // `.MapFrom(s => s.Lines.Sum(...).ToString("0.00"))`: a hand-written conversion, with
            // no format provider, that sent "1596,00" from a German server.
            //
            // Handing the value over instead puts it back through the conversion table, so it gets
            // the invariant ToString AND the table's diagnostics, in both backends.
            .ForMember(d => d.LineCount, opt => opt.MapFromSource(s => s.Lines.Count));

        // ------------------------------------------------------------------
        // FLATTENING — reading VALUES out of a graph rather than mapping objects.
        // ------------------------------------------------------------------
        //
        // One option, no per-member configuration. Each name on InvoiceLineFlatDto is split on its
        // PascalCase boundaries and walked into the source:
        //
        //   ProductName       -> Product.Name
        //   ProductBrandName  -> Product.Brand.Name
        //   ProductPrice      -> Product.Price, converted to text by the same table a direct
        //                        match would have used
        //
        // Compare it with the InvoiceLine -> InvoiceLineDto map above, which is the same data
        // NESTED: that one needs a CreateMap for every type in the graph, and this one needs none.
        //
        // ON BY DEFAULT, so this map needs no option at all — which is the point: a destination
        // that reads like a path just works. Turn it off with o.Flattening = false, per map or for
        // a whole mapper in ConfigureDefaults, and these members go back to being SM0001.
        //
        // It is still a GUESS, so every member it fills is reported WITH THE PATH IT CHOSE:
        //
        //   info SM0020: 'InvoiceLineFlatDto.ProductBrandName' is filled by flattening,
        //                from 'InvoiceLine.Product.Brand.Name'
        //
        // Raise that where the maps matter:  dotnet_diagnostic.SM0020.severity = warning
        //
        // GET /api/invoices/lines/flat?sql=true is ONE query with three joins. See
        // InvoiceLineFlatDto for what is deliberately NOT in it.
        CreateMap<InvoiceLine, InvoiceLineFlatDto>();

        // ------------------------------------------------------------------
        // CONDITION — how the update overload stops being a PUT.
        // ------------------------------------------------------------------
        //
        // mapper.Map(dto, entity) assigns every mapped member, every time. That is right for a
        // full replace and wrong for a PATCH: a client who sends only a city would blank the name
        // and zero the founded year, because an absent JSON field arrives as "" and 0.
        //
        // A Condition guards the assignment and changes nothing else — the member still matches
        // by name, still goes through the same generated conversion, still reports the same
        // diagnostics. Think of it as a runtime opt.Ignore(): Ignore decides once at build time,
        // this decides per object.
        //
        // THE TRADE, which the build states rather than leaving to be found:
        //
        //   warning SM0017: the map from 'BrandPatch' to 'Brand' assigns 'Name', 'Country',
        //                   'ISOCode', 'FoundedYear' behind a Condition, so ProjectTo cannot use
        //                   it; Map is unaffected
        //
        // A projection is one member initializer handed to the database and there is no way to
        // leave a binding out per row. Asking for one throws a message naming this map.
        //
        // Try it: PATCH /api/brands/1 with only { "country": "Ireland" }.
        // FORALLMEMBERS replaces what used to be the same line written three times, once for
        // Name, Country and ISOCode. The rule is uniform — "do not overwrite with a blank" — so
        // it is said once.
        //
        // The value arrives as an OBJECT, because one predicate has to serve members of every
        // type. That is the price of saying it once, and it is why FoundedYear keeps a condition
        // of its OWN below: "blank" for a number is 0, not an empty string, and a typed predicate
        // says that far better than a cast would.
        //
        // A MEMBER'S OWN CONDITION WINS, never both. FoundedYear uses the typed one; the other
        // three fall back to the blanket rule.
        CreateMap<BrandPatch, Brand>()
            .ForAllMembers(opt => opt.Condition((s, d, value) => value is not string text || text.Length > 0))
            .ForMember(d => d.FoundedYear, opt => opt.Condition((s, d, value) => value > 0))

            // Everything Brand has that a patch body does not. Without these the map reports four
            // SM0001s for members a partial update was never going to carry.
            .ForMember(d => d.Id, opt => opt.Ignore())
            .ForMember(d => d.Tags, opt => opt.Ignore())
            .ForMember(d => d.ExternalIds, opt => opt.Ignore())
            .ForMember(d => d.Aliases, opt => opt.Ignore())
            .ForMember(d => d.Products, opt => opt.Ignore());

        // CONSTRUCTUSING, for the case convention cannot reach: no constructor ShiftMapper could
        // pick would know about the numbering service. It replaces CONSTRUCTION and nothing else
        // — CustomerName is still mapped by name onto the object this expression returned.
        //
        // The cost is stated at build time rather than discovered at run time:
        //
        //   info SM0015: the map from 'Invoice' to 'InvoiceLabelDto' builds its destination with
        //                ConstructUsing, so ProjectTo cannot use it; Map is unaffected
        //
        // GET /api/invoices/{id}/label?project=true asks for the projection anyway, to show what
        // the refusal reads like.
        // AFTERMAP is added here because this is the one case it earns: Display is derived from
        // the FINISHED destination — the factory's Label plus the mapped CustomerName — and no
        // MapFrom could produce it, since a MapFrom sees the source and this needs the result.
        //
        // The Ignore is the pattern, not boilerplate. A hook is an Action the generator cannot see
        // inside, so it has no idea Display gets filled, and "'InvoiceLabelDto.Display' is not
        // mapped" is a true statement about the conventions. Ignoring it says which member the
        // hook owns.
        CreateMap<Invoice, InvoiceLabelDto>()
            .ConstructUsing(s => new InvoiceLabelDto(_numbering.Prefix + s.Number))
            .ForMember(d => d.Display, opt => opt.Ignore())
            .AfterMap((s, d) => d.Display = d.Label + " — " + d.CustomerName);

        // ------------------------------------------------------------------
        // CONVERTUSING — the expression IS the map, and the one hook that projects.
        // ------------------------------------------------------------------
        //
        // No member is matched, converted or reported: BrandLabelDto.Label has no counterpart on
        // Brand and never produces an SM0001, because this map does not do conventions at all.
        //
        // AND IT REACHES THE DATABASE, which is why it is the important one. BeforeMap, AfterMap
        // and Condition are STATEMENTS, and a projection is one expression handed to SQL — there
        // is nowhere in it for a statement to be, so those three cost the map its projection. A
        // ConvertUsing is already an expression, so nothing has to be composed into it:
        //
        //     SELECT [b].[Name] + N' (' + [b].[ISOCode] + N')' FROM [Brands] AS [b]
        //
        // That is what makes it the foundation of the global conversion table this library is
        // heading for: a conversion registered once for a TYPE PAIR is a ConvertUsing declared
        // somewhere else, and it is worth nothing to a list endpoint unless it reaches SQL.
        //
        // GET /api/brands/labels?sql=true
        CreateMap<Brand, BrandLabelDto>()
            .ConvertUsing(b => new BrandLabelDto { Label = b.Name + " (" + b.ISOCode + ")" });

        // ------------------------------------------------------------------
        // DICTIONARIES — the other shape a collection comes in.
        // ------------------------------------------------------------------
        //
        // Nothing is configured here either. A dictionary is copied rather than shared, its keys
        // and values convert by the ordinary rules, and a null one follows the same policy every
        // other collection does. The only thing it needs of its own is a second element type:
        //
        //   Prices = ValueConverter.ToDictionaryOrEmpty<string, decimal, string, string>(
        //                source.Prices, static key => key,
        //                static value => ValueConverter.ToInvariantString(value))
        //
        // This map is also the sample's live demonstration of SM0008 on a dictionary. Notes has
        // int keys on the way in and string keys on the way out, and CONVERTING KEYS is the one
        // thing a dictionary can lose that a list cannot — two keys that were distinct in the
        // source can arrive as one, and the later entry wins. See SupplierFeed.Notes.
        //
        // Read the pair through POST /api/supplier-feeds/preview.
        CreateMap<SupplierFeed, SupplierFeedDto>();
    }

    /// <summary>Proof that constructor injection works on this class.</summary>
    // MAPPER-WIDE DEFAULTS. Override this to change a setting for every map at once, instead of
    // repeating it on each CreateMap. It is an OVERRIDE, a member of the class — not something
    // you call from the constructor, where it would compile and do nothing.
    //
    // protected override void ConfigureDefaults(MapOptions options)
    //     => options.Matching = PropertyMatching.CaseSensitive;

    public string InjectedDependency => _logger.GetType().Name;

    /// <summary>
    /// Proof that AddShiftMapper filled in Services — we resolve something through it that
    /// was never passed to the constructor. This is the hook custom mappings will use.
    /// </summary>
    public bool CanResolveThroughServices =>
        Services.GetService(typeof(ILoggerFactory)) is not null;
}
