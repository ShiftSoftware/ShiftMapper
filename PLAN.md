# ShiftMapper — what is left to build

This file is the roadmap. It is written to be read top to bottom and implemented in the
order it is written: each step assumes the ones above it are done.

Two things drive every decision below.

**One.** ShiftMapper has TWO backends, not one — the in-memory `Map` methods and the
`ProjectTo` expression handed to EF Core. Every feature added from here on has to answer
for both, or say out loud which one it does not support and report it at build time. A
feature that silently works in memory and silently client-evaluates (or throws) in a query
is worse than no feature.

**Two.** ShiftMapper must stay a GENERAL mapper. It must never know what a `ShiftFileDTO`
or a `ShiftEntitySelectDTO` is. What it owes ShiftFramework is a set of extension points
good enough that ShiftFramework can register those helpers ITSELF, once, in its own
assembly — and every application that references it gets them automatically, in memory and
in SQL. Phase 3 is that layer, and it is the reason the whole plan exists.

---

## Status

Every step heading below carries the same marker: ✅ done, ⬜ pending.

**Phase 1 — Trust**

- [x] **Step 1** — Tests
- [x] **Step 2** — Fix the runtime cost
- [x] **Step 3** — Ship it
- [x] **Step 4** — One entry point a library can be written against

**Phase 2 — Close the mapping gaps**

- [x] **Step 5** — Collections and null policy at the top level
- [ ] **Step 6** — Destinations that are not `new T { }`
- [ ] **Step 7** — Per-member power tools
- [ ] **Step 8** — Map-level hooks
- [ ] **Step 9** — Flattening and naming conventions
- [ ] **Step 10** — Inheritance, polymorphism, open generics

**Phase 3 — The general layer**

- [ ] **Step 11** — Profiles: maps declared outside the mapper class
- [ ] **Step 12** — Global type-pair converters
- [ ] **Step 13** — The compile-time extension contract for referenced assemblies
- [ ] **Step 14** — Declarative member conventions
- [ ] **Step 15** — What ShiftFramework then builds (ShiftEntity repository, not this one)

**Phase 4 — Finish**

- [ ] **Step 16** — Diagnostics and analyzer completeness
- [ ] **Step 17** — Docs and sample
- [ ] **Step 18** — Benchmarks

Phase 1 is complete, and Step 5 of Phase 2 with it. Next on ShiftFramework's critical path is
**Step 8**, then 8 → 10 → 11 → 12 → 13 → 14 → 15 (the summary at the foot of this file).

---

## 0. Where we are today

### What works

- `CreateMap<TSource, TDestination>()` declared in a mapper's constructor, read at COMPILE
  time by `ShiftMapper.Generator`, which writes the other half of the partial class.
- `.ReverseMap()` — the opposite direction, analysed independently rather than mirrored.
- `.ForMember(d => d.X, opt => opt.Ignore())` and `.ForMember(d => d.X, opt => opt.MapFrom(s => ...))`.
- Property matching by name, exact-first with a case-insensitive fallback
  (`PropertyMatching`), settable per map or per mapper (`ConfigureDefaults`).
- Type conversion between matched properties: implicit conversions, numeric pairs, text
  parsing/formatting, enums, `Guid`/`TimeSpan`/date-time types, user-defined implicit
  operators, and collections of all of those (`ToArray` / `ToList` / `ToHashSet`), plus
  dictionaries.
- Nested objects and collections of objects, composed to any depth from the maps you
  declared, in memory AND inside one EF projection.
- Cycle detection as a build error (SM0012).
- Twelve build-time diagnostics, SM0001–SM0012.
- Generated surface per map: `TDestination Map<TDestination>(TSource)`,
  `TDestination Map(TSource, TDestination)`,
  `IQueryable<TDestination> ProjectTo<TDestination>(IQueryable<TSource>)`, plus
  extension-method spellings of each.
- DI registration through `AddShiftMapper<TMapper>()`, with constructor injection and a
  `Services` escape hatch.
- **(Step 1)** `ShiftMapper.Tests` and `ShiftMapper.Generator.Tests` — 275 tests covering every
  diagnostic, the conversion matrix, projections against real SQLite, and Map/ProjectTo parity —
  running in CI on every push.
- **(Step 2)** Customizations compiled once per mapper TYPE per process, projections held in
  lazily-initialised cached fields, and a non-generic direct create method behind the generic
  dispatcher. Measured by `ShiftMapper.Benchmarks`.
- **(Step 3)** A NuGet package, `ShiftSoftware.ShiftMapper`, carrying the generator as an
  analyzer; and the SM#### rules reported by a real `DiagnosticAnalyzer`, so `.editorconfig`
  retunes them per folder.
- **(Step 4)** `IShiftMapper`, implemented explicitly on every generated mapper and registered by
  `AddShiftMapper`, so a library can map, update, project and ask `CanMap` without naming the
  application's mapper class.
- **(Step 5)** Collection overloads on every map (`List` / array / `HashSet` / `IReadOnlyList`),
  `MapOrNull`, dictionaries in the conversion table, and one null-collection policy answered the
  same way by both backends.

### What is missing, in one paragraph

