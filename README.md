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

Every map produces four entry points. The instance methods live on your class; the extension
methods forward to them, so use whichever reads better where you are.

```csharp
BrandDto dto = mapper.Map<BrandDto>(brand);         // create
mapper.Map(brand, existingDto);                     // update in place

BrandDto dto = brand.Map<BrandDto>(mapper);         // the same two, as extensions
brand.Map(existingDto, mapper);

IQueryable<BrandDto> q = mapper.ProjectTo<BrandDto>(db.Brands);
IQueryable<BrandDto> q = db.Brands.ProjectTo<BrandDto>(mapper);
```

`ProjectTo` is the one that matters for a database. It hands EF Core a single expression tree
covering the whole graph, so only the columns the DTO needs are selected, and filtering and
paging happen in SQL against the projected shape.

---

## What it maps

- **Properties by name**, exact match first with a case-insensitive fallback, so `Sku` finds
  `SKU`. Configurable per map or per mapper (`PropertyMatching`, `ConfigureDefaults`).
- **Type conversions** between matched properties: implicit conversions, numeric pairs, text
  parsing and formatting, enums, `Guid`, `TimeSpan` and the date and time types, user-defined
  implicit operators, and collections of all of those (`ToArray` / `ToList` / `ToHashSet`).
- **Nested objects and collections of objects**, composed to any depth from the maps you
  declared — in memory and inside one EF projection.
- **Per-member overrides**: `opt.Ignore()` and `opt.MapFrom(s => ...)`.

Both backends are generated from the same analysis, so `Map` and `ProjectTo` agree on what a
map means.

### What it deliberately refuses

Four conversions are refused because the answer would come from something other than the two
types: `DateTime` to `DateTimeOffset` (whose time zone?), `DateTimeOffset` to `DateTime`, one
enum to a different enum (a cast pairs them by number), and `TimeSpan` to `TimeOnly`. A
user-defined *explicit* operator is refused too — its author chose the keyword that says stop
and think. Each is reported as SM0002 rather than skipped in silence.

---

## Diagnostics

Twelve rules, `SM0001` to `SM0012`. Ten describe a property that will not be mapped; two stop
the build.

| Id | Default | What it means |
|---|---|---|
| SM0001 | Warning | Destination property has no matching source property |
| SM0002 | Warning | Names match; ShiftMapper does not convert between the two types |
| SM0003 | Warning | Destination property's setter is not public |
| SM0004 | Warning | Destination type has no public parameterless constructor |
| SM0005 | Warning | Nothing was generated for a `ShiftMapperBase` class (not `partial`, or generic) |
| SM0006 | Info | A `ReverseMap` leaves a destination property unmapped |
| SM0007 | Warning | Several source properties match when case is ignored |
| SM0008 | Info | Mapped through a conversion that loses information by design |
| SM0009 | Info | Mapped by parsing text at runtime, so bad data throws |
| SM0010 | Warning | Mapped through a conversion that can change the value (e.g. `long` to `int`) |
| SM0011 | **Error** | A nested object property has no `CreateMap` for its types |
| SM0012 | **Error** | Nested maps form a circular graph |

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

What works today is listed above. What does not exist yet — collection-level `Map`, records and
constructor-initialised destinations, `Condition` / `NullSubstitute` / `BeforeMap` / `AfterMap`,
flattening, inheritance, open generics, and the global configuration layer a library needs — is
laid out in order in [PLAN.md](PLAN.md), which is the roadmap this repository is built from.

## License

MIT. See [LICENSE](LICENSE).
