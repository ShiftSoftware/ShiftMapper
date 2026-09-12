# Migrating from AutoMapper

You have an AutoMapper configuration and want to know two things: what each line becomes, and
what has no equivalent. This page answers both, in that order, and the second part is the one
worth reading slowly — some of the gaps are things nobody has written yet, and some are things a
compile-time mapper cannot have and never will. They are not the same kind of gap, and the page
keeps them apart.

For what ShiftMapper *is*, start at the [README](../README.md). This page assumes you have read
its five-minute section and know that `CreateMap` never runs.

## The one difference that explains the rest

AutoMapper assembles a configuration object at run time. Everything follows from that: maps can be
added conditionally or in a loop, a resolver can be resolved from the container per call, a hook
can read a `ResolutionContext`, and whether the configuration is complete is a question you ask it
(`AssertConfigurationIsValid`) after it has been built.

ShiftMapper reads your `CreateMap` calls from **source**, at **compile time**, and writes the map as
ordinary C# into the other half of your partial class. That is what buys the things AutoMapper
cannot offer: a generated file you can step through, 38 build diagnostics that name the property
and the line, and a `ProjectTo` that is a real expression tree EF Core turns into one `SELECT`. It is
also, exactly, what costs the dynamic forms. A decision the generator cannot see cannot be baked,
and ShiftMapper's rule is that such a decision is an **error, never a silent default** — which is
why an `if` around a `CreateMap` is SM0035 rather than a map that may or may not exist.

Two invariants shape every row below:

1. **Two backends.** Every feature either works for both in-memory `Map` and EF `ProjectTo`, or the
   build says which one it does not support, by id. AutoMapper's `ProjectTo` quietly ignores
   configuration it cannot express; ShiftMapper reports it (SM0015, SM0017, SM0018, SM0024, SM0030,
   SM0036) and the generated projection throws a sentence naming the map rather than returning rows
   the hook never touched.
2. **Nothing is left default in silence.** An unmapped destination member is SM0001 on every map,
   not a `MemberList` you opt into checking.

## Part one: the API, side by side

Every ShiftMapper cell below was checked against the runtime source
(`ShiftMapperBase.cs`, `MapExpression.cs`, `MemberOptions.cs`, `AllMemberOptions.cs`,
`MapOptions.cs`, `PropertyMatching.cs`, `MemberConventionExpression.cs`). "none" means there is no
equivalent; Part two says why.

### Declaring maps

| AutoMapper | ShiftMapper |
|---|---|
| `class CatalogProfile : Profile` with `CreateMap` in its constructor | `class CatalogProfile : ShiftMapperProfile`, same shape; a mapper adds it with `AddProfile<CatalogProfile>()` |
| `new MapperConfiguration(cfg => cfg.AddProfile<CatalogProfile>())` | a `partial class AppMapper : ShiftMapperBase` whose constructor declares maps and calls `AddProfile<T>()` |
| `services.AddAutoMapper(typeof(Startup).Assembly)` — assembly scanning | `services.AddShiftMapper<AppMapper>()` — one explicit mapper, profiles named by `AddProfile<T>()`, no scanning |
| `CreateMap<TSource, TDestination>()` | `CreateMap<TSource, TDestination>()` |
| `CreateMap<TSource, TDestination>(MemberList.Source)` / `.ValidateMemberList(...)` | none — validation is per destination member, always on (SM0001); tune severity per folder in `.editorconfig` |
| `CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))` — open generics | `CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>))`; one type parameter a side (SM0026), closed over the pairs the mapper already declares |
| `cfg.AllowNullCollections = true` | `CreateMap<A, B>(o => o.AllowNullCollections = true)`, or once in `ConfigureDefaults`; same default as AutoMapper (off — a null source collection becomes an empty one) |
| `cfg.ShouldMapProperty` / `cfg.ShouldMapField` | none — public, non-static, non-indexer properties are mapped; a non-public setter is SM0003; fields are not mapped |

A profile in ShiftMapper is a place to write declarations, not a second mapper — nothing is
generated onto it, and there is no `CatalogProfile.Map`. Its maps become the maps of whichever
mapper adds it. The one thing that does not carry across from AutoMapper's model is discovery:
there is no scan, because the generator has to be able to point at the line that declared each map.