Destinations must have a parameterless constructor, so records and constructor-initialised
DTOs are out.
There is no `Condition`, `NullSubstitute`, `BeforeMap`/`AfterMap`, `ConstructUsing`,
`ConvertUsing`, flattening, inheritance or open generics. And — the item this plan is
mostly about — there is no GLOBAL configuration layer at all: every rule has to be
restated on every map, in every application, so a framework cannot contribute a rule that
applies everywhere.

---

## Phase 1 — Make what already exists trustworthy

Nothing in Phase 2 or 3 is safe to build on top of an untested generator.

### ✅ Step 1 — Tests

Add `ShiftMapper.Tests` (xunit) and `ShiftMapper.Generator.Tests`.

- **Generator tests**: drive `CSharpGeneratorDriver` over source snippets and assert on the
  emitted text and on the diagnostics. One test per diagnostic SM0001–SM0012, asserting the
  id, the severity AND the location (several of them deliberately point at `.ReverseMap()`
  rather than at `CreateMap`, and nothing currently guards that).
- **Conversion matrix tests**: every pair `ConversionResolver` claims to handle, both
  directions where both exist, plus the pairs it deliberately REFUSES (`DateTime` to
  `DateTimeOffset`, enum to a different enum, `TimeSpan` to `TimeOnly`, explicit operators)
  asserted as SM0002 rather than as a silent skip.
- **Runtime tests**: `ValueConverter` round-trips, invariant-culture behaviour under a
  hostile `CurrentCulture`, null handling, `MapCustomizations.Compose` splicing, and
  "last call wins" for `MapFrom` followed by `Ignore`.
- **Projection tests against a real database**: EF Core + SQLite, asserting the generated SQL
  shape for the nested four-level graph the sample builds, and asserting that the correlated
  subquery for `InvoiceDto.Total` really is a subquery and not a client evaluation. Do NOT
  use the EF in-memory provider — it translates things a database will not.
- **Parity tests**: for each map, assert `Map<TDto>(entity)` and `ProjectTo<TDto>(query)`
  produce the SAME values. The two backends are written by different code paths and this is
  the single most valuable test in the suite.

**Done when** the suite covers every diagnostic and every conversion pair, and runs in CI.

**What landed.** `ShiftMapper.Generator.Tests` drives the generator AND the analyzer over source
snippets and asserts on id, severity, location, emitted text and whether the result still
compiles; `ShiftMapper.Tests` covers the runtime, projections against real SQLite, and
Map/ProjectTo parity. `.github/workflows/ci.yml` restores, builds, tests and packs on every push
and pull request.

### ✅ Step 2 — Fix the runtime cost

Two real problems, both invisible until load:

1. `MapCustomizations._compiled` is an instance field, and `AddShiftMapper` registers the
   mapper as **Scoped** by default. So every `MapFrom` expression is `Compile()`d again on
   the first request that uses it, for every request. Expression compilation costs hundreds
   of microseconds.
2. Each generated projection is an **expression-bodied property**
   (`private Expression<Func<A,B>> ShiftMapperProjection_A_To_B => Customizations.Compose(...)`),
   so `Compose` re-walks the tree, re-runs its LINQ scan of `_values`, and rebuilds every
   `MemberInit` on EVERY `ProjectTo` call — and, because a nested map is reached through the
   parent's property, once per level per call.

Fix both by keying the caches on the mapper's **type**, not its instance:

- Move `_compiled` to a `static ConcurrentDictionary<(Type Mapper, CustomizationKey), Delegate>`,
  or hold the whole `MapCustomizations` in a static per-mapper-type holder built once.
- Emit each projection as a lazily-initialised cached field rather than a computed property.
- While here: `Map<TDestination>` boxes through `(TDestination)(object)destination`. Emit a
  non-generic `MapToBrandDto(Brand)` alongside the generic dispatcher and have the dispatcher
  call it, so struct destinations stop allocating and the `typeof` chain stops being the only
  route in.

Add a benchmark project (BenchmarkDotNet) in the same step so the fix is measured, not
assumed.

**Done when** a mapper resolved per request compiles each customization once per process and
allocates no projection tree per `ProjectTo` call.

**What landed.** `MapCustomizations` keys its compiled delegates on the mapper TYPE in a static
dictionary, falling back to a per-instance one only for a lambda that captured instance state;
each projection is emitted as a lazily-initialised cached field rather than a computed property;
and a non-generic direct create method sits behind the generic dispatcher.
`ShiftMapper.Benchmarks` measures all three.

### ✅ Step 3 — Ship it

- Pack `ShiftMapper` as a NuGet package that carries the generator as an analyzer
  (`ShiftMapper.Generator.dll` under `analyzers/dotnet/cs`, `PrivateAssets`), so a consumer
  adds ONE `PackageReference` and gets both halves.
- `PackageId`, `Description`, `Authors`, license, icon, `RepositoryUrl`, SourceLink,
  deterministic build, symbol package.
- Decide and document the version/TFM policy. `net10.0` only is fine for ShiftFramework
  today; state it explicitly rather than leaving it implied.
- Write the `README.md` this repository does not have — the five-minute version of what the
  XML docs already say at length.
