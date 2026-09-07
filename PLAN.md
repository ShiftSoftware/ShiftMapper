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
- [x] **Step 6** — Destinations that are not `new T { }`
- [x] **Step 7** — Per-member power tools
- [x] **Step 8** — Map-level hooks
- [x] **Step 9** — Flattening and naming conventions
- [x] **Step 10** — Inheritance, polymorphism, open generics

**Phase 3 — The general layer**

- [x] **Step 11** — Profiles: maps declared outside the mapper class
- [x] **Step 12** — Global type-pair converters
- [x] **Step 13** — The compile-time extension contract for referenced assemblies
- [ ] **Step 14** — Declarative member conventions
- [ ] **Step 15** — What ShiftFramework then builds (ShiftEntity repository, not this one)

**Phase 4 — Finish**

- [ ] **Step 16** — Diagnostics and analyzer completeness
- [ ] **Step 17** — Docs and sample
- [ ] **Step 18** — Benchmarks

Phases 1 and 2 are complete, and Steps 11, 12 and 13 with them. Next on ShiftFramework's
critical path is **Step 14**, then 14 → 15 (the summary at the foot of this file).

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
- Twenty-one build-time diagnostics, SM0001–SM0021.
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
- **(Step 6)** Destinations built through a CONSTRUCTOR — positional records, primary
  constructors, `required` members — in memory and in a projection; plus `ConstructUsing` for
  what convention cannot reach.
- **(Step 7)** `MapFromSource`, which puts a customized member back through the conversion table;
  `Condition`, which turns the update overload from a PUT into a PATCH; and a per-member delegate
  cache that takes the customization lookup off the per-object path.
- **(Step 8)** The map-level hooks: `ConvertUsing`, which replaces the whole map AND projects;
  `BeforeMap` / `AfterMap`, which do not; and `ForAllMembers`, which says one rule once.
- **(Step 9)** Opt-in FLATTENING — `OrderDto.CustomerName` from `Order.Customer.Name`, in memory
  and as a join — plus `RecognizePrefixes` / `RecognizePostfixes`.

### What is missing, in one paragraph

There is no `NullSubstitute`,
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

### ✅ Step 6 — Destinations that are not `new T { }`

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

**What landed.**

- **A constructor plan per map.** A public PARAMETERLESS constructor still always wins, so nothing
  that already mapped changed shape. Otherwise the public constructors are tried GREEDIEST FIRST
  and the first whose every parameter can be filled is taken — a constructor exists to be given
  values, so a type offering both `(int, string)` and `(int)` means the shorter one for callers
  who have less, not for a mapper that has both. A record's copy constructor is skipped by shape.
- **A CONSTRUCTOR PARAMETER IS A DESTINATION MEMBER** that happens to be written inside the
  parentheses. It matches a source property by name, converts by the same table, maps a nested
  object when a `CreateMap` exists, and an `opt.Ignore()` leaves it `default`. A `ForMember`
  naming the member it stands for FILLS it — which on a positional record is the only way to
  customize anything, since the property is init-only and the constructor has already set it.
- **Parameter-to-property matching always ignores case**, unlike source matching, which still
  follows the map's own `PropertyMatching`. A primary constructor's `id` backing a property `Id`
  is a C# convention, not a mapping decision, and a developer who asked for case-sensitive SOURCE
  matching did not thereby ask for their own constructor to stop being recognised.
- **Records project**, which is the whole reason this was worth doing. The generator writes the
  `new` itself, so EF is handed one construction with real arguments and produces the same query
  it produces for an ordinary class — verified against SQL Server in the sample, where
  `/api/products/summary` and `/api/products` emit the same joins and the same columns.
- **`required` members.** Not a property left empty: C# REFUSES an object initializer that omits
  one, so an unmapped required member stops the destination being built at all. SM0014 says which,
  rather than leaving a CS9035 inside a generated file. `[SetsRequiredMembers]` is taken at its
  word, and a required member the constructor also fills is bound in the initializer anyway,
  because the C# compiler does not accept a constructor as having filled one without that
  attribute.
- **`ConstructUsing`**, kept as an expression tree in the developer's file exactly as `MapFrom` is,
  and cached on the same terms — per instance when it captured a service, per process when it did
  not. It replaces CONSTRUCTION and nothing else, so the members that can still be assigned still
  are, and the generated `<remarks>` names the init-only ones it leaves to the expression.
- **No update overload where there is nothing to update.** A positional record's every property is
  init-only, so the method would return the object it was handed having done nothing. A compile
  error at the call site is the better of the two answers.
- **Three new diagnostics** — SM0013 (parameter cannot be filled, naming it), SM0014 (required
  member not mapped), SM0015 (ConstructUsing is not projectable) — and SM0004 reworded, since
  "no public parameterless constructor" stopped being the interesting case. It now means what is
  left: an abstract type, an interface, nothing public to call.

**Two things the projection needed that the design did not predict.**

