# Getting started

The package is installed. This page takes you from there to a mapper you can inject, with both
backends working and a build you can read. It assumes you have skimmed
[Five minutes](../README.md#five-minutes) in the README; where that gives you the shape, this
gives you the reasons and the exact names.

Five things, in the order you need them: the mapper class, a map, the two backends, the DI
registration, and the build output.

---

## 1. The mapper class, and the class you inject

```csharp
using Microsoft.Extensions.Logging;
using ShiftMapper;

public class AppMapper : ShiftMapperBase
{
    private readonly ILogger<AppMapper> _logger;

    public AppMapper(ILogger<AppMapper> logger)
    {
        _logger = logger;                  // ordinary constructor injection

        CreateMap<Brand, BrandDto>();      // declarations go here
    }
}
```

That is the whole hand-written half. `ShiftMapperBase` gives you the declaration API
(`CreateMap`, `AddConversions`, `CreateConversion`, `CreateMemberConvention`, `ConfigureDefaults`)
plus the `Services` provider, and no `Map` method. **Nothing is generated onto this class** — it
need not be `partial`, and nothing injects it. The mapping methods arrive in ONE generated class
per assembly, holding every map from every mapper class in the project and every mapper class the
referenced packages declare:

```csharp
// ShiftMapper.Sample/Generated/.../ShiftMapper.Generated.g.cs
namespace ShiftMapper.Generated.ShiftMapper_Sample
{
    internal sealed class GeneratedMapper : global::ShiftMapper.ShiftMapperBase, global::ShiftMapper.IMapper
    {
        public global::ShiftMapper.Sample.Dtos.BrandDto MapToBrandDto(/* ... */)
```

and you reach it through **`Mapper`**, the one class in the runtime package: inject it, call
`mapper.Map<BrandDto>(brand)`. Every typed method on it is an extension method the generator wrote
into your project, forwarding to your assembly's generated mapper.

Two consequences worth knowing up front. The generated mapper builds each mapper class from the
container the first time anything is mapped, so anything you injected into a constructor is
available to a `MapFrom` or a `ConstructUsing` without ceremony. And because every mapper class in
the project is read, splitting maps over files is only that — see §1a below.

### When a class cannot be included

A generic mapper class cannot be constructed by the generated mapper — there are no type arguments
to construct it with — and is reported as **SM0005**, a warning: the class still compiles, it is
simply a mapper with no maps.

### Where declarations may be written

Anywhere the call is a plain statement in the class: the constructor, an expression-bodied
constructor, or a private helper the constructor calls. A mapper with two hundred maps can split
them across methods.

```csharp
public AppMapper()
{
    AddCatalogMaps();
    AddOrderMaps();
}

private void AddCatalogMaps() => CreateMap<Product, ProductDto>();
```

What is refused is a declaration whose *surrounding code decides whether it applies* — inside an
`if`, a loop, a ternary, a `switch`, a `try`, a lambda or a property accessor. That is
**SM0035**, and it is an error. The reason is invariant (2) of the library: the generator bakes
the declaration in and the condition is silently discarded, so a mapper would end up doing
something its own source does not say. See
[Where a declaration may be written](../README.md#where-a-declaration-may-be-written).

---

### 1a. When the constructor outgrows one file

Write the rest of the declarations in other classes deriving from `ShiftMapperBase`. Nothing names
them: the generator reads every one in the project into the generated mapper. Each map keeps its
own class's `ConfigureDefaults` and conversions, and is reported in its own file; a pair declared
in two classes is an error (SM0042). The
[README](../README.md#several-mapper-classes-one-mapper) has the rules; the sample's `AppMapper`,
`CatalogMapper` and `InvoiceLabelMapper` are the shape.

## 2. One `CreateMap`, and what it generates

```csharp
CreateMap<Brand, BrandDto>();                                  // defaults
CreateMap<Brand, BrandDto>(o => o.AllowNullCollections = true); // per-map options
```

`CreateMap` never runs. It returns a `MapExpression<TSource, TDestination>` so refinements can be
chained in ordinary, compiler-checked C# (`ForMember`, `ReverseMap`, `ConvertUsing`, …); the
generator reads the call at build time. Ignoring the return value is normal.

For that one line, the generated part of `AppMapper` contains — real names, read out of
`ShiftMapper.Sample/Generated/ShiftMapper.Generator/ShiftMapper.Generator.ShiftMapperGenerator/ShiftMapper_Sample_Mapping_AppMapper.ShiftMapper.g.cs`:

| Generated member | What it does |
|---|---|
| `MapToBrandDto(Brand source)` | Creates a `BrandDto`. Throws `ArgumentNullException` on a null source. |
| `MapToBrandDtoOrNull(Brand? source)` | The same map; null in, null out. Emitted only when source and destination are both reference types. |
| `Map(Brand source, BrandDto destination)` | Copies onto an object you already have and returns it. |
| `MapToBrandDtoList` / `MapToBrandDtoArray` / `MapToBrandDtoHashSet` | Take `IEnumerable<Brand>?`, build the named shape. |
| `Map<TDestination>(Brand source)` | Picks the map by type argument. |
| `Map<TDestination>(IEnumerable<Brand>? source)` | Picks map **and** collection shape by type argument. |
| `MapOrNull<TDestination>(Brand? source)` | The type-argument form of `MapToBrandDtoOrNull`. |
| `ProjectTo<TDestination>(IQueryable<Brand> source)` | Hands EF one expression. |

The naming rule is `MapTo` + the destination type's own name. The type-argument methods are
**one per source type**, not one per pair: `Map<TDestination>(Brand)` in the sample tests five
destinations, because five maps are declared from `Brand`. `IReadOnlyList<BrandDto>` has no method
of its own — it routes through `MapToBrandDtoList`. A destination with nothing assignable after
construction (a positional record) gets **no** update overload, so
`mapper.Map(product, summaryDto)` is a compile error rather than a call that does nothing.

Alongside the class, the generator emits extension methods in a `ShiftMapper.Generated` namespace
and a `global using` for it, so they are in scope in every file of the project with no `using` of
your own:

```csharp
var dto = mapper.Map<BrandDto>(brand);   // instance
var dto = brand.Map<BrandDto>(mapper);   // extension — forwards to the line above
```

The body is ordinary C# you can read and step through. This is the generated `MapToBrandDto` from
the sample, unedited:

```csharp
return new global::ShiftMapper.Sample.Dtos.BrandDto
{
    Id = source.Id,
    Name = source.Name,
    Country = source.Country,
    FoundedYear = global::ShiftMapper.ValueConverter.ToInvariantString(source.FoundedYear),
    Tags = global::ShiftMapper.ValueConverter.ToListOrEmpty(source.Tags),
    ExternalIds = global::ShiftMapper.ValueConverter.ToListOrEmpty<long, int>(source.ExternalIds, static item => unchecked((int)item)),
    Aliases = global::ShiftMapper.ValueConverter.ToListOrEmpty(source.Aliases),
    IsoCode = source.ISOCode,
};
```

`IsoCode` found `ISOCode` through the default case-insensitive fallback; `FoundedYear` went
through the conversion table; `ExternalIds` is the one line in the sample that earns a warning
(SM0010 — see §5).

### Reading your own generated file

Nothing is required for the mapper to work, but it is worth doing once. The sample asks the
compiler to write generated files to disk (`ShiftMapper.Sample/ShiftMapper.Sample.csproj`):

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
<CompilerGeneratedFilesOutputPath>Generated</CompilerGeneratedFilesOutputPath>
```

```xml
<!-- those files are for reading, not for compiling a second time -->
<Compile Remove="Generated/**" />
```

Two ShiftMapper files appear there: `<namespace>_<Mapper>.ShiftMapper.g.cs`, the mapper's other
half, and `ShiftMapper.Declarations.g.cs`, the assembly attributes that let a *referenced*
assembly's mappers and packs be read later (covered in [extension-points.md](extension-points.md)).

---

## 3. Two backends, and why both must answer

```csharp
// objects already in hand
var brands = await db.Brands.AsNoTracking().OrderBy(b => b.Id).ToListAsync();
var dtos   = mapper.Map<List<BrandDto>>(brands);              // GET /api/brands

// the same DTO, built by the database
IQueryable<BrandDto> query = db.Brands.AsNoTracking()
    .OrderBy(b => b.Id)
    .ProjectTo<BrandDto>(mapper);                             // GET /api/brands/projected
```

Those are the sample's `GET /api/brands` and `GET /api/brands/projected` — the same map, side by
side, deliberately. Add `?sql=true` to the second to see the query
(`ShiftMapper.Sample/Endpoints/BrandEndpoints.cs`). `GET /api/products` is the one worth reading
next: `ProductDto` carries a nested `BrandDto` and `StockDto`, the endpoint writes no `Include`,
and the whole graph still arrives as one query — because a projection is one expression assembled
from the projections of the maps it nests.

Which door to use is not a preference. `Map` needs the objects loaded; `ProjectTo` selects only
the columns the DTO uses and lets `Where`/`Skip`/`Take` happen in SQL against the projected
shape. `Map` also **throws on a null source**, on purpose — building a DTO out of nothing is
nearly always a bug, and `MapOrNull` is the door for when a missing source is ordinary data.

**Both backends are generated from the same analysis**, which is the invariant the rest of the
library is arranged around: every feature either answers for `Map` and `ProjectTo` alike, or says
loudly which one it does not support. Saying so is not documentation — it is a diagnostic:

- a map-level feature that needs a statement costs the projection and reports it where the map is
  declared (SM0015, SM0017, SM0018, SM0024, SM0030);
- a map that merely *nests* one of those loses its own projection, reported as **SM0036** naming
  the child map — the one to go and fix;
- and the refusal is repeated at the query, as **SM0037**, because whoever writes
  `db.Invoices.ProjectTo<InvoiceLabelDto>(mapper)` is usually looking at a different file.

At run time the generated projection member for such a pair throws a message naming the
alternative rather than quietly returning rows a hook never touched.

---

## 4. Registering it

```csharp
builder.Services.AddShiftMapper();                                        // Scoped; nothing to name
builder.Services.AddShiftMapper(ServiceLifetime.Singleton);               // if no mapper class has scoped deps

builder.Services.AddShiftMapper(o =>                                      // the long form
{
    o.AddConversions<PlatformConversions>();                              // a pack of rules for every map
    // — or not even that: a package that registers itself and shares its pack (see the README)
    o.Discovery = MapperDiscovery.All;                                    // the default: every class in sight;
    //   LocalAndRegistered or Registered to name classes with o.AddMapper<T>() instead
    o.Lifetime = ServiceLifetime.Scoped;                                  // the default
});
```

One call, hand-written library code
(`ShiftMapper/ShiftMapperServiceCollectionExtensions.cs`), and it does three things: registers the
calling assembly's **generated mapper**, read from the assembly's own metadata so nothing is named;
registers **`Mapper`**, the one object application code injects, made of every generated mapper
any call registered — this assembly's first; and registers **`IMapper`** for libraries that
cannot name your types, resolving to the same object. Mapper classes are not registered: the
generated mapper builds them from the provider on first use, with their dependencies injected.
Scoped is the default so a mapper class may safely depend on a `DbContext`.

**The generator reads the lambda.** A pack added in it is baked into every map of the generated
mapper at compile time, exactly as if it had been written in every constructor, which is why the
lambda must be inline and its statements plain (**SM0035** otherwise).

### Services timing — the one piece of ordering to know

`Services` is assigned by `AddShiftMapper` **after** your constructor returns; the object has to
exist before anything can be set on it. So:

- **Do not touch `Services` from the mapper's constructor.** Inject what you need there instead.
  Reading it before it is assigned throws an `InvalidOperationException` saying no service provider has been set on the mapper — the same message a mapper constructed by hand instead of through `AddShiftMapper` gets.
- **Mapper classes and packs are materialised on first use, not at startup.** The generated
  mapper's metadata lists them; each is constructed the first time anything is actually mapped, by
  which point DI is in place. That is what allows a mapper class to take dependencies at all.

A mapper class with a dependency needs no registration of its own — it is built from the provider
with its dependencies injected. This is the sample's `Program.cs`, and
`ShiftMapper.Sample/Mapping/InvoiceLabelMapper.cs` documents it in full:

```csharp
builder.Services.AddSingleton<IInvoiceNumbering, InvoiceNumbering>();
builder.Services.AddShiftMapper();                                   // InvoiceLabelMapper is built on first map
```

The edge worth knowing: **one class that takes dependencies makes the whole assembly's mapper
DI-only.** Every class is built together on first use, so one that cannot be built fails the first
map, including maps unrelated to it. Skipping it instead would leave its `MapFrom` members quietly
unfilled, which is the divergence the library exists to prevent. `Mapper.Create(assembly)` in a
test works only when every mapper class is parameterless.

---

## 5. Reading the build output

Every ShiftMapper message is `SM####`, from a real `DiagnosticAnalyzer` — so the IDE shows them
live, `.editorconfig` tunes them per folder, and CI sees the same list. Forty-one rules in use today (SM0029,
SM0039–SM0041 and SM0045 are retired); eight stop the build. The
[summary table in the README](../README.md#diagnostics) lists all of them, and
[diagnostics.md](diagnostics.md) goes through them one at a time.

The one to understand first is **SM0001**, because it is the point of the whole exercise: a
destination member nothing fills is *reported*, never silently left empty.

(This is what `CreateMap<Brand, BrandDto>(o => o.Matching = PropertyMatching.CaseSensitive)` would report; the sample's default case-insensitive match finds `ISOCode` and stays quiet.)

```
warning SM0001: ShiftMapper: 'BrandDto.IsoCode' is not mapped because 'Brand' has no readable
                property named 'IsoCode'
```

Its honest answers are "map it" or "I know, and I mean to leave it" — and the second one is
written down, not suppressed. The IDE offers `Ignore 'IsoCode'` as a code fix, which puts
`.ForMember(d => d.IsoCode, o => o.Ignore())` in your source where the next reader sees a
decision. A `#pragma` leaves nothing.

**SM0011** is the same idea as an error, and the message carries all three ways out:

```
error SM0011: ShiftMapper: 'OrderDto.Line' needs a map from 'Line' to 'LineDto'. Add
              CreateMap<Line, LineDto>(), or CreateMap<LineDto, Line>().ReverseMap(), or
              .ForMember(d => d.Line, opt => opt.Ignore()) to leave it unmapped on purpose.
```

It is an error rather than a warning because a null nested object in a JSON response looks exactly
like a null in the database.

### What a healthy build looks like

A clean rebuild of `ShiftMapper.Sample` reports **seven SM warnings, five of them projection
notices**, and every one is a feature the sample demonstrates on purpose:

```
SM0010  'BrandDto.ExternalIds' converts List<long> to List<int>, which cannot hold every value
SM0017  BrandPatch -> Brand assigns members behind a Condition, so ProjectTo cannot use it
SM0018  Stock -> StockHookDto runs BeforeMap and AfterMap, so ProjectTo cannot use it
SM0018  Invoice -> InvoiceLabelDto runs AfterMap, so ProjectTo cannot use it
SM0024  CatalogItem -> CatalogItemDto dispatches through Include, so ProjectTo cannot use it
SM0030  Brand -> BrandFilesDto converts with a conversion that has no query form
SM0030  Product -> ProductFingerprintDto converts with a conversion that has no query form
```

Note the shape of five of those seven: *"so ProjectTo cannot use it; Map is unaffected"*. That is
invariant (1) speaking. None of them is a bug; each is the cost of a feature, stated at build time
by the person who can still act on it.

Two practical notes:

- **Analyzer diagnostics come out of the compile**, so an incremental build that skips a project
  reports nothing for it. On this repo, a plain `dotnet build` of the sample after a previous
  build printed two of the seven; `dotnet build --no-incremental` printed all seven. Use a
  rebuild when you want the full list.
- **Info-level rules do not show up in a default console build** — SM0006 (a `ReverseMap` leaving a member unmapped), SM0008 (a conversion that loses information by design), SM0009 (mapped by parsing text), SM0015 (`ConstructUsing` is not projectable) and SM0020 (a member filled by flattening).

---

## 6. Where to go next

- **[conversions.md](conversions.md)** — what converts between matched properties, what is
  refused and why, and `CreateConversion` for a type pair across every map (including the
  `memory`/`query` pair, which is the two-backend decision in its purest form).
- **[extension-points.md](extension-points.md)** — `ForMember` and its options, the map-level
  hooks, included mappers, packs, member conventions, inheritance and open generics, rules arriving from a
  referenced assembly, and `IMapper` for library code that cannot name your mapper.
- **[diagnostics.md](diagnostics.md)** — all thirty-eight rules, what each one is protecting, and
  how to answer it.
- **[automapper-migration.md](automapper-migration.md)** — what carries over unchanged, what is
  spelled differently, and what has no equivalent by design.
- **`ShiftMapper.Sample`** — a working ASP.NET app where every feature has an endpoint and a
  comment explaining the decision behind it. Start at `GET /` for the index, then
  `GET /api/brands` and `GET /api/brands/projected` for the two backends on one map. Run the
  requests from `ShiftMapper.Sample/ShiftMapper.Sample.http`; it needs a local SQL Server Express
  instance, and creates and seeds its database on first run.
