using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// NULLABLE WARNINGS IN THE GENERATED FILE. Every test here compiles the generated code with nullable
/// warnings treated as errors (<see cref="GeneratorRunAssertions.CompilesWithoutNullableWarnings"/>).
///
/// <para>A nullable warning in the generated file is one the developer can neither fix nor suppress:
/// they cannot edit the file, and it has no line in their own code for a <c>#pragma</c>. Two of them
/// were found in a consumer on ShiftMapper 0.3.0, both from the framework's own rules:</para>
///
/// <list type="bullet">
/// <item>CS8604. A conversion was called with type arguments that had lost their <c>?</c>
/// (<c>Conversion&lt;string, List&lt;FileDto&gt;&gt;</c>), so passing a <c>string?</c> member to it
/// was a warning. The type arguments now carry the members' nullable annotations.</item>
/// <item>CS8602. The projection of the write direction of a select convention read the key with no
/// null check (<c>source.Campaign.Value</c>), where <c>Map</c> already checks. The projection now
/// checks the same way.</item>
/// </list>
///
/// <para>Neither change alters what a map produces. Nullable annotations do not exist at run time,
/// and the new check in the projection gives the value <c>Map</c> already gives.</para>
/// </summary>
public class NullableWarningTests
{
    private const string Usings =
        """
        using ShiftMapper;
        using System;
        using System.Collections.Generic;
        using System.Linq;

        """;

    /// <summary>
    /// The framework's rules, in the shape the framework writes them: files stored as JSON text
    /// (method groups, no query form), text that must be a number (with a query form), and a select
    /// convention whose Value entry is reversible.
    /// </summary>
    private const string FrameworkRules =
        """
        public class FileDto { public string Name { get; set; } = ""; }

        public class SelectDTO
        {
            public string Value { get; set; } = "";
            public string Text { get; set; } = "";
        }

        public static class Files
        {
            public static List<FileDto>? Read(string? text) =>
                string.IsNullOrWhiteSpace(text)
                    ? new List<FileDto>()
                    : text.Split(',').Select(name => new FileDto { Name = name }).ToList();

            public static string? Write(List<FileDto>? files) =>
                files is null ? null : string.Join(",", files.Select(file => file.Name));
        }

        public class FrameworkRules : ShiftMapperConversions
        {
            public FrameworkRules()
            {
                CreateMemberConvention<SelectDTO>()
                    .Fill(d => d.Value, "{Member}ID")
                    .FillIfPossible(d => d.Text, "{Member}.Name");

                CreateConversion<string?, List<FileDto>?>(memory: Files.Read);
                CreateConversion<List<FileDto>?, string?>(memory: Files.Write);

                CreateConversion<string?, long>(
                    memory: text => long.Parse(text!),
                    query: text => Convert.ToInt64(text));
            }
        }

        """;

    /// <summary>
    /// Two maps, as a consumer has them. A claim with files, whose projection is lost to the file
    /// conversion (SM0030). And an entry with two keys, a required one (<c>long CampaignID</c>) and
    /// an optional one (<c>long? InspectionTypeID</c>), whose projection is kept. Both select
    /// members of the DTO are nullable, as they are in the consumer that found this.
    /// </summary>
    private const string Application =
        """
        public class Campaign { public long ID { get; set; } public string Name { get; set; } = ""; }

        public class InspectionType { public long ID { get; set; } public string Name { get; set; } = ""; }

        public class Claim
        {
            public long ID { get; set; }
            public string? Attachments { get; set; }
        }

        public class ClaimDto
        {
            public string ID { get; set; } = "";
            public List<FileDto>? Attachments { get; set; }
        }

        public class Entry
        {
            public long ID { get; set; }
            public long CampaignID { get; set; }
            public Campaign? Campaign { get; set; }
            public long? InspectionTypeID { get; set; }
            public InspectionType? InspectionType { get; set; }
        }

        public class EntryDto
        {
            public string ID { get; set; } = "";
            public SelectDTO? Campaign { get; set; }
            public SelectDTO? InspectionType { get; set; }
        }

        public partial class TestMapper : ShiftMapperBase
        {
            public TestMapper()
            {
                AddConversions<FrameworkRules>();
                CreateMap<Claim, ClaimDto>().ReverseMap();
                CreateMap<Entry, EntryDto>().ReverseMap();
            }
        }

        """;

    /// <summary>
    /// Maps a DTO whose optional select is null with <c>Map</c> and with the projection, run in
    /// memory, and returns both results as one line, so a test can see that they agree.
    /// </summary>
    private const string Probe =
        """
        public static class Probe
        {
            public static string Run()
            {
                var mapper = new Mapper();
                var dto = new EntryDto { ID = "1", Campaign = new SelectDTO { Value = "7" }, InspectionType = null };

                Entry mapped = mapper.MapToEntry(dto);
                Entry projected = new[] { dto }.AsQueryable().ProjectTo<Entry>(mapper).Single();

                return Describe(mapped) + " | " + Describe(projected);
            }

            private static string Describe(Entry entry) =>
                entry.CampaignID + "," + (entry.InspectionTypeID?.ToString() ?? "null");
        }

        """;

    // -----------------------------------------------------------------
    // CS8604 — THE TYPE ARGUMENTS OF A CONVERSION CALL.
    // -----------------------------------------------------------------

