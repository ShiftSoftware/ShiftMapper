namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// A SHARED PACK, exercised across a REAL assembly boundary.
//
// Contoso.Platform's own registration — AddContosoPlatform() — SHARES its pack with every project
// that references it. This project references it, so every mapper this project REGISTERS gets the
// pack: this mapper names nothing of the package's, composes nothing, and its long still renders as
// the package's hash id once it has been through AddShiftMapper. Kept apart from the other mappers
// so nothing else registers it, and what it gets from the share is all it has.
// ---------------------------------------------------------------------------------------------

public class Ticket
{
    public long Id { get; set; }

    public string Subject { get; set; } = string.Empty;
}

public class TicketDto
{
    /// <summary><c>long</c> to <c>string</c>: the package's rule, from the share alone.</summary>
    public string Id { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;
}

/// <summary>Declares one map and nothing else. See the note at the top of the file.</summary>
public partial class SharedRulesMapper : ShiftMapperBase
{
    public SharedRulesMapper() => CreateMap<Ticket, TicketDto>();
}
