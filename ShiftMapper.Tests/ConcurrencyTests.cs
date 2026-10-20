using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftMapper.Tests;
using ShiftMapper.Tests.Model;
using Xunit;

// Two mapper classes that include each other, written the way the runtime reads an include: as
// composition metadata on the assembly. See the include-cycle tests below.
[assembly: ShiftMapperDeclaredComposition(typeof(CycleOne), typeof(CycleTwo))]
[assembly: ShiftMapperDeclaredComposition(typeof(CycleTwo), typeof(CycleOne))]

namespace ShiftMapper.Tests;

/// <summary>
/// ONE MAPPER, MANY THREADS.
///
/// <para>A mapper registered as a singleton is shared by every thread from the moment it is
/// resolved, so its first maps can all arrive at once — the timers of a cold host firing together,
/// a burst of requests after a deploy — and everything written into its store after construction
/// is written while other threads read it. These tests make those moments happen on purpose: the
/// first fill, the first resolution of each conversion, a configuration surface applied or pulled
/// in, a pack added late. And the two shapes the fill must still handle on one thread: a
/// constructor that reaches back into the mapper being filled, and two mappers that include each
/// other.</para>
///
/// <para>Each concurrent test repeats its race over many fresh mappers, because whether a race
/// shows is a matter of chance on any one attempt. The one that holds the fill open and sends a
/// second map in does not depend on chance.</para>
/// </summary>
public class ConcurrencyTests
{
    // ----------------------------------------------------------------------------------------
    // The first fill
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// THE REPORTED CASE. Fresh providers, each with this assembly's generated mapper as
    /// a singleton, and eight first maps released together on each — every one through a member
    /// that a mapper class or pack folded in by the fill registered. The flag that said "filled"
    /// used to be set before the folding, so a thread could map against half a store; and two
    /// threads could fold at once, into plain dictionaries, and break them for good.
    /// </summary>
    [Fact]
    public void Concurrent_first_maps_on_a_fresh_singleton_all_see_the_whole_store()
    {
        var failures = new List<string>();

        for (int round = 0; round < 40; round++)
        {
            using ServiceProvider provider = SingletonProvider();

            failures.AddRange(Together(8, thread => FirstMap(provider, thread))
                .Select(failure => $"round {round}: {failure}"));
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// THE SAME RACE, HELD OPEN. The fill builds a pack whose constructor waits for the test, and a
    /// second map arrives meanwhile. It must wait for the fill rather than read what has been
    /// folded in so far — which, before the fix, was everything but the pack.
    /// </summary>
    [Fact]
    public void A_map_that_arrives_while_the_first_is_still_filling_waits_for_the_whole_store()
    {
        var mapper = new HeldMapper();

        string seen = ReadWhileHeld(
            fill: () => _ = mapper.Store,
            read: () => mapper.Store.Conversion<int, string>(typeof(HeldPack))(7));

        Assert.Equal("held:7", seen);
    }

    /// <summary>
    /// The same for a fill that runs again, after a pack was added late: a map that comes after the
    /// pack was added waits for the fill that folds it in, rather than reading the store as it was —
    /// which, before the fix, it did, because the flag was set again before that fill began.
    /// </summary>
    [Fact]
    public void A_map_after_a_late_pack_waits_for_the_fill_that_folds_it_in()
    {
        var mapper = new LateMapper();
        _ = mapper.Store;

        mapper.AddLate<HeldPack>();

        string seen = ReadWhileHeld(
            fill: () => _ = mapper.Store,
            read: () => mapper.Store.Conversion<int, string>(typeof(HeldPack))(7));

        Assert.Equal("held:7", seen);
        Assert.Equal("own:7", mapper.Store.Conversion<int, string>(typeof(LateMapper))(7));
    }

    /// <summary>
    /// A FILL THAT FAILED IS NOT A FILL. Outside a container a mapper class that needs a service
    /// cannot be built, and the first map says so. The second map must say the same thing, rather
    /// than run against whatever had been folded in before the failure — which used to answer some
    /// maps and leave others missing their customizations.
    /// </summary>
    [Fact]
    public void A_first_map_that_failed_fails_the_same_way_the_next_time()
    {
        Mapper mapper = Mapper.Create(typeof(TestMapper).Assembly);

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(
            () => mapper.Map<GadgetDto>(new Gadget { Code = "1" }));

        InvalidOperationException second = Assert.Throws<InvalidOperationException>(
            () => mapper.Map<GadgetDto>(new Gadget { Code = "1" }));

        Assert.Contains("NumberedMapper", first.Message);
        Assert.Contains("NumberedMapper", second.Message);
    }

    // ----------------------------------------------------------------------------------------
    // What is written at map time
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// THE CONVERSION CACHE, which is written when a map first converts a pair. A mapper with a
    /// conversion registered under hundreds of scopes, and eight threads making the first call for
    /// each of them at once, from different starting points so the first resolutions of different
    /// pairs land together. Afterwards every pair must still answer, from one thread: a cache the
    /// storm broke fails every later map of its pairs, not only the ones that raced.
    /// </summary>
    [Fact]
    public void Concurrent_first_resolutions_of_conversions_all_resolve_and_stay_resolved()
    {
        Type[] scopes = typeof(object).Assembly.GetExportedTypes().Take(256).ToArray();
        var failures = new List<string>();

        for (int round = 0; round < 20; round++)
        {
            // Filled before the threads start: this is about the conversion caches alone.
            MapCustomizations store = new ManyScopesMapper(scopes).Store;

            failures.AddRange(Together(8, thread =>
            {
                for (int k = 0; k < scopes.Length; k++)
                {
                    if (Resolve(store, scopes[(k + (thread * 37)) % scopes.Length]) is { } wrong)
                        return wrong;
                }

                return null;
            }).Select(failure => $"round {round}: {failure}"));

            foreach (Type scope in scopes)
            {
                if (Resolve(store, scope) is { } wrong)
                    failures.Add($"round {round}, afterwards: {wrong}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// A CONFIGURATION SURFACE APPLIED WHILE OTHER THREADS MAP. Surfaces arrive one after another,
    /// each customizing a pair the store has never held, so every one of them grows it; four threads
    /// meanwhile do what generated code does on every object and every projection — fetch a tree,
    /// enumerate the trees to compose a projection, ask a condition. The surface used to be folded
    /// into the very dictionaries they were reading.
    /// </summary>
    [Fact]
    public void A_surface_applied_while_other_threads_map_disturbs_nothing_they_read()
    {
        var probe = new SurfaceProbe();
        MapCustomizations store = probe.Store;
        Type[] slots = Slots().Take(400).ToArray();

        List<string> failures = WhileReading(store, () =>
        {
            foreach (Type slot in slots)
            {
                var surface = new SlotSurface();
                CustomizeSlot.MakeGenericMethod(slot).Invoke(null, [surface]);
                probe.Apply(surface);
            }
        });

        Assert.Empty(failures);

        // And every surface landed, over the mapper's own customizations.
        Assert.All(slots, slot => Assert.True(store.Has(typeof(Note), slot, nameof(Valued.Value))));
        Assert.Equal("own:b", store.Value<Note, NoteDto, string>(nameof(NoteDto.Body))(new Note { Body = "b" }));
    }

    /// <summary>
    /// CONFIGURATOR PULLS, several at once, on a warm singleton — filled, so what races is the pull
    /// alone. Every thread makes the first map of a pair a configuration surface customizes, so each
    /// finds the member missing and has the container build the configuring type, which applies its
    /// surface to the mapper they all share; then each maps pairs that read the store on every
    /// object, while the surfaces of the others may still be arriving.
    /// </summary>
    [Fact]
    public void Concurrent_configuration_pulls_all_find_the_configuration()
    {
        var failures = new List<string>();

        for (int round = 0; round < 40; round++)
        {
            using ServiceProvider provider = SingletonProvider();
            Mapper mapper = provider.GetRequiredService<Mapper>();

            _ = mapper.Map<GadgetDto>(new Gadget { Code = "warm" });

            failures.AddRange(Together(8, _ => Configured(mapper) ?? ReadAlong(mapper))
                .Select(failure => $"round {round}: {failure}"));
        }

        Assert.Empty(failures);
    }

    /// <summary>The first map of the configured pair, which pulls the configuration in; null when every customized member came out right.</summary>
    private static string? Configured(Mapper mapper)
    {
        LedgerDto dto = mapper.Map<LedgerDto>(new Ledger { Code = "Ab7" });

        return Expect("LedgerDto.Label", "L-Ab7", dto.Label)
            ?? Expect("LedgerDto.Upper", "AB7", dto.Upper)
            ?? Expect("LedgerDto.Lower", "ab7", dto.Lower)
            ?? Expect("LedgerDto.Quoted", "'Ab7'", dto.Quoted)
            ?? Expect("LedgerDto.Twice", "Ab7Ab7", dto.Twice)
            ?? Expect("LedgerDto.Padded", " Ab7", dto.Padded);
    }

    /// <summary>
    /// A PACK ADDED AFTER THE FIRST MAP — not a supported place to add one, and kept loud rather than
    /// silent: the next map fills again and folds it in. That second fill runs while other threads
    /// are reading the shared store, so what it adds has to arrive as copies too — including the
    /// conversions the readers resolve, from a pack that was already there.
    /// </summary>
    [Fact]
    public void A_pack_added_after_the_first_map_is_folded_in_by_the_next_while_other_threads_read()
    {
        var mapper = new LateMapper();
        MapCustomizations store = mapper.Store;
        Type[] packs = Slots().Take(65).Select(slot => typeof(LatePack<>).MakeGenericType(slot)).ToArray();

        // One late pack before anyone reads, so the readers have a pack's conversion to resolve.
        AddLatePack.MakeGenericMethod(packs[0]).Invoke(mapper, null);
        _ = mapper.Store;

        string expected = packs[0].GetGenericArguments()[0] + ":7";

        List<string> failures = WhileReading(
            store,
            () =>
            {
                foreach (Type pack in packs.Skip(1))
                {
                    AddLatePack.MakeGenericMethod(pack).Invoke(mapper, null);
                    _ = mapper.Store;
                }
            },
            alsoRead: () =>
                store.Conversion<int, string>(typeof(LateMapper))(7) != "own:7" ? "The mapper's own conversion answered wrongly."
                : store.Conversion<int, string>(packs[0])(7) != expected ? "The first late pack's conversion answered wrongly."
                : null);

        Assert.Empty(failures);

        Assert.Same(store, mapper.Store);
        Assert.Equal("own:7", store.Conversion<int, string>(typeof(LateMapper))(7));
        Assert.All(packs, pack => Assert.Equal(
            pack.GetGenericArguments()[0] + ":7",
            store.Conversion<int, string>(pack)(7)));
    }

    // ----------------------------------------------------------------------------------------
    // What the fill must still do on one thread
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A CONSTRUCTOR THAT REACHES BACK into the mapper being filled, on the filling thread: a pack
    /// whose constructor reads the very store it is about to be folded into. It gets that store as
    /// far as it is filled — not a second fill, which would build the pack again and again, and not
    /// a wait for the lock its own thread holds.
    /// </summary>
    [Fact]
    public void A_constructor_that_reaches_back_during_the_fill_neither_loops_nor_deadlocks()
    {
        var mapper = new ReachingMapper();
        ReachingPack.Reset(mapper);

        MapCustomizations? store = null;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                store = mapper.Store;
            }
            catch (Exception caught)
            {
                error = caught;
            }
        }) { IsBackground = true };

        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The fill never finished: the constructor's call back waited on it.");
        Assert.True(error is null, error?.ToString());

        Assert.Equal(1, ReachingPack.Constructions);
        Assert.Same(store, ReachingPack.SeenDuringFill);

        // The fill then finished, and finishing it built nothing twice.
        Assert.Equal("reached:7", store!.Conversion<int, string>(typeof(ReachingPack))(7));
        Assert.Equal("own:7", store.Conversion<long, string>(typeof(ReachingMapper))(7));
        Assert.Same(store, mapper.Store);
        Assert.Equal(1, ReachingPack.Constructions);
    }

    /// <summary>
    /// TWO MAPPERS THAT INCLUDE EACH OTHER. Each one's fill brings in the other's registrations, and
    /// stops where the include path comes back to itself instead of building the two in turn forever.
    /// </summary>
    [Fact]
    public void Two_mappers_that_include_each_other_each_fill_once_with_both_halves()
    {
        MapCustomizations one = new CycleOne().Store;
        MapCustomizations two = new CycleTwo().Store;

        Assert.Equal("one:7", one.Conversion<int, string>(typeof(CycleOne))(7));
        Assert.Equal("two:7", one.Conversion<int, string>(typeof(CycleTwo))(7));
        Assert.Equal("one:7", two.Conversion<int, string>(typeof(CycleOne))(7));
        Assert.Equal("two:7", two.Conversion<int, string>(typeof(CycleTwo))(7));
    }

    /// <summary>The same cycle, with every first read of the mapper's store arriving at once.</summary>
    [Fact]
    public void Concurrent_first_reads_of_a_mapper_in_an_include_cycle_all_see_both_halves()
    {
        var failures = new List<string>();

        for (int round = 0; round < 100; round++)
        {
            var one = new CycleOne();

            failures.AddRange(Together(8, _ =>
            {
                MapCustomizations store = one.Store;

                return Expect("CycleTwo's conversion", "two:7", store.Conversion<int, string>(typeof(CycleTwo))(7))
                    ?? Expect("CycleOne's conversion", "one:7", store.Conversion<int, string>(typeof(CycleOne))(7));
            }).Select(failure => $"round {round}: {failure}"));
        }

        Assert.Empty(failures);
    }

    // ----------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="fill"/> on a thread of its own until it is held inside
    /// <see cref="HeldPack"/>'s constructor, sends <paramref name="read"/> in on another thread,
    /// waits until that one is either blocked or done, then lets the fill go. Returns what the read
    /// saw, or the exception it threw.
    /// </summary>
    private static string ReadWhileHeld(Action fill, Func<string> read)
    {
        HeldPack.Reset();

        var filling = new Thread(() => fill()) { IsBackground = true };
        filling.Start();

        Assert.True(HeldPack.Entered.Wait(TimeSpan.FromSeconds(10)), "The fill never reached the pack it folds in.");

        string? seen = null;

        var reading = new Thread(() =>
        {
            try
            {
                seen = read();
            }
            catch (Exception error)
            {
                seen = $"{error.GetType().Name}: {error.Message}";
            }
        }) { IsBackground = true };

        reading.Start();

        // Until the read is waiting — for the fill — or done, which it can only be if it read the
        // store without waiting for it.
        SpinWait.SpinUntil(
            () => !reading.IsAlive || (reading.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(10));

        HeldPack.Release.Set();

        Assert.True(filling.Join(TimeSpan.FromSeconds(10)), "The fill never finished.");
        Assert.True(reading.Join(TimeSpan.FromSeconds(10)), "The read never finished.");

        return seen!;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on <paramref name="threads"/> threads of their own, released at
    /// the same moment, and returns what went wrong: an answer the work reported as wrong, an
    /// exception, or a thread that never finished. A thread each, rather than the pool, so none of
    /// them waits for the pool to grow and they really do start together.
    /// </summary>
    private static List<string> Together(int threads, Func<int, string?> work)
    {
        var failures = new ConcurrentQueue<string>();
        using var start = new ManualResetEventSlim();
        var workers = new Thread[threads];

        for (int i = 0; i < threads; i++)
        {
            int index = i;

            workers[i] = new Thread(() =>
            {
                start.Wait();

                try
                {
                    if (work(index) is { } wrong)
                        failures.Enqueue(wrong);
                }
                catch (Exception error)
                {
                    failures.Enqueue($"{error.GetType().Name}: {error.Message}");
                }
            }) { IsBackground = true };

            workers[i].Start();
        }

        start.Set();

        foreach (Thread worker in workers)
        {
            if (!worker.Join(TimeSpan.FromSeconds(30)))
                failures.Enqueue("A thread did not finish within 30 seconds.");
        }

        return failures.ToList();
    }

    /// <summary>
    /// Runs <paramref name="write"/> while four threads read <paramref name="store"/> the way
    /// generated code does — its own customization fetched, a projection composed, a condition asked,
    /// and whatever <paramref name="alsoRead"/> adds — and returns what the readers got wrong. The
    /// probe mappers below all declare the same <see cref="Note"/> map, so one reader serves them all.
    /// </summary>
    private static List<string> WhileReading(MapCustomizations store, Action write, Func<string?>? alsoRead = null)
    {
        var failures = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();

        Thread[] readers = Enumerable.Range(0, 4).Select(number => new Thread(() =>
        {
            var note = new Note { Title = "t", Body = "b" };
            var dto = new NoteDto();

            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (store.Value<Note, NoteDto, string>(nameof(NoteDto.Body))(note) != "own:b")
                        failures.Enqueue("The mapper's own MapFrom answered wrongly.");

                    _ = store.Compose<Note, NoteDto>(source => new NoteDto { Title = source.Title });

                    if (!store.Condition(nameof(NoteDto.Title), note, dto, dto.Title, note.Title))
                        failures.Enqueue("The mapper's own Condition answered wrongly.");

                    if (alsoRead?.Invoke() is { } wrong)
                        failures.Enqueue(wrong);
                }
            }
            catch (Exception error)
            {
                failures.Enqueue($"{error.GetType().Name}: {error.Message}");
            }
        }) { IsBackground = true }).ToArray();

        foreach (Thread reader in readers)
            reader.Start();

        try
        {
            write();
        }
        finally
        {
            stop.Cancel();
        }

        foreach (Thread reader in readers)
        {
            if (!reader.Join(TimeSpan.FromSeconds(30)))
                failures.Enqueue("A reader did not finish within 30 seconds.");
        }

        return failures.ToList();
    }

    /// <summary>A provider with this assembly's generated mapper as a singleton, and what its mapper classes and configurator need.</summary>
    private static ServiceProvider SingletonProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInvoiceNumbering>(new InvoiceNumbering());
        services.AddShiftMapper(ServiceLifetime.Singleton);
        services.AddTransient<LedgerRepository>();

        return services.BuildServiceProvider();
    }

    /// <summary>One first map through a member the fill brings in; null when it came out right.</summary>
    private static string? FirstMap(IServiceProvider provider, int thread)
    {
        // A scope each, as each request and each timer has; the mapper is the one singleton behind them.
        using IServiceScope scope = provider.CreateScope();

        Mapper mapper = scope.ServiceProvider.GetRequiredService<Mapper>();
        IMapper door = scope.ServiceProvider.GetRequiredService<IMapper>();

        return (thread % 4) switch
        {
            // A MapFrom written in another mapper class.
            0 => Expect("GadgetDto.Code", "G-42", mapper.Map<GadgetDto>(new Gadget { Code = "42" }).Code),

            // One written in a mapper class built from the container, reached through the run-time door.
            1 => Expect("DoodadDto.Label", "IQ/thing", door.Map<Doodad, DoodadDto>(new Doodad { Name = "thing" }).Label),

            // A conversion from a pack another mapper class added.
            2 => Expect("CatalogueDto.Id", "H5", mapper.Map<CatalogueDto>(new Catalogue { Id = 5 }).Id),

            // A base map's MapFrom, found through the lineage the merge carried across.
            _ => Expect(
                "PremiumGadgetDto.Code",
                "G-77",
                door.Map<PremiumGadget, PremiumGadgetDto>(new PremiumGadget { Code = "77", Rank = 1 }).Code),
        };
    }

    /// <summary>
    /// Maps that read the store on every object — a ConstructUsing factory, three Conditions, and
    /// two conversions from a pack, whose cache a merge that changes conversions drops — many times
    /// over; null when every answer was right.
    /// </summary>
    private static string? ReadAlong(Mapper mapper)
    {
        for (int i = 0; i < 200; i++)
        {
            if (mapper.Map<CatalogSummaryDto>(new Catalog()).Label != "IQ/0")
                return "CatalogSummaryDto.Label: the ConstructUsing factory answered wrongly.";

            Profile profile = mapper.Map<Profile>(new ProfileUpdate { Name = "Ali" });

            if (profile.Name != "Ali" || profile.City != "unset-city" || profile.Age != -1)
                return "Profile: a Condition answered wrongly.";

            CatalogueDto catalogue = mapper.Map<CatalogueDto>(new Catalogue { Id = 5, Price = new Money { Amount = 2 } });

            if (catalogue.Id != "H5" || catalogue.Price != "$2")
                return $"CatalogueDto: a conversion answered '{catalogue.Id}' and '{catalogue.Price}'.";
        }

        return null;
    }

    private static string? Expect(string what, string expected, string actual) =>
        expected == actual ? null : $"{what} was '{actual}', expected '{expected}'.";

    /// <summary>Both conversion forms for one scope; null when both answered right.</summary>
    private static string? Resolve(MapCustomizations store, Type scope)
    {
        const string Mapping = "Note.Size -> NoteDto.Size";

        string text = store.Conversion<int, string>(scope)(7);

        if (text != scope.Name + ":7")
            return $"{scope.Name}: the one-argument form answered '{text}'.";

        long sum = store.ConversionWithMapping<int, long>(scope)(7, Mapping);

        return sum == 7 + Mapping.Length ? null : $"{scope.Name}: the two-argument form answered {sum}.";
    }

    /// <summary>
    /// Pairs the store has never held, as many as a test needs: <see cref="Slot{TFirst, TSecond}"/>
    /// closed over two of a couple of dozen types, which makes several hundred distinct types.
    /// </summary>
    private static IEnumerable<Type> Slots()
    {
        Type[] arguments =
        [
            typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte), typeof(uint),
            typeof(ulong), typeof(ushort), typeof(float), typeof(double), typeof(decimal), typeof(char),
            typeof(bool), typeof(string), typeof(object), typeof(Guid), typeof(DateTime), typeof(DateTimeOffset),
            typeof(TimeSpan), typeof(DateOnly), typeof(TimeOnly), typeof(Uri), typeof(Version), typeof(Type),
        ];

        foreach (Type first in arguments)
        {
            foreach (Type second in arguments)
                yield return typeof(Slot<,>).MakeGenericType(first, second);
        }
    }

    private static readonly MethodInfo CustomizeSlot =
        typeof(ConcurrencyTests).GetMethod(nameof(Customize), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo AddLatePack = typeof(LateMapper).GetMethod(nameof(LateMapper.AddLate))!;

    /// <summary>What one surface says: <c>Note → TSlot</c>'s <c>Value</c> member, from the note's title.</summary>
    private static void Customize<TSlot>(SlotSurface surface)
        where TSlot : Valued =>
        surface.For<Note, TSlot>().ForMember(d => d.Value, opt => opt.MapFrom(n => "surface:" + n.Title));

    // ----------------------------------------------------------------------------------------
    // The probes. Private, so the generator takes none of them into this assembly's generated
    // mapper: each one is built and read here, by hand, and nowhere else.
    // ----------------------------------------------------------------------------------------

    private sealed class Note
    {
        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;
    }

    private sealed class NoteDto
    {
        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;
    }

    /// <summary>The one member a surface customizes on every <see cref="Slot{TFirst, TSecond}"/>.</summary>
    private class Valued
    {
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>A destination type per pair of type arguments, so every surface brings a pair of its own.</summary>
    private sealed class Slot<TFirst, TSecond> : Valued
    {
    }

    /// <summary>A surface with a handle for any pair.</summary>
    private sealed class SlotSurface : ShiftMapperConfigurationSurface
    {
        public MapExpression<TSource, TDestination> For<TSource, TDestination>() => Map<TSource, TDestination>();
    }

    /// <summary>The mapper the surfaces are applied to, with a customization and a condition of its own.</summary>
    private sealed class SurfaceProbe : ShiftMapperBase
    {
        public SurfaceProbe() =>
            CreateMap<Note, NoteDto>()
                .ForMember(d => d.Body, opt => opt.MapFrom(n => "own:" + n.Body))
                .ForMember(d => d.Title, opt => opt.Condition((s, d, value) => value.Length > 0));

        public MapCustomizations Store => Customizations;

        public void Apply(ShiftMapperConfigurationSurface surface) => ApplyConfiguration(surface);
    }

    /// <summary>A pack whose constructor holds the fill open until the test lets it go.</summary>
    private sealed class HeldPack : ShiftMapperConversions
    {
        public static ManualResetEventSlim Entered { get; private set; } = new();

        public static ManualResetEventSlim Release { get; private set; } = new();

        public static void Reset()
        {
            Entered = new ManualResetEventSlim();
            Release = new ManualResetEventSlim();
        }

        public HeldPack()
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));

            CreateConversion<int, string>(memory: value => "held:" + value);
        }
    }