### Per-member configuration (`ForMember`)

| AutoMapper | ShiftMapper |
|---|---|
| `.ForMember(d => d.X, opt => opt.MapFrom(s => expr))` | `.ForMember(d => d.X, opt => opt.MapFrom(s => expr))` — the expression must return the destination member's type |
| `opt.MapFrom(s => s.Count)` onto a member of a different type, converting by hand | `opt.MapFromSource(s => s.Count)` — returns the *source* type and lets the conversion table convert it, in both backends |
| `opt.MapFrom((src, dest) => ...)` — destination-aware | none |
| `opt.MapFrom("Customer.Name")` — string path | none; write `opt.MapFrom(s => s.Customer.Name)`, or let flattening fill `CustomerName` |
| `opt.MapFrom<TResolver>()` / `IValueResolver<,,>` / `IMemberValueResolver<,,,>` | none as a type; inject the service into the mapper's constructor and call it from `MapFrom` |
| `opt.Ignore()` | `opt.Ignore()` — also the acknowledgement that settles SM0001/SM0011/SM0012 for that member; a code fix offers it |
| `opt.Condition((src, dest, srcMember, destMember) => ...)` | `opt.Condition((s, d, value) => ...)` — three arguments; declined members are left alone; the map loses its projection (SM0017) |
| `opt.PreCondition(...)` | none |
| `opt.NullSubstitute(value)` | none; `opt.MapFrom(s => s.X ?? value)` today |
| `opt.UseValue(constant)` (older AutoMapper) | `opt.MapFrom(s => constant)` |
| `opt.ConvertUsing<TValueConverter, TSourceMember>()` / `IValueConverter<,>` | per type pair: `CreateConversion<TSource, TDestination>(memory, query)`; per member: a `MapFrom` calling a static method |
| `opt.UseDestinationValue()` | none — a nested member is always freshly mapped and assigned on update |
| `opt.SetMappingOrder(n)` | none |
| `opt.ExplicitExpansion()` | none |
| `.ForPath(d => d.Inner.X, opt => ...)` | none — the `ForMember` selector must be a single property access on the parameter |
| `.ForCtorParam("name", opt => opt.MapFrom(...))` | `.ForMember(d => d.Name, ...)` — a constructor parameter is a destination member; a `ForMember` naming the property it stands for fills the argument |
| `.ForAllMembers(opt => opt.Condition(...))` | `.ForAllMembers(opt => opt.Condition((s, d, value) => ...))` — `value` arrives boxed as `object`; a member's own `Condition` wins |
| `.ForAllOtherMembers(opt => opt.Ignore())` | none — the type that maps nothing is the better tool than a rule that ignores everything |

`ForCtorParam` deserves one more sentence. It works through `ForMember` because on a positional
record or primary constructor the parameter and the property share a name, so the property selector
reaches the argument. A constructor parameter with **no** corresponding property cannot be named by
a selector at all; it is SM0013, and the answer is `ConstructUsing` — which costs the projection
(SM0015) — or a constructor whose parameters ShiftMapper can match by name.

### Map-level configuration

| AutoMapper | ShiftMapper |
|---|---|
| `.ConstructUsing(src => new D(...))` / `.ConstructUsing((src, ctx) => ...)` | `.ConstructUsing(s => new D(...))` — `Expression<Func<TSource, TDestination>>`, no context; in-memory only (SM0015) |
| `.ConvertUsing(Func<S, D>)` / `.ConvertUsing<TTypeConverter>()` / `ITypeConverter<S, D>` | `.ConvertUsing(s => ...)` — one overload, an expression; it **projects**, and any other configuration on that map is SM0019 |
| `.BeforeMap((src, dest) => ...)` | `.BeforeMap((s, d) => ...)` — `Action<TSource, TDestination>` only; in-memory only (SM0018) |
| `.BeforeMap((src, dest, ctx) => ...)` / `.BeforeMap<TMappingAction>()` | none |
| `.AfterMap((src, dest) => ...)` | `.AfterMap((s, d) => ...)` — same shape and the same limit (SM0018) |
| `.AfterMap((src, dest, ctx) => ...)` / `.AfterMap<TMappingAction>()` / `IMappingAction<S, D>` | none |
| `.ReverseMap()` | `.ReverseMap()` — returns `MapExpression<TDestination, TSource>`; nothing chained before it carries over; takes its own `MapOptions` |
| `.MaxDepth(n)` / `.PreserveReferences()` | none — a circular graph of maps is SM0012, an error |
| `.DisableCtorValidation()` / `cfg.DisableConstructorMapping()` | none — constructor selection is always on; a parameter nothing fills is SM0013 |