- Make the diagnostics tunable from `.editorconfig`. Today they come from a source generator,
  which the compiler treats like its own CS diagnostics: `NoWarn` and `WarningsAsErrors`
  work, `dotnet_diagnostic.SM0001.severity` does not. Split the reporting half into a real
  `DiagnosticAnalyzer` so teams can retune per folder, which is what everyone expects.

**Done when** `dotnet add package ShiftSoftware.ShiftMapper` is all a consumer needs.

**What landed.**

- `ShiftMapper.csproj` packs as `ShiftSoftware.ShiftMapper` and copies
  `ShiftMapper.Generator.dll` into `analyzers/dotnet/cs` from an analyzer-typed project
  reference with `PrivateAssets="all"`, so one `PackageReference` installs both halves and the
  generator never becomes a dependency. The pack FAILS if the generator is missing rather than
  shipping a package that compiles and maps nothing.
- `Directory.Build.props` carries the shared identity (authors, license, repository URL, one
  `Version` for the whole repository), deterministic builds, `ContinuousIntegrationBuild` on CI,
  and SourceLink — which is part of the SDK since .NET 8, so it is two properties rather than a
  package. `IsPackable` defaults to false there and only `ShiftMapper` opts back in.
- Symbols ship as a `.snupkg`, and the library's XML documentation ships in the package.
- `README.md` (packed), `LICENSE` — MIT — and `ShiftMapper/Images/icon.png`, which is the
  shared ShiftFramework icon, byte-identical to the one every ShiftEntity package ships and
  packed from the same `Images\icon.png` location they use.
- **Version and TFM policy**, stated in the README rather than left implied: runtime library
  `net10.0` only, generator `netstandard2.0` because a Roslyn component has no choice, one
  version number for both halves, and pre-1.0 while the declaration API is still growing.
- **The diagnostics moved into a real `DiagnosticAnalyzer`** — `ShiftMapperAnalyzer` — and the
  generator now reports nothing at all. Both halves read the maps through the same
  `BuildMapperClass` and `MergeAndResolve`, so they cannot drift; the analyzer passes a
  `DiagnosticReporter` and the generator passes null. The reporter attaches every message to a
  real `SyntaxTree`, which is what `.editorconfig` resolution is keyed on — a file-path-only
  location prints identically and cannot be retuned at all. `AnalyzerConfigTests` proves
  promotion, suppression, raising an informational rule and turning an error down, all through
  the real `SyntaxTreeOptionsProvider` the compiler builds from an .editorconfig.

One consequence, and it is in the README: a project that turns analyzers off entirely still gets
mapping code, but silently — including SM0011 and SM0012, the two that stop a build.

### ✅ Step 4 — One entry point a library can be written against

Today a consumer must reference the concrete `AppMapper` type. A framework cannot: it has to
be written against something it can resolve from DI without knowing the application's mapper
class.

Generate an implementation of a hand-written interface:

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

- The generator emits the dispatch table (source type by destination type) on the mapper
  class.
- `AddShiftMapper<TMapper>()` registers `TMapper` AND `IShiftMapper`.
- `CanMap` matters: framework code needs to ask "is there a map for this pair?" before
  falling back, instead of catching an exception.

Keep the strongly typed generated methods as the primary API — this interface is for
libraries and for reflection-driven call sites, and it is explicitly the slower door.

**Done when** ShiftFramework can be compiled against `IShiftMapper` with no reference to any
application's mapper type.

**What landed.**

- `IShiftMapper` in the runtime library, with the five members above.
- The generator implements every one of them on the mapper, and adds the interface to the class
  from the GENERATED part — a base list may name a base class in only one part, but any part may
  add interfaces, so the hand-written half still reads `: ShiftMapperBase` and nothing else.
- **Implemented EXPLICITLY**, which is the decision worth recording. An implicit
  `Map<TDestination>(object)` would sit on the mapper class beside the typed
  `Map<TDestination>(Brand)` overloads and accept the calls they refuse — a destination with no
  map would stop being a compile error and start being a runtime exception. Explicit members are
  invisible on the class and reachable only through the interface, so the typed API keeps failing
  at build time.
- **Nothing is re-implemented.** Each member works out which map applies and calls the generated
  method that already exists, so the two doors cannot come to disagree and both raise the same
  errors.
- **Runtime dispatch rules**, chosen rather than inherited: the create doors try the EXACT runtime
  type first and then assignability, so an EF proxy maps through its base while a mapper holding
  maps for both a base and a derived type still answers with the one registered for what it was
  handed. `Map<TSource, TDestination>(source)` falls through to the object door when `TSource` is
  not itself mapped, because generic library code binds `TSource` to whatever its own caller had.
  The UPDATE overload needs the exact declared pair — no fallback — since the destination handed
  in is the object being written to. `ProjectTo` is exact only, because a queryable's element type
  is fixed when it is created and there is no runtime value to look at.
- `CanMap` answers for the create doors and matches them rule for rule, subclasses included. A
  destination ShiftMapper cannot construct (SM0004) is absent from it, because nothing on the
  interface can produce one.
- `AddShiftMapper<TMapper>()` registers `IShiftMapper` alongside `TMapper`, resolving THROUGH it
  so both hand back one instance per scope. A mapper the generator produced nothing for cannot
  implement the interface, and registering one now throws at registration naming SM0005 rather
  than handing the application a mapper whose every call fails.
