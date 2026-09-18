using System.Reflection;
using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// PACK-LEVEL IGNORE RULES — "this member is mine", said once, as code.
///
/// <para>A framework owns some members of every entity: a key the save pipeline assigns, a flag the
/// request sets, a collection a pipeline attaches. It cannot write <c>opt.Ignore()</c> on maps it
/// never sees, and an attribute on every type is the thing a framework's user should not have to
/// write. So a pack says it once — <c>IgnoreMember&lt;EntityBase&gt;(e =&gt; e.Id, MemberRole.Destination)</c>
/// — and every map whose type declares, inherits or implements the member honours it.</para>
/// </summary>
public class IgnoreRuleTests
{
    private const string Types =
        """
        using System;
        using System.Collections.Generic;
        using ShiftMapper;

        public abstract class EntityBase { public long Id { get; set; } }

        public abstract class Entity<T> : EntityBase
        {
            public bool ReloadAfterSave { get; set; }
            public DateTime CreateDate { get; set; }
        }

        public interface ITaggable { List<string> Tags { get; set; } }

        public class Brand : Entity<Brand>, ITaggable
        {
            public string Name { get; set; } = "";
            public List<string> Tags { get; set; } = new();
        }

        public class BrandDto
        {
            public long Id { get; set; }
            public string Name { get; set; } = "";
            public DateTime CreateDate { get; set; }
            public List<string> Tags { get; set; } = new();
        }
        """;

    private const string Pack =
        """
        public class PlatformConversions : ShiftMapperConversions
        {
            public PlatformConversions()
            {
                IgnoreMember<EntityBase>(e => e.Id, MemberRole.Destination);          // never written from a request
                IgnoreMember(typeof(Entity<>), "ReloadAfterSave");                   // an open generic base, by name
                IgnoreMember<ITaggable>(e => e.Tags, MemberRole.Destination);        // owned by a pipeline
            }
        }
        """;

    private static string Mapper(string maps) =>
        $$"""
        public class AppMapper : ShiftMapperBase
        {
            public AppMapper()
            {
                AddConversions<PlatformConversions>();
                {{maps}}
            }
        }
        """;

    [Fact]
    public void An_ignored_destination_member_is_omitted_from_the_write_map_and_not_reported()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + Pack + Mapper("CreateMap<BrandDto, Brand>();"));

        // Id and Tags are never assigned onto the entity — in the initializer or the update overload —
        // and neither is reported as unmapped: the rule is an Ignore, said once.
        run.Compiles()
           .DoesNotEmit("Id = source.Id")
           .DoesNotEmit("Tags = ")
           .Emits("Name = source.Name");

        run.None("SM0001");
    }

    [Fact]
    public void An_ignored_destination_member_is_still_read_by_the_read_map()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + Pack + Mapper("CreateMap<Brand, BrandDto>();"));

        // The rule is about WRITING Id and Tags; reading them into the DTO is untouched.
        run.Compiles()
           .Emits("Id = source.Id,")
           .Emits("Tags = ");

        run.None("SM0001");
    }

    [Fact]
    public void A_member_ignored_in_both_roles_is_never_read_either()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + Pack + Mapper("CreateMap<Brand, BrandDto>().ReverseMap();"));

        // ReloadAfterSave: Both roles, on an open generic base — not a source candidate in either
        // direction, so a DTO carrying it would be told so (SM0001); this DTO does not carry it.
        run.Compiles().DoesNotEmit("ReloadAfterSave");
    }

    [Fact]
    public void The_rules_run()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + Pack + Mapper("CreateMap<Brand, BrandDto>().ReverseMap();") +
            """
            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new Mapper();

                    var brand = new Brand { Id = 7, Name = "Acme", ReloadAfterSave = true, Tags = { "a" } };
                    BrandDto dto = mapper.MapToBrandDto(brand);

                    var existing = new Brand { Id = 99, Name = "old", Tags = { "keep" } };
                    mapper.Map(new BrandDto { Id = 1, Name = "new", Tags = { "drop" } }, existing);

                    return dto.Id + "|" + dto.Tags.Count + "|" + existing.Id + "|" + existing.Name + "|" + existing.Tags[0];
                }
            }
            """);

        Assembly assembly = run.Load();

        // Read: Id and Tags cross. Write: Id and Tags are left alone, Name is written.
        Assert.Equal("7|1|99|new|keep", assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void A_rule_on_a_mapper_class_reaches_its_own_maps()
    {
        GeneratorRun run = GeneratorHarness.Run(Types +
            """
            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
                    IgnoreMember<EntityBase>(e => e.Id, MemberRole.Destination);
                    IgnoreMember(typeof(Entity<>), "ReloadAfterSave");
                    CreateMap<BrandDto, Brand>();
                }
            }
            """);

        run.Compiles().DoesNotEmit("destination.Id = source.Id;");
        run.None("SM0001");
    }

    [Fact]
    public void A_rule_travels_in_a_packages_metadata()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Types + Pack,
            """
            using ShiftMapper;

            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
                    AddConversions<PlatformConversions>();
                    CreateMap<BrandDto, Brand>();
                }
            }
            """);

        run.Compiles()
           .DoesNotEmit("destination.Id = source.Id;")
           .DoesNotEmit("destination.Tags = ")
           .DoesNotEmit("destination.ReloadAfterSave = ");

        run.None("SM0001");
    }

    [Fact]
    public void The_rules_are_written_to_the_metadata()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + Pack);

        Assert.Contains("ShiftMapperDeclaredIgnore(typeof(global::PlatformConversions), typeof(global::EntityBase), \"Id\", 2)", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredIgnore(typeof(global::PlatformConversions), typeof(global::Entity<>), \"ReloadAfterSave\", 0)", run.Metadata);
        Assert.Contains("ShiftMapperDeclaredIgnore(typeof(global::PlatformConversions), typeof(global::ITaggable), \"Tags\", 2)", run.Metadata);
    }

    [Fact]
    public void A_rule_does_not_reach_a_map_whose_types_do_not_carry_the_member()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + Pack +
            """
            // A domain type with its own Id, not an EntityBase: the rule is about EntityBase.Id, not the name.
            public class Country { public long Id { get; set; } public string Name { get; set; } = ""; }
            public class CountryDto { public long Id { get; set; } public string Name { get; set; } = ""; }

            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
                    AddConversions<PlatformConversions>();
                    CreateMap<CountryDto, Country>();
                }
            }
            """);

        run.Compiles().Emits("destination.Id = source.Id;");
    }
}
