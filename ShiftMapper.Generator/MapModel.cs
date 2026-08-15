using System.Collections.Immutable;

namespace ShiftMapper.Generator;

/// <summary>
/// Everything the generator needs to know about ONE registered map, worked out at
/// compile time from a single <c>CreateMap&lt;TSource, TDestination&gt;()</c> call.
///
/// Note it holds only plain strings — no Roslyn symbols. That is deliberate: this
/// object gets cached by the compiler between keystrokes, and symbols must not be
/// held onto across compilations.
/// </summary>
internal sealed class MapModel
{
    public MapModel(
        string sourceType,
        string destinationType,
        string methodName,
        ImmutableArray<string> propertyNames)
    {
        SourceType = sourceType;
        DestinationType = destinationType;
        MethodName = methodName;
        PropertyNames = propertyNames;
    }

    /// <summary>Fully qualified source type, e.g. <c>global::MyApp.Entities.Brand</c>.</summary>
    public string SourceType { get; }

    /// <summary>Fully qualified destination type, e.g. <c>global::MyApp.Dtos.BrandDto</c>.</summary>
    public string DestinationType { get; }

    /// <summary>Name of the method we will generate, e.g. <c>MapBrandToBrandDto</c>.</summary>
    public string MethodName { get; }

    /// <summary>Properties that exist on BOTH types with the same name and same type.</summary>
    public ImmutableArray<string> PropertyNames { get; }

    /// <summary>Identifies this map so duplicate registrations can be collapsed.</summary>
    public string Key => SourceType + "->" + DestinationType;
}