- 30 tests: nine on the emitted shape, twenty-one at runtime — including `PretendFramework`, a
  class written the way ShiftFramework has to write one, which reads, updates and projects
  without naming `TestMapper` anywhere.

Two things this step deliberately does NOT do. Where several mapped source types match a value by
assignability the first declared wins; choosing properly between them is Step 10, and a rule
invented here would be one to unpick there. And `IShiftMapper` has no collection overloads — those
arrive with Step 5, on the typed API first.

---

## Phase 2 — Close the mapping gaps

This is the "as usable as AutoMapper" work. Each step is independent of the others; the
order below is by how often the gap is actually hit.

### ✅ Step 5 — Collections and null policy at the top level

```csharp
mapper.Map<List<BrandDto>>(brands);      // does not exist today
mapper.Map<BrandDto[]>(brands);
mapper.MapOrNull<BrandDto>(maybeNull);   // Map throws on null, deliberately
```

- Generate collection overloads for every declared map (`IEnumerable<TSource>` to
  `List` / array / `HashSet` / `IReadOnlyList` of `TDestination`).
- Add `MapOrNull` for the case where a null source is ordinary data.
- Decide, document and make configurable the **null collection policy**: does a null source
  collection produce null, or an empty destination collection? (AutoMapper's
  `AllowNullCollections`. Default to empty, which is what `MappingHelpers.ToShiftFiles`
  already does in ShiftFramework.)
- Add `Dictionary<K,V>` / `IDictionary` / `IReadOnlyDictionary` to the collection builders in
  `ConversionResolver` and `ValueConverter`. Today a dictionary is SM0002.

**What landed.**

- **Collection overloads per map.** Three direct methods — `MapToBrandDtoList`,
  `MapToBrandDtoArray`, `MapToBrandDtoHashSet`, each taking any `IEnumerable<Brand>` — plus a
  `Map<TDestination>(IEnumerable<Brand>)` switchboard answering for `List<T>`, `T[]`,
  `HashSet<T>` and `IReadOnlyList<T>`, and extension spellings of both. `IReadOnlyList` gets no
  builder of its own because a `List` already is one. A shape nothing builds throws a message
  naming the four rather than guessing.
- **`MapOrNull`**, per map and as a switchboard, for the case where a missing source is ordinary
  data. Constrained to `class` and emitted only where BOTH sides are reference types: a struct
  destination has no null to return, and an unconstrained version would hand back `default` — a
  zero-filled struct indistinguishable from a mapped one.
- **`Dictionary` in the conversion table** as its own step, since a dictionary is not an
  `IEnumerable<T>` of anything the list builders can construct. Read from any
  `IEnumerable<KeyValuePair<K, V>>` (so `SortedDictionary` and `ConcurrentDictionary` feed one),
  built as a `Dictionary`, and keys and values convert independently. Converting the KEYS is the
  one thing a dictionary can lose that a list cannot — two source keys can arrive as one — so
  it takes SM0008 exactly as `ToHashSet` does, with the same last-one-wins behaviour rather than
  a throw halfway through building a DTO.
- **The null-collection policy**, `MapOptions.AllowNullCollections`, defaulting to FALSE, meaning
  a null source collection becomes an EMPTY destination collection. Per map or per mapper, read
  through the same `ReadOption` as `Matching`. It covers collections of values, collections of
  mapped objects, dictionaries, and the top-level collection overloads — so
  `Map<List<BrandDto>>(null)` answers the same question the same way instead of throwing. In
  memory it is one method name: `ValueConverter` grew an `OrEmpty` twin of every builder, whose
  return types are non-nullable, which is half the point of them.

**The projection half, which is the part that had to be learned rather than designed.** The first
version guarded every collection in the projection with `?? Enumerable.Empty<T>()`. It passed the
whole suite — and broke `GET /api/products` in the sample, because EF recognises a primitive
collection by the shape of the expression around it and a coalesce is a shape it cannot see
through. A `Select` over a JSON column that translated perfectly stopped translating, at run time,
to guard against a null the type said could not happen. The rule now:

- a NAVIGATION collection is never null in a projection, so it is never guarded;
- a collection of VALUES whose source property is declared NULLABLE is guarded, and EF turns that
  into a `COALESCE` in the SELECT list — verified against SQL Server in the sample and against
  SQLite in the suite;
- a collection of VALUES declared NON-nullable is not guarded. The in-memory map still is, because
  there it costs one null check and breaks nothing.

The nullable ANNOTATION is the test because it is the developer's own statement about the column,
and it is the same test `NestedProperty.SourceIsNullable` already applied to nested objects for
the same reason. `CollectionConversionTests.A_non_nullable_source_is_left_unguarded_so_EF_can_still_see_it`
pins it, and says why.

**Two consequences worth recording.** A bare `null` literal is now ambiguous at
`Map<BrandDto>(null)` and `MapOrNull<BrandDto>(null)`, because there is an overload per mapped
source type — an ordinary consequence of adding an overload, and a compile error where the old
behaviour was a runtime throw. And `IShiftMapper` still has no collection members: this step
delivers them on the typed API, as Step 4 said it would, and putting them on the interface is a
separate decision about a published contract.