The hooks divide on one question the README puts in a table: *can it be an expression?*
`ConvertUsing` is one, so it is the only map-level hook `ProjectTo` uses. `ConstructUsing`, the
hooks and `Condition` are statements or per-row decisions, so each costs the map its projection and
the build says so. `GET /api/brands/labels?sql=true` in the sample shows a `ConvertUsing` reaching
SQL; `GET /api/stocks/hooks?project=true` shows what a hooked map's projection says when asked.

### Inheritance and polymorphism

| AutoMapper | ShiftMapper |
|---|---|
| `.Include<TDerived, TDerivedDto>()` | `.Include<TDerived, TDerivedDestination>()` — dispatches on the runtime type, deepest-first; in-memory only (SM0024); project with `OfType<TDerived>().ProjectTo<TDerivedDto>(mapper)` |
| `.IncludeBase<TBase, TBaseDto>()` | `.IncludeBase<TSourceBase, TDestinationBase>()` — inherits the base map's `ForMember` configuration, transitively, and projects; the map-level hooks are not inherited |
| `.IncludeAllDerived()` | none — name each derived pair |
| `.As<TConcrete>()` | `.As<TConcrete>()` — for an interface or abstract destination; a redirection to the concrete map; projects |
| `.IncludeMembers(s => s.Child)` | none; flattening covers `ChildName` from `Child.Name`, and `MapFrom` covers the rest |

`GET /api/catalog` (in memory, via `Include`) and `GET /api/catalog/physical?sql=true` (projected,
via `OfType`) are the two shapes side by side, with the base map's `Sku` expression reaching SQL
through `IncludeBase` as `UPPER([c].[Sku])`.

### Naming, matching and flattening

| AutoMapper | ShiftMapper |
|---|---|
| exact name match, then flattening | exact match first, then case-insensitive (`Sku` finds `SKU`), then flattening |
| `cfg.SourceMemberNamingConvention` / `DestinationMemberNamingConvention` | `PropertyMatching.CaseInsensitive` (default) or `CaseSensitive`, per map or in `ConfigureDefaults`; no other convention |
| `cfg.RecognizePrefixes("Db")` / `RecognizePostfixes("Id")` — profile-wide | `CreateMap<A, B>(o => o.RecognizePrefixes("Db"))` per map, or once in `ConfigureDefaults`; the bare name is always tried first |
| `cfg.RecognizeDestinationPrefixes(...)` / `RecognizeDestinationPostfixes(...)` | none — prefixes and postfixes are matched on the **source** side only |
| `cfg.ReplaceMemberName("Ä", "A")` | none |
| flattening (`Customer.Name` fills `CustomerName`), silent | flattening, on by default, and every member it fills is reported with the path it chose (SM0020, Info); a name that resolves two ways is refused (SM0021); it will not walk into a `string`, a collection or a nullable value type |
| `cfg.ForAllMaps(...)` / `cfg.ForAllPropertyMaps(...)` | none for members; for a whole type pair, `CreateConversion`; for a member *shape*, `CreateMemberConvention<TMember>()` |

### Entry points

