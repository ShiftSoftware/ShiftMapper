# ShiftMapper

A compile-time object mapper for .NET.

You declare the type pairs you want mapped. A source generator reads them at build time and
writes one generated mapper per assembly — the in-memory `Map` methods **and** a `ProjectTo`
expression EF Core turns into SQL — reached through one class you inject, `Mapper`. Anything it
cannot map becomes a build diagnostic naming the property, not a silently unfilled field.

```csharp
public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Brand, BrandDto>();
}

builder.Services.AddShiftMapper();

public class BrandService(Mapper mapper, AppDbContext db)
{
    var dto  = mapper.Map<BrandDto>(brand);                 // in memory
    var page = db.Brands.ProjectTo<BrandDto>(mapper)        // in the database
                        .Where(b => b.Name.StartsWith("A"))
                        .ToListAsync();
}
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

Maps go in the constructor of a class deriving from `ShiftMapperBase`. Write as many such classes as
read well; nothing is generated onto them, so they need not be `partial`.

```csharp
using ShiftMapper;

public class AppMapper : ShiftMapperBase
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
in ordinary C# that the compiler checks; the generator reads the calls at build time — from every
mapper class in the project, and from every mapper class in the packages the project references —
and writes ONE generated mapper for the assembly holding all of their maps.

A mapper class is an ordinary class, so it can take constructor dependencies and use them from a
`MapFrom`. It is built the first time anything is mapped, with those dependencies injected.

### 2. Register it

```csharp
builder.Services.AddShiftMapper();       // Scoped by default; nothing to name
```

or, when there is more to say — a pack of rules for every map, which mapper classes to take, a
different lifetime:

```csharp
builder.Services.AddShiftMapper(o =>
{
    o.AddConversions<PlatformConversions>();        // a pack of rules, for every map in this assembly
    o.Discovery = MapperDiscovery.Registered;       // take only the classes named below (default: All)
    o.AddMapper<AppMapper>();
    o.Lifetime = ServiceLifetime.Singleton;
});
```