**In the sample.** `Brand.Aliases` is a genuinely nullable primitive collection, null for six of
the eight seeded brands, and `GET /api/brands` (in memory) and `GET /api/brands/projected` (SQL)
return the same `[]` for every one of them. `GET /api/brands/{id}` uses `MapOrNull` for the row
that may not be there. `SupplierFeed` and `POST /api/supplier-feeds/preview` carry the dictionary
demonstration — values converted, keys converted (the live SM0008), and an absent dictionary
arriving as `{}` — on a pair with no table behind it, because that is where dictionaries
actually turn up.

### ⬜ Step 6 — Destinations that are not `new T { }`

`MapModel.CanConstructDestination` requires a public parameterless constructor, so today a
positional `record`, a DTO with a primary constructor, or one with `required` members cannot
be a destination at all — it is SM0004. Modern DTOs are exactly those shapes.

- Match constructor parameters to source properties by name (same matching rules as members),
  and emit `new BrandDto(source.Id, source.Name) { Other = ... }`.
- Support `required` and `init` members in the create path (`init` is already filled on create
  and skipped on update — extend rather than redo).
- Add `.ConstructUsing(s => new BrandDto(s.Id))` for the cases convention cannot reach.
- Diagnostic when a constructor parameter cannot be filled, naming the parameter.

This also unblocks projections into records, which EF handles fine.

### ⬜ Step 7 — Per-member power tools

Everything here is another method on `MemberOptions`, which is the shape `ForMember` was
built for. Each needs a query form as well as an in-memory form.

| API | In memory | In a projection |
|---|---|---|
| `opt.MapFrom("Customer.Name")` — string path | member walk | inline member access + null guard |
| `opt.MapFrom((s, d) => ...)` — destination-aware | fine | **not projectable** — diagnostic if the map is projected |
| `opt.Condition((s, d, v) => ...)` | `if` around the assignment | not projectable — diagnostic |
| `opt.PreCondition(s => ...)` | as above | as above |
| `opt.NullSubstitute(value)` | `?? value` | `?? value`, translates |
| `opt.UseValue(constant)` | constant | constant |
| `opt.ConvertUsing(converter)` | call | needs an expression form (see Step 12) |
| `opt.Order(n)` | assignment order | n/a |

The important design point: **a member option that cannot be translated must be a build-time
diagnostic on any map that is also projected**, not a runtime surprise. The generator already
knows which maps get a `ProjectTo`, so it can say so.

### ⬜ Step 8 — Map-level hooks

```csharp
CreateMap<Brand, BrandDto>()
    .BeforeMap((s, d) => ...)
    .AfterMap((s, d) => ...)
    .ConstructUsing(s => new BrandDto(...))
    .ConvertUsing(s => new BrandDto { ... })        // replaces the whole map
    .ForAllMembers(opt => opt.Condition(...));
```

- `BeforeMap` / `AfterMap` are in-memory only, and are what ShiftFramework's
  `DefaultEntityToDtoAfterMap` / `DefaultDtoToEntityAfterMap` need (see Step 15).
- `ConvertUsing` taking an `Expression<Func<TSource, TDestination>>` DOES project — this is
  the single most important entry in Phase 2 for Phase 3, because a global type converter is
  just a `ConvertUsing` that was registered globally.
- `ForAllMembers` is how ShiftFramework's "never write a nested entity back" rule is
  expressed.
- Emit a diagnostic when a map carrying an in-memory-only hook is used inside a projection —
  the hook will not run, and today nothing says so.

### ⬜ Step 9 — Flattening and naming conventions

AutoMapper maps `Order.Customer.Name` onto `OrderDto.CustomerName` with no configuration.
ShiftMapper reports SM0001 and stops.

- Add opt-in flattening: when a destination member has no direct match, split its name on
  PascalCase boundaries and walk the source graph. Emit a null-guarded chain
  (`s.Customer == null ? null : s.Customer.Name`) so it translates.
- Add `SourceMemberNamingConvention` / prefixes / postfixes to `MapOptions`
  (`RecognizePrefixes("Db")`, `RecognizePostfixes("Id")`).
- Make it OPT-IN per map or per mapper, defaulting off. Flattening that fires by surprise is
  how AutoMapper maps end up filling members nobody meant to fill, and this library's whole
  posture is to report rather than guess.

### ⬜ Step 10 — Inheritance, polymorphism, open generics

- `.IncludeBase<TSourceBase, TDestinationBase>()` — inherit a base map's members and its
  `ForMember` configuration. This is what makes "every entity to every DTO maps its audit
  fields the same way" expressible once.
- `.Include<TDerivedSource, TDerivedDestination>()` plus runtime type dispatch for in-memory
  maps; for projections, either a `case`-based expression or an explicit diagnostic saying it
  is not projectable.
- Open generic maps — `CreateMap(typeof(PagedResult<>), typeof(PagedResultDto<>))` — closed by
  the generator for every closed pair it can see in the compilation.
- Abstract / interface destinations with a registered concrete implementation.

`IncludeBase` is a prerequisite for Step 14. Do not skip it.

---

## Phase 3 — The general layer

**This is the part ShiftFramework is waiting for**, and it is the reason this plan exists.

The requirement, stated plainly:

