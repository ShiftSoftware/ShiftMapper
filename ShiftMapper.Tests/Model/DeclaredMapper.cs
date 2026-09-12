using Contoso.Platform;

namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// THE COMPILE-TIME EXTENSION CONTRACT, exercised across a REAL assembly boundary.
//
// Contoso.Platform is referenced as a compiled library with no source here. Nothing in this file
// declares a conversion or that map; they arrive from the profile added below, whose declarations
// its OWN build wrote into its assembly as metadata.
// ---------------------------------------------------------------------------------------------

/// <summary>An entity that nominates its own display member, the way ShiftFramework's do.</summary>
[KeyAndName(nameof(Id), nameof(Name))]
public class Folder
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A source with the shape a member convention is written for: an id and the navigation beside it.
/// </summary>
public class FiledDocument
{
    public long FolderId { get; set; }

    public Folder? Folder { get; set; }
}

/// <summary>
/// The shaped destination. NOTHING in this project configures <c>Folder</c> — the rule is one
/// CreateMemberConvention in the framework's profile, and it names no type here.
/// </summary>
public class FiledDocumentDto
{
    public SelectDto Folder { get; set; } = new();
}

/// <summary>
/// An entity that nominates NO display member — no <c>[KeyAndName]</c> at all.
///
/// <para>THE ID-ONLY SHAPE, and it is common: whatever renders the list already holds the names, so
/// the response carries the key and nothing else. The framework's ONE rule serves it because its
/// text entry is a <c>FillIfPossible</c>.</para>
/// </summary>
public class Bin
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>A source with the navigation there — but pointing at a type that nominates nothing.</summary>
public class ShelvedDocument
{
    public long BinId { get; set; }

    public Bin? Bin { get; set; }
}

/// <summary>
/// The OTHER way an optional entry drops: a source carrying the foreign key and NO navigation
/// beside it, which is what a request body looks like.
/// </summary>
public class LooseDocument
{
    public long FolderId { get; set; }
}

/// <summary>Shaped like <see cref="FiledDocumentDto"/>, and filled by the same one rule.</summary>
public class ShelvedDocumentDto
{
    public SelectDto Bin { get; set; } = new();
}

public class Document
{
    public long Id { get; set; }

    public string Files { get; set; } = "[]";

    public List<long> Related { get; set; } = new();
}

public class DocumentDto
{
    /// <summary>Hash ids, which the framework declared with BOTH forms.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>A JSON column becoming objects, declared with a memory form only.</summary>
    public List<FileDto> Files { get; set; } = new();

    /// <summary>The hash-id rule again, one list element at a time.</summary>
    public List<string> Related { get; set; } = new();
}

/// <summary>A destination that touches ONLY the projectable pair, so it keeps its projection.</summary>
public class DocumentIdDto
{
    public string Id { get; set; } = string.Empty;

    public List<string> Related { get; set; } = new();
}

/// <summary>Its own mapper, so a package's rules do not reach the rest of the suite's maps.</summary>
public partial class DeclaredMapper : ShiftMapperBase
{
    public DeclaredMapper()
    {
        // THE ONE LINE. Contoso.Platform is a compiled assembly with no source here, and this is
        // identical to adding a profile from this project — which is the whole of the contract.
        AddProfile<PlatformProfile>();

        CreateMap<Document, DocumentDto>();
        CreateMap<Document, DocumentIdDto>();

        // Filled entirely by the framework's member convention.
        CreateMap<FiledDocument, FiledDocumentDto>();
        CreateMap<FiledDocumentDto, FiledDocument>();

        // THE SAME RULE, THE ID-ONLY SHAPE. Neither of these adds anything: in one the related type
        // nominates no display member, in the other there is no navigation to read one from. The
        // rule's text entry is a FillIfPossible, so it drops and the id is still set.
        CreateMap<ShelvedDocument, ShelvedDocumentDto>();
        CreateMap<LooseDocument, FiledDocumentDto>();
    }
}