*A constructor argument cannot be left out and added later.* A member binding can be appended to an
initializer after the fact, which is how `MapFrom` and nested maps have always reached a
projection; an argument cannot, because the call would not be a call. So the generator writes
`default(T)!` in every position it cannot spell and names it, and `Compose` gained an overload that
rebuilds the `NewExpression` with the real trees in those positions. It also had to learn that a
bare `NewExpression` is a valid projection body — a record with nothing left to initialise is
exactly that.

*A `required` member filled by a `MapFrom` broke the template.* A customized member is normally
ABSENT from the generated projection, since Compose splices the real tree in at runtime. A required
one cannot be absent from a template that is itself compiled: C# refuses the initializer, in a file
the developer cannot edit. It now gets a `Member = default!` placeholder that Compose drops on its
way to binding the real thing. The sample's `/api/invoices/{id}/receipt` returns both backends side
by side so a regression there would be visible rather than silent.

**Deliberately not done.** `ConstructUsing` does not project, and the build says so as SM0015
rather than leaving it to be discovered. A projection has to reach EF as one expression it can read
all the way down, and there is no general way to graft mapped properties onto an object a delegate
returned. The generator emits a projection that THROWS with that explanation rather than none at
all, because a missing projection member becomes a CS0103 the moment another map nests this one.
Making it projectable needs `Compose` to assemble a member-init from parts, which is also what
Step 8's `ConvertUsing` needs — so it belongs there.

**In the sample.** `ProductSummaryDto` and `BrandSummaryDto` are positional records nested one
inside the other, projected by `GET /api/products/summary` (`?sql=true` to compare the query with
the ordinary-class one). `InvoiceReceiptDto` carries three `required` members, one of them also
customized, and `GET /api/invoices/{id}/receipt` returns the in-memory and projected results side
by side with a `totalsAgree` flag. `InvoiceLabelDto` is built by a `ConstructUsing` reading the
injected numbering service, and `?project=true` shows what the refusal reads like.

### ✅ Step 7 — Per-member power tools

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

**What landed — and it is not the table above.** Four agents researched the eight rows against the
codebase and against the real compiler, and most of the table did not survive contact. Three rows
turned out to add no capability, two to be strictly weaker than a neighbour, and the one thing the
step most needed was not on the list at all. What shipped:

**1. `opt.MapFromSource<TValue>(Expression<Func<TSource, TValue>>)` — the row that was missing.**

`MapFrom`'s expression must return `TProperty`, the DESTINATION member's type, fixed by the
`ForMember` selector. So `opt.MapFrom(s => s.Lines.Count)` onto a `string` does not compile, and
the developer converts by hand — which hand-writes the conversion this library exists to write,
takes the member out of `ConversionResolver`'s hands so SM0002/SM0008/SM0009/SM0010 stop being
reported for it, and loses the invariant-culture rule.

**That had already bitten this repository.** Step 6's own sample line read
`.MapFrom(s => s.Lines.Sum(...).ToString("0.00"))` — no format provider, so the receipt total
left a German server as `"1596,00"`, in a library whose `ValueConverter` documents in bold that
text never depends on the machine, and which gets it right two maps up by convention.

`MapFromSource` hands the value over and the table writes the conversion, in BOTH backends: the
query spelling travels into the projection as a one-parameter lambda the generated file writes
down, which `Compose` SPLICES onto the developer's tree rather than invoking (an `Invoke` would
hand EF a delegate it cannot see inside).

**A second NAME, not an overload, and that is the load-bearing decision.** An overload compiles
clean and then crashes. `opt.MapFrom(s => s.Rank)` onto a `long` member works today because the
implicit `int`→`long` conversion happens INSIDE the tree; a generic overload is the better match
and captures it, and the generated `Value<..., long>` cast then throws `InvalidCastException` on
the first mapped object. Thirteen of this repository's thirty-three `MapFrom` calls move that way,
and `int?`→`int` re-binds so that `Map` throws while `ProjectTo` succeeds — the exact
"silently works in memory and throws in a query" this plan's opening forbids. A distinct name
cannot re-bind anything.

**2. `opt.Condition((s, d, value) => ...)` — the row the table was right about.**

The most valuable of the eight, and the argument is concrete: `Map(source, destination)` is
unconditionally a PUT. Every mapped member is assigned every time, so a PATCH whose body omits a
field arrives with `""` and `0` and overwrites real data — including, in the checked-in generated
file, `Parse<int>("")` writing `0` over a tracked entity's primary key.

Think of it as a RUNTIME `Ignore`: `Ignore` decides once at build time that a member is not
mapped, `Condition` decides per object, and a declined member is LEFT ALONE rather than set to
`default`. Everything else about it is unchanged — name match, generated conversion, diagnostics.

Three things it needed that were not obvious:

- **A third create shape.** A conditioned member cannot be in an object initializer, so the create
  method builds the object without it and then assigns it behind the guard. "Left untouched" on a
  create therefore means the property's OWN initializer value, which is what AutoMapper does too.