> A LIBRARY — ShiftFramework — must be able to declare mapping rules ONCE, in its own
> assembly, and have every map in every application that references it pick them up
> automatically, in memory and inside EF projections, without the application repeating
> anything and without ShiftMapper knowing what those rules are about.

The concrete rules ShiftFramework has today, all currently hand-rolled in
`DefaultAutoMapperProfile`, `AutoMapperExtensions` and `MappingHelpers`:

- `string` (a JSON column) to and from `List<ShiftFileDTO>`
- any `ShiftEntityBase` to `ShiftEntitySelectDTO`, using the `[ShiftEntityKeyAndName]`
  attribute on the entity to decide which members are `Value` and `Text`
- `ShiftEntitySelectDTO` to a `long` / `long?` foreign key, by the `{Member}ID` convention
- hash ids: `long ID` to `string ID`, and back
- audit/base fields (`ID`, `IsDeleted`, `CreateDate`, `LastSaveDate`, `CreatedByUserID`,
  `LastSavedByUserID`) on every view DTO, and `ID` + `IsDeleted` on every list DTO — and the
  list ones must be **inside the projection**, which is precisely what
  `MappingHelpers.MapBaseListFields` cannot do today
- "never write a navigation entity back from a DTO" (`ForAllMembers` plus a condition)
- entity to same-entity copy excluding `ID` and audit fields (`CopyEntity`)

Phase 3 is done when ShiftFramework can express all of that in ShiftMapper's own vocabulary
and delete its bespoke mapper generator.

### ⬜ Step 11 — Profiles: maps declared outside the mapper class

Today every map lives in one class's constructor. A framework cannot add to it.

```csharp
public class ShiftEntityProfile : ShiftMapperProfile
{
    public ShiftEntityProfile()
    {
        CreateMap<Brand, BrandDto>();
    }
}

public partial class AppMapper : ShiftMapperBase
{
    public AppMapper() => AddProfile<ShiftEntityProfile>();
}
```

- `ShiftMapperProfile` gets the same `CreateMap` surface as `ShiftMapperBase`.
- The generator resolves `AddProfile<T>()` and pulls the profile's maps into the mapper it is
  generating — **when the profile is in the same compilation**. A profile in a REFERENCED
  assembly is the hard case, and is Step 13.
- Profiles are how an application splits a large mapper across files; they are also the
  mental model everyone arriving from AutoMapper already has.
- The mapper class is already `partial`, so a second generator (ShiftFramework's, or a
  scaffolder) can contribute `CreateMap` calls as generated source. State that as a supported
  extension route and test it.

### ⬜ Step 12 — Global type-pair converters

The keystone. One registration, applied to every map, everywhere, in both backends.

```csharp
public class ShiftEntityProfile : ShiftMapperProfile
{
    public ShiftEntityProfile()
    {
        // any member of type string feeding a member of type List<ShiftFileDTO>, anywhere
        CreateConversion<string?, List<ShiftFileDTO>?>(
            memory: json  => MappingHelpers.ToShiftFiles(json),
            query:  json  => ShiftJson.Files(json));      // or: declare it not projectable

        CreateConversion<List<ShiftFileDTO>?, string?>(
            memory: files => MappingHelpers.ToJsonString(files));
    }
}
```

Requirements:

- A conversion is looked up by `(sourceType, destinationType)` and consulted by
  `ConversionResolver` **before** it gives up and reports SM0002. Framework conversions extend
  the built-in table rather than replacing it; a map-level `ForMember` still wins over both.
- **Two forms.** `memory` is a `Func<>` (or a method group) used by the `Map` methods. `query`
  is an `Expression<Func<>>` spliced into the projection. Supplying only `memory` is legal and
  means "this pair cannot be projected" — and then any map that uses the pair AND is projected
  gets a build diagnostic naming the pair and the member. That diagnostic is the whole value
  of doing this at compile time.
- Applies transitively: element types of collections, and nested members, use the same table.
- Registrable for a base type: a conversion declared for `ShiftEntityBase` to
  `ShiftEntitySelectDTO` must fire for `Brand` to `ShiftEntitySelectDTO`. Assignability, not
  exact type identity.

This one API covers the `ShiftFileDTO` case and the hash-id case outright.

### ⬜ Step 13 — The compile-time extension contract for referenced assemblies

Steps 11 and 12 work when the profile is source in the same compilation. ShiftFramework's is
not — it arrives as a compiled DLL, and a source generator sees referenced assemblies as
METADATA ONLY. It cannot read the framework's constructor body, and it cannot execute the
framework's code. So the framework's global configuration has to be expressed in something
that survives into metadata.

**Mechanism: attributes on public types in the framework assembly.** The generator walks
`compilation.SourceModule.ReferencedAssemblySymbols`, collects the declared rules, and folds
them into the same tables Step 12 fills from source.

```csharp
// in ShiftFramework, once
[assembly: ShiftMapperConversions(typeof(ShiftEntityConversions))]

[ShiftMapperConversions]
public static class ShiftEntityConversions
{
    // in-memory form: matched by signature (TSource) -> TDestination
    public static List<ShiftFileDTO>? ToFiles(string? json) => MappingHelpers.ToShiftFiles(json);

    // query form for the SAME pair, matched by return type Expression<Func<TSource, TDestination>>
    [ShiftMapperQueryForm]
    public static Expression<Func<string?, List<ShiftFileDTO>?>> ToFilesQuery => json => ...;
}
```