    /// <summary>
    /// A conversion declared in a PACKAGE, called on nullable members. The package's metadata records
    /// the pair with <c>typeof(...)</c>, which cannot carry a <c>?</c>, so the annotations come from
    /// the members being converted. This is the consumer's case.
    /// </summary>
    [Fact]
    public void A_conversion_from_a_package_is_called_with_the_members_nullability()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Usings + "namespace Framework;\n\n" + FrameworkRules,
            Usings + "using Framework;\n\n" + Application);

        run.CompilesWithoutNullableWarnings()
           .Emits("Customizations.Conversion<string?, global::System.Collections.Generic.List<global::Framework.FileDto>?>(typeof(global::Framework.FrameworkRules))(source.Attachments)")
           .Emits("Customizations.Conversion<global::System.Collections.Generic.List<global::Framework.FileDto>?, string?>(typeof(global::Framework.FrameworkRules))(source.Attachments)");
    }

    /// <summary>
    /// The same rules declared in the PROJECT. The generated call is the same text as for a package,
    /// because it takes its annotations from the members in both cases.
    /// </summary>
    [Fact]
    public void A_conversion_from_the_project_is_called_the_same_way()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + FrameworkRules + Application);

        run.CompilesWithoutNullableWarnings()
           .Emits("Customizations.Conversion<string?, global::System.Collections.Generic.List<global::FileDto>?>(typeof(global::FrameworkRules))(source.Attachments)")
           .Emits("Customizations.Conversion<global::System.Collections.Generic.List<global::FileDto>?, string?>(typeof(global::FrameworkRules))(source.Attachments)");
    }

    /// <summary>
    /// A conversion WITH a query form, on a nullable member. The projection carries the conversion
    /// as a <c>Splice</c> marker with the same type arguments, so it was the same warning there.
    /// </summary>
    [Fact]
    public void A_projected_conversion_is_spliced_with_the_members_nullability()
    {
        GeneratorRun run = GeneratorHarness.Run(
            Usings +
            """
            public class Code { public string Text { get; set; } = ""; }

            public class Item { public long Id { get; set; } public string? Code { get; set; } }

            public class ItemDto { public string Id { get; set; } = ""; public Code? Code { get; set; } }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<string?, Code?>(
                        text => text == null ? null : new Code { Text = text },
                        text => text == null ? null : new Code { Text = text });

                    CreateMap<Item, ItemDto>();
                }
            }
            """);

        run.CompilesWithoutNullableWarnings()
           .Emits("Customizations.Conversion<string?, global::Code?>(typeof(global::TestMapper))(source.Code)")
           .Emits("global::ShiftMapper.MapCustomizations.Splice<string?, global::Code?>(source.Code, typeof(global::TestMapper))");
    }

    /// <summary>
    /// A non-nullable member keeps the spelling it always had, so a mapper with no nullable members
    /// generates exactly what it did before.
    /// </summary>
    [Fact]
    public void A_non_nullable_member_keeps_its_spelling()
    {
        GeneratorRun run = GeneratorHarness.Run(
            Usings +
            """
            public class Brand { public long Id { get; set; } }
            public class BrandDto { public string Id { get; set; } = ""; }

            public partial class TestMapper : ShiftMapperBase
            {
                public TestMapper()
                {
                    CreateConversion<long, string>(id => "H" + id, id => "H" + id);
                    CreateMap<Brand, BrandDto>();
                }
            }
            """);

        run.CompilesWithoutNullableWarnings()
           .Emits("Customizations.Conversion<long, string>(typeof(global::TestMapper))(source.Id)")
           .Emits("global::ShiftMapper.MapCustomizations.Splice<long, string>(source.Id, typeof(global::TestMapper))");
    }

    // -----------------------------------------------------------------
    // CS8602 — THE KEY OF A SELECT, READ IN A PROJECTION.
    // -----------------------------------------------------------------

    /// <summary>
    /// The write direction of a select convention, in the PROJECTION. <c>Map</c> reads the key as
    /// <c>(source.Campaign is null ? default(string)! : source.Campaign.Value)</c>; the projection
    /// now reads it the same way. It writes <c>== null</c> because an expression tree cannot
    /// contain an <c>is</c> pattern (CS8122).
    /// </summary>
    [Fact]
    public void The_projection_checks_a_select_for_null_before_reading_its_key()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Usings + "namespace Framework;\n\n" + FrameworkRules,
            Usings + "using Framework;\n\n" + Application);

        run.CompilesWithoutNullableWarnings()
           // Map, unchanged.
           .Emits("(source.Campaign is null ? default(string)! : source.Campaign.Value)")
           .Emits("(source.InspectionType is null ? default(string)! : source.InspectionType.Value)")
           // The projection, now checked the same way.
           .Emits("global::ShiftMapper.MapCustomizations.Splice<string, long>((source.Campaign == null ? default(string)! : source.Campaign.Value), typeof(global::Framework.FrameworkRules))")
           .Emits("global::ShiftMapper.ValueConverter.ParseOrNull<long>((source.InspectionType == null ? default(string)! : source.InspectionType.Value), \"EntryDto.InspectionType.Value -> Entry.InspectionTypeID\")")
           .DoesNotEmit("(source.Campaign.Value, typeof(")
           .DoesNotEmit("(source.InspectionType.Value, \"");
    }

    /// <summary>
    /// What the check changes at run time: a projection run in memory over a DTO whose optional
    /// select is null now gives the value <c>Map</c> gives, a null key. Before, it threw a
    /// <see cref="NullReferenceException"/>. A database never runs this projection: repositories
    /// read entities, not DTOs.
    /// </summary>
    [Fact]
    public void The_projection_gives_the_key_that_Map_gives()
    {
        GeneratorRun run = GeneratorHarness.Run(Usings + FrameworkRules + Application + Probe);

        run.CompilesWithoutNullableWarnings();

        Assert.Equal("7,null | 7,null", run.Load().GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }
}