- **A type witness.** The generator does not know a member's declared type — the models it caches
  hold names and conversion templates, not types — so it cannot write
  `Condition<Src, Dest, string>(...)`. Passing `destination.Member` alongside the candidate lets the
  compiler infer it, and best-common-type lands on the DECLARED type every time: a `long` member
  fed an `int` infers `long`, an `IReadOnlyList<T>` fed a `List<T>` infers the interface. That is
  the type the predicate was registered under, so the cast inside is exact. It is also the free
  upgrade path to AutoMapper's four-argument form.
- **Braces.** Two conditioned members each declare a local called `value`; without a scope that is
  CS0128.

**SM0016 (Error)** for a member whose value is settled during construction — `init`-only,
`required` in an object initializer, a constructor argument. The generator still emits the member
UNCONDITIONED, because an analyzer error does not stop the generated file being compiled in the
same pass and a project with analyzers off must not get a CS error in a file it cannot edit.
Note the qualifier on `required`: on a `ConstructUsing` map the generator writes no `new` at all,
so the same member IS conditionable there.

**SM0017 (Warning)** because the map loses its projection. A `MemberInit` cannot leave a binding
out per row, so this is genuinely binary: the whole projection goes. A WARNING where SM0015 is a
note, and the severity is the argument — `ConstructUsing`'s projection THROWS, loudly; a
condition's would not, because `Compose` never sees one, so the projection would bind
unconditionally and quietly return different data from `Map`, per row, in a list endpoint.

**3. A per-member delegate cache, which replaced two rows of the table.**

`MapFrom("Product.Name")` and `UseValue(constant)` were both really asking for one thing: let the
generator SEE the value at compile time so the per-object `Customizations.Value` lookup disappears.
Measured, that lookup is ~96 ns per customized member per mapped object, and per element of a
nested collection.

But **0 of the 13 `MapFrom` bodies in this repository would fold** — they are arithmetic, `Sum`s,
concatenation, service reads. And hoisting the lookup into a lazily-initialised field, exactly as
Step 2 did for the projections, measured 96 ns → 7.7 ns: **92% of the win, on 100% of the
`MapFrom`s**, with no literal renderer, no path predicate, no null-guard policy, no `Compose`
contract change and no new failure mode. The field is per INSTANCE, not static, because `Value`
returns a delegate compiled for THIS mapper when the expression captured its services.

**Deliberately not shipped.** `MapFrom(string path)` and `UseValue` add no capability —
`opt.MapFrom(s => s.Product.Brand.Name)` and `opt.MapFrom(s => "USD")` already work in both
backends and produce identical SQL (measured), and a string path is not type-safe, not renameable
and cannot be navigated. `PreCondition` is strictly weaker than `Condition`. `Order` is a modifier
for a destination-aware `MapFrom` that does not exist, and the generator could infer it anyway by
topologically sorting destination reads. `ConvertUsing`'s object form is worse than a static method
called from a `MapFrom`: same tree, plus a `ConstantExpression` EF compares by reference, so a
non-singleton converter costs a query recompile per request — the EXPRESSION form is what Step 12
needs and is where it belongs. `MapFrom((s, d) => ...)` is real and unbuilt: it wants `Condition`'s
"do not assign when blank" semantics for its own motivating example, and its ordering contract is
most of what `Order` was for.

**One correction to this step's own text.** "The generator already knows which maps get a
`ProjectTo`, so it can say so" is false. `AppendProjectionMember` runs for EVERY map; the generator
knows it EMITTED a projection, not that anyone calls one, and the analyzer is scoped to the mapper
class and never sees a call site. The workable answer is the SM0015 precedent: report
unconditionally, choose the SEVERITY by consequence, and emit a projection member that throws with
a written explanation rather than none at all — a missing member is a CS0103 the moment another
map nests this one.

**A finding the sample surfaced, worth recording.** Converting a COMPUTED `decimal` to text
diverges between the backends, and not because of anything ShiftMapper does: EF writes
`CAST([Quantity] AS decimal(18,2)) * [UnitPrice]`, so SQL multiplies scale 2 by scale 2 and gets 4
where C# gets 2 — `"1596.0000"` against `"1596.00"`. The old `ToString("0.00")` hid it by pinning
both sides to two places. Money stays a `decimal` in the sample now, and `LineCount` (`int` to
text, which cannot drift) carries the `MapFromSource` demonstration.

**In the sample.** `InvoiceReceiptDto.LineCount` is `MapFromSource`, and
`GET /api/invoices/{id}/receipt` returns both backends side by side with an `agree` flag.
`BrandPatch` and `PATCH /api/brands/{id}` are `Condition`: send `{ "country": "Ireland" }` and
watch the name, ISO code and founded year survive the blanks that would have overwritten them.

### ✅ Step 8 — Map-level hooks

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

**What landed.** `ConstructUsing` arrived early, in Step 6, so this step is the other four — and
they turn out to divide on ONE question, which is the thing worth taking away from it:

> **Can it be an EXPRESSION?** A projection is one expression handed to the database. Anything that
> needs a STATEMENT cannot be in one.

| Hook | What it replaces | Projects? |
|---|---|---|
| `ConvertUsing` | the WHOLE map | **yes** — it is already an expression |
| `ConstructUsing` | construction only | no (SM0015, info) |
| `BeforeMap` / `AfterMap` | nothing; adds a statement | no (SM0018, warning) |
| `ForAllMembers(opt.Condition)` | nothing; guards every assignment | no (SM0017, warning) |

