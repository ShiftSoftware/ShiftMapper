using System.Reflection;
using Microsoft.CodeAnalysis;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// ELEMENT CONVENTIONS — a member convention that also claims COLLECTIONS of its member type.
///
/// <para><c>List&lt;SelectDto&gt; Departments</c> from <c>ICollection&lt;Department&gt; Departments</c>: one
/// shaped value per element, each element's paths resolved on the element type, inline on both
/// backends. The write side is deliberately not derived — a collection of shaped values written
/// back is a reconciliation, not an assignment — and is reported rather than guessed at.</para>
/// </summary>
public class ElementConventionTests
{
    private const string Types =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using ShiftMapper;

        [AttributeUsage(AttributeTargets.Class)]
        public sealed class KeyAndNameAttribute : Attribute
        {
            public KeyAndNameAttribute(string value, string text) { Value = value; Text = text; }
            public string Value { get; }
            public string Text { get; }
        }

        public class SelectDTO
        {
            public string Value { get; set; } = "";
            public string? Text { get; set; }
        }

        [KeyAndName("Id", "Name")]
        public class Department
        {
            public long Id { get; set; }
            public string Name { get; set; } = "";
        }

        public class Service
        {
            public long Id { get; set; }
            public string Title { get; set; } = "";
        }

        public class Branch
        {
            public long Id { get; set; }
            public long DepartmentId { get; set; }
            public Department Department { get; set; } = new();
            public ICollection<Department> Departments { get; set; } = new List<Department>();
            public Service[] Services { get; set; } = Array.Empty<Service>();
        }

        public class BranchDto
        {
            public string Id { get; set; } = "";
            public SelectDTO Department { get; set; } = new();
            public List<SelectDTO> Departments { get; set; } = new();
            public SelectDTO[] Services { get; set; } = Array.Empty<SelectDTO>();
        }
        """;

    private const string Convention =
        """
                CreateMemberConvention<SelectDTO>()
                    .NameFrom<KeyAndNameAttribute>("Text")
                    .Fill(d => d.Value, "{Member}ID")
                    .FillIfPossible(d => d.Text, "{Member}.{NameOf}")
                    .ForEachElement()
                        .Fill(d => d.Value, "ID")
                        .FillIfPossible(d => d.Text, "{NameOf}");
        """;

    private static GeneratorRun RunMapper(string maps = "CreateMap<Branch, BranchDto>();") =>
        GeneratorHarness.Run(Types + $$"""
            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
            {{Convention}}
                    {{maps}}
                }
            }
            """);

    [Fact]
    public void A_collection_member_is_filled_element_by_element_on_both_backends()
    {
        GeneratorRun run = RunMapper();

        run.Compiles()
           // In memory: the builder over the source collection, each element a member-init.
           .Emits("Departments = global::ShiftMapper.ValueConverter.ToListOrEmpty<global::Department, global::SelectDTO>(source.Departments, item => new global::SelectDTO { Value = global::ShiftMapper.ValueConverter.ToInvariantString(item.Id), Text = item.Name })")
           // The container is the member's own: an array member gets ToArray.
           .Emits("Services = global::ShiftMapper.ValueConverter.ToArrayOrEmpty<global::Service, global::SelectDTO>(source.Services, item => new global::SelectDTO { Value = global::ShiftMapper.ValueConverter.ToInvariantString(item.Id) })")
           // In the projection: the two Enumerable calls a provider translates.
           .Emits("global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Select(source.Departments, item => new global::SelectDTO { Value = item.Id.ToString(), Text = item.Name }))");

        run.None("SM0001");
        run.None("SM0011");
    }

    [Fact]
    public void An_element_type_that_nominates_no_name_gets_the_id_only_shape()
    {
        GeneratorRun run = RunMapper();

        // Service has no [KeyAndName]: the FillIfPossible for Text is dropped, Value is still set.
        run.Compiles()
           .Emits("Services = global::ShiftMapper.ValueConverter.ToArrayOrEmpty<global::Service, global::SelectDTO>(source.Services, item => new global::SelectDTO { Value = global::ShiftMapper.ValueConverter.ToInvariantString(item.Id) })");

        run.None("SM0034");
    }

    [Fact]
    public void The_rule_runs()
    {
        GeneratorRun run = GeneratorHarness.Run(Types + $$"""
            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
            {{Convention}}
                    CreateMap<Branch, BranchDto>();
                }
            }

            public static class Probe
            {
                public static string Run()
                {
                    var mapper = new Mapper();

                    var branch = new Branch
                    {
                        Id = 1, DepartmentId = 5, Department = new Department { Id = 5, Name = "Sales" },
                        Departments = { new Department { Id = 5, Name = "Sales" }, new Department { Id = 6, Name = "Ops" } },
                        Services = new[] { new Service { Id = 9, Title = "Wash" } },
                    };

                    BranchDto dto = mapper.MapToBranchDto(branch);

                    return dto.Department.Value + ":" + dto.Department.Text + "|" +
                           string.Join(",", dto.Departments.Select(d => d.Value + ":" + d.Text)) + "|" +
                           dto.Services[0].Value + ":" + (dto.Services[0].Text ?? "-");
                }
            }
            """);

        Assembly assembly = run.Load();

        Assert.Equal("5:Sales|5:Sales,6:Ops|9:-", assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void The_write_direction_reports_the_collection_rather_than_demanding_a_map_from_the_shaped_type()
    {
        GeneratorRun run = RunMapper("CreateMap<Branch, BranchDto>().ReverseMap();");

        // BranchDto.Departments (List<SelectDTO>) onto Branch.Departments (ICollection<Department>) is a
        // reconciliation. Without the claim this would be SM0011 demanding a SelectDTO -> Department map;
        // with it the member is reported as what it is — two types nothing converts between — and left
        // to an AfterMap or the caller.
        run.Compiles();
        run.None("SM0011");

        Assert.Contains(run.All("SM0002"), n => n.GetMessage().Contains("Departments"));
    }

    [Fact]
    public void The_element_entries_travel_in_a_packages_metadata()
    {
        GeneratorRun run = GeneratorHarness.RunWithPackage(
            Types + $$"""
            public class PlatformConversions : ShiftMapperConversions
            {
                public PlatformConversions()
                {
            {{Convention}}
                }
            }
            """,
            """
            using ShiftMapper;

            public class AppMapper : ShiftMapperBase
            {
                public AppMapper()
                {
                    AddConversions<PlatformConversions>();
                    CreateMap<Branch, BranchDto>();
                }
            }
            """);

        run.Compiles()
           .Emits("Departments = global::ShiftMapper.ValueConverter.ToListOrEmpty<global::Department, global::SelectDTO>(source.Departments, item => new global::SelectDTO { Value = global::ShiftMapper.ValueConverter.ToInvariantString(item.Id), Text = item.Name })");

        run.None("SM0001");
    }

    [Fact]
    public void The_element_entries_are_written_to_the_metadata()
    {
        GeneratorRun run = RunMapper();

        Assert.Contains("ElementFill = new string[] { \"Value=ID\", \"?Text={NameOf}\" }", run.Metadata);
    }
}
