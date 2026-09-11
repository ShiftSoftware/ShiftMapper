using System.Linq;
using ShiftMapper.Generator.Tests.Infrastructure;
using Xunit;

namespace ShiftMapper.Generator.Tests;

/// <summary>
/// SM0035 — "configuration the generator cannot bake must be an error, never a silent default."
///
/// <para><b>WHAT THIS REPLACED.</b> A declaration written inside an <c>if</c>, a loop, a ternary, a
/// <c>switch</c>, a <c>try</c>, a lambda or a local function used to produce output BYTE-IDENTICAL
/// to writing it plainly in the constructor, with no diagnostic at all. The condition was silently
/// discarded. When the branch that was baked then did not run, the two backends disagreed: an
/// in-memory <c>Map</c> threw from the customization store while <c>ProjectTo</c> quietly dropped
/// the member.</para>
///
/// <para><b>The half of this suite that matters most is the ALLOWED half.</b> A position rule is
/// easy to write and easy to write too broadly — the expression-bodied constructor
/// <c>public TestMapper() =&gt; CreateMap&lt;A, B&gt;();</c> is the dominant spelling across this
/// repository's own tests, and a rule that rejected it would be worse than the bug it fixes.</para>
/// </summary>
public class UnbakeablePositionTests
{
    private const string Types =
        """
        using ShiftMapper;
        using System;
        using System.Collections.Generic;

        public class Source { public string Name { get; set; } = ""; public long Id { get; set; } }
        public class Destination { public string Name { get; set; } = ""; public long Id { get; set; } }
        """;

    private static GeneratorRun Run(string body) => GeneratorHarness.Run(Types + "\n" + body);

    /// <summary>One mapper body, wrapped in the class the harness expects.</summary>
    private static GeneratorRun Mapper(string members) =>
        Run($$"""
            public partial class TestMapper : ShiftMapperBase
            {
            {{members}}
            }
            """);