**`ConvertUsing`, and it is as important as this step's own bullet said.** The expression IS the
map: no member is matched, converted or reported, so a destination the developer is building by
hand does not produce an SM0001 per property. And because a tree is exactly what a projection
needs, `ProjectTo` returns it UNCHANGED — nothing is composed into it, because there is nothing to
merge. Verified against SQL Server in the sample:
`SELECT [b].[Name] + N' (' + [b].[ISOCode] + N')' AS [Label]`. That is the shape Step 12's global
conversion table is: a conversion registered once for a type PAIR is a `ConvertUsing` declared
somewhere else, and it is worth nothing to a list endpoint unless it reaches SQL.

Two consequences worth stating. It has **no update overload** — `Map(source, destination)` promises
to fill the object it was handed and give it back, and an expression that builds a new one cannot,
so the method is not generated and calling it is a compile error. And anything else on such a map
does nothing, which **SM0019** names rather than leaving to look configured: a `ForMember` written
above a `ConvertUsing` reads as though it refines the map, refines nothing, and leaves no trace at
runtime to work backwards from.

**`BeforeMap` / `AfterMap`, and one thing the first attempt got wrong.** "Before" has to mean
something, and on a create it very nearly did not: the destination must EXIST to be handed over, so
the hook ran after the object initializer — which is every convention-mapped member. `BeforeMap`
would then have differed from `AfterMap` only in which conditioned members had run, a distinction
nobody could use. The fix is to move every member the map can assign afterwards OUT of the
initializer when a `BeforeMap` is declared, which is the same build-then-assign shape `Condition`
already introduced. What construction genuinely settles — constructor arguments, `init`-only and
`required` members — is still settled, and that is the documented limit rather than a gap. A test
asserts it: the hook sees the property's own initializer value, not the mapped one.

**SM0018 is a WARNING, not SM0015's note,** and the severity is the argument. `ConstructUsing`'s
projection throws the moment it is asked for, so nobody is misled. A hook's would not: `Compose`
has no idea a hook exists, so the projection would be built, run, and hand back rows the hook never
touched — silently, per row, with `Map` and `ProjectTo` disagreeing about the same map.

**A hook is an `Action` the generator cannot see inside**, so it does not know which members the
hook fills, and reports them as unmapped — correctly. The answer is `opt.Ignore()` on those
members, which is what it has always meant: this is the pattern, and both the sample and the test
model use it deliberately rather than working around it.

**`ForAllMembers` got its own options type** rather than reusing `MemberOptions`, and that is the
decision worth recording. A blanket `MapFrom` has no meaning — there is no expression that fills
every member — and a blanket `Ignore` is a map that maps nothing. Offering them and then reporting
them would be a diagnostic where a TYPE will do, so `AllMemberOptions<TSource, TDestination>` offers
one method and there is nothing to get wrong. It also avoids a real hazard: a `MapFrom` registered
under a wildcard member name would reach `Compose`, which would try to bind a member called `*` and
throw at runtime.

Its value is an `object`, since one predicate serves members of every type, and that boxing is the
price of saying the rule once. A member's own `Condition` always wins, never both; members that
cannot be guarded at all are SKIPPED rather than refused, unlike naming one individually, which is
SM0016 — a rule about everything is understood to apply where it can, and a rule about one member
is a statement about that member.

**In the sample.** `BrandLabelDto` is a `ConvertUsing` map and `GET /api/brands/labels?sql=true`
shows it reaching SQL. `InvoiceLabelDto.Display` is an `AfterMap`, and it is the case the hook
exists for: derived from the FINISHED destination — the factory's label plus the mapped customer
name — which no `MapFrom` could produce. And the `BrandPatch` map's four conditions collapsed into
one `ForAllMembers` plus one typed `ForMember` for `FoundedYear`, which demonstrates the precedence
rule and why the typed form still earns its place: "blank" for a number is `0`, not an empty
string.

### ✅ Step 9 — Flattening and naming conventions

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

**What landed.**

- **`o.Flattening`**, per map or per mapper through `ConfigureDefaults`, with the same three-step
  precedence every other option has.

  **ON by default, which is a deliberate departure from this step's own third bullet.** That bullet
  argued for opt-in on the grounds that flattening fires by surprise. The measurement said
  otherwise: turning it on across the sample's thirteen maps produced BYTE-IDENTICAL generated code,
  because flattening only ever runs where the direct match already failed — it cannot change a map
  that already compiles clean, only fill something that was SM0001. What is left of the original
  worry is that a VISIBLE SM0001 warning becomes an INVISIBLE SM0020 note, and the answer to that is
  SM0020 itself: it names every member and the path chosen, and `.editorconfig` raises it. Set
  `o.Flattening = false` for the stricter behaviour.