    private sealed class HeldMapper : ShiftMapperBase
    {
        public HeldMapper() => AddConversions<HeldPack>();

        public MapCustomizations Store => Customizations;
    }

    /// <summary>
    /// A mapper whose conversions are registered under many scopes: before each registration it
    /// says whose declarations it is making, which is what <see cref="ShiftMapperBase.DeclaringType"/> is for.
    /// </summary>
    private sealed class ManyScopesMapper : ShiftMapperBase
    {
        private Type? _declaring;

        public ManyScopesMapper(Type[] scopes)
        {
            foreach (Type scope in scopes)
                DeclareFor(scope);

            _declaring = null;
        }

        protected override Type DeclaringType => _declaring ?? GetType();

        public MapCustomizations Store => Customizations;

        // A method of its own, because a declaration written inside a loop is refused (SM0035).
        private void DeclareFor(Type scope)
        {
            _declaring = scope;
            string name = scope.Name;

            CreateConversion<int, string>(memory: value => name + ":" + value);
            CreateConversion<int, long>(memory: (value, mapping) => value + mapping.Length);
        }
    }

    /// <summary>A pack whose constructor reads the store of the mapper that is folding it in.</summary>
    private sealed class ReachingPack : ShiftMapperConversions
    {
        private static ReachingMapper? _root;