- Each `public static` method on a `[ShiftMapperConversions]` type with exactly one parameter
  declares a conversion for `(parameterType, returnType)`.
- A `[ShiftMapperQueryForm]` member supplies the projection form for the same pair.
- The generated code CALLS these methods directly — fully qualified, no reflection, no runtime
  registry lookup on the hot path. The framework's rule ends up inlined in the application's
  generated mapper exactly as if the developer had written it.
- Version the contract: a `[ShiftMapperContract(1)]` assembly attribute, so a newer generator
  can reject or adapt an older framework package instead of emitting code that will not
  compile.
- Diagnostics: two conversions for the same pair from two different assemblies is an error
  naming both; a query form whose signature does not match its memory form is an error.

**Also add the runtime mirror**, for the `IShiftMapper` reflection path and for anything
resolved at runtime: a `ShiftMapperConversionRegistry` populated by module initializers.
Compile time is the fast path; the registry is the fallback, and the two must be built from
the same declarations so they cannot disagree.

**Explicitly rejected alternatives**, recorded so this is not relitigated later:

- *The framework ships its own Roslyn analyzer that ShiftMapper's generator loads.* Powerful,
  but analyzer load order and version skew make it fragile, and it is close to what ShiftEntity
  already does today — which is the thing being replaced.
- *Runtime-only configuration, generator ignores it.* Kills `ProjectTo`, which is the whole
  reason to use this library over hand-written code.
- *The framework emits the entire mapper itself.* That is `ShiftEntityMapperGenerator` today,
  and the goal is to delete it, not to re-derive it.

### ⬜ Step 14 — Declarative member conventions

Type-pair conversions (Steps 12 and 13) handle "this type becomes that type". They cannot
express ShiftFramework's select-DTO rule, which is member-SHAPED:

> destination member `Brand` of type `ShiftEntitySelectDTO` is filled from source member
> `BrandID` (the value) plus the source navigation `Brand`'s key/name members (the text), and
> on the way back `BrandID` is filled from `Brand.Value`.

Today `AutoMapperExtensions` does this reflectively in an `AfterMap`, which means it cannot
appear in a list projection at all — so ShiftEntity's generator inlines a member-init instead.
That split is exactly what a proper convention vocabulary removes.

Add a small, declarative, compile-time-readable vocabulary — deliberately NOT a general
callback, because the generator cannot execute framework code:

```csharp
[ShiftMapperMemberConvention(
    DestinationType  = typeof(ShiftEntitySelectDTO),
    SourceMemberName = "{Member}ID",                    // BrandID for destination member Brand
    ValueFrom        = "{Member}ID",
    TextFrom         = "{Member}.{KeyAndName.Text}",    // resolved via an attribute on the entity
    Direction        = MappingDirection.Both)]
```

The pieces the vocabulary needs, and no more:

- a destination type, or a destination-type-assignable-to filter
- a source member NAME PATTERN (`{Member}ID`, prefix and suffix forms)
- member paths for the sub-members of the destination (`Value`, `Text`)
- an attribute-driven indirection: "read the member named by `[ShiftEntityKeyAndName].Text` on
  the source type" — this is what makes the rule work for every entity without the framework
  listing them
- a direction (read, write, both)
- an ordering/priority against convention matching and against explicit `ForMember`
  (`ForMember` always wins)

Keep this deliberately small. If a rule cannot be expressed here, the framework writes a
`ForMember` in a profile, or contributes generated source (Step 11). The vocabulary is for the
rules that must apply to types the framework has never seen.

**Done when** an application declares `CreateMap<Brand, BrandListDTO>()` and the generated
projection contains an inline `Brand = new ShiftEntitySelectDTO { Value = ..., Text = ... }`
member-init that SQL translates, with nothing written in the application.

### ⬜ Step 15 — What ShiftFramework then builds (checklist, not ShiftMapper work)

Tracked here so Phase 3 can be validated against a real consumer. All of it lives in the
ShiftEntity repository, none of it in ShiftMapper.

1. `ShiftEntityMappingProfile` in `ShiftEntity.Core`, registering via Steps 12–14:
   - `string` to and from `List<ShiftFileDTO>` (both forms; the query form is the interesting
     one)
   - `ShiftEntityBase` to `ShiftEntitySelectDTO` via `[ShiftEntityKeyAndName]`
   - `ShiftEntitySelectDTO` to `long` / `long?` FK, with the existing 400-on-bad-input
     behaviour of `MappingHelpers.ToForeignKey` preserved on the in-memory path
   - hash ids, `long` to and from `string`
2. Base maps via `IncludeBase` (Step 10) for `ShiftEntity` to `ShiftEntityViewAndUpsertDTO` and
   `ShiftEntity` to `ShiftEntityListDTO`, replacing `MapBaseFields` / `MapBaseListFields` — and
   making the list one projectable, which it is not today.
3. The `{Member}ID` select-DTO convention (Step 14), replacing `DefaultEntityToDtoAfterMap` /
   `DefaultDtoToEntityAfterMap` and their reflection.
