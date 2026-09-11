namespace ShiftMapper.Generator;

/// <summary>
/// The keys a diagnostic carries alongside its message, for the code fixes to read.
///
/// <para><b>WHY A SHARED FILE.</b> The analyzer writes these and a DIFFERENT ASSEMBLY reads them —
/// a code-fix provider cannot live beside the analyzer, because fixes need
/// <c>Microsoft.CodeAnalysis.Workspaces</c> and the compiler does not load that. A key spelled two
/// ways would not fail to build; it would simply mean the lightbulb never appears, which is the
/// quietest possible way to break a feature. One definition, linked into both projects.</para>
/// </summary>
internal static class DiagnosticProperties
{
    /// <summary>
    /// The destination member a diagnostic is about — what SM0001's fix offers to ignore.
    ///
    /// <para>Carried structurally rather than parsed back out of the sentence: the message is prose
    /// for a person and is free to be reworded, and a fix that depended on its exact shape would
    /// silently stop offering itself the first time somebody improved it.</para>
    /// </summary>
    public const string MemberName = "ShiftMapper.MemberName";

    /// <summary>The source type of a nested pair that has no map — half of what SM0011's fix writes.</summary>
    public const string NestedSource = "ShiftMapper.NestedSource";

    /// <summary>And its destination type.</summary>
    public const string NestedDestination = "ShiftMapper.NestedDestination";
}