        public static int Constructions;

        public static MapCustomizations? SeenDuringFill;

        public static void Reset(ReachingMapper root)
        {
            _root = root;
            Constructions = 0;
            SeenDuringFill = null;
        }

        public ReachingPack()
        {
            // A second construction means the call back started a second fill — the loop the fill
            // is guarded against. Stopped here, rather than left to run until the stack overflows.
            if (Interlocked.Increment(ref Constructions) > 1)
                throw new InvalidOperationException("The pack was built again: reaching back started a second fill.");

            SeenDuringFill = _root!.Store;

            CreateConversion<int, string>(memory: value => "reached:" + value);
        }
    }

    private sealed class ReachingMapper : ShiftMapperBase
    {
        public ReachingMapper()
        {
            AddConversions<ReachingPack>();
            CreateConversion<long, string>(memory: value => "own:" + value);
        }

        public MapCustomizations Store => Customizations;
    }

    /// <summary>A mapper that takes packs after its first map, which only a test would do.</summary>
    private sealed class LateMapper : ShiftMapperBase
    {
        public LateMapper()
        {
            CreateMap<Note, NoteDto>()
                .ForMember(d => d.Body, opt => opt.MapFrom(n => "own:" + n.Body))
                .ForMember(d => d.Title, opt => opt.Condition((s, d, value) => value.Length > 0));

            CreateConversion<int, string>(memory: value => "own:" + value);
        }

        public MapCustomizations Store => Customizations;

        public void AddLate<TPack>()
            where TPack : ShiftMapperConversions => AddConversions<TPack>();
    }

    /// <summary>One late pack per type argument, each its own scope.</summary>
    private sealed class LatePack<T> : ShiftMapperConversions
    {
        public LatePack() => CreateConversion<int, string>(memory: value => typeof(T) + ":" + value);
    }
}

/// <summary>
/// One of two mapper classes that include each other, through the composition metadata at the top
/// of this file. Public, so the generated mapper of this assembly takes both in as well, and every
/// fill of it in this suite goes through the cycle.
/// </summary>
public sealed class CycleOne : ShiftMapperBase
{
    public CycleOne() => CreateConversion<int, string>(memory: value => "one:" + value);

    public MapCustomizations Store => Customizations;
}

/// <inheritdoc cref="CycleOne"/>
public sealed class CycleTwo : ShiftMapperBase
{
    public CycleTwo() => CreateConversion<int, string>(memory: value => "two:" + value);

    public MapCustomizations Store => Customizations;
}
