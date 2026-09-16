using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// The generated implementation of <c>IShiftMapper</c> — the door a library comes in through when
/// it cannot name the application's mapper class.
///
/// These tests are about the SHAPE of what is emitted. What it does at runtime is covered in
/// ShiftMapper.Tests, against a real generated mapper and a real database.
/// </summary>
public class ShiftMapperInterfaceTests
{
    private const string TwoMaps =
        """
        using ShiftMapper;

        public class Source { public int Id { get; set; } }
        public class Destination { public int Id { get; set; } }
        public class Other { public int Id { get; set; } }
        public class OtherDto { public int Id { get; set; } }

        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper()
            {
                CreateMap<Source, Destination>();
                CreateMap<Other, OtherDto>();
            }
        }
        """;

    /// <summary>
    /// The interface is added by the GENERATED part. A base list may name a base class in only
    /// one part of a partial type, but any part may add interfaces — which is what lets the
    /// developer write `: ShiftMapperBase` and get the interface without repeating it.
    /// </summary>
    [Fact]
    public void The_generated_part_adds_the_interface_to_the_mapper()
    {
        GeneratorRun run = GeneratorHarness.Run(TwoMaps);

        run.Compiles().Emits("partial class TestMapper : global::ShiftMapper.IShiftMapper");
    }

    /// <summary>
    /// EXPLICIT implementations, all five, and this is the test that guards the reason.
    ///
    /// An implicit <c>Map&lt;TDestination&gt;(object)</c> on the class would accept every call the
    /// typed overloads refuse, so <c>mapper.Map&lt;SomeDto&gt;(unmappedThing)</c> would stop being
    /// a compile error and start being a runtime exception. The explicit form is invisible on the
    /// class, so the typed API keeps failing at build time.
    /// </summary>
    [Fact]
    public void Every_member_is_implemented_explicitly()
    {
        GeneratorRun run = GeneratorHarness.Run(TwoMaps);

        run.Compiles()
           .Emits("TDestination global::ShiftMapper.IShiftMapper.Map<TDestination>(object source)")
           .Emits("TDestination global::ShiftMapper.IShiftMapper.Map<TSource, TDestination>(TSource source)")
           .Emits("TDestination global::ShiftMapper.IShiftMapper.Map<TSource, TDestination>(TSource source, TDestination destination)")
           .Emits("global::System.Linq.IQueryable<TDestination> global::ShiftMapper.IShiftMapper.ProjectTo<TSource, TDestination>(")
           .Emits("bool global::ShiftMapper.IShiftMapper.CanMap(global::System.Type source, global::System.Type destination)")

           // The same members in their implicit spelling would be the bug this guards against.
           .DoesNotEmit("public virtual TDestination Map<TDestination>(object source)")
           .DoesNotEmit("public bool CanMap(");
    }

    /// <summary>
    /// The object door tests the EXACT runtime type before falling back to assignability, so a
    /// mapper holding maps for a base and a derived type answers with the one registered for what
    /// it was handed rather than whichever appears first.
    /// </summary>
    [Fact]
    public void The_object_door_tries_the_exact_type_before_assignability()
    {
        GeneratorRun run = GeneratorHarness.Run(TwoMaps);

        string generated = run.Compiles().Generated;

        int exact = generated.IndexOf("if (sourceType == typeof(global::Source))", StringComparison.Ordinal);
        int assignable = generated.IndexOf("if (source is global::Source)", StringComparison.Ordinal);

        Assert.True(exact >= 0, "the exact-type branch was not emitted");
        Assert.True(assignable >= 0, "the assignability branch was not emitted");
        Assert.True(exact < assignable, "assignability is tested before the exact type");
    }

    /// <summary>
    /// Nothing is re-implemented here: each branch forwards to the generated method that already
    /// exists, so the two doors cannot come to disagree about what a map does.
    /// </summary>
    [Fact]
    public void The_interface_forwards_to_the_generated_methods()
    {
        GeneratorRun run = GeneratorHarness.Run(TwoMaps);

        run.Compiles()
           .Emits("return Map<TDestination>((global::Source)source);")
           .Emits("return ProjectTo<TDestination>((global::System.Linq.IQueryable<global::Source>)(object)source);");
    }

    /// <summary>
    /// A pair of type arguments that names no map falls through to the runtime type rather than
    /// refusing. TSource is whatever the caller's own type parameter was bound to, and generic
    /// library code very often has a BASE of the thing it is actually mapping.
    /// </summary>
    [Fact]
    public void An_unmapped_TSource_falls_through_to_the_runtime_type()
    {
        GeneratorRun run = GeneratorHarness.Run(TwoMaps);

        run.Compiles()
           .Emits("return ((global::ShiftMapper.IShiftMapper)this).Map<TDestination>((object)source!);");
    }

    /// <summary>
    /// A struct destination has no update method — copying onto one would write to a copy — so it
    /// gets no branch in the update door either, and the message says why.
    /// </summary>
    [Fact]
    public void A_struct_destination_gets_no_update_branch()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public struct Destination { public int Id { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles()
           // Created, so the create doors and CanMap know about it,
           .Emits("if (sourceType == typeof(global::Source))")
           .Emits("if (destination == typeof(global::Destination) && typeof(global::Source).IsAssignableFrom(source))")
           // and not updated. The pair test is what the update door is made of; the create
           // dispatcher tests the destination on its own, so only the pair form is unique to it.
           .DoesNotEmit("typeof(TSource) == typeof(global::Source) && typeof(TDestination) == typeof(global::Destination)")
           .Emits("has no update method on purpose");
    }

    /// <summary>
    /// CanMap answers for the CREATE methods, so a destination ShiftMapper cannot construct
    /// (SM0004) is absent from it. Saying true there would be an invitation to call something
    /// that throws.
    /// </summary>
    [Fact]
    public void CanMap_leaves_out_a_destination_that_cannot_be_constructed()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public class Source { public int Id { get; set; } }
            public class Made { public int Id { get; set; } }
            public abstract class NotMade
            {
                public int Id { get; set; }
            }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateMap<Source, Made>();
                    CreateMap<Source, NotMade>();
                }
            }
            """);

        run.Single("SM0004");
        run.Compiles()
           .Emits("if (destination == typeof(global::Made) && typeof(global::Source).IsAssignableFrom(source))")
           .DoesNotEmit("destination == typeof(global::NotMade)");
    }

    /// <summary>
    /// A mapper with nothing in it still implements the interface, so registering it is not a
    /// special case — every method simply reports that there is no map.
    /// </summary>
    [Fact]
    public void A_mapper_with_no_maps_still_implements_the_interface()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            public partial class TestMapper : ShiftMapperBase
            {
            }
            """);

        run.Compiles()
           .Emits("partial class TestMapper : global::ShiftMapper.IShiftMapper")
           .Emits("bool global::ShiftMapper.IShiftMapper.CanMap(global::System.Type source, global::System.Type destination)")
           .Emits("return false;");
    }

    /// <summary>
    /// An internal mapper implements the public interface without any accessibility trouble:
    /// explicit members declare none, so there is no CS0051 to run into.
    /// </summary>
    [Fact]
    public void An_internal_mapper_implements_the_interface_too()
    {
        GeneratorRun run = GeneratorHarness.Run(
            """
            using ShiftMapper;

            internal class Source { public int Id { get; set; } }
            internal class Destination { public int Id { get; set; } }

            internal partial class TestMapper : ShiftMapperBase
            {
                public TestMapper() => CreateMap<Source, Destination>();
            }
            """);

        run.Compiles().Emits("partial class TestMapper : global::ShiftMapper.IShiftMapper");
    }
}