The generator reads that lambda too, so what it says is baked at compile time — see
[Registration](#registration) and [Choosing which classes are taken](#choosing-which-classes-are-taken).
A framework package can also register itself and share its rules with every project that
references it — see [Mappers from a referenced assembly](#mappers-from-a-referenced-assembly).

### 3. Map

Inject `Mapper` — the one class in the runtime package — and call it. Each map produces a small
family of entry points, in two spellings that do the same work, so use whichever reads better
where you are.

```csharp
public class BrandService(Mapper mapper, AppDbContext db) { ... }

BrandDto dto = mapper.Map<BrandDto>(brand);           // create
BrandDto dto = mapper.MapToBrandDto(brand);           // create, no type argument, no dispatch
mapper.Map(brand, existingDto);                       // update in place
BrandDto? dto = mapper.MapOrNull<BrandDto>(maybe);    // null in, null out

BrandDto dto = brand.Map<BrandDto>(mapper);           // the same, source first
brand.Map(existingDto, mapper);
BrandDto? dto = maybe.MapOrNull<BrandDto>(mapper);

IQueryable<BrandDto> q = mapper.ProjectTo<BrandDto>(db.Brands);
IQueryable<BrandDto> q = db.Brands.ProjectTo<BrandDto>(mapper);
```

`Mapper` itself is compiled in the runtime package and knows none of your types; every method above
is an EXTENSION METHOD the generator writes into your project, forwarding to your assembly's
generated mapper. At the call they bind like members: a pair with no map is a compile error.

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

**A second entry for the same target is a fallback.** The first entry that resolves fills a target;
a later one for the same target is tried only when the earlier did not — so a framework whose types
nominate a display member by attribute, and otherwise call it `Name`, writes one rule:

```csharp
.FillIfPossible(d => d.Text, "{Member}.{NameOf}")   // the nominated member, where there is one
.FillIfPossible(d => d.Text, "{Member}.Name")       // else Name, where there is one
```

**No key, no value.** A required entry read from a NULLABLE member of the source — an optional
foreign key, `long? BrandId` — leaves the whole shaped member null when that key is null, in memory
and in the projection, rather than building a select around an absent key. A required key (a plain
`long`) is not tested.

```csharp
CountryOfOrigin = (source.CountryOfOriginId is null ? default(SelectDto)! : new SelectDto { … }),
```

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
`CreateMap`, `AddConversions`, `CreateConversion`, `CreateMemberConvention` — and to the
`AddShiftMapper` lambda, which is read the same way.

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

### Several mapper classes, one mapper

One constructor is a fine place for a dozen maps and a poor place for fifty. Split them over as
many classes as read well:

```csharp
public class CatalogMapper : ShiftMapperBase
{
    public CatalogMapper()
    {
        CreateMap<CatalogItem, CatalogItemDto>()
            .ForMember(d => d.Sku, opt => opt.MapFrom(s => s.Sku.ToUpper()));

        CreateMap<PhysicalItem, PhysicalItemDto>().IncludeBase<CatalogItem, CatalogItemDto>();
    }
}

public class AppMapper : ShiftMapperBase
{
    public AppMapper() => CreateMap<Brand, BrandDto>();
}
```

**Nothing names the other.** By default the generator reads every `ShiftMapperBase` subclass in
the project into the one generated mapper, so `mapper.Map<CatalogItemDto>(item)` and
`db.CatalogItems.ProjectTo<CatalogItemDto>(mapper)` work on the same `Mapper` as `BrandDto` does,
and a map in one class may nest a map from another. A class is a place to write; files are not
walls. An `IncludeBase` finds a base map declared in another class, and an open generic closes over
pairs declared anywhere in the project. (A project that would rather name its classes can — see
[Choosing which classes are taken](#choosing-which-classes-are-taken).)

**Each map keeps its own class's defaults and rules.** `ConfigureDefaults` on `CatalogMapper`
governs `CatalogMapper`'s maps; so do its `CreateConversion`s and member conventions (see
[Packs](#packs-rules-shared-between-mappers)). And each map is REPORTED where it was written: a
diagnostic about a map in `CatalogMapper.cs` lands on that `CreateMap`.

**One declaration per pair.** A pair declared in two classes of the project, or twice in one
class, has nothing to choose between the two and is an **error** (**SM0042**): picking by file
order would make a map silently depend on which class came first. A pair the project declares AND
a referenced package declares keeps the project's — nearer, and overriding a package's map is a
thing to do on purpose — and the build says so (**SM0027**, a warning).

**Dependencies work, and are resolved late:**

```csharp
public class InvoiceMapper : ShiftMapperBase
{
    public InvoiceMapper(IInvoiceNumbering numbering) =>
        CreateMap<Invoice, InvoiceLabelDto>()
            .ConstructUsing(s => new InvoiceLabelDto(numbering.Prefix + s.Number));
}
```

Nothing needs registering: the generated mapper builds each mapper class from the container the
first time anything is mapped, with its dependencies injected — not at startup, because the
provider the generated mapper is given arrives after it is constructed.

The edge that follows is worth knowing: **one class taking dependencies makes the whole assembly's
mapper DI-only.** Every class is built together on first use, so one that cannot be built fails the
first map, including maps unrelated to it. Skipping it instead would leave its `MapFrom` members
quietly unfilled, which is the divergence this library exists to prevent. `Mapper.Create(assembly)`
and a bare `new Mapper()` are for tests over parameterless mapper classes; anything else comes from
`AddShiftMapper`.

### Packs: rules shared between mappers

Every other feature configures a MEMBER of a MAP. A conversion configures a TYPE PAIR, once, for
every map of the class that declares it:

```csharp
public class AppMapper : ShiftMapperBase
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

**A conversion may take the mapping.** The two-argument form is handed the property pair being
converted, as the built-in parsers are — `"ProductDto.Brand.Value -> Product.BrandID"` — for the
conversion that REFUSES a value and has to say which field it refused:

```csharp
CreateConversion<string, long>(
    memory: (text, mapping) => text.Length == 0 ? throw new BlankKeyException(mapping) : long.Parse(text),
    query:  text => Convert.ToInt64(text));
```

**What a failed conversion throws is its own type.** Every parse in `ValueConverter` throws
`ShiftMapperConversionException` — a `FormatException`, so a catch for one still catches it — carrying
the value, the target type, the mapping, and `SourceMember`, the member of the source the value was
read from (`Brand` for the mapping above). A framework answering a bad request field with a 400 reads
those; nothing has to be parsed out of the message.

**A rule reaches the maps of the class that wrote it, and no further.** It does not leak into the
maps another class declares, in this project or in one that references it. That is what makes a
package safe to reference: it cannot change how YOUR `long`s render. A rule several classes should
share belongs in a **pack** — a class holding only rules:

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

and is added in one of three places, each one line:

```csharp
AddConversions<PlatformConversions>();        // in a mapper class's constructor: that class's maps

o.AddConversions<PlatformConversions>();      // at registration: every map in this assembly

o.ShareConversions<PlatformConversions>();    // in a PACKAGE's registration: every map in the package,
                                              // and every map in every project that references it
```

**Nearest wins.** For a map declared by class P: P's own `CreateConversion` → the packs P added →
the packs the registration gave every map → the packs referenced packages shared → the built-in
table. A `ForMember` on a particular member beats all of them. Two packs at the same
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
    o.AddConversions<ReportingConversions>();    // baked into every map of this assembly's generated mapper
    o.Lifetime = ServiceLifetime.Scoped;         // the default
});
```

**What it registers** is the calling assembly's GENERATED mapper — the class the generator wrote
holding every map the assembly can see — read from the assembly's own metadata, so nothing is
named; and `Mapper`, the one object application code injects, together with `IMapper` for
library code. Both resolve to the same instance. Mapper classes are not registered: the generated
mapper builds them from the provider on first use.

**A framework registers on behalf of what it scanned** with `AddShiftMapper(assembly)`: the named
assembly's generated mapper, into the same registry, so a host that hands its data assembly to the
framework's own registration has that assembly's maps registered without writing the call itself.
Lifetime only, no packs — a pack added here could reach the store but never the generated code — and
the generator does not read it as a registration of the calling project. An assembly with no generated
mapper registers nothing and is not an error.

### Choosing which classes are taken

`o.Discovery` decides which mapper classes the generated mapper is built from. One setting per
project, read at compile time; the default is the one that needs nothing named.

```csharp
builder.Services.AddShiftMapper(o =>
{
    o.Discovery = MapperDiscovery.LocalAndRegistered;
    o.AddMapper<PlatformMapper>();               // a package's class, taken because it is named
});
```

| `MapperDiscovery` | Local classes | Package classes |
|---|---|---|
| **`All`** (default) | every one | every one, from every referenced package |
| **`LocalAndRegistered`** | every one | those named with `AddMapper<T>()`, and those the package shared with `ShareMapper<T>()` |
| **`Registered`** | those named with `AddMapper<T>()` | those named with `AddMapper<T>()` |

`All` is the zero-ceremony shape. `LocalAndRegistered` keeps that for your own classes and gives
you a say over packages — which matters when two packages you cannot edit declare the same pair
(SM0042 under `All`), or when a package you reference for other reasons carries maps you do not
want in your assembly. `Registered` names everything, for the project that wants nothing generated
it did not write down; a local class it leaves out is reported (**SM0005**) so nothing goes missing
in silence. An `AddMapper` under `All` changes nothing and is reported (**SM0046**), because the
line says a mode was probably intended. A package class not taken stays reachable through
`IMapper` if the package registered itself — only the typed methods are absent.

A package can share a mapper class the way it shares a pack: `o.ShareMapper<T>()` in its own
registration puts the class into the generated mapper of every referencing project under
`LocalAndRegistered` (announced, **SM0043**). The class must be public (**SM0044**).

**The lambda has to be readable.** It is baked at compile time, so it must be an inline lambda of
plain statements; a method group, a delegate variable or an `if` around a line is reported
(**SM0035**) rather than half-applied. Two calls in one project add up — the generated mapper gets
the union of their packs, and every call applies it. A `Mapper` built with `Mapper.Create` rather
than resolved has everything the calls composed in its code, and only what the constructors did in
its store; the first map that needs the difference fails naming the pack.

**A package may make this call too.** A framework's own `AddXxx` extension can call it for the
framework's assembly, and every call, from whichever assembly, lands in the one registry. `Mapper`
is then made of every generated mapper registered, the application's FIRST: it already carries
every package's maps, re-baked with the application's rules, so a package's own registration is the
fallback — for a host that has no generator of its own, or a library mapping through
`IMapper`. See the next section.

### Mappers from a referenced assembly

**One vocabulary.** A package writes the same `CreateMap`, `CreateConversion` and `ForMember` an
application writes, in an ordinary mapper class and an ordinary pack:

```csharp
// in a package — Contoso.Platform is the sample's stand-in for one
public class PlatformMapper : ShiftMapperBase
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

and an application that references the package has the map with nothing written:

```csharp
FileSummary summary = mapper.MapToFileSummary(dto);   // in the application's own generated mapper
```

**How it can work at all.** A source generator sees a referenced assembly as METADATA — type
names, signatures, attributes — and never a method body. So a mapper class compiled into a package
is, from the outside, a class with an empty constructor. The package's OWN build fixes that: the
same generator runs there and writes what every mapper class and pack declares into the assembly as
attributes, while the source is still in front of it. The application's generator reads those from
every reference and generates the package's maps into the application's own generated mapper —
re-baked with the application's rules, exactly as if the package's class were in the application.

**No expression is ever copied.** The work splits cleanly:

- the **shape** — which pairs, which members, which options — goes into the metadata;
- the **expressions** arrive at run time, because the generated mapper constructs the package's
  mapper class on first use and its constructor registers them, exactly as it does for a class in
  your own project.

So a package's `MapFrom` is emitted as `Customizations.Value<Source, Dest, T>("Member")` —
character for character what an in-project `MapFrom` emits.

**Rules stay where they were written.** A package mapper's own `CreateConversion`s, and the packs
its own registration gave it, apply to ITS maps in your generated mapper too; they do not reach
yours unless you add the pack, or the package shares it. A map over a type the package keeps
internal cannot be generated in your project — the method could not name the type — and is left to
the package's own generated mapper, which the run-time door still reaches.

**A package can register itself, and share its rules.** A framework wants its hash ids and its
select conventions applied by every application, and a line each application has to remember is a
line one of them forgets. So the package's own `AddXxx` extension makes the registration, and says
`ShareConversions` where an application would say `AddConversions`:

```csharp
// in the package
public static IServiceCollection AddContosoPlatform(this IServiceCollection services) =>
    services.AddShiftMapper(o => o.ShareConversions<PlatformConversions>());
    // registers the package's own generated mapper, AND shares the pack with every referencing project

// in the application — nothing of the package's in its own registration
builder.Services.AddContosoPlatform();
builder.Services.AddShiftMapper();
```

The package's build writes the share down as metadata; the application's generator reads it from
every reference and treats it as an `o.AddConversions<PlatformConversions>()` for every map in the
application — the furthest level, so a rule the application writes for the same pair still wins —
and records the composition so the runtime applies it from the application's own metadata. A
project that makes no `AddShiftMapper` call at all does not get it: a shared pack is a
REGISTRATION's pack. The application's build says which packs arrived this way (**SM0043**, Info).
The pack has to be public, because the application's generated code names it (**SM0044**).

**The package must be built with the ShiftMapper generator** referenced as an analyzer, or nothing
is written down: its mappers are invisible, and asking for one of its packs is an error
(**SM0028**) rather than mapping nothing in silence. `[assembly: ShiftMapperContract(2)]` lets a
package built against a different ShiftMapper be refused whole (**SM0033**) instead of half-read.
See [docs/extension-points.md](docs/extension-points.md).

### Implicit maps: a framework's base type declares maps for whoever closes it

A framework whose base class is closed once per entity — a repository, an endpoint — knows which pairs
every application maps. Instead of asking each application to write those pairs down again, the
framework marks its base type **once**, in its own package:

```csharp
// in the framework — the application's author never sees this attribute
[ShiftMapperDeclaresMap("TEntity", "TView", Reverse = true, Nested = 10, Flattening = DeclaredOption.False,
                        Rules = typeof(PlatformConversions))]
[ShiftMapperDeclaresMap("TEntity", "TList", Nested = 10, Flattening = DeclaredOption.False,
                        Rules = typeof(PlatformConversions))]
public abstract class Repository<TEntity, TList, TView> { … }

// on an attribute class, "this" is the type the attribute is applied to
[ShiftMapperDeclaresMap("this", "TView", Reverse = true, Nested = 10)]
public sealed class EndpointAttribute<TList, TView> : Attribute { … }
```

and an application writes the thing it was going to write anyway:

```csharp
public class InvoiceRepository : Repository<Invoice, InvoiceListDto, InvoiceDto> { }
```

The generator compiling the application reads the marker off the base type's metadata, substitutes
the closing type's arguments, and declares `Invoice → InvoiceDto`, `InvoiceDto → Invoice` and
`Invoice → InvoiceListDto` in the application's generated mapper — exactly as if a mapper class had
written `CreateMap` for each. **The maps are ordinary maps**: `mapper.MapToInvoiceDto(invoice)`,
`db.Invoices.ProjectTo<InvoiceListDto>(mapper)`, the `IMapper` door, a referencing project's mapper —
all of it works with nothing else written, because the maps are declared by a generated
`ImplicitMapper` class that travels in the assembly's metadata like any package mapper's.

Four things make an implicit map different from one you declared:

- **Anything explicit wins.** A `CreateMap` for the pair, anywhere the project can see, replaces the
  implicit map in full — and the build says so with an informational note (**SM0047**), because
  replacing a framework's map is the customization path, not an accident. The other pairs the marker
  declared stay implicit.
- **It nests.** `Nested = n` declares implicit maps for the class-typed members below it, `n` levels
  deep, so `InvoiceDto.Lines` maps through `InvoiceLine → InvoiceLineDto` with nothing written. A
  nested pair that already has a map is used as it is, customizations included — which is how a child is
  customized once for every parent that nests it. A member a `ForMember` or a member convention claims
  is not nested; a cycle stops with a note (**SM0048**).
- **It takes the marker's rules.** `Rules = typeof(Pack)` gives the implicit maps that pack at the
  level a mapper class's own `AddConversions` would — nearer than the registration's packs. The same
  pack is also at the FURTHEST level of every other map in a project that closes the marker, as a pack a
  referenced package shared would be: the `CreateMap` a mapper class writes to replace an implicit map
  replaces the map, not the framework's rules for the pair, and anything the class writes nearer still
  wins.
- **It can be configured where the framework's user configures everything else** — see the next section.

A marker that names nothing concrete on a closing type (a type parameter that does not exist, a
closing type that is itself generic) declares nothing and says so (**SM0053**).

### Configuration surfaces: customizing an implicit map in the repository

A framework can hand its user an object with one `MapExpression` per implicit map, and the user writes
ShiftMapper's own vocabulary against it — in the repository, or wherever the framework runs the lambda:

```csharp
// in the framework
public sealed class RepositoryMapping<TEntity, TList, TView> : ShiftMapperConfigurationSurface
{
    public MapExpression<TEntity, TView>   View   { get; } = …   // Map<TEntity, TView>()
    public MapExpression<TView, TEntity>   Entity { get; } = …
    public MapExpression<TEntity, TList>   List   { get; } = …
}

// in the application's repository
public InvoiceRepository(DB db, IHashIdService hashIds) : base(db, o => o.Mapping(m =>
{
    m.List.ForMember(d => d.Total, opt => opt.MapFrom(e => e.Lines.Sum(l => l.Price)));   // projects
    m.Entity.ForMember(e => e.Number, opt => opt.Ignore())
            .AfterMap((dto, entity) => entity.Touch());
    m.View.ForMember(d => d.PublicKey, opt => opt.MapFrom(e => hashIds.Encode(e.Id)));    // a captured service is fine
    m.Nested(2);                                                                          // cap nesting for this repository
}))
{ }
```

It is the same split as a package mapper: the generator reads the lambda at **build time** for its
shape — which members are customized or ignored, whether there is a hook — exactly as it reads a
mapper class's constructor, with every diagnostic; the lambda runs at **run time** wherever the
framework runs it, its expressions land in the surface's store, and the framework hands the surface
to the mapper with `IMapper.Configure(surface)`. The rules that follow:

- The lambda must be inline and unconditional (**SM0035**), like every declaration.
- **One type per pair** may configure it (**SM0050**, an error): the map is one map.
- **A mapper class declaring the pair wins**, and the lambda's lines for that pair are reported dead
  (**SM0051**). A lambda configuring a pair nothing declares is reported too (**SM0052**).
- A customized map used before its configuring type has run in the scope — a service mapping the pair
  directly — is pulled in: the mapper asks the container for the configuring type (or an
  `IShiftMapperConfiguratorResolver`, when the framework registers one), whose construction applies the
  surface. Failing that, the message names the type and the two ways out.

### Member rules a framework states once: `IgnoreMember`

A framework owns some members of every entity — a key its save pipeline assigns, a flag a request sets,
a collection a pipeline attaches. A pack says so **once**, as code, and every map whose source or
destination type declares, inherits or implements the member honours it:

```csharp
public class PlatformConversions : ShiftMapperConversions
{
    public PlatformConversions()
    {
        IgnoreMember<EntityBase>(e => e.Id, MemberRole.Destination);      // never written from a request
        IgnoreMember<ITaggable>(e => e.Tags, MemberRole.Destination);     // owned by a pipeline
        IgnoreMember(typeof(Entity<>), "ReloadAfterSave");                // an open generic base, by name
    }
}
```

An ignored destination member is treated exactly as `opt.Ignore()` would treat it — omitted, and not
reported; an ignored source member is not a candidate for anything. It reaches maps by the same
distance rule a conversion does, and it travels in the pack's metadata.

### Collections of shaped members: `ForEachElement`

A member convention can claim **collections** of its member type too:

```csharp
CreateMemberConvention<SelectDto>()
    .NameFrom<KeyAndNameAttribute>("Text")
    .Fill(d => d.Value, "{Member}ID")                // single member: Brand = { Value = BrandID, Text = Brand.Name }
    .FillIfPossible(d => d.Text, "{Member}.{NameOf}")
    .ForEachElement()                                // collection: Departments = Departments.Select(x => { Value = x.ID, Text = x.Name })
        .Fill(d => d.Value, "ID")
        .FillIfPossible(d => d.Text, "{NameOf}");
```

Paths after `ForEachElement()` are relative to the **element** of the name-matched source collection,
and the result is inline on both backends — in memory a builder over the collection, in the projection
`Select(...).ToList()` a provider turns into a correlated sub-select. Read direction only: writing a
collection of shaped values back is a reconciliation, and the write map reports the member (SM0002)
rather than guessing at one.

### The update overload and nested collections

`Map(source, destination)` assigns a nested collection member a **new** collection of newly mapped
objects; nothing is matched against what the destination held. That is right for a DTO and wrong for
rows with an identity of their own, and nothing in the types tells the two apart — so every map with a
nested collection carries an informational note (**SM0049**) naming the members. Where they are tracked
rows, reconcile them in an `AfterMap` or the caller and `Ignore` the member.

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

### For libraries: `IMapper`

The methods above are strongly typed, and that is the point of them: a destination with no map is
a compile error at the call site. A **library** cannot use them — code in a shared package has to
map an entity to a DTO in an application it has never seen, and its types are generic parameters,
which no typed overload can be chosen for. So `Mapper` also implements one interface:

```csharp
public interface IMapper
{
    TDestination Map<TDestination>(object source);
    TDestination Map<TSource, TDestination>(TSource source);
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);
    IQueryable<TDestination> ProjectTo<TSource, TDestination>(IQueryable<TSource> source);
    bool CanMap(Type source, Type destination);
    void Configure(ShiftMapperConfigurationSurface surface);
}
```

`AddShiftMapper` registers it alongside `Mapper`, and both resolve to the same instance. Under it,
every generated mapper the container registered answers in turn — the application's first, since
it carries every package's maps as well — so a library reaches every pair the application mapped.

```csharp
public class Repository<TEntity, TDto>(IMapper mapper, DbContext db)
{
    public IQueryable<TDto> List() => mapper.ProjectTo<TEntity, TDto>(db.Set<TEntity>());

    public TDto? Read(TEntity entity) =>
        mapper.CanMap(typeof(TEntity), typeof(TDto)) ? mapper.Map<TEntity, TDto>(entity) : default;
}
```

Worth knowing:

- **It is the slower door, on purpose.** Every method finds its map by comparing types at runtime,
  and a struct destination is boxed on the way back. Use the typed methods wherever the call site
  knows both types.
- **The members are implemented explicitly**, so they stay invisible on `Mapper`.
  `mapper.Map<SomeDto>(unmappedThing)` keeps failing to compile rather than binding to the
  `object` overload and throwing at runtime.
- **`CanMap` is there so you never have to catch an exception to find out.** It answers for the
  create methods and matches them rule for rule, subclasses included.
- **The create doors accept a subclass** of a mapped type — exact runtime type first, then
  assignability — so an EF proxy maps through its base. The update overload needs the exact
  declared pair, because the destination you passed in is the object being written to.
- **A `Mapper` built outside a container has no run-time door** unless it was built with
  `Mapper.Create(assembly)`: there is nothing registered to dispatch to, and a `Map` says so.
  `CanMap` is a question, and answers it: false.

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
[`ShiftMapper.Benchmarks/RESULTS.md`](ShiftMapper.Benchmarks/RESULTS.md). The numbers were taken
calling the generated class directly; going through `Mapper` adds one extension call and one type
test (`mapper.Root<GeneratedMapper>()`) per map — a nanosecond or two — and the benchmarks now go
that way, so re-run them to refresh the table.

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

Forty-eight rules, `SM0001` to `SM0053` (`SM0029`, `SM0039`–`SM0041` and `SM0045` are retired). Nine
stop the build; the rest describe something that will not be mapped, or will be mapped in a way
worth knowing about.

| Id | Default | What it means |
|---|---|---|
| SM0001 | Warning | Destination property has no matching source property |
| SM0002 | Warning | Names match; ShiftMapper does not convert between the two types |
| SM0003 | Warning | Destination property's setter is not public |
| SM0004 | Warning | Destination type has no constructor ShiftMapper can call |
| SM0005 | Warning | A generic mapper class cannot be included in the generated mapper |
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
| SM0027 | Warning | A pair is declared both in this project and by a referenced package |
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
| SM0042 | **Error** | A map is declared twice with nothing to choose between the two |
| SM0043 | Info | A referenced package shared a pack or a mapper class with this project |
| SM0044 | **Error** | A pack or mapper class shared with referencing projects is not public |
| SM0046 | Warning | A registration line has no effect under the project's discovery mode |
| SM0047 | Info | An implicit map is replaced by an explicit declaration of the pair |
| SM0048 | Info | Automatic nesting stopped at a cycle; the member is left unmapped |
| SM0049 | Info | The update overload replaces a nested collection with new objects |
| SM0050 | **Error** | Two configuration surfaces configure the same pair |
| SM0051 | Warning | A configuration surface is ignored because a mapper class declares the pair |
| SM0052 | Warning | A configuration surface configures a pair nothing declares |
| SM0053 | Warning | An implicit map marker could not be applied to a closing type |

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
- The declaration metadata contract is **3** as of 0.3.0 (implicit maps, ignore rules and element
  conventions travel in it). A package built with 0.2.x is refused whole by a 0.3 consumer (SM0033)
  and the other way round; rebuild the package.
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
