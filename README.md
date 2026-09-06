# ShiftMapper

A compile-time object mapper for .NET.

You declare the type pairs you want mapped. A source generator reads them at build time and
writes the mapping code into your own partial class — the in-memory `Map` methods **and** a
`ProjectTo` expression EF Core turns into SQL. Anything it cannot map becomes a build
diagnostic naming the property, not a silently unfilled field.

```csharp
public partial class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Brand, BrandDto>();
}

var dto  = mapper.Map<BrandDto>(brand);                 // in memory
var page = db.Brands.ProjectTo<BrandDto>(mapper)        // in the database
                    .Where(b => b.Name.StartsWith("A"))
                    .ToListAsync();
```

No reflection, no runtime configuration scan, no `IMapper.ConfigurationProvider`. The
generated file is ordinary C# you can read, step through and diff.

---

## Install

```
dotnet add package ShiftSoftware.ShiftMapper
```

One package, both halves. NuGet hands the runtime types to your application and the source
generator to the compiler; there is nothing else to reference and nothing to register with the
build.

Requires **.NET 10**. See [Versioning and target frameworks](#versioning-and-target-frameworks).

---

## Five minutes

### 1. Declare a mapper

Maps go in the constructor, and the class must be `partial` — that is where the generated half
lands.

```csharp
using ShiftMapper;

public partial class AppMapper : ShiftMapperBase
{
    public AppMapper()
    {
        CreateMap<Brand, BrandDto>();

        CreateMap<Stock, StockDto>()
            .ReverseMap();                                   // and back again

        CreateMap<Product, ProductDto>()
            .ForMember(d => d.Internal,  opt => opt.Ignore())
            .ForMember(d => d.BrandName, opt => opt.MapFrom(s => s.Brand.Name));
    }
}
```

`CreateMap`, `ForMember` and the rest never run. They exist so a type pair can be written down
in ordinary C# that the compiler checks; the generator reads the calls at build time.

A mapper is an ordinary class, so it can take constructor dependencies and use them from a
`MapFrom`.

### 2. Register it

```csharp
builder.Services.AddShiftMapper<AppMapper>();       // Scoped by default
```

### 3. Map

Each map produces a small family of entry points. The instance methods live on your class; the
extension methods forward to them, so use whichever reads better where you are.

```csharp
BrandDto dto = mapper.Map<BrandDto>(brand);           // create
mapper.Map(brand, existingDto);                       // update in place
BrandDto? dto = mapper.MapOrNull<BrandDto>(maybe);    // null in, null out

BrandDto dto = brand.Map<BrandDto>(mapper);           // the same three, as extensions
brand.Map(existingDto, mapper);
BrandDto? dto = maybe.MapOrNull<BrandDto>(mapper);

IQueryable<BrandDto> q = mapper.ProjectTo<BrandDto>(db.Brands);
IQueryable<BrandDto> q = db.Brands.ProjectTo<BrandDto>(mapper);
```

`Map` **throws on a null source**, deliberately: asking to build a DTO out of nothing is almost
always a bug, and one far cheaper to hear about at the mapping call than three layers away.
`MapOrNull` is the door for the cases where a missing source is ordinary data — a row that was not
found, an optional relationship.

`ProjectTo` is the one that matters for a database. It hands EF Core a single expression tree
covering the whole graph, so only the columns the DTO needs are selected, and filtering and
paging happen in SQL against the projected shape.

### Collections

Every map also maps a sequence, in whichever shape you ask for:

```csharp
List<BrandDto>         list  = mapper.Map<List<BrandDto>>(brands);
BrandDto[]             array = mapper.Map<BrandDto[]>(brands);
HashSet<BrandDto>      set   = mapper.Map<HashSet<BrandDto>>(brands);
IReadOnlyList<BrandDto> read = mapper.Map<IReadOnlyList<BrandDto>>(brands);

List<BrandDto> list = mapper.MapToBrandDtoList(brands);   // typed, no runtime type test
List<BrandDto> list = brands.Map<List<BrandDto>>(mapper); // extension
```

The source is any `IEnumerable<T>`; the destination is one of those four. A shape nothing builds
throws a message naming the four, rather than guessing.

### Null collections

One decision that had to be made rather than inherited: **a null source collection becomes an
EMPTY destination collection.** It is what AutoMapper does (`AllowNullCollections`, off by
default), and it is the answer that removes a null check from every consumer of the DTO forever —
including the first place somebody would have forgotten one.

```csharp
CreateMap<Brand, BrandDto>();                                   // null Tags -> []
CreateMap<Brand, BrandDto>(o => o.AllowNullCollections = true);  // null Tags -> null
```

Set it per map, or for a whole mapper from `ConfigureDefaults`. It covers collections of values,
collections of mapped objects, dictionaries, and the collection overloads above — so
`Map<List<BrandDto>>(null)` answers the same question the same way instead of throwing.

**In a projection** the answer depends on what EF can produce. A navigation collection is never
null there, so the default is already what you get. A collection of values in a column you
declared **nullable** is guarded, and EF turns the guard into a `COALESCE` in the SELECT list. A
collection you declared **non-nullable** is not guarded, on purpose: EF recognises a primitive
collection by the shape around it, and a coalesce is a shape it cannot see through — a projection
that translated perfectly would stop translating, at run time, to guard against a null the type
says cannot happen. So declare a collection nullable when it really is, and both halves of
ShiftMapper answer alike.

---

## What it maps

- **Properties by name**, exact match first with a case-insensitive fallback, so `Sku` finds
  `SKU`. Configurable per map or per mapper (`PropertyMatching`, `ConfigureDefaults`).
- **Type conversions** between matched properties: implicit conversions, numeric pairs, text
  parsing and formatting, enums, `Guid`, `TimeSpan` and the date and time types, user-defined
  implicit operators, and collections of all of those (`ToArray` / `ToList` / `ToHashSet`).
- **Dictionaries**, read from any `IEnumerable<KeyValuePair<K, V>>` and built as a
  `Dictionary<K, V>`, `IDictionary<K, V>` or `IReadOnlyDictionary<K, V>`. Keys and values convert
  independently, by the same rules. Converting the *keys* is the one thing a dictionary can lose
  that a list cannot — two source keys can arrive as one, the later winning — so that is reported
  as SM0008.
- **Nested objects and collections of objects**, composed to any depth from the maps you
  declared — in memory and inside one EF projection.
- **Destinations built through a constructor**: positional records, primary constructors, and
  `required` members. See below.
- **Per-member overrides**: `opt.Ignore()` and `opt.MapFrom(s => ...)`.

Both backends are generated from the same analysis, so `Map` and `ProjectTo` agree on what a
map means.

### Flattening and naming conventions

A destination member with no source of its own is filled by WALKING into the source —
`OrderDto.CustomerName` from `Order.Customer.Name`. It is **on by default**, so this is the whole
map:

```csharp
CreateMap<InvoiceLine, InvoiceLineFlatDto>();

// and this turns it off, per map or for a whole mapper in ConfigureDefaults
CreateMap<Order, OrderDto>(o => o.Flattening = false);
```

The name is split on PascalCase boundaries and re-joined every way that resolves, each step matched
by this map's own `Matching` rule. The leaf goes through the conversion table like any other member,
so a `decimal Price` fills a `string ProductPrice` with no extra configuration. **It projects**: the
chain reaches EF as one expression, so it becomes a join rather than a second query.

**It is a guess, and SM0020 is how you audit it.** Nothing in the name `CustomerName` says it
means `Customer.Name` rather than a column nobody has added yet — so every member flattening fills
is reported, **with the path it chose**, as an informational **SM0020**. That is the audit trail
AutoMapper does not give you. Raise it where the maps matter:
`dotnet_diagnostic.SM0020.severity = warning`.

Two things keep the guessing narrow. Flattening **never competes with a real property** — it runs
only where the direct match already failed, so it can only fill something that would otherwise have
been SM0001. And a name that resolves **more than one way** is refused outright, not decided.

The limits are as deliberate as the switch:

- **It will not walk into a `string`**, so `NameLength` never quietly becomes `Name.Length`. Nor
  into a collection or a nullable value type — neither has one traversal to pick.
- **Two paths is a question, not a tie to break.** When a name resolves more than one way the member
  is left unmapped and both paths are named (**SM0021**).
- **A nullable step is guarded**, a required one is not: `source.X == null ? default(string)! :
  source.X.Y`, which runs in memory and translates to a `CASE WHEN`. Guarding a required
  relationship would change its SQL to defend against a null the model says cannot happen. Note the
  consequence — a guarded `int` leaf lands as `0`, indistinguishable from a real zero.

**Naming conventions** widen a plain match, and apply to each step of a walk:

```csharp
CreateMap<DbBrand, BrandDto>(o => o.RecognizePrefixes("Db"));    // Name  <- DbName
CreateMap<Order, OrderDto>(o => o.RecognizePostfixes("Id"));     // Customer <- CustomerId
```

The bare name is always tried first, so a source declaring both `Name` and `DbName` is not
ambiguous. All three settings can be stated once for a whole mapper in `ConfigureDefaults`.

### Map-level hooks

Four things can be said about a map as a whole, and they divide on one question — **can it be an
EXPRESSION?** A projection is one expression handed to the database, so anything needing a
statement cannot be in one.

```csharp
CreateMap<Brand, BrandLabelDto>()
    .ConvertUsing(b => new BrandLabelDto { Label = b.Name + " (" + b.ISOCode + ")" });

CreateMap<Invoice, InvoiceLabelDto>()
    .ForMember(d => d.Display, opt => opt.Ignore())
    .AfterMap((s, d) => d.Display = d.Label + " — " + d.CustomerName);

CreateMap<BrandPatch, Brand>()
    .ForAllMembers(opt => opt.Condition((s, d, value) => value is not string text || text.Length > 0));
```

| Hook | What it replaces | Projects? |
|---|---|---|
| `ConvertUsing` | the WHOLE map | **yes** — it is already an expression |
| `ConstructUsing` | construction only | no (SM0015) |
| `BeforeMap` / `AfterMap` | nothing; adds a statement | no (SM0018) |
| `ForAllMembers(opt => opt.Condition(...))` | nothing; guards every assignment | no (SM0017) |

**`ConvertUsing` is the one that matters most.** The expression IS the map: no member is matched,
converted or reported, and because a tree is exactly what a projection needs, EF gets it unchanged
— `SELECT [b].[Name] + N' (' + [b].[ISOCode] + N')'`. That is the foundation of the global
conversion table this library is heading for: a conversion registered once for a type PAIR is a
`ConvertUsing` declared somewhere else, and it is worth nothing to a list endpoint unless it
reaches SQL. It has no update overload — an expression that builds a new object cannot fill one
it was handed — and anything else configured on such a map is reported as doing nothing
(**SM0019**).

**`BeforeMap` / `AfterMap` are in-memory only, and the map loses its projection (SM0018).** A
warning rather than a note, because the failure would otherwise be silent: the projection would be
built, run, and hand back rows the hook never touched. `AfterMap` earns its place where the value
needs the FINISHED destination; anything derivable from the source belongs in a `ForMember`, which
projects. On a create, ShiftMapper moves every member it can assign afterwards out of the object
initializer so `BeforeMap` genuinely precedes them — only what construction settles (constructor
arguments, `init` and `required` members) is already there.

A hook is an `Action` the generator cannot see inside, so it does not know which members the hook
fills. Give those an `opt.Ignore()`: it is what says which members the hook owns.

**`ForAllMembers`** says one thing about every member instead of repeating it. It offers only
`Condition`, because a blanket `MapFrom` has no meaning and a blanket `Ignore` is a map that maps
nothing — a type does that job better than a diagnostic would. The value arrives boxed as
`object`, since one predicate serves members of every type; a member's own `Condition` always wins
over the blanket one, and members that cannot be guarded at all are skipped rather than refused.

### Per-member options

`ForMember` takes more than `Ignore` and `MapFrom`. Each option changes one part of the single
line the generator writes — where the value comes from, or whether it is assigned at all:

```csharp
CreateMap<Invoice, InvoiceReceiptDto>()
    // the expression returns the SOURCE member's type; ShiftMapper converts it
    .ForMember(d => d.LineCount, opt => opt.MapFromSource(s => s.Lines.Count));

CreateMap<BrandPatch, Brand>()
    // assign only when the predicate says so; otherwise leave the member alone
    .ForMember(d => d.Name, opt => opt.Condition((s, d, value) => !string.IsNullOrWhiteSpace(value)));
```

**`MapFromSource`** exists because `MapFrom`'s expression must return the DESTINATION member's
type. So `opt.MapFrom(s => s.Lines.Count)` onto a `string` does not compile, and the workaround is
to convert by hand — which hand-writes the conversion this library exists to write, takes the
member out of the conversion table so SM0002/SM0008/SM0009/SM0010 stop being reported for it, and
loses the invariant-culture guarantee (`ToString()` with no format provider reads the machine's
culture). `MapFromSource` hands the value over instead. **It projects**: the conversion travels
into the projection as a small lambda spliced onto your expression, so EF still sees one
expression.

It is a second NAME rather than an overload on purpose. An overload would silently re-bind
existing calls — `opt.MapFrom(s => s.Rank)` onto a `long` member compiles today through the
implicit conversion inside the tree, and a generic overload is the better match — producing a
delegate the generated cast then refuses at run time.

**`Condition`** is a runtime `Ignore`. `Ignore` decides once at build time that a member is not
mapped; `Condition` decides per object, and when it says no the member is **left alone** — not
set to `default`. That is what turns `Map(source, destination)` from a PUT into a PATCH: without
it, a caller who sent one field blanks the rest, because an absent JSON field arrives as `""` or
`0`.

- On a **create**, "left alone" means the property keeps its own initializer value — the map
  builds the object, then assigns the conditioned members behind their guards.
- A member whose value is settled during construction cannot be conditioned: `init`-only,
  `required` in an object initializer, or a constructor argument. That is **SM0014**'s
  sibling **SM0016**, an error naming the member.
- **The map loses its projection** (**SM0017**). A projection is one member initializer handed to
  the database; there is no way to leave a binding out per row. Put conditions on write maps, not
  on the read maps your list endpoints project.

### Records, primary constructors and `required` members

A destination does not have to be `new T { }`. ShiftMapper picks a constructor and matches its
parameters to source properties, so the shapes most DTOs actually take work with nothing
configured:

```csharp
public record ProductSummaryDto(int Id, string Name, string Price, BrandSummaryDto Brand);

CreateMap<Brand, BrandSummaryDto>();
CreateMap<Product, ProductSummaryDto>();   // that is all
```

**A constructor parameter is a destination member** that happens to be written inside the
parentheses. It matches a source property by name, converts when the types differ, maps a nested
object when a `CreateMap` exists for the pair, and a `ForMember` naming the member it stands for
fills it — which on a positional record is the only way to customize anything, since the property
is init-only and the constructor has already set it.

**And it projects.** EF is handed one `new` with real arguments, so a record produces the same
query an ordinary class does. That is the reason this is worth having rather than a convenience.

The rules, briefly:

- A public **parameterless** constructor always wins, so nothing that already mapped changes shape.
- Otherwise the **greediest** constructor whose every parameter can be filled.
- Parameter-to-property matching always ignores case (`id` backs `Id`); source matching follows
  the map's own `PropertyMatching`.
- A parameter nothing can fill is **SM0013**, naming the parameter.
- An unmapped `required` member is **SM0014**. It is not a property left empty — C# refuses an
  initializer that omits one, so it stops the whole destination. `[SetsRequiredMembers]` is taken
  at its word.
- A destination with nothing assignable after construction — a positional record — gets **no
  update overload**. It would return the object it was handed having done nothing, and a compile
  error at the call site is the better answer.

### Profiles: maps written outside the mapper

One constructor is a fine place for a dozen maps and a poor place for fifty.

```csharp
public class CatalogProfile : ShiftMapperProfile
{
    public CatalogProfile()
    {
        CreateMap<CatalogItem, CatalogItemDto>()
            .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Sku.ToUpper()));

        CreateMap<PhysicalItem, PhysicalItemDto>().IncludeBase<CatalogItem, CatalogItemDto>();
    }
}

public partial class AppMapper : ShiftMapperBase
{
    public AppMapper() => AddProfile<CatalogProfile>();
}
```

**A profile is a place to write declarations, not a second mapper.** Nothing is generated onto it:
its maps become the maps of every mapper that adds it, called through that mapper exactly as if the
`CreateMap` had been written in its own constructor. There is no `CatalogProfile.Map` to find.

The whole surface is inherited, because it is literally the same method — `CreateMap`, the open
generic `CreateMap`, and every refinement chained onto them mean the same thing in a profile. So
does crossing between them: an `IncludeBase` finds a base map declared in another profile, and an
open generic closes over pairs declared anywhere. A profile is a place to write, not a wall.

**Defaults come from the mapper.** One mapper has one `ConfigureDefaults` whichever file a map was
written in; an override on a profile is reported as doing nothing (**SM0029**). A pair declared both
in a profile and outside it keeps the one outside, and the clash is reported (**SM0027**) rather
than left to be discovered.

**Dependencies work, and are resolved late:**

```csharp
public class InvoiceProfile : ShiftMapperProfile
{
    public InvoiceProfile(IInvoiceNumbering numbering) =>
        CreateMap<Invoice, InvoiceLabelDto>()
            .ConstructUsing(s => new InvoiceLabelDto(numbering.Prefix + s.Number));
}
```

Register it and it is resolved from the mapper's `Services` the first time anything is mapped —
not while the mapper's constructor runs, because that provider does not exist yet. A parameterless
profile needs no registration at all.

The edge that follows is worth knowing: **a profile taking dependencies makes the whole mapper
DI-only.** All of a mapper's profiles are built together on first use, so one that cannot be built
fails the mapper's first map, including maps unrelated to it. Skipping it instead would leave its
`MapFrom` members quietly unfilled, which is the divergence this library exists to prevent. If you
construct mappers by hand in tests, keep their profiles parameterless.

**Same compilation only.** A profile is read as SOURCE, so one compiled into a referenced package
cannot be read at all — a generator sees a referenced assembly as metadata, and metadata has no
method bodies. That is reported (**SM0028**), not silently mapped as nothing. Extending a mapper
from another assembly needs a different mechanism entirely, and is what the next steps are about.

### Inheritance, polymorphism and open generics

A table-per-hierarchy table is one table, a discriminator column, and a base type you can query
without knowing which row is which. Four things follow from that, and each has an answer.

**`IncludeBase` — say it once.**

```csharp
CreateMap<CatalogItem, CatalogItemDto>()
    .ForMember(d => d.Sku,  opt => opt.MapFrom(s => s.Sku.ToUpper()))
    .ForMember(d => d.Kind, opt => opt.Ignore());

CreateMap<PhysicalItem, PhysicalItemDto>().IncludeBase<CatalogItem, CatalogItemDto>();
CreateMap<DigitalItem,  DigitalItemDto>().IncludeBase<CatalogItem, CatalogItemDto>();
```

What is inherited is the **configuration, not the members**. The members were never the problem:
`PhysicalItem` already *is* a `CatalogItem`, so `Sku` already matched by name. What could not be
shared was everything said *about* it, which had to be repeated on every map in the family. Own
configuration always wins per member, so a derived map can disagree about one member without
restating the rest; bases are merged nearest-first and across every part of a partial mapper. An
`IncludeBase` naming a pair with no `CreateMap` is **SM0022**.

**It is transitive**, so a deeper family names only its parent at each step:

```csharp
CreateMap<BundleItem, BundleItemDto>().IncludeBase<PhysicalItem, PhysicalItemDto>();
```

That map never mentions `CatalogItem` and still gets its `Sku` expression and its `Kind` ignore
from two levels up. Nearest-first is what makes a middle map able to override one member and pass
everything else down untouched.

**And it projects.** Worth stating because it very nearly did not: everything you write is stored
against the pair you wrote it for, so that `Sku` expression lives under
`CatalogItem → CatalogItemDto` and a derived map asking under its own pair would find nothing.
That is true in memory and equally true inside the projection, which collects by the same key. The
customization store keeps a lineage and **both backends walk it** — otherwise `Map` would
upper-case the SKU and `ProjectTo` would not, which is the kind of divergence that survives review
because each answer looks right on its own.

**`Include` — dispatch on what the value really is.**

```csharp
CreateMap<CatalogItem, CatalogItemDto>()
    .Include<PhysicalItem, PhysicalItemDto>()
    .Include<DigitalItem,  DigitalItemDto>();
```

Without it, a row that is really a `PhysicalItem` maps to a bare `CatalogItemDto` and the weight is
dropped in silence — nothing in the types was wrong, the map for `CatalogItem` ran and was correct
as far as it could see. With it, that map tests the runtime type first, per value and per collection
element:

```csharp
if (source is PhysicalItem derived0) return MapToPhysicalItemDto(derived0);
if (source is DigitalItem  derived1) return MapToDigitalItemDto(derived1);
```

**Deeper families work, in either arrangement.** Chained — each map including only its immediate
child — composes on its own, because the derived map does its own dispatch. Listing a child and a
grandchild on the SAME map works too, and the order you write them in does not matter: type tests
are checked in the order they are written, so `is PhysicalItem` would otherwise catch a
`BundleItem` and answer with a `PhysicalItemDto`, dropping in silence exactly what `Include` was
added to keep. **The tests are emitted deepest-first.** It is the rule C# enforces for `catch`
clauses, except that sorting is kinder than an error: these calls can be spread across parts of a
class, so there is no single place a developer could read to get the order right.

**It costs the projection, and the build says so (SM0024).** A projection has one element type,
fixed when the query is written; SQL returns rows of one shape, and there is no per-row type test a
provider could translate. So the generated projection member *throws*, with a message naming the
alternative, rather than quietly returning bare `CatalogItemDto`s — the wrong answer wearing the
right type. The alternative is not a workaround but the query you meant:

```csharp
db.CatalogItems.OfType<PhysicalItem>().ProjectTo<PhysicalItemDto>(mapper)
```

Still one round trip, with `WHERE [Discriminator] = N'PhysicalItem'` doing the filtering.

**`As` — an interface or abstract destination.**

```csharp
CreateMap<PhysicalItem, ICatalogLabel>().As<PhysicalItemDto>();
```

An interface has no constructor, so the pair was SM0004 and no map at all. `As` names the type that
stands in for it, and what comes out is a **redirection** rather than a second copy of the mapping:
`Map<ICatalogLabel>` is one line calling `MapToPhysicalItemDto`. A named type that is not assignable
to the destination is **SM0025**.

**And unlike `Include`, `As` projects** — the distinction is the reason both exist. Nothing is
decided per row: the concrete type was fixed when the `CreateMap` was written, so the projection is
the concrete map's own expression with a widening cast on the end, and the database sees the
`SELECT` it always did.

**Open generics — one declaration, closed per pair.**

```csharp
CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>));
```

Closed for **every pair the mapper already maps**, which is both the useful rule and the only
decidable one — closing over every closed type in the compilation would mean guessing which of a
program's thousands of types somebody meant to wrap, and would change its answer when an unrelated
`using` was added. Constraints are checked, interface and abstract element destinations are skipped,
and the closed maps are ordinary in every respect, projection included. One type parameter on each
side: with two there is no single pairing to choose, only a combinatorial one, so it is refused
(**SM0026**) rather than guessed at.

### `ConstructUsing`, for what convention cannot reach

```csharp
CreateMap<Invoice, InvoiceLabelDto>()
    .ConstructUsing(s => new InvoiceLabelDto(_numbering.Prefix + s.Number));
```

It replaces **construction and nothing else**: every property ShiftMapper would have mapped is
still assigned onto the object your expression returned, so the ones it cannot assign afterwards
(`init` and `required` members) are yours to fill in the expression. The generated method's
`<remarks>` lists exactly which those are.

**It is in-memory only, and the build says so (SM0015).** A projection reaches EF as one
expression it reads all the way down, and there is no general way to graft mapped properties onto
an object a delegate returned. Asking for one throws a message naming the map rather than failing
somewhere inside EF. When a map has to project, the answer is a constructor ShiftMapper can match
by name plus `ForMember` for the arguments convention cannot work out.

### For libraries: `IShiftMapper`

The methods above are strongly typed, and that is the point of them: a destination with no map is
a compile error at the call site. A **library** cannot use them — code in a shared package has to
map an entity to a DTO in an application it has never seen, whose mapper class it cannot name. So
every generated mapper also implements one interface:

```csharp
public interface IShiftMapper
{
    TDestination Map<TDestination>(object source);
    TDestination Map<TSource, TDestination>(TSource source);
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);
    IQueryable<TDestination> ProjectTo<TSource, TDestination>(IQueryable<TSource> source);
    bool CanMap(Type source, Type destination);
}
```

`AddShiftMapper<AppMapper>()` registers it alongside the mapper's own type, and both resolve to
the same instance:

```csharp
public class Repository<TEntity, TDto>(IShiftMapper mapper, DbContext db)
{
    public IQueryable<TDto> List() => mapper.ProjectTo<TEntity, TDto>(db.Set<TEntity>());

    public TDto? Read(TEntity entity) =>
        mapper.CanMap(typeof(TEntity), typeof(TDto)) ? mapper.Map<TEntity, TDto>(entity) : default;
}
```

Worth knowing:

- **It is the slower door, on purpose.** Every method finds its map by comparing types at runtime,
  and a struct destination is boxed on the way back. Use the generated methods wherever the call
  site knows both types.
- **The members are implemented explicitly**, so they stay invisible on your mapper class.
  `mapper.Map<SomeDto>(unmappedThing)` keeps failing to compile rather than binding to the
  `object` overload and throwing at runtime.
- **`CanMap` is there so you never have to catch an exception to find out.** It answers for the
  create methods and matches them rule for rule, subclasses included.
- **The create doors accept a subclass** of a mapped type — exact runtime type first, then
  assignability — so an EF proxy maps through its base. The update overload needs the exact
  declared pair, because the destination you passed in is the object being written to.
- Registering two mappers is allowed; the last one wins for `IShiftMapper`, as DI always does.

### What it deliberately refuses

Four conversions are refused because the answer would come from something other than the two
types: `DateTime` to `DateTimeOffset` (whose time zone?), `DateTimeOffset` to `DateTime`, one
enum to a different enum (a cast pairs them by number), and `TimeSpan` to `TimeOnly`. A
user-defined *explicit* operator is refused too — its author chose the keyword that says stop
and think. Each is reported as SM0002 rather than skipped in silence.

---

## Diagnostics

Twenty-nine rules, `SM0001` to `SM0029`. Three stop the build; the rest describe something that
will not be mapped, or will be mapped in a way worth knowing about.

| Id | Default | What it means |
|---|---|---|
| SM0001 | Warning | Destination property has no matching source property |
| SM0002 | Warning | Names match; ShiftMapper does not convert between the two types |
| SM0003 | Warning | Destination property's setter is not public |
| SM0004 | Warning | Destination type has no constructor ShiftMapper can call |
| SM0005 | Warning | Nothing was generated for a `ShiftMapperBase` class (not `partial`, or generic) |
| SM0006 | Info | A `ReverseMap` leaves a destination property unmapped |
| SM0007 | Warning | Several source properties match when case is ignored |
| SM0008 | Info | Mapped through a conversion that loses information by design |
| SM0009 | Info | Mapped by parsing text at runtime, so bad data throws |
| SM0010 | Warning | Mapped through a conversion that can change the value (e.g. `long` to `int`) |
| SM0011 | **Error** | A nested object property has no `CreateMap` for its types |
| SM0012 | **Error** | Nested maps form a circular graph |
| SM0013 | Warning | A constructor parameter cannot be filled (names the parameter) |
| SM0014 | Warning | A `required` member is not mapped, so the destination cannot be built |
| SM0015 | Info | The map uses `ConstructUsing`, so `ProjectTo` cannot use it |
| SM0016 | **Error** | A member whose value is settled at construction cannot carry a `Condition` |
| SM0017 | Warning | The map carries a `Condition`, so `ProjectTo` cannot use it |
| SM0018 | Warning | The map runs a `BeforeMap`/`AfterMap` hook, so `ProjectTo` cannot use it |
| SM0019 | Warning | `ConvertUsing` replaces the whole map, so other configuration does nothing |
| SM0020 | Info | A destination property was filled by flattening (names the path) |
| SM0021 | Warning | A destination property flattens more than one way, so none was taken |
| SM0022 | Warning | `IncludeBase` names a pair with no `CreateMap` |
| SM0023 | Warning | `Include` names a pair that does not derive from this one, or has no map |
| SM0024 | Warning | The map dispatches through `Include`, so `ProjectTo` cannot use it |
| SM0025 | Warning | `As` names a type that is not assignable to the destination |
| SM0026 | Warning | An open generic `CreateMap` was not closed (needs one type parameter a side) |
| SM0027 | Warning | A pair is declared both in a profile and outside it |
| SM0028 | Warning | A profile in a referenced assembly cannot be read |
| SM0029 | Warning | `ConfigureDefaults` on a profile has no effect |

`SM0011` is an error because a null nested object in a response looks exactly like a null in the
database. Two ways forward, both one line: declare the map, or `opt.Ignore()` the property.
Either way the decision ends up written down.

### Tuning them

These come from a real `DiagnosticAnalyzer`, so `.editorconfig` works, including per folder:

```ini
[*.cs]
dotnet_diagnostic.SM0010.severity = error

[tests/**.cs]
dotnet_diagnostic.SM0001.severity = none
```

`<NoWarn>` and `<WarningsAsErrors>` work too, for a whole project at once.

> Turning analyzers off (`<RunAnalyzers>false</RunAnalyzers>`) still generates mapping code, but
> silently — including `SM0011` and `SM0012`, the two that stop a build.

---

## Versioning and target frameworks

- The runtime library targets **`net10.0` only**. That is deliberate rather than incidental:
  ShiftFramework, the consumer this library exists for, is on `net10.0`, and every additional
  target would need its own pass over the conversion table and the projection shapes. If you
  need an earlier target, open an issue rather than assuming one will appear.
- The generator targets `netstandard2.0`, as every Roslyn component must — the compiler loads
  it as a plugin and the compiler itself runs on `netstandard2.0`. You never reference it
  directly.
- Versions are **pre-1.0**. The declaration API still grows — profiles, global conversions and
  member conventions are all planned (see [PLAN.md](PLAN.md)) — so a `0.x` minor bump is
  allowed to change it. `1.0` is when that layer is finished.
- Both halves ship in one package on one version number. There is no combination of versions to
  get wrong.

---

## Status

What works today is listed above. What does not exist yet — `NullSubstitute`, inheritance, open
generics, and the global configuration layer a library needs — is laid out in order in
[PLAN.md](PLAN.md), which is the roadmap this repository is built from.

## License

MIT. See [LICENSE](LICENSE).
