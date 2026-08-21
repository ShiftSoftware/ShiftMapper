namespace ShiftMapper.Generator;

/// <summary>
/// One destination property that IS mapped, but not by copying the value straight across —
/// its type had to be converted first.
///
/// Recorded alongside <see cref="UnmappedProperty"/> and for the same reason: so the
/// developer hears about it. The difference is what there is to say. An unmapped property is
/// a hole to be filled; a converted one works, and only the ones carrying a caveat are
/// mentioned at all — a lossy conversion becomes SM0008 and a conversion that parses text at
/// runtime becomes SM0009, while a widening like <c>int</c> to <c>long</c> says nothing
/// because there is nothing to say.
///
/// All of them, caveat or not, are also named where the developer will run into them: the
/// update overloads carry them in an XML <c>&lt;remarks&gt;</c>, so the tooltip in the editor
/// says which properties went through a conversion without anybody opening the generated
/// file, and the create method — which serves several destinations from one body, and so has
/// no single summary to hang a remark on — gets a plain comment beside the branch instead.
/// </summary>
internal sealed class ConvertedProperty
{
    public ConvertedProperty(
        string propertyName,
        string sourcePropertyType,
        string destinationPropertyType,
        ConversionRisk risk,
        string? note)
    {
        PropertyName = propertyName;
        SourcePropertyType = sourcePropertyType;
        DestinationPropertyType = destinationPropertyType;
        Risk = risk;
        Note = note;
    }

    /// <summary>Name as the DESTINATION type declares it.</summary>
    public string PropertyName { get; }

    /// <summary>Short type name of the source property, e.g. <c>decimal</c>.</summary>
    public string SourcePropertyType { get; }

    /// <summary>Short type name of the destination property, e.g. <c>string</c>.</summary>
    public string DestinationPropertyType { get; }

    /// <summary>What, if anything, the developer should be told — see <see cref="ConversionRisk"/>.</summary>
    public ConversionRisk Risk { get; }

    /// <summary>The tail of the SM0008 message explaining what this conversion can lose.</summary>
    public string? Note { get; }

    /// <summary>How this pair reads in a remark, e.g. <c>Price (decimal to string)</c>.</summary>
    public string Describe() => $"{PropertyName} ({SourcePropertyType} to {DestinationPropertyType})";
}