- **The search.** The destination name is split on PascalCase boundaries — acronyms kept whole, so
  `BrandISOCode` is `Brand` + `ISO` + `Code` — and then re-joined EVERY way that resolves. Each
  step is matched by the map's own `PropertyMatching` and its recognized prefixes and postfixes, so
  flattening reuses the rules the direct match already used rather than inventing a second set.
- **It never competes with a real property.** Flattening runs only where the direct match has
  already failed, so turning it on cannot change what an existing map does — it can only fill
  something that was SM0001 before. That is what makes it safe to switch on for a whole mapper.
- **The leaf goes through `ConversionResolver`**, so a walked `decimal` fills a `string` member with
  no extra configuration and carries SM0008/SM0009/SM0010 with it.
- **It projects**, which is the half worth having. The chain reaches EF as one expression and
  becomes a join: the sample's `/api/invoices/lines/flat` is one query, three joins, eight columns.
- **Constructor arguments flatten too**, so a record summary DTO taking a `string CustomerName`
  works — a parameter is a destination member written inside the parentheses, as Step 6 settled.
- **`RecognizePrefixes` / `RecognizePostfixes`** widen a plain match and apply to each step of a
  walk. METHODS rather than properties, because what the generator needs is the ARGUMENT LIST, and
  a list of string literals in a call is the shape it reads most reliably.
- **Two new diagnostics.** SM0020 (Info) names every member flattening filled AND the path it took,
  so an opt-in guess can be read back rather than trusted; SM0021 (Warning) reports a name that
  resolves more than one way, and maps nothing.

**The three refusals, which are most of the design.**

*It will not walk into a `string`.* `NameLength` quietly becoming `Name.Length` is the surprise
every flattening mapper is remembered for. Collections and nullable value types are out for a
related reason: neither has one traversal the generator could pick, and a `ForMember` says it
better than anything that could be invented. A non-nullable value type IS walkable, so
`CreatedAtYear` from `CreatedAt.Year` works and translates.

*Two paths is a question, not a tie to break.* The walk collects EVERY resolution rather than
returning the first, because finding a second one is the point. A source carrying both `Order`
(with a `CustomerName`) and `OrderCustomer` (with a `Name`) makes `OrderCustomerName` genuinely
ambiguous, and picking the leftmost split would be exactly the silent guess this library exists not
to make — the same answer SM0007 gives two source names differing only by case.

*The null guard is tied to the ANNOTATION, not added everywhere.* A step the model declares nullable
gets `source.X == null ? default(string)! : source.X.Y`; a required one gets nothing. This is a
traversal the GENERATOR invented, so unlike a developer-written `MapFrom` chain it has to pick a
policy, and the policy is the one the nested-object maps and the null-collection policy already use.
An unnecessary guard is not free: it turns a required relationship's INNER JOIN into a CASE the
provider has to reason about, for a null the type says cannot happen. The consequence is worth
stating rather than hiding — `default(T)` is null for a reference type and ZERO for an `int`, so a
guarded value leaf cannot be told from a real zero. Declare the destination member nullable if that
matters.

**One thing the projection needed that the design did not predict.** The guard has to be spelled
TWICE. An expression tree may not contain an `is` pattern at all (CS8122), so the in-memory chain
guards with `is null` and the query one with `== null` — which is why `PropertyPair` now carries
two access templates beside its two conversion templates.

**In the sample.** `InvoiceLineFlatDto` carries the product, its brand and its stock beside the
line instead of nested inside it, and the whole map is one option; `/api/invoices/lines/flat`
returns it and `?sql=true` shows the three joins, with no `CASE` (every step is required) and no
`Brand.Country` (nothing asks for it). Naming conventions have no natural home in that schema and
live in the tests instead, which is said out loud in the `.http` file rather than contrived into an
endpoint.

### ✅ Step 10 — Inheritance, polymorphism, open generics

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

**What landed.**

- **`IncludeBase<TSourceBase, TDestinationBase>()`** inherits the base map's `ForMember`
  configuration — not its members. The members were never the problem: `PhysicalItem` already IS
  a `CatalogItem`, so `Sku` already matched by name. What could not be shared was everything said
  ABOUT it. Bases merge NEAREST-FIRST and transitively, own configuration always wins per member,
  conditions union, and the walk is loop-safe and capped. Map-level hooks are deliberately NOT
  inherited: a `BeforeMap` written for the base's members would silently run over a derived
  destination it has never seen.
- **It reads every part of the mapper.** A base `CreateMap` may live in another file or another
  `partial` half, so the generator walks EVERY `DeclaringSyntaxReference` of the class, lazily —
  the lookup is only built when something actually asks for a base.
- **And it projects, which is the half that took the work.** Everything written is stored against
  the pair it was written for, so an inherited `MapFrom` lives under the BASE pair and a derived map
  asking under its own would find nothing — in memory AND inside `Compose`, which collects by the
  same key. The fix is a LINEAGE in `MapCustomizations` that `Value<>`, `Condition<>` and `Compose`
  all walk. The alternative — deferring the merge to run time — was rejected because it would
  have forced Roslyn symbols into the cached `MapModel`, which is the one thing that model may not
  hold.

  Doing this only in the in-memory path would have made `Map` upper-case the SKU and `ProjectTo`
  not: two answers that each look right on their own, which is this library's whole reason to exist.
