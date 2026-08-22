namespace ShiftMapper.Generator;

/// <summary>
/// A destination property whose value is another OBJECT ShiftMapper maps — either one of them
/// (<c>InvoiceLineDto.Product</c>) or a collection of them (<c>InvoiceDto.Lines</c>).
///
/// These cannot be settled while a single map is being analysed, and that is what this type is
/// for. Whether <c>ProductDto</c> can be filled depends on whether a <c>CreateMap&lt;Product,
/// ProductDto&gt;()</c> exists SOMEWHERE in the mapper — possibly further down the constructor,
/// possibly in another file of the same partial class. So the analysis records what it found and
/// leaves the verdict to <see cref="ShiftMapperGenerator"/>'s resolve pass, which runs once every
/// declaration of the class has been collected.
///
/// The verdict is then one of three: a map exists and the property is filled; no map exists and
/// the build stops with SM0011; or following it would go round in a loop and the build stops with
/// SM0012.
/// </summary>
internal sealed class NestedProperty
{
    public NestedProperty(
        string destination,
        string source,
        string sourceElementType,
        string destinationElementType,
        string destinationElementName,
        string? collectionBuilder,
        string destinationCollectionType,
        bool sourceIsNullable,
        bool canSetAfterConstruction)
    {
        Destination = destination;
        Source = source;
        SourceElementType = sourceElementType;
        DestinationElementType = destinationElementType;
        DestinationElementName = destinationElementName;
        CollectionBuilder = collectionBuilder;
        DestinationCollectionType = destinationCollectionType;
        SourceIsNullable = sourceIsNullable;
        CanSetAfterConstruction = canSetAfterConstruction;
    }

    /// <summary>The destination property being filled, e.g. <c>Product</c>.</summary>
    public string Destination { get; }

    /// <summary>The source property it is read from.</summary>
    public string Source { get; }

    /// <summary>
    /// The OBJECT type on the source side — <c>Product</c> for both a single
    /// <c>Product</c> property and a <c>List&lt;Product&gt;</c> one. This and
    /// <see cref="DestinationElementType"/> are the pair a <c>CreateMap</c> has to exist for.
    /// </summary>
    public string SourceElementType { get; }

    /// <summary>The object type on the destination side.</summary>
    public string DestinationElementType { get; }

    /// <summary>The destination object's short name, for readable diagnostics.</summary>
    public string DestinationElementName { get; }

    /// <summary>
    /// Which <c>ValueConverter</c> method builds the destination collection — <c>ToList</c>,
    /// <c>ToArray</c>, <c>ToHashSet</c> — or null when this is a single object rather than a
    /// collection.
    ///
    /// This is what makes a collection of objects work the same way a collection of ints does:
    /// the SHAPE is converted by the same helpers, and only the per-element step differs.
    /// </summary>
    public string? CollectionBuilder { get; }

    /// <summary>
    /// The destination property's declared type in full — <c>IReadOnlyList&lt;ProductDto&gt;</c>
    /// rather than <c>ProductDto</c>. Needed by the projection, which builds the collection with
    /// LINQ and has to state what it is producing.
    /// </summary>
    public string DestinationCollectionType { get; }

    /// <summary>Whether the source property can be null, so the generated code guards it.</summary>
    public bool SourceIsNullable { get; }

    /// <summary>False for an <c>init</c> property, which the update overload cannot assign.</summary>
    public bool CanSetAfterConstruction { get; }

    /// <summary>True when this is a collection of objects rather than a single one.</summary>
    public bool IsCollection => CollectionBuilder is not null;

    /// <summary>The pair a CreateMap must exist for, in the same spelling <c>MapModel.Key</c> uses.</summary>
    public string Key => SourceElementType + "->" + DestinationElementType;

}