| AutoMapper | ShiftMapper |
|---|---|
| `IMapper` injected | the mapper class itself (`AppMapper`), injected; `IShiftMapper` for a library that cannot name it |
| `mapper.Map<TDestination>(source)` | `mapper.Map<TDestination>(source)` or `source.Map<TDestination>(mapper)` |
| `mapper.Map<TSource, TDestination>(source)` | generated methods infer the source; on `IShiftMapper`, `Map<TSource, TDestination>(source)` |
| `mapper.Map(source, destination)` | `mapper.Map(source, destination)` — not generated for a destination with nothing assignable after construction (a positional record) |
| `mapper.Map<TDestination>(null)` | `Map` throws on a null source; `mapper.MapOrNull<TDestination>(maybe)` returns null |
| `mapper.Map<List<TDto>>(items)` | `mapper.Map<List<TDto>>(items)`, also `TDto[]`, `HashSet<TDto>`, `IReadOnlyList<TDto>`, and a typed `MapToBrandDtoList(items)` |
| `mapper.Map(source, sourceType, destinationType)` | `IShiftMapper.Map<TDestination>(object source)` — the source type is taken from the object; the destination is still a type parameter |
| `mapper.Map<TDestination>(source, opts => opts.Items["key"] = value)` | none |
| `query.ProjectTo<TDto>(mapper.ConfigurationProvider)` | `query.ProjectTo<TDto>(mapper)` or `mapper.ProjectTo<TDto>(query)` — one parameter, no `parameters`, no `membersToExpand` |
| `mapper.ConfigurationProvider` / `IConfigurationProvider` | none — there is no configuration object at run time |
| `cfg.ConstructServicesUsing(type => provider.GetService(type))` | constructor injection on the mapper or profile; `ShiftMapperBase.Services` for what you only discover while mapping |

### Extension points and validation

| AutoMapper | ShiftMapper |
|---|---|
| `ITypeConverter<S, D>` registered for a pair, used everywhere | `CreateConversion<S, D>(memory: ..., query: ...)` — applies to every member of those types in every map, through collections, dictionaries and nested maps; beats the built-in table for that pair |
| a converter with no expression form | `CreateConversion<S, D>(memory: ...)` with `query` omitted — a declaration that the pair cannot be projected; every map touching it is SM0030 |
| `IValueResolver` reading a service | a `MapFrom` closing over an injected field; in a projection a row-independent service *value* becomes a SQL parameter, a row-dependent *call* is client-evaluated by EF, and filtering on such a member throws |
| `cfg.AssertConfigurationIsValid()` | none — the build is the assertion |
| `[AutoMap(typeof(Source))]`, `[IgnoreMap]`, `[SourceMember("X")]` | none — declarations are C# calls the compiler checks; the `ShiftMapper*` attributes in the package are emitted by the generator, never written by hand |
| an AutoMapper `Profile` in a referenced package, picked up by scanning | a `ShiftMapperProfile` in a package **built with the ShiftMapper generator**, added with `AddProfile<T>()`; a package built without it is SM0028 |

`CreateMemberConvention<TMember>()` has no AutoMapper counterpart in either direction. It is a rule
about a member *shape* — "any destination member of type `ShiftEntitySelectDTO` is filled from
`{Member}ID` and `{Member}.{NameOf}`" — that resolves to text at compile time and therefore reaches
the projection. The nearest AutoMapper idiom is an `AfterMap` per map, which is exactly the thing
that cannot appear in a list query. `GET /api/products/list?sql=true` is the worked example.

## Part two: the honest gaps

### Structural: a compile-time mapper cannot have these

Each of these depends on something that exists only at run time. They are not on a roadmap, because
building them would mean giving up the generated code, the diagnostics, or the projections — and a
half-version that worked in `Map` and not in `ProjectTo` is the divergence this library exists to
refuse.

**Conditional and dynamic configuration.** `if (options.IncludeReporting) CreateMap<Report,
ReportDto>()`, a `foreach` over a list of types, a map registered from a plugin — anything where
whether a map exists is decided by a value. The generator reads statements, not values, and before
SM0035 existed such a `CreateMap` was baked unconditionally with the condition silently discarded;
now it is an error. Expression-bodied constructors and private helper methods are fine, because the
rule keys on statement position, not on which member the call is in.

**`ResolutionContext`, `Items` and per-call options.** `mapper.Map<D>(s, opts => opts.Items["tenant"]
= id)`, the `(src, dest, ctx)` overloads of `BeforeMap`/`AfterMap`/`ConstructUsing`, and
`IMappingAction<S, D>` with its `context` parameter. A hook in ShiftMapper is
`Action<TSource, TDestination>` and nothing else: the generated call is written at build time with
exactly two arguments, and there is no object threaded through a projection for a third to read. What
you would have put in `Items`, inject into the mapper's constructor.