- **`Include<TDerived, TDerivedDestination>()`** emits an ordinary type test at the top of the create
  method (`if (source is Circle derived0) return MapToCircleDto(derived0);`), a paired test in the
  update overload, and dispatches per ELEMENT in the collection overloads — a mixed list is the
  normal case for a TPH table.
- **MULTI-LEVEL FAMILIES WORK BOTH WAYS ROUND, and the second needed a fix.** Chaining — each map
  including only its immediate child — composes for free, because the derived map runs its own
  dispatch. Listing a child AND a grandchild on the same map did not: type tests are checked in
  the order they are written, so `is PhysicalItem` caught a `BundleItem` and answered with a
  `PhysicalItemDto`, dropping `ItemCount` in silence — the feature defeating its own purpose, and
  reachable by nothing worse than writing two `Include` calls in the obvious order.

  Fixed by sorting the emitted tests DEEPEST-FIRST (`DerivedPair.Depth`, worked out while the
  symbols are in hand so the cached model still holds only strings). Declaration order now cannot
  change the generated file at all — which also took the SM0024 message's `OfType<>` example,
  since it named `IncludedDerived[0]`. The same rule C# enforces for `catch` clauses; sorting beats
  a diagnostic here because the calls may be spread across parts of a partial class, so there is no
  one place to read to get the order right.
- **`Include` costs the projection (SM0024), `As` does not.** That asymmetry is the whole design and
  is worth stating as a rule: a projection has ONE element type, fixed when the query is written, so
  a PER-ROW decision has nowhere to live — but `As` decides nothing per row, because the concrete
  type was fixed at the `CreateMap`. So `Include` emits a THROWING projection member whose message
  names the alternative (`OfType<Circle>().ProjectTo<CircleDto>(mapper)`), and `As` emits
  `MapCustomizations.Widen<TSource, TConcrete, TDestination>(...)` — the concrete map's own
  expression with a widening cast, and the same SQL as before.
- **A throwing member, never a missing one.** Same rule as Steps 6→8: a projection member that is
  absent is CS0103 the moment the map is nested inside another.
- **`CreateMap(typeof(Page<>), typeof(PageDto<>))`** is closed for every pair the mapper ALREADY
  maps. That rule is both the useful one and the only decidable one — "every closed pair in the
  compilation" would mean guessing which of a program's thousands of types somebody meant to wrap,
  and would change answer when an unrelated `using` was added. Constraints are checked; interface
  and abstract element destinations are skipped, because a `PageDto<IWidgetDto>` has elements
  nothing can construct. In the sample, one line produced SIXTEEN closed maps.
- **Five diagnostics, SM0022→SM0026**, all Warning: a base pair with no `CreateMap`, an `Include`
  pair that does not derive or has no map, the projection refusal, an `As` type that is not
  assignable, and an open generic that could not be closed.

**Severity, by the rule the earlier steps set.** SM0024 is a WARNING rather than the Info that
SM0015 got, because `ConstructUsing` is written by someone who knows they are leaving the query
path, while `Include` is written to fix an in-memory bug and takes the projection away as a side
effect — the person who loses it is not the person who chose it.

**In the sample.** `Entities/CatalogItem.cs` is a real TPH table, seeded with two physical and two
digital rows, so `/api/catalog` dispatches per row, `/api/catalog/projected` shows the refusal,
`/api/catalog/physical?sql=true` shows `WHERE [Discriminator] = N'PhysicalItem'` next to the
INHERITED `UPPER([c].[Sku])` reaching SQL, `/api/catalog/labels?sql=true` shows `As` producing that
same `SELECT`, and `/api/catalog/paged` and `/paged-brands` are two closed maps from one line.
`BundleItem` is the THIRD level: declared last among the `Include`s and dispatched first, and its
map names only `PhysicalItem` while inheriting `CatalogItem`'s configuration through it.

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

### ✅ Step 11 — Profiles: maps declared outside the mapper class

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

**What landed.**

- **`ShiftMapperProfile` DERIVES FROM `ShiftMapperBase`**, which is why there is no second API to
  keep in step: `CreateMap`, the open generic `CreateMap` and every refinement are literally the
  same methods. The cost is that a profile passes the generator's "is this a mapper" test, so it is
  turned away explicitly — by equality as well as derivation, or the library's own abstract class
  reports SM0005 against itself.
- **`AddProfile<T>()` means two different things at two times.** At compile time it is read like any
  other declaration and the profile's `CreateMap` calls are merged into the mapper being generated.
  At run time it records the type so the profile can be CONSTRUCTED — which is what puts its
  `MapFrom` trees where the generated lookups will find them.
- **Materialised on FIRST USE, not in the constructor**, and that is the whole design. A profile may
  take dependencies; a mapper's `Services` is assigned by `AddShiftMapper` AFTER its constructor
  returns. So `CreateMap` writes to the store through a private field while `Customizations` — the
  property the generated code reads, and only at map time — materialises profiles first.
