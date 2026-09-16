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

**Reference documentation** lives in [`docs/`](docs/):
[getting started](docs/getting-started.md) —
[conversions](docs/conversions.md) —
[diagnostics](docs/diagnostics.md) —
[extension points for library authors](docs/extension-points.md) —
[migrating from AutoMapper](docs/automapper-migration.md).

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

or, when there is more to say — several mappers, mappers from packages, rules for all of them:

```csharp
builder.Services.AddShiftMapper(o =>
{
    o.AddMapper<AppMapper>();
    o.AddMapper<PlatformMapper>();                  // from a referenced package
    o.AddConversions<PlatformConversions>();        // a pack of rules, for every mapper above
});
```

The generator reads that lambda too, so what it composes is baked at compile time — see
[Registration](#registration). A framework package can also register itself and share its rules
with every project that references it, so that neither of the two package lines above has to be
written — see [Mappers from a referenced assembly](#mappers-from-a-referenced-assembly).

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

### Member conventions

A conversion answers "this type becomes that type". Some rules are MEMBER-shaped instead: which
source members fill a destination member depends on that member's own NAME.

```csharp
CreateMemberConvention<SelectDto>()
    .NameFrom<KeyAndNameAttribute>(nameof(KeyAndNameAttribute.Text))
    .Fill(d => d.Value, "{Member}ID")
    .FillIfPossible(d => d.Text, "{Member}.{NameOf}");
```

Now any destination member of that type is filled, in any map:

```csharp
CreateMap<Product, ProductListDto>();     // the whole of the application's involvement
```

```csharp
// what the generator writes
Brand = new SelectDto
{
    Value = ValueConverter.ToInvariantString(source.BrandId),
    Text  = (source.Brand is null ? default(string)! : source.Brand.Name),
}
```

**The target is a selector, the path is a string.** `d => d.Value` is compile-checked, so renaming
`Value` is a compile error rather than a build warning. The path has to stay text — it names source
members that are not symbols anywhere until a map is declared.

Two placeholders, and no more. `{Member}` is the destination member's own name, so `{Member}ID` reads
`source.BrandId`. `{NameOf}` is **the indirection that makes one rule serve types nobody listed**: it
means "the member this type nominates in its own attribute", so an entity calling its display member
`Title` is served by the same rule as one calling it `Name`.

**It resolves to TEXT at compile time, which is why it reaches the projection.** The same rule as an
`AfterMap` works in memory and cannot appear in a list query at all — which is why frameworks that
reach for one end up maintaining a second, hand-inlined path for lists:

```sql
SELECT [p].[Id], [p].[Name], CONVERT(varchar(11), [p].[BrandId]) AS [Value], [b].[Name] AS [Text], ...
FROM [Products] AS [p]
INNER JOIN [Brands] AS [b] ON [p].[BrandId] = [b].[Id]
```

**One rule, both shapes.** `FillIfPossible` is a `Fill` that is DROPPED when its path does not
resolve, instead of failing the member. Often only the id is set and the label is filled in by
whatever renders it — the source has a foreign key and no navigation to read a name from, or the
related type nominates no display member at all:

```csharp
// Brand nominates a name; Stock does not. Same rule, nothing added:
Brand = new SelectDto { Value = ..., Text = source.Brand!.Name },
Stock = new SelectDto { Value = ... },
```

```sql
-- and the id-only member costs no join, because nothing reads through it
SELECT [p].[Id], [p].[Name], CONVERT(varchar(11), [p].[BrandId]), [b].[Name], CONVERT(varchar(11), [p].[StockId])
FROM [Products] AS [p]
INNER JOIN [Brands] AS [b] ON [p].[BrandId] = [b].[Id]
```

With a required `Fill` that is SM0034 and an unmapped member, and a framework needs a SECOND rule for
every entity that leaves its label to the UI — which is the thing conventions exist to avoid. It
skips QUIETLY, and that is why it is a separate method rather than a flag: writing `FillIfPossible`
IS the acknowledgement, exactly as `Ignore` is. A required `Fill` that cannot resolve is still
reported.

`NameFrom` is only needed by a path that uses `{NameOf}`. A rule that fills nothing but an id needs
neither it nor the attribute.

**It composes with everything else.** Each value goes through the ordinary conversion table, so a
global conversion applies inside a shaped member — which is how hash ids reach a select DTO without
either rule mentioning the other. Declared in a pack it crosses an assembly like everything else,
so a framework ships the rule and an application's own DTOs are filled by something that names none
of its types.

**Narrowing.** `.WhenDestinationIs<T>()` limits a rule to maps whose destination fits, so a
framework's rule cannot reach into unrelated application types that happen to use the same member
type. Several conventions coexist, each claiming its own member type.

**Paths resolve exact-first, then by the mapper's own case rule**, so a framework pattern of
`{Member}ID` still finds an entity's `BrandId`. A convention that only worked when the application
already agreed on casing would not be a convention.

**The write direction is DERIVED, not declared.** A picker posts back what it was given, so the
request carries a select DTO and the entity needs its foreign key set. The entry written for the
response does it:

```csharp
CreateMap<ProductRequest, Product>();   // ProductRequest.Brand is a SelectDto
```

```csharp
// what the generator writes
BrandId = ValueConverter.Parse<int>((source.Brand is null ? default(string)! : source.Brand.Value), ...)
```

A `Fill` whose path is a plain member reverses on its own; entries that walk a navigation do not, and
should not — a display name is read from the related row, never written back to it.

And **the navigation beside the key is left alone**. `Product.Brand` name-matches the request's
`Brand`, so without the convention claiming it the build would demand a map from
`SelectDto` to `Brand` — an error on every write map a framework has. You set the key;
the related row is the database's business.

An explicit `ForMember` always wins. A convention that claims a member and cannot fill it leaves it
**unmapped** and says why (SM0034), rather than quietly falling back to name matching and mapping it
to the very thing the convention existed to override.

### Projection is transitive

A projection is one expression, assembled from the projections of the maps it nests. So a map is only
as projectable as what it nests, all the way down:

```csharp
CreateMap<Inner, InnerDto>().AfterMap(...);   // SM0018 — needs a statement
CreateMap<Outer, OuterDto>();                 // SM0036 — nests the above
```

> SM0036: the map from 'Outer' to 'OuterDto' nests the map from 'Inner' to 'InnerDto', which cannot
> be projected, so ProjectTo cannot use this one either; Map is unaffected

The message names the CHILD, because that is the map to go and fix. Closures of an open generic are
exempt: one `CreateMap(typeof(Page<>), typeof(PageDto<>))` closes over every pair the mapper has, so
reporting there is N messages about maps nobody wrote, each derivable from the child's own.

### And it is reported where you call it

The rules above describe a mapper where it is DECLARED. Whoever writes the query is usually looking
at a different file:

```csharp
db.Invoices.ProjectTo<InvoiceLabelDto>(mapper);   // SM0037, right here
```

> SM0037: 'Invoice' to 'InvoiceLabelDto' cannot be projected: the map runs AfterMap over its
> destination. Use Map instead.

**It reasons only from what it can see.** A generic repository projecting `IQueryable<TEntity>` to
`TDto` names no pair, so nothing is said about it — which is what keeps correct code from being
accused. The reason travels from the mapper as metadata, so it works across a package reference too.

### Code fixes

`SM0001` (— a destination member nothing fills) offers a lightbulb:

```csharp
CreateMap<Brand, BrandDto>()
    .ForMember(d => d.Country, o => o.Ignore());   // "Ignore 'Country'"
```

**It is an acknowledgement, not a silencer.** SM0001's honest answers are "map it" or "I know, and I
mean to leave it"; `Ignore` is the second one written into the source, where the next reader sees a
decision instead of an oversight. A `#pragma` or an `.editorconfig` severity tweak leaves neither.

`SM0011` (— a nested member whose pair has no map) offers the declaration it needs:

```csharp
CreateMap<Order, OrderDto>();
CreateMap<Line, LineDto>();   // "Add CreateMap<Line, LineDto>()"
```

Only the forward phrasing is offered. `CreateMap<B, A>().ReverseMap()` is often better, but whether
it reads better depends on what is already there — a judgement a lightbulb must not make silently.

There is deliberately **no "fix all"** on either — bulk-ignoring every unmapped member, or declaring
a dozen maps from one gesture, is exactly the review nobody would then do.

The fixes ship in the same package, as a **second assembly** under `analyzers/dotnet/cs`. They have
to: a code fix needs `Microsoft.CodeAnalysis.Workspaces`, which does not ship beside `csc`, so an
analyzer assembly referencing it would fail to load during a command-line build and take every
`SM####` rule with it. Only the IDE loads the fixes.

### Where a declaration may be written

Declarations are read from SOURCE at compile time and baked into the generated mapper. The code
around one therefore cannot decide whether it applies:

```csharp
public AppMapper(bool includeReporting)
{
    if (includeReporting)                       // SM0035: error
        CreateMap<Report, ReportDto>();
}
```

**This is an error, and it is the one place the library raises one for a matter of style.** Before
the rule existed that snippet produced output byte-for-byte identical to writing the `CreateMap`
plainly — the condition silently discarded, no diagnostic of any kind. When the branch then did not
run, the two backends disagreed: an in-memory `Map` threw, and `ProjectTo` quietly dropped the
member. A mapper that does not do what its source says, and says nothing, is the one failure this
library refuses to have.

Rejected: `if`/`else`, loops, ternaries and other expression positions, `switch`, `try`/`catch`,
local functions, free-standing lambdas, property accessors. Applies to every declaration API —
`CreateMap`, `IncludeMapper`, `AddConversions`, `CreateConversion`, `CreateMemberConvention` — and
to the `AddShiftMapper` lambda, which is read the same way.

**What stays legal is deliberate.** The rule keys on statement POSITION within whatever member holds
the call — never on which member that is:

```csharp
public AppMapper() => CreateMap<Brand, BrandDto>();   // fine: expression-bodied constructor

public AppMapper()                                    // fine: a mapper with 200 maps
{                                                     // splits them across methods
    AddCatalogMaps();
    AddOrderMaps();
}

private void AddCatalogMaps() => CreateMap<Product, ProductDto>();
```

And **reachability is not chased.** A private helper that is never called still contributes its maps,
because proving otherwise needs a call graph whose answer is unbounded — another partial part, a
source-generated part, DI or reflection can all reach it. A rule whose false-positive rate cannot be
bounded by reading one file is worse than no rule, so this stops at the line it can draw.

### Composing mappers: `IncludeMapper`

One constructor is a fine place for a dozen maps and a poor place for fifty. Write another mapper,
and include it:

```csharp
public partial class CatalogMapper : ShiftMapperBase
{
    public CatalogMapper()
    {
        CreateMap<CatalogItem, CatalogItemDto>()
            .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Sku.ToUpper()));

        CreateMap<PhysicalItem, PhysicalItemDto>().IncludeBase<CatalogItem, CatalogItemDto>();
    }
}

public partial class AppMapper : ShiftMapperBase
{
    public AppMapper() => IncludeMapper<CatalogMapper>();
}
```

**An included mapper is an ordinary mapper.** It gets its own generated `Map` methods, so a service
that only deals with the catalogue can inject `CatalogMapper` and call it directly. AND its maps
become `AppMapper`'s maps — `mapper.Map<CatalogItemDto>(item)` and `ProjectTo` work on `AppMapper`
exactly as if the `CreateMap` had been written in its constructor, and a map written there may nest
one of them. There is one concept, not two: a mapper is a place to write maps, and a mapper may use
another's.

Inclusion crosses boundaries the way you would expect: an `IncludeBase` finds a base map declared in
another included mapper, and an open generic closes over pairs declared anywhere in the set. Two
mappers may include each other; the result is the union of what they declare.

**Each map keeps its own mapper's defaults and rules.** `ConfigureDefaults` on `CatalogMapper`
governs `CatalogMapper`'s maps wherever they end up; so do its `CreateConversion`s and member
conventions (see [Packs](#packs-rules-shared-between-mappers)).

**One declaration per pair.** A pair declared both in an included mapper and in the mapper that
includes it keeps the including mapper's — it is the composition root, and nearer — and the clash
is reported (**SM0027**, a warning) rather than left to be discovered. A pair declared in two
included mappers, or twice in one mapper, has nothing nearer to settle it and is an **error**
(**SM0042**): picking by include order would make a map silently depend on which line came first.
The same declaration reached twice — two parts including one mapper, a diamond of includes — is one
`CreateMap` and collapses silently.

**Dependencies work, and are resolved late:**

```csharp
public partial class InvoiceMapper : ShiftMapperBase
{
    public InvoiceMapper(IInvoiceNumbering numbering) =>
        CreateMap<Invoice, InvoiceLabelDto>()
            .ConstructUsing(s => new InvoiceLabelDto(numbering.Prefix + s.Number));
}
```

`AddShiftMapper` registers what a mapper includes along with it, so nothing needs registering by
hand; an included mapper is built with its dependencies injected the first time anything is mapped
— not while the including mapper's constructor runs, because that provider does not exist yet.

The edge that follows is worth knowing: **an include taking dependencies makes the whole mapper
DI-only.** Everything a mapper includes is built together on first use, so one that cannot be built
fails the mapper's first map, including maps unrelated to it. Skipping it instead would leave its
`MapFrom` members quietly unfilled, which is the divergence this library exists to prevent. If you
construct mappers by hand in tests, keep what they include parameterless.

### Packs: rules shared between mappers

Every other feature configures a MEMBER of a MAP. A conversion configures a TYPE PAIR, once, for
every map of the mapper that declares it:

```csharp
public partial class AppMapper : ShiftMapperBase
{
    public AppMapper()
    {
        CreateConversion<DateTime, string>(
            memory: issued => issued.Year + "/" + issued.Month + "/" + issued.Day,
            query:  issued => issued.Year + "/" + issued.Month + "/" + issued.Day);

        CreateMap<Invoice, InvoiceStampDto>();      // IssuedAt converts, and nobody said so
    }
}
```

**A rule reaches the maps of the mapper that wrote it, and no further.** It does not leak into a
mapper that includes this one, and a rule written in an included mapper does not reach the maps
written here. That is what makes a package safe to include: it cannot change how YOUR `long`s
render. A rule several mappers should share belongs in a **pack** — a class holding only rules:

```csharp
public class PlatformConversions : ShiftMapperConversions
{
    public PlatformConversions()
    {
        CreateConversion<long, string>(id => "H" + id, id => "H" + id);       // hash ids

        CreateMemberConvention<SelectDto>()
            .NameFrom<KeyAndNameAttribute>("Text")
            .Fill(d => d.Value, "{Member}ID")
            .FillIfPossible(d => d.Text, "{Member}.{NameOf}");
    }
}
```

and is added in one of four places, each one line:

```csharp
AddConversions<PlatformConversions>();                       // in a mapper's constructor: this mapper

o.AddMapper<AppMapper>(m => m.AddConversions<PlatformConversions>());   // at registration: this mapper

o.AddConversions<PlatformConversions>();                     // at registration: every mapper in the call

o.ShareConversions<PlatformConversions>();                   // in a PACKAGE's registration: every mapper in the call,
                                                             // and every mapper every referencing project registers
```

**Nearest wins.** For a map declared by mapper P: P's own `CreateConversion` → the packs P added →
(when P was included by M) M's own → the packs M added → the packs the registration gave every mapper
→ the packs referenced packages shared → the built-in table. A `ForMember` on a particular member beats all of them. Two packs at the same
distance claiming one pair is an error (**SM0031**) unless something nearer settles it. The generated
call names the scope that answered — `Customizations.Conversion<long, string>(typeof(PlatformConversions))`
— so the runtime looks in exactly that store and the two halves cannot disagree.

**A registered pair BEATS the built-in table.** `long` to `string` already converts, so under the
other ordering a rule written for that pair — which is exactly what a hash-id rule is — would be
ignored in silence. Pairs you did not register are untouched.

**Assignability, not identity.** A rule registered for a base type answers for everything assignable
to it, so one rule covers an entity hierarchy; where two could answer at the same distance, the
nearest by inheritance wins, so a general rule can always be narrowed.

**Two forms, because there are two backends.** `memory` is a delegate the `Map` methods call and may
do anything C# can do. `query` is an expression tree, and it is not invoked by the projection — it
is INLINED into it, because a delegate call is opaque to EF. That is the difference between

```sql
SELECT CAST(DATEPART(year, [i].[IssuedAt]) AS nvarchar(max)) + N'/' + ...
```

and loading every row to format it in C#.

**Omitting the query form is a declaration, not an oversight.** It says the pair cannot be
translated, and every map that touches it loses its projection — which the build reports:

```
warning SM0030: the map from 'Product' to 'ProductFingerprintDto' converts 'Brand' to 'String'
                with a conversion that has no query form, so ProjectTo cannot use it;
                Map is unaffected
```

That warning is the reason to do this at compile time at all. A runtime conversion table converts
just as well and cannot tell you which of your list endpoints has quietly stopped being one query.
It is a Warning rather than the Info `ConstructUsing` gets, because the person who loses the
projection is not the person who chose to: whoever wrote the `CreateConversion` made a decision
about a type pair, and whoever writes a map that happens to touch it inherits the consequence.

### Registration

`AddShiftMapper` is the one place outside a mapper class where declarations are written, and the
generator reads it exactly as it reads a constructor:

```csharp
builder.Services.AddShiftMapper(o =>
{
    o.AddMapper<AppMapper>(m =>
    {
        m.IncludeMapper<ReportingMapper>();          // baked into AppMapper, as if written in its constructor
        m.AddConversions<ReportingConversions>();    // AppMapper only
    });

    o.AddMapper<PlatformMapper>();                   // from a referenced package — see below
    o.AddConversions<PlatformConversions>();         // every mapper in this call
    o.Lifetime = ServiceLifetime.Scoped;             // the default
});
```

**What ends up in the container:** every mapper under its own type; everything a mapper includes
and every pack it adds under theirs, so they can be injected on their own and take constructor
dependencies without a registration of their own; and `IShiftMapper`, which resolves to the mapper
when there is one and to a composite over all of them when there are several — it asks each mapper
`CanMap` and dispatches, first registered first.

**One owner per pair in `IShiftMapper`.** Two registered mappers may both map a pair when it is ONE
declaration reached two ways — `AppMapper` includes `CatalogMapper` and both are registered so
either can be injected; whichever answers runs that same map. Two mappers each writing their OWN
`CreateMap` for a pair, both registered anywhere in the project, is a build **error** (**SM0040**,
and the same check runs at startup for registrations made from other projects): a library going
through the interface would otherwise be handed one of two different mappings, chosen by line
order. Declare the pair in one mapper — have the other include it — or register only one of them.

**The lambda has to be readable.** It is baked at compile time, so it must be an inline lambda of
plain statements, in the same project as the mappers it configures; a method group, a delegate
variable or an `if` around a line is reported (**SM0035**) rather than half-applied. And because a
mapper is generated ONCE per project, what any call composes into it is what every call gets — a
second call that says less is told so (**SM0041**) and gets the union anyway, so the code and the
store never disagree. The same holds for a mapper built with `new` rather than resolved: its code
has everything its registration composed, its store has only what its constructor did, and the first
map that needs the difference fails naming the pack. A mapper whose registration composes something
is a DI mapper; put the composition in the constructor if it must also be built by hand.

**A package may make this call too.** A framework's own `AddXxx` extension can register the
framework's mapper from the framework's assembly, and every call, from whichever assembly, lands in
the one registry: `IShiftMapper` covers all of them, in any order. Should the application register
the package's mapper as well, the application's adapter wins — in either order — because it carries
the package's rules AND the application's. See the next section for what a package's registration
can share.

### Mappers from a referenced assembly

**One vocabulary.** A package writes the same `CreateMap`, `CreateConversion` and `ForMember` an
application writes, in an ordinary mapper and an ordinary pack:

```csharp
// in a package — Contoso.Platform is the sample's stand-in for one
public partial class PlatformMapper : ShiftMapperBase
{
    public PlatformMapper() =>
        CreateMap<FileDto, FileSummary>()
            .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Name.Trim()));
}

public class PlatformConversions : ShiftMapperConversions
{
    public PlatformConversions() =>
        CreateConversion<long, string>(id => "H" + id, id => "H" + id);
}
```

and an application uses them with the lines it would use for its own:

```csharp
public AppMapper() => IncludeMapper<PlatformMapper>();      // its maps become AppMapper's

o.AddMapper<PlatformMapper>();                              // or: injectable on its own
o.AddConversions<PlatformConversions>();                    // its rules, for every mapper
```

That is the whole of it. No attributes written by hand, no second API, and nothing in the
application naming the package's internals.

**How it can work at all.** A source generator sees a referenced assembly as METADATA — type
names, signatures, attributes — and never a method body. So a mapper compiled into a package is,
from the outside, a class with an empty constructor. The package's OWN build fixes that: the same
generator runs there and writes what every mapper and pack declares into the assembly as attributes,
while the source is still in front of it. The application's generator reads those and produces
exactly the code it would have produced from source.

**No expression is ever copied.** The work splits cleanly:

- the **shape** — which pairs, which members, which options — goes into the metadata;
- the **expressions** arrive at run time, because including or registering the mapper constructs it
  and its constructor registers them, exactly as it does for a mapper in your own project.

So a package's `MapFrom` is emitted as `Customizations.Value<Source, Dest, T>("Member")` —
character for character what an in-project `MapFrom` emits.

**A package mapper registered directly gets an adapter.** Its `Map` methods were compiled inside
the package and cannot pick up your packs, so `o.AddMapper<PlatformMapper>()` makes your generator
write a subclass in your project — the same maps, re-baked with your rules, overriding the package's
virtual members — and `AddShiftMapper` hands that out wherever `PlatformMapper` is asked for. Nothing
that injects it can tell. A sealed package mapper has nothing to override and is refused (**SM0039**);
maps over the package's non-public types stay as the package compiled them.

**A package can register itself, and share its rules.** A framework wants its hash ids and its
select conventions applied by every application, and a line each application has to remember is a
line one of them forgets. So the package's own `AddXxx` extension makes the registration, and says
`ShareConversions` where an application would say `AddConversions`:

```csharp
// in the package
public static IServiceCollection AddContosoPlatform(this IServiceCollection services) =>
    services.AddShiftMapper(o =>
    {
        o.AddMapper<PlatformMapper>();               // the package's mapper, from the package's assembly
        o.ShareConversions<PlatformConversions>();   // this call's mappers, AND every mapper every referencing project registers
    });

// in the application — nothing of the package's in its own registration
builder.Services.AddContosoPlatform();
builder.Services.AddShiftMapper(o => o.AddMapper<AppMapper>());
```

The package's build writes the share down as metadata; the application's generator reads it from
every reference and treats it as an `o.AddConversions<PlatformConversions>()` appended to every
`AddShiftMapper` call in the application — the furthest level, so a rule the application writes for
the same pair still wins — and records the composition so the runtime applies it from the
application's own metadata. The application's build says which packs arrived this way (**SM0043**,
Info). The pack has to be public, because the application's generated code names it (**SM0044**).

**Maps stay opt-in.** A shared pack is the one thing a package applies on your behalf, and it is
announced. Maps are keyed by their declaring mapper, so referencing a package changes no map of
yours until something includes or registers it, and a package cannot quietly alter which map runs.

**The package must be built with the ShiftMapper generator** referenced as an analyzer, or nothing
is written down. That case is an error (**SM0028**) rather than mapping nothing in silence, and
`[assembly: ShiftMapperContract(2)]` lets a package built against a different ShiftMapper be refused
whole (**SM0033**) instead of half-read. See [docs/extension-points.md](docs/extension-points.md).

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

`AddShiftMapper` registers it alongside the mapper's own type, and with one mapper both resolve to
the same instance. With several, `IShiftMapper` is a composite that asks each registered mapper
`CanMap` and dispatches — first registered first — so a library still reaches every pair the
application mapped. It never chooses between two *different* mappings of one pair: two registered
mappers each declaring their own map for a pair is refused at build time and at startup (SM0040).

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

## Replacing AutoMapper

ShiftMapper can take over an AutoMapper configuration without changing its shape: the
declaration vocabulary is the same — `CreateMap`, `ForMember`, `ReverseMap` — so most of a
`Profile` moves across as it is, as a mapper of its own. What changes is when it is read: at build time, so an
unmapped member, a missing conversion or a nested map nobody declared is a diagnostic naming the
property, where AutoMapper reports nothing until a runtime call fails or an
`AssertConfigurationIsValid` somebody remembered to write. `ProjectTo` is an expression tree EF
Core turns into one `SELECT`, and any configuration it cannot express is reported by id rather
than dropped. ShiftFramework is adopting ShiftMapper for exactly that replacement.
[Migrating from AutoMapper](docs/automapper-migration.md) lists what each line becomes and what
has no equivalent.

---

## Performance

Measured, not asserted. `ShiftMapper.Benchmarks` runs the same four maps through ShiftMapper,
AutoMapper and Mapperly — four type pairs, an `int` to `string` conversion, a case-insensitive
member match, a `List` to `IReadOnlyList` copy, and three computed members — with every mapper
warm and every configuration built once. BenchmarkDotNet 0.15.8, .NET 10, i7-13700H; the full
report with error bars is committed as
[`ShiftMapper.Benchmarks/RESULTS.md`](ShiftMapper.Benchmarks/RESULTS.md).

| Shape | ShiftMapper | AutoMapper 14 | Mapperly 4.3 |
|---|---:|---:|---:|
| **One object** (6 members) | 24.6 ns / 168 B | 58.0 ns / 184 B | 11.9 ns / 96 B |
| **Nested graph** (4 levels, 10 lines, 3 `MapFrom`) | 943 ns / 3.3 KB | 1,137 ns / 3.6 KB | 398 ns / 2.5 KB |
| **10,000 objects**, one call | 360 µs / 1.76 MB | 1,787 µs / 2.10 MB | 140 µs / 1.04 MB |
| **Building the projection** (warm) | 343 ns / 424 B | 697 ns / 872 B | 8,270 ns / 16.6 KB |

*Medians for the 10k row, where GC makes the mean noisy.*

**Against AutoMapper, ShiftMapper is 2—5× faster in memory and allocates less** — the expected
result of code that was compiled rather than assembled from a configuration at run time. AutoMapper
also builds its projection expression 2× slower.

**Against Mapperly, ShiftMapper is about 2× slower in memory**, and it is worth saying exactly why,
because each reason is a decision rather than an accident:

- **ShiftMapper copies collections; Mapperly aliases them.** For `Tags`, ShiftMapper emits
  `ValueConverter.ToListOrEmpty(source.Tags)` — a new list, 72 B — and Mapperly emits
  `(IReadOnlyList<string>)source.Tags`, a cast. That is the entire gap on one object: Mapperly's DTO
  shares the entity's list, so a later change to either shows in both. ShiftMapper's DTO owns its
  collection. Where you want the alias, an explicit `MapFrom(s => s.Tags)` says so.
- **ShiftMapper's `MapFrom` is a cached delegate; Mapperly's is inlined C#.** `LineTotal` runs
  `Customizations.Value<…>("LineTotal")(source)` — a delegate compiled from the expression tree
  and cached in a field — where Mapperly emits `x1.Quantity * x1.UnitPrice` in place. Twelve of
  those on the nested graph is most of that row's gap. The delegate exists because the expression
  lives in a runtime store: that is what lets the same `MapFrom` be spliced into the projection, and
  what lets a **package's** mapper supply one. A capturing lambda has to work this way; a pure
  one could be inlined and is not yet, which is the one clear optimisation this table points at.
- **Building the projection goes the other way, by 24×.** ShiftMapper composes its expression
  tree once and keeps it in a field; Mapperly's projection is an expression-tree literal, which the
  C# compiler turns into `Expression.*` factory calls that run on **every** invocation — 16 KB of
  allocation per call. EF's query cache absorbs this in practice, but it is real per-call work.

### What the projections contain

Speed of building the tree matters less than what is in it, because the database decides what to
do with it. `dotnet run -c Release --project ShiftMapper.Benchmarks -- --shapes` prints all three
for the nested graph. The findings:

- **All three produce one member-init tree with the computed members inlined** — `Number = "IQ/" +
  x.Number`, `LineTotal = x.Quantity * x.UnitPrice`, `Total = x.Lines.Sum(…)`. Mapperly inlines
  its expression-bodied helper methods; ShiftMapper splices the `MapFrom` trees; AutoMapper reads
  its configuration. None of them leaves a method call a provider cannot translate, and none falls
  back to client evaluation for the nested objects.
- **AutoMapper guards every nested navigation**: `Product = IIF(x.Product == null, null, new
  ProductDto {…})`, which becomes a `CASE` per level in SQL. ShiftMapper and Mapperly trust the
  schema and emit the member-init plainly — the right answer for a required navigation, and the
  reason ShiftMapper keeps its null guards for the **in-memory** path only, where there is no schema
  to trust.
- **Collections**: ShiftMapper leaves `Tags = x.Tags` for the provider to shape; the other two
  cast. Equivalent once translated.

### Reproducing

```
dotnet run -c Release --project ShiftMapper.Benchmarks -- --filter *Comparison*
dotnet run -c Release --project ShiftMapper.Benchmarks -- --shapes
```

AutoMapper is pinned to 14.0.0, the last MIT release, so the comparison carries no licence-key
caveat; the engine is the same one later versions use. Change the version in the `.csproj` to
compare against another.

---

## Diagnostics

Forty-three rules, `SM0001` to `SM0044` (`SM0029` is retired). Ten stop the build; the rest describe
something that will not be mapped, or will be mapped in a way worth knowing about.

| Id | Default | What it means |
|---|---|---|
| SM0001 | Warning | Destination property has no matching source property |
| SM0002 | Warning | Names match; ShiftMapper does not convert between the two types |
| SM0003 | Warning | Destination property's setter is not public |
| SM0004 | Warning | Destination type has no constructor ShiftMapper can call |
| SM0005 | Warning | Nothing was generated for a `ShiftMapperBase` class (not `partial`, nested in a type that is not `partial`, or generic) |
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
| SM0027 | Warning | A pair is declared both in an included mapper and in the mapper that includes it |
| SM0028 | **Error** | A referenced assembly carries no ShiftMapper declaration metadata |
| SM0030 | Warning | A conversion has no query form, so `ProjectTo` cannot use the map |
| SM0031 | **Error** | Two packs at the same distance declare a conversion for the same type pair |
| SM0032 | Warning | A package's declared-conversion metadata could not be read (a version skew between its generator and this one) |
| SM0033 | Warning | A referenced assembly declares a different ShiftMapper contract |
| SM0034 | Warning | A member convention could not fill the member it claimed |
| SM0035 | **Error** | A declaration cannot be honoured where it is written |
| SM0036 | Warning | Map cannot be projected because a map it nests cannot |
| SM0037 | Warning | `ProjectTo` called on a pair that cannot be projected |
| SM0038 | Warning | A member convention fills nothing |
| SM0039 | **Error** | A sealed mapper from a referenced assembly cannot be registered here |
| SM0040 | **Error** | Two registered mappers each declare their own map for the same pair |
| SM0041 | Warning | A mapper is registered with different includes or packs in two calls |
| SM0042 | **Error** | A map is declared twice with nothing to choose between the two |
| SM0043 | Info | A referenced package shared a pack with every mapper this call registers |
| SM0044 | **Error** | A pack shared with referencing projects is not public |

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
  one target means the conversion table and the projection shapes are verified against one BCL
  and one EF Core, and every additional target would need its own pass over both. If you need
  an earlier target, open an issue rather than assuming one will appear.
- The generator targets `netstandard2.0`, as every Roslyn component must — the compiler loads
  it as a plugin and the compiler itself runs on `netstandard2.0`. You never reference it
  directly.
- Versions are **pre-1.0**. The declaration API — included mappers, packs, conversions, member
  conventions, registration and the metadata that carries them across a package boundary — is
  complete, but a
  `0.x` minor bump is still allowed to change it while it has a consumer's worth of use behind it.
  `1.0` is when it has.
- Both halves ship in one package on one version number. There is no combination of versions to
  get wrong.
- The published number is `ShiftMapperVersion` in the Shift Framework's
  `ShiftTemplates/ShiftFrameworkGlobalSettings.props`, and releases come from that repository's
  Azure pipeline on a `release-shiftmapper` or `release-all` tag. `Directory.Build.props` imports
  that file when the two repositories sit side by side; the number it carries itself is only the
  fallback for a standalone clone.

---

## Status

What works today is listed above, with the reasoning behind each decision given where the
feature is described. What does not exist is `NullSubstitute` — deliberately, see
[Migrating from AutoMapper](docs/automapper-migration.md) — and the code fixes for rules other
than SM0001 and SM0011.

## License

MIT. See [LICENSE](LICENSE).
