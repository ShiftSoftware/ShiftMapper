using System.Linq.Expressions;
using AutoMapper.QueryableExtensions;
using BenchmarkDotNet.Running;
using ShiftMapper.Benchmarks;

// ShiftMapper's benchmarks. Nothing here runs in CI — these take minutes and are for deciding
// whether a change to the generated shape was worth making, and for the numbers in the README.
//
//   dotnet run -c Release --project ShiftMapper.Benchmarks -- --filter *Comparison*
//   dotnet run -c Release --project ShiftMapper.Benchmarks -- --filter *
//   dotnet run -c Release --project ShiftMapper.Benchmarks -- --shapes
//
// Release only: BenchmarkDotNet refuses a Debug build, and it is right to.

if (args.Length == 1 && args[0] == "--shapes")
{
    Shapes.Print();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Needed only so the switcher above has an assembly to look in.</summary>
public partial class Program;

/// <summary>
/// THE QUERY-SHAPE COMPARISON — what each library's <c>ProjectTo</c> actually hands a provider.
///
/// <para>Timing how fast an expression is BUILT says little; what a database does with it is
/// decided by what is in it. Three things to read for in the trees below: whether the computed
/// members are inlined as expressions or left as calls a provider cannot translate; whether nested
/// objects are member-inits (one SQL projection) or method calls (client evaluation); and how each
/// treats a collection member.</para>
/// </summary>
internal static class Shapes
{
    public static void Print()
    {
        IQueryable<Invoice> invoices = new List<Invoice> { Sample.Invoice(lines: 1) }.AsQueryable();

        var shift = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ShiftMapper.Mapper>(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(Container.Build()).ServiceProvider);
        var mapperly = new MapperlyMapper();

        Show("ShiftMapper", shift.ProjectTo<InvoiceDto>(invoices).Expression);
        Show("AutoMapper", invoices.ProjectTo<InvoiceDto>(Competitors.AutoMapperConfiguration).Expression);
        Show("Mapperly", mapperly.ProjectToInvoiceDto(invoices).Expression);
    }

    private static void Show(string library, Expression expression)
    {
        // The Select's lambda is the projection; the Queryable.Select call around it is noise.
        Expression body = expression is MethodCallExpression { Arguments.Count: 2 } call
            && call.Arguments[1] is UnaryExpression { Operand: LambdaExpression lambda }
                ? lambda
                : expression;

        Console.WriteLine();
        Console.WriteLine("=== " + library + " ===");
        Console.WriteLine(Readable(body.ToString()));
    }

    /// <summary>Strips the noise a fully qualified expression string carries, and breaks it onto lines.</summary>
    private static string Readable(string text) =>
        text.Replace("ShiftMapper.Benchmarks.", string.Empty)
            .Replace("System.Collections.Generic.", string.Empty)
            .Replace("System.Linq.", string.Empty)
            .Replace(", ", ",\n    ")
            .Replace("{", "{\n    ")
            .Replace("}", "\n}");
}