- **The edge that follows, accepted rather than engineered around:** a profile taking dependencies
  makes the whole mapper DI-only, because all profiles are built together and one that cannot be
  built fails the first map. Skipping it would leave its members quietly unfilled, which is the
  divergence this library exists to prevent. Found by the test suite, which builds mappers by hand
  in ten places; kept, documented, and pinned by a test.
- **Crossing between declarations works in every direction**, because the lookups that were already
  walking every part of a partial mapper now walk profile parts too: `IncludeBase` resolves a base
  map declared in another profile, and an open generic closes over pairs declared anywhere —
  including an open generic declared IN a profile, which the first sample split immediately caught.
- **Profiles are transitive and cycle-safe.** A profile may add profiles; two that add each other
  terminate and map the union, which is what anyone writing it would expect.
- **Precedence is the same on both sides.** The mapper's own declaration wins over a profile's, in
  the generated code and in the runtime merge alike — which is what lets SM0027 be a warning
  rather than an error, since both halves already agree on the answer.
- **Three diagnostics.** SM0027 a pair declared twice, SM0028 a profile that arrived as metadata
  rather than source, SM0029 a `ConfigureDefaults` override on a profile, which configures nothing
  because defaults are read from the mapper's type.
- **Diagnostics point INTO the profile file**, which fell out of `LocationInfo` already carrying a
  path, and is the thing that would have made profiles feel broken had it not.

**In the sample.** `AppMapper` was 638 lines; the catalogue family moved to
`Mapping/CatalogProfile.cs` and the `ConstructUsing` label map — which needs `IInvoiceNumbering` —
to `Mapping/InvoiceLabelProfile.cs`, registered in Program.cs. No endpoint changed, and
`/api/invoices/1/label` still answers `IQ/INV-2026-0001`, which is the DI profile having been
resolved and run.

---
- The mapper class is already `partial`, so a second generator (ShiftFramework's, or a
  scaffolder) can contribute `CreateMap` calls as generated source — **but only through
  `RegisterPostInitializationOutput`, and that is far more limited than it sounds.** Measured, and
  pinned by `CrossGeneratorTests`:

  | How the other generator adds its source | ShiftMapper sees it? |
  |---|---|
  | `RegisterPostInitializationOutput` | **yes**, in either generator order |
  | `RegisterSourceOutput` (the ordinary pass) | **no**, in either order |

  Post-initialization sources enter the compilation before any generation pass, so everyone sees
  them. Ordinary generated source nobody sees: every generator is handed the compilation as it was
  BEFORE any generator ran, and there is no ordering, no chaining, and no way to ask for one.

  **The catch is what a post-init generator is able to say.** Its context has no compilation, so
  its text is fixed at build time and cannot name a type the application declared. That makes it a
  route for a framework's OWN maps between its OWN types, and NOT for the application's entities —
  scanning those needs the compilation, and needing the compilation puts the output in exactly the
  pass nobody else can read.

  So this is a real but narrow extension route, and it is not the one ShiftFramework needs. That
  is Step 13, and this measurement is the argument for it.

### ✅ Step 12 — Global type-pair converters

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

**What landed.**

- **`CreateConversion<TSource, TDestination>(memory, query)`** on a mapper or a profile. The
  generator reads only the TYPE ARGUMENTS and whether a `query:` argument was written; the
  expressions stay in the developer's file and arrive at run time, exactly as a `MapFrom` tree does.
  So a conversion may call anything C# can call without the generator having to understand it.
- **It plugs into `ConversionResolver`**, which is why transitivity was free: collections,
  dictionaries and nullable lifts already recurse through the same method, so a rule for
  `Money → string` fills a `List<Money>` member and a `Dictionary<string, Money>` value without a
  line of extra code. The one thing that was NOT free is that both shapes wrap the element
  conversion in a `static` lambda, and `static` forbids capturing — so a `CapturesMapper` flag
  drops the keyword exactly where a global conversion is involved, and nowhere else.
- **A REGISTERED PAIR WINS OVER THE BUILT-IN TABLE**, a deliberate departure from this step's own
  "extend rather than replace". The case that settles it is the hash ids: `long → string` already
  converts, so under the other ordering the rule would be ignored in silence — and silently
  ignoring an explicit declaration is the one behaviour this library is arranged never to have.
  Pairs nobody registered are untouched, which is the sense in which the table is still extended.
- **Assignability with nearest-wins**, and the SAME rule in the generator and the runtime store —
  they each resolve the pair independently, so two rules would mean generated code finding a
  different conversion from the one its diagnostics described. Value types match exactly only:
  the generated code hands the delegate back as a `Func` over the member's own types, which works
  by contravariance for references and not at all for a boxed value.
- **The projection is a MARKER, not a call.** The generated projection carries
  `MapCustomizations.Splice<A, B>(member)`, and `Compose` replaces it with the registered tree
  inlined around its argument. Inlined rather than invoked for the reason the codebase already had
  written down for `MapFromSource`: a delegate is opaque to EF. The sample's SQL shows `DATEPART`,
  which is the proof.
