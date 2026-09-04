; Unshipped analyzer release
; Rules that exist in the current build but have not been published in a release yet.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SM0001 | ShiftMapper | Warning | Destination property has no matching source property
SM0002 | ShiftMapper | Warning | Destination property is not mapped because ShiftMapper does not convert between the two types
SM0003 | ShiftMapper | Warning | Destination property is not mapped because its setter is not public
SM0004 | ShiftMapper | Warning | Destination type has no constructor ShiftMapper can call
SM0005 | ShiftMapper | Warning | No mapping code was generated for a ShiftMapperBase-derived class
SM0006 | ShiftMapper | Info | Reverse map leaves a destination property unmapped
SM0007 | ShiftMapper | Warning | Destination property matches several source properties when case is ignored
SM0008 | ShiftMapper | Info | Destination property is mapped through a conversion that can lose information
SM0009 | ShiftMapper | Info | Destination property is mapped by parsing text at runtime
SM0010 | ShiftMapper | Warning | Destination property is mapped through a conversion that can change the value
SM0011 | ShiftMapper | Error | Nested object property has no CreateMap registered for its types
SM0012 | ShiftMapper | Error | Nested object maps form a circular graph
SM0013 | ShiftMapper | Warning | Destination cannot be created because a constructor parameter cannot be filled
SM0014 | ShiftMapper | Warning | Destination cannot be created because a required member is not mapped
SM0015 | ShiftMapper | Info | Map cannot be projected because it builds its destination with ConstructUsing