    // -----------------------------------------------------------------
    // THE POSITIONS THAT CANNOT BE BAKED.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE HEADLINE. A runtime condition cannot be honoured at compile time, and baking it
    /// unconditionally — which is what happened before — is exactly what the rule forbids.
    /// </summary>
    [Fact]
    public void A_declaration_inside_an_if_is_an_error()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper(bool flag)
                {
                    if (flag) { CreateMap<Source, Destination>(); }
                }
            """);

        string message = run.Single("SM0035").GetMessage();

        Assert.Contains("CreateMap", message);
        Assert.Contains("'if'", message);

        // And it says what to do instead, which is the whole value of an error over silence.
        Assert.Contains("Move it", message);
    }

    /// <summary>The <c>else</c> half is the same mistake and gets the same answer.</summary>
    [Fact]
    public void A_declaration_inside_an_else_is_an_error()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper(bool flag)
                {
                    if (flag) { } else { CreateMap<Source, Destination>(); }
                }
            """);

        Assert.Contains("'else'", run.Single("SM0035").GetMessage());
    }

    /// <summary>
    /// A loop is more misleading than an <c>if</c>: the developer plausibly believes N maps were
    /// registered, and exactly one was, with the loop variable playing no part at all.
    /// </summary>
    [Fact]
    public void A_declaration_inside_a_loop_is_an_error()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper(List<int> items)
                {
                    foreach (int i in items) { CreateMap<Source, Destination>(); }
                }
            """);

        Assert.Contains("loop", run.Single("SM0035").GetMessage());
    }

    [Fact]
    public void A_declaration_inside_a_switch_is_an_error()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper(int mode)
                {
                    switch (mode) { case 1: CreateMap<Source, Destination>(); break; }
                }
            """);

        Assert.Contains("'switch'", run.Single("SM0035").GetMessage());
    }

    [Fact]
    public void A_declaration_inside_a_try_is_an_error()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper()
                {
                    try { CreateMap<Source, Destination>(); } catch { }
                }
            """);

        Assert.Contains("'try'", run.Single("SM0035").GetMessage());
    }

    [Fact]
    public void A_declaration_inside_a_local_function_is_an_error()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper()
                {
                    void Add() { CreateMap<Source, Destination>(); }
                    Add();
                }
            """);

        Assert.Contains("local function", run.Single("SM0035").GetMessage());
    }

    [Fact]
    public void A_declaration_inside_a_lambda_is_an_error()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper()
                {
                    Action add = () => CreateMap<Source, Destination>();
                    add();
                }
            """);

        Assert.Contains("lambda", run.Single("SM0035").GetMessage());
    }

    /// <summary>
    /// EVERY declaration API, not just <c>CreateMap</c>. A convention or a conversion smuggled into
    /// a branch is the same lie, and the message names which call it was.
    /// </summary>
    [Fact]
    public void Every_declaration_api_is_covered()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper(bool flag)
                {
                    if (flag)
                    {
                        CreateConversion<long, string>(id => id.ToString(), id => id.ToString());
                        CreateMemberConvention<Destination>();
                    }

                    CreateMap<Source, Destination>();
                }
            """);

        string[] messages =
        [
            .. run.Diagnostics.Where(d => d.Id == "SM0035").Select(d => d.GetMessage())
        ];

        Assert.Equal(2, messages.Length);
        Assert.Contains(messages, m => m.Contains("CreateConversion"));
        Assert.Contains(messages, m => m.Contains("CreateMemberConvention"));

        // And the legitimate CreateMap beside them is untouched.
        Assert.DoesNotContain(messages, m => m.Contains("'CreateMap'"));
    }

    /// <summary>It points at the CALL, not at the class — the difference between a usable error and a hunt.</summary>
    [Fact]
    public void The_error_points_at_the_offending_call()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper(bool flag)
                {
                    CreateMap<Source, Destination>();
                    if (flag) { CreateMap<Destination, Source>(); }
                }
            """);

        Assert.Equal("CreateMap<Destination, Source>()", run.CodeUnder(run.Single("SM0035")));
    }

    // -----------------------------------------------------------------
    // THE POSITIONS THAT MUST KEEP WORKING. This half is the load-bearing one.
    // -----------------------------------------------------------------

    /// <summary>
    /// THE SPELLING THE WHOLE REPOSITORY USES. An expression-bodied constructor appears fifty-odd
    /// times across this test project alone; a rule that rejected it would break the suite on the
    /// day it landed.
    /// </summary>
    [Fact]
    public void An_expression_bodied_constructor_is_allowed()
    {
        GeneratorRun run = Mapper("    public TestMapper() => CreateMap<Source, Destination>();");

        run.Compiles().None("SM0035");
        Assert.Contains("MapToDestination", run.Generated);
    }

    /// <summary>The ordinary block-bodied constructor, for completeness.</summary>
    [Fact]
    public void A_block_bodied_constructor_is_allowed()
    {
        Mapper(
            """
                public TestMapper()
                {
                    CreateMap<Source, Destination>();
                }
            """).Compiles().None("SM0035");
    }

    /// <summary>
    /// A HELPER METHOD IS FINE, and this is the case the rule was deliberately kept narrow for: a
    /// mapper with two hundred maps splits them across AddCatalogMaps() / AddOrderMaps(), and an
    /// unconditional call is unconditional wherever it sits.
    /// </summary>
    [Fact]
    public void A_helper_method_is_allowed()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper() { AddMaps(); }

                private void AddMaps()
                {
                    CreateMap<Source, Destination>();
                }
            """);

        run.Compiles().None("SM0035");
        Assert.Contains("MapToDestination", run.Generated);
    }

    /// <summary>
    /// REACHABILITY IS NOT CHASED, on purpose. Proving a private helper is never called needs a call
    /// graph and the answer is unbounded — another partial part, a source-generated part, DI or
    /// reflection can all reach it. A rule whose false-positive rate cannot be bounded by reading one
    /// file is worse than no rule, so this stops at the line it can actually draw.
    /// </summary>
    [Fact]
    public void An_uncalled_helper_is_not_reported()
    {
        Mapper(
            """
                public TestMapper() { }

                private void AddMaps()
                {
                    CreateMap<Source, Destination>();
                }
            """).Compiles().None("SM0035");
    }

    /// <summary>
    /// A DECLARATION'S OWN LAMBDAS ARE NOT "inside a lambda". Every real chain nests them —
    /// <c>ForMember(d =&gt; d.X, o =&gt; o.MapFrom(...))</c> — and a naive ancestor walk would fire on
    /// all of them. The check runs on the chain's ROOT, whose lambdas are descendants, never
    /// ancestors.
    /// </summary>
    [Fact]
    public void A_declarations_own_lambdas_are_not_reported()
    {
        GeneratorRun run = Mapper(
            """
                public TestMapper()
                {
                    CreateMap<Source, Destination>()
                        .ForMember(d => d.Name, o => o.MapFrom(s => s.Name + "!"))
                        .ForAllMembers(o => o.Condition((s, d) => true));

                    CreateConversion<long, string>(id => id.ToString(), id => id.ToString());

                    CreateMemberConvention<Destination>()
                        .Fill(d => d.Name, "{Member}Name");
                }
            """);

        run.None("SM0035");
    }

    /// <summary>A chain kept in a local is an ordinary statement and still works.</summary>
    [Fact]
    public void A_chain_held_in_a_local_is_allowed()
    {
        Mapper(
            """
                public TestMapper()
                {
                    var map = CreateMap<Source, Destination>();
                    map.ForMember(d => d.Name, o => o.Ignore());
                }
            """).None("SM0035");
    }

    /// <summary>
    /// A method on the mapper that happens to share a name with a declaration API is not accused of
    /// anything — the rule binds the symbol before it says a word.
    /// </summary>
    [Fact]
    public void A_users_own_method_named_CreateMap_is_not_reported()
    {
        Mapper(
            """
                public TestMapper(bool flag)
                {
                    if (flag) { CreateMap("not ours"); }
                    CreateMap<Source, Destination>();
                }

                private void CreateMap(string label) { }
            """).None("SM0035");
    }
}
