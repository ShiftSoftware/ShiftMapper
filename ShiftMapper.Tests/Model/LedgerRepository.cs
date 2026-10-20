namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// A CONFIGURATION SURFACE, end to end.
//
// A stand-in for a framework's repository base: a marked type whose closing type declares an
// implicit map, a surface with one handle for it, and a closing type that customizes the map in
// its Mapping lambda and hands the surface to the mapper when it is constructed. The generated
// lookup for the customized member names the closing type, so a map used before any repository
// was built has the container build one — the configurator pull — and finds the expression after.
// ---------------------------------------------------------------------------------------------

public class Ledger
{
    public string Code { get; set; } = string.Empty;

    public decimal Balance { get; set; }
}

public class LedgerDto
{
    public string Code { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    // Six members customized by LedgerRepository's surface and by nothing else — six, so that
    // applying the surface is enough work to overlap with another thread applying it.

    public string Label { get; set; } = string.Empty;

    public string Upper { get; set; } = string.Empty;

    public string Lower { get; set; } = string.Empty;

    public string Quoted { get; set; } = string.Empty;

    public string Twice { get; set; } = string.Empty;

    public string Padded { get; set; } = string.Empty;
}

/// <summary>The surface: one handle, the view map.</summary>
public sealed class LedgerMapping<TEntity, TDto> : ShiftMapperConfigurationSurface
{
    public MapExpression<TEntity, TDto> View => Map<TEntity, TDto>();
}

/// <summary>The marked base. Every type that closes it declares <c>TEntity → TDto</c>.</summary>
[ShiftMapperDeclaresMap("TEntity", "TDto")]
public abstract class LedgerStore<TEntity, TDto>
{
    /// <summary>Runs the configuration lambda and applies the surface, as a framework's base would on construction.</summary>
    protected LedgerStore(IMapper mapper, Action<LedgerMapping<TEntity, TDto>> mapping)
    {
        var surface = new LedgerMapping<TEntity, TDto>();
        mapping(surface);

        mapper.Configure(surface);
    }
}

/// <summary>The closing type, and the one place the <see cref="LedgerDto"/> members it names are customized.</summary>
public sealed class LedgerRepository : LedgerStore<Ledger, LedgerDto>
{
    public LedgerRepository(IMapper mapper)
        : base(mapper, m => m.View
            .ForMember(d => d.Label, opt => opt.MapFrom(e => "L-" + e.Code))
            .ForMember(d => d.Upper, opt => opt.MapFrom(e => e.Code.ToUpperInvariant()))
            .ForMember(d => d.Lower, opt => opt.MapFrom(e => e.Code.ToLowerInvariant()))
            .ForMember(d => d.Quoted, opt => opt.MapFrom(e => "'" + e.Code + "'"))
            .ForMember(d => d.Twice, opt => opt.MapFrom(e => e.Code + e.Code))
            .ForMember(d => d.Padded, opt => opt.MapFrom(e => e.Code.PadLeft(4))))
    {
    }
}
