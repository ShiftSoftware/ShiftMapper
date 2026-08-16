; Unshipped analyzer release
; Rules that exist in the current build but have not been published in a release yet.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SM0001 | ShiftMapper | Warning | Destination property has no matching source property
SM0002 | ShiftMapper | Warning | Destination property is not mapped because the types differ
SM0003 | ShiftMapper | Warning | Destination property is not mapped because its setter is not public
SM0004 | ShiftMapper | Warning | Destination type has no public parameterless constructor
SM0005 | ShiftMapper | Warning | No mapping code was generated for a ShiftMapperBase-derived class