- **SM0030 for a pair with no query form**, collected from a USAGE LOG on the conversion table
  rather than threaded out of the analysis — a conversion can be reached from a plain member, a
  flattened path, a collection element, a dictionary value or a constructor argument, and recording
  it where the lookup happens catches all of them by construction.

**The bug the sample caught.** Every generator test passed while the sample silently did not report
SM0030. `MapModel.WithNested` and `WithConstructor` rebuild the model to settle nested members, and
a rebuild that forgets a field loses it — which no test noticed because none of their maps had
anything to resolve. Fixed, and pinned by a test whose map has nested members on purpose. It is the
argument for keeping the sample honest rather than treating it as a demo.

---

### ✅ Step 13 — The compile-time extension contract for referenced assemblies

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

**What landed.**

- **`[assembly: ShiftMapperConversions(typeof(Holder))]`**, plus `[ShiftMapperQueryForm]` on the
  member supplying the projection expression and `[ShiftMapperContract(1)]` for versioning. A
  conversion is a `public static` method with one parameter and a return value — the SIGNATURE is
  the declaration, so nothing can drift out of step with a name.
- **ASSEMBLY ATTRIBUTES ONLY.** Scanning every exported type of every referenced assembly for a
  marker would be paid on every keystroke of every project that references anything; the
  assembly-level list is the contract and the attribute on the holder is documentation.
- **The generated code CALLS the declared method by name**, which is the part worth stating loudly:
  the metadata route is FASTER than the in-source one, not a degraded fallback. A `CreateConversion`
  lambda can only be looked up at run time; a declared one is
  `global::ShiftFramework.ShiftEntityConversions.ToFiles(source.Files)`. It also keeps a collection's
  element lambda `static`, because nothing is captured.
- **Only the query forms are registered at run time**, through a generated
  `RegisterDeclaredConversions` override, because an expression tree is the one thing a name cannot
  stand in for. Everything else Step 12 built — `Splice`, `Compose`, the resolver — was reused
  unchanged.
- **Precedence, near to far:** `ForMember`, then this project's own `CreateConversion`, then a
  package's declaration, then the built-in table. The same order in the generator and in the runtime
  merge, so the two halves cannot disagree.
- **Three diagnostics.** SM0031 (two packages claiming one pair) is an ERROR because there is no
  answer to pick; SM0032 (a malformed declaration) is a warning because the mistake belongs to the
  package author rather than to whoever is building now; SM0033 (a newer contract) ignores the
  assembly's conversions rather than half-reading a shape it does not know.

**`ShiftFramework.Mock`, a new project, and what building it taught.** It is referenced by the
sample and the test suite as a compiled library — no analyzer, no source — so the contract is
exercised the way an application actually meets it. Two things came out of writing it that no unit
test would have:

- **The two forms of a conversion can disagree, and nothing can catch it.** The mock's hash id was
  `"H" + id.ToString("D6")` in memory and `"H" + id` in the query form: both well-typed, both
  translate, and the same brand came back as `H010010` from `Map` and `H10010` from `ProjectTo`.
  That is a framework author's own responsibility, and the reason the sample shows both backends of
  one map side by side.
- **A JSON column genuinely cannot be parsed in SQL**, so the honest declaration is a memory form
  and NO query form. The first attempt invented a query expression that "translated", which EF then
  refused outright — and would have been a lie if it had not. So the mock declares that pair one
  way only, and every application referencing it is told at build time which of its endpoints stops
  being one query (SM0030). That is the whole argument for compile time over a runtime table, and
  it is now a live example rather than a paragraph.
- **A profile shipped in a package is invisible to the compiler and LIVE at run time.** Registering
  one that declared a conversion silently replaced the framework's own query form while the build
  said the profile contributed nothing. The mock's profile now declares an inert map instead, and
  the asymmetry is written down where somebody will hit it.

---

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
| 2 — Gaps | ~~5 Collections~~, ~~6 Constructors/records~~, ~~7 Member options~~, ~~8 Map hooks~~, ~~9 Flattening~~, ~~10 Inheritance/generics~~ | ✅ done | Unblocked Phase 3 |
| 3 — General layer | ~~11 Profiles~~, ~~12 Global conversions~~, ~~13 Compile-time contract~~, 14 Member conventions, 15 ShiftFramework port | ⬜ 11—13 done | The goal |
| 4 — Finish | 16 Diagnostics, 17 Docs, 18 Benchmarks | ⬜ pending | Can run alongside 2 and 3 |

The shortest path to ShiftFramework being able to adopt this is
**1 → 4 → 8 → 10 → 11 → 12 → 13 → 14 → 15**; with 1 and 4 done, it starts at **8**. Steps 5, 6, 7
and 9 are needed for ShiftMapper to be a good general-purpose mapper, but they are not on
ShiftFramework's critical path. **Phase 2 is now complete, so the path is
11 → 12 → 13 → 14 → 15.** Everything in Phase 2 is done: 5, 6 and 7 because every
application hits them on its first day (a list endpoint, a DTO that is a record, a PATCH), 8
because it was the other Phase 3 blocker, and 9 because it is the one every DTO that is a grid row
hits.
