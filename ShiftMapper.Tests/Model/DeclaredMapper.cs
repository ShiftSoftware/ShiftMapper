using ShiftFramework;

namespace ShiftMapper.Tests.Model;

// ---------------------------------------------------------------------------------------------
// THE COMPILE-TIME EXTENSION CONTRACT, exercised across a REAL assembly boundary.
//
// ShiftFramework.Mock is referenced as a compiled library with no source here. Nothing in this file
// declares a conversion or that map; they arrive from the profile added below, whose declarations
// its OWN build wrote into its assembly as metadata.
// ---------------------------------------------------------------------------------------------

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
    public List<ShiftFileDTO> Files { get; set; } = new();

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
        // THE ONE LINE. ShiftFramework.Mock is a compiled assembly with no source here, and this is
        // identical to adding a profile from this project — which is the whole of Step 14.
        AddProfile<ShiftEntityProfile>();

        CreateMap<Document, DocumentDto>();
        CreateMap<Document, DocumentIdDto>();
    }
}