**Resolver and converter types resolved from the container.** `MapFrom<TResolver>()`,
`ConvertUsing<TTypeConverter>()`, `ConstructServicesUsing`. Each is a runtime indirection the
generator cannot see inside and EF cannot translate. The replacement is not weaker: a `MapFrom` that
calls a static method is the same tree, and a `CreateConversion` with a `query` form is a converter
that reaches SQL, which a class implementing `ITypeConverter` never could.

**`AssertConfigurationIsValid`.** There is nothing to assert after the build. An unmapped member is
SM0001 on the line that declared the map; a missing nested map is SM0011 and stops the build; a
constructor parameter nothing fills is SM0013. The information AutoMapper collects into one exception
at startup arrives one diagnostic at a time, in the IDE, before the code runs — and the reverse is
also true: a build with no `SM` output is a configuration that is valid, and there is no second check
to forget.

**Maps over types that have no members at compile time.** `dynamic`, `ExpandoObject`, and a
`Dictionary<string, object>` standing in for an object. There is nothing to bake. Dictionaries as
*members* are fully supported, converting keys and values by the ordinary rules; a dictionary as
the *shape* of a source or destination is not.

**Reference tracking and depth limits (`PreserveReferences`, `MaxDepth`).** Both exist to make a
cyclic object graph terminate. An in-memory map could carry a visited set; a projection cannot,
because a `SELECT` has one fixed shape and no per-row memory. Rather than let `Map` succeed where
`ProjectTo` cannot, a cycle in the map graph is SM0012, an error, and the fix is to `Ignore` the
member that closes the loop — which is also the decision that records which type is the view and
which is the thing being viewed.