4. `ForAllMembers` condition (Step 8) replacing the "never write a `ShiftEntityBase` back"
   rule.
5. `CreateMap<TEntity, TEntity>()` with `ID` / `ReloadAfterSave` / `AuditFieldsAreSet` ignored,
   replacing `MappingHelpers.ShallowCopyTo` and its per-property reflection.
6. An adapter from `IShiftMapper` (Step 4) to `IShiftEntityMapper<TEntity, TListDTO, TViewDTO>`,
   replacing `AutoMapperShiftEntityMapper`. Note the two hard contract requirements
   `IShiftEntityMapper.MapToList` documents: the projection MUST bind `ID` and `IsDeleted`,
   because the OData pipeline filters the already-projected DTO queryable. Add a
   ShiftFramework-side diagnostic for that; it is a framework rule, not a ShiftMapper rule.
7. Map discovery. ShiftEntity currently finds its triples by scanning assemblies at runtime for
   `ShiftRepository<...>` subclasses. A compile-time mapper cannot do that, so ShiftFramework
   emits `CreateMap` declarations for each discovered triple as generated source (Step 11) —
   which is the ONLY part of `ShiftEntityMapperGenerator` that survives, and it shrinks from
   roughly 2,100 lines to a few dozen.
8. Delete `DefaultAutoMapperProfile`, `AutoMapperExtensions`, `AutoMapperShiftEntityMapper`, and
   the mapping half of `ShiftEntityMapperGenerator`. Keep `MappingHelpers` — its conversion
   methods are exactly what Step 13 registers.
9. Port `ShiftEntity.Tests/Mapping/*` onto the new stack. Those tests encode a lot of hard-won
   behaviour (write asymmetry, FK guards, member gating) and are the best acceptance suite
   Phase 3 could have.

---

## Phase 4 — Finish

### ⬜ Step 16 — Diagnostics and analyzer completeness

- `CreateMap` called somewhere the generator cannot read it (inside an `if`, a loop, a ternary,
  a helper method) currently generates NOTHING and says nothing. ShiftEntity's generator
  learned this the hard way and reports SHENGEN005 / SHENGEN009. ShiftMapper needs the same:
  **configuration the generator cannot bake must be an error, never a silent default.**
- Mapper class not `partial`, mapper generic, mapper nested in a non-partial type — reported as
  SM0005 today with a reason; verify each path has a test (Step 1).
- Diagnostic when a map is only ever `ProjectTo`'d but uses an in-memory-only feature, and vice
  versa.
- Code fixes for the common ones: SM0001 offers `opt.Ignore()`; SM0011 offers the missing
  `CreateMap`.
- ~~Move reporting into a `DiagnosticAnalyzer` (Step 3) so `.editorconfig` works.~~ Done in
  Step 3 — `ShiftMapperAnalyzer` reports all twelve rules and the generator reports none. What is
  left here is the CODE FIXES, which need `Microsoft.CodeAnalysis.CSharp.Workspaces` and a
  second assembly under `analyzers/dotnet/cs`, since a code-fix provider must not be loaded into
  the compiler's own analyzer context.

### ⬜ Step 17 — Docs and sample

- `README.md`, plus a `docs/` folder: getting started, the conversion table, the diagnostics
  reference (one page per SM id, which is what people search for), and the extension-points
  page for library authors (Steps 11–14) — that last one is the document ShiftFramework will be
  written from.
- An **AutoMapper migration guide**: a two-column table of every AutoMapper API against its
  ShiftMapper equivalent, and an honest list of what has no equivalent and why.
- Extend `ShiftMapper.Sample` with a records DTO, a flattened DTO, and a small "pretend
  framework" project that registers a global conversion through Step 13 — so the extension
  contract is exercised by the sample, not only by the tests.

### ⬜ Step 18 — Benchmarks

BenchmarkDotNet against AutoMapper and Mapperly: single map, nested graph, 10k collection, and
a `ProjectTo` query-shape comparison. Publish the numbers in the README. A source-generated
mapper that cannot show its numbers has given up its main argument.

---

## Ordering summary

| Phase | Steps | State | Blocking? |
|---|---|---|---|
| 1 — Trust | 1 Tests, 2 Runtime cost, 3 Packaging, 4 `IShiftMapper` | ✅ done | Everything depended on 1 and 4 |
| 2 — Gaps | ~~5 Collections~~, 6 Constructors/records, 7 Member options, 8 Map hooks, 9 Flattening, 10 Inheritance/generics | ⬜ 5 done | 8 and 10 block Phase 3 |
| 3 — General layer | 11 Profiles, 12 Global conversions, 13 Compile-time contract, 14 Member conventions, 15 ShiftFramework port | ⬜ pending | The goal |
| 4 — Finish | 16 Diagnostics, 17 Docs, 18 Benchmarks | ⬜ pending | Can run alongside 2 and 3 |

The shortest path to ShiftFramework being able to adopt this is
**1 → 4 → 8 → 10 → 11 → 12 → 13 → 14 → 15**; with 1 and 4 done, it starts at **8**. Steps 5, 6, 7
and 9 are needed for ShiftMapper to be a good general-purpose mapper, but they are not on
ShiftFramework's critical path — 5 is done because it is the one of those four that every list
endpoint hits on its first day.
