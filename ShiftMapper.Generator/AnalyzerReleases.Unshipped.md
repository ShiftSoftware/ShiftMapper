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
SM0016 | ShiftMapper | Error | Member cannot be given a Condition because its value is settled when the object is created
SM0017 | ShiftMapper | Warning | Map cannot be projected because a member carries a Condition
SM0018 | ShiftMapper | Warning | Map cannot be projected because it runs a BeforeMap or AfterMap hook
SM0019 | ShiftMapper | Warning | Configuration has no effect because ConvertUsing replaces the whole map
SM0020 | ShiftMapper | Info | Destination property is filled by flattening
SM0021 | ShiftMapper | Warning | Destination property could be flattened more than one way
SM0022 | ShiftMapper | Warning | IncludeBase names a map that does not exist
SM0023 | ShiftMapper | Warning | Include cannot dispatch to the derived pair
SM0024 | ShiftMapper | Warning | Map cannot be projected because it dispatches on the runtime type
SM0025 | ShiftMapper | Warning | As names a type that cannot stand in for the destination
SM0026 | ShiftMapper | Warning | Open generic map was not closed
SM0027 | ShiftMapper | Warning | A map is declared both in a profile and outside it
SM0028 | ShiftMapper | Warning | A referenced assembly carries no ShiftMapper declaration metadata
SM0029 | ShiftMapper | Warning | ConfigureDefaults on a profile has no effect
SM0030 | ShiftMapper | Warning | Map cannot be projected because a conversion has no query form
SM0031 | ShiftMapper | Error | Two assemblies declare a conversion for the same type pair
SM0032 | ShiftMapper | Warning | A declared conversion could not be read
SM0033 | ShiftMapper | Warning | A referenced assembly declares a newer ShiftMapper contract
SM0034 | ShiftMapper | Warning | A member convention could not fill the member it claimed
SM0035 | ShiftMapper | Error | A declaration cannot be honoured where it is written