**`ProjectTo` parameters and `ExplicitExpansion`.** AutoMapper's `ProjectTo(config, parameters,
membersToExpand)` shapes the query per call. ShiftMapper's projection is one cached expression per
pair; a per-call shape would be a second projection the generator never saw. Filter and page on the
projected `IQueryable` instead — that is what it is for.

### Not built yet

These are real gaps with no structural obstacle. Most have a workaround that projects; a few were
evaluated and declined, and the reason is recorded in [PLAN.md](../PLAN.md) so it can be argued with.

- **`NullSubstitute`.** Would be `?? value` in both backends and translates. Not written.
  `opt.MapFrom(s => s.Name ?? "")` does the same thing today and projects.
- **`ForPath`.** The `ForMember` selector must be a single property access on the parameter;
  anything deeper has no member name to record and is rejected with a message saying so. Configure
  the nested pair's own map instead.
- **Destination-aware `MapFrom((src, dest) => ...)`.** Real and unbuilt. Its ordering contract is
  what `SetMappingOrder` was for, and the generator could infer it by sorting destination reads.
- **`IncludeAllDerived`.** `Include` names each derived pair. Note that it would not project either
  way (SM0024) — a projection has one element type.
- **`UseDestinationValue`.** On an update, a nested member is assigned a freshly mapped object
  (`destination.Brand = MapToBrandDto(source.Brand)`) and a collection is rebuilt
  (`destination.Tags = ValueConverter.ToListOrEmpty(source.Tags)`). Mapping onto the existing child,
  or merging a collection by key as AutoMapper.Collection does, is not built.
- **Destination-side prefixes and postfixes.** `RecognizePrefixes`/`RecognizePostfixes` strip from the
  source name only.
- **Naming conventions other than case.** `PropertyMatching` has two values. A `snake_case` source
  needs a `MapFrom` per member.
- **`MemberList.Source` validation.** SM0001 asks whether every *destination* member is filled; no
  rule asks whether every *source* member was used.
- **Fields.** Properties only.
- **`ReverseMap` carrying configuration over.** Deliberate rather than pending: an `Ignore` names a
  member of the other destination, and a `MapFrom` that composes two members has no way back. The
  reverse map starts clean and reports what it cannot fill as SM0006 (Info), which is the normal
  shape of a DTO-to-entity direction.

Evaluated and declined, with the reasoning in PLAN.md:

- **`PreCondition`** is strictly weaker than `Condition`, which already receives the source.
- **`MapFrom(string path)`** and **`UseValue`** add nothing over `opt.MapFrom(s => s.A.B)` and
  `opt.MapFrom(s => "USD")`, produce identical SQL, and a string path is not compile-checked.
- **`SetMappingOrder`** modifies a destination-aware `MapFrom` that does not exist yet.
- **`ConvertUsing(converterInstance)`** per member is worse than a static method in a `MapFrom`: the
  same tree plus a `ConstantExpression` EF compares by reference, so a non-singleton instance costs a
  query recompile per request.

## What a first port sounds like

Port a working AutoMapper configuration and the first build is loud. Two sources account for nearly
all of it, and both are the point.

**SM0001 — a destination member nothing fills.** AutoMapper leaves such a member at its default and
says nothing unless you call `AssertConfigurationIsValid`. ShiftMapper reports each one, as a warning,
on the `CreateMap` responsible. The honest answers are the same two AutoMapper's validator would
accept — map it, or `opt.Ignore()` it — and there is a code fix for the second. There is no fix-all
on purpose: bulk-ignoring is exactly the review nobody would then do. If a folder of maps is
genuinely fine as-is, `.editorconfig` can set `dotnet_diagnostic.SM0001.severity = none` for that
folder alone.

**SM0018 — the map runs `BeforeMap` or `AfterMap`, so `ProjectTo` cannot use it.** AutoMapper's
`ProjectTo` does not run hooks either; it builds the projection from member configuration and the
hook is simply absent from the rows that come back. ShiftMapper refuses to build that projection at
all and says so at build time — and, since SM0037, at the `ProjectTo` call site too, in another
file or another assembly. On a port, go through each SM0018 and ask what the hook computes. Anything
derivable from the *source* belongs in a `ForMember`, which projects; only a value that needs the
*finished destination* earns an `AfterMap`, and that map should be one you `Map`, not one a list
endpoint projects. `GET /api/stocks/hooks` is the sample's demonstration of both halves.

Three more you may meet, in order of likelihood:

- **SM0011 (Error)** — a nested member whose pair has no `CreateMap`. AutoMapper would have thrown
  at run time, or at validation; ShiftMapper stops the build. A code fix offers the declaration.
- **SM0030 / SM0036** — a global conversion with no query form, or a map that nests a map which
  cannot project. The message names the child; that is the map to fix.
- **SM0028** — a profile in a package that was built without the ShiftMapper generator. The
  package's own build has to write its declarations into metadata; add the analyzer reference there
  and rebuild.

A workable order for the port itself:

1. Turn each `Profile` into a `ShiftMapperProfile`, and the `MapperConfiguration` into one partial
   mapper class that calls `AddProfile<T>()` for each. Replace `AddAutoMapper(...)` with
   `AddShiftMapper<AppMapper>()`. Everything inside the profiles that is `CreateMap`, `ForMember`,
   `MapFrom`, `Ignore`, `ReverseMap`, `Include`, `IncludeBase`, `As` and open-generic `CreateMap`
   compiles unchanged.
2. Rewrite what does not compile: `ForPath`, `ForCtorParam`, resolver and converter types, the
   context overloads of the hooks. The table above says what each becomes.
3. Build. Work through SM0001 and SM0018 as above; leave SM0020 (flattening chose a path) at Info
   unless the maps are ones where a wrong guess would matter, in which case raise it to warning and
   read every path it names.
4. Delete `AssertConfigurationIsValid` and the test that called it. The build now does that job on
   every compile.

## Where to go next

- [Getting started](getting-started.md) for the mapper, registration and the generated entry points.
- [Conversions](conversions.md) for which type pairs convert, which are refused, and why.
- [Diagnostics](diagnostics.md) for every `SM` id with its reasoning.
- [Extension points](extension-points.md) if the configuration you are porting lives in a shared
  package rather than an application.
- [`ShiftMapper.Sample`](../ShiftMapper.Sample) — `GET /` on the running app lists every endpoint
  and what it demonstrates.
