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

Fifteen rules, `SM0001` to `SM0015`. Two stop the build; the rest describe something that will
not be mapped, or will be mapped in a way worth knowing about.

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

What works today is listed above. What does not exist yet — `Condition` / `NullSubstitute` /
`BeforeMap` / `AfterMap`, flattening, inheritance, open generics, and the global configuration
layer a library needs — is laid out in order in [PLAN.md](PLAN.md), which is the roadmap this
repository is built from.

## License

MIT. See [LICENSE](LICENSE).
