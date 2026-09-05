namespace ShiftMapper.Generator;

/// <summary>Why one member cannot carry a <c>Condition</c>.</summary>
internal enum ConditionRefusalReason
{
    /// <summary>
    /// The member is <c>init</c>-only, so it can be set while the object is being created and
    /// never again. There is no assignment to guard, and no way to leave it out of one.
    /// </summary>
    InitOnly,

    /// <summary>
    /// The member is <c>required</c> and this map builds the destination with an object
    /// initializer, which C# refuses to leave a required member out of (CS9035).
    ///
    /// Note the qualifier. On a <c>ConstructUsing</c> map the generator writes no <c>new</c> at
    /// all — the developer's expression does — so a required member with an ordinary setter is
    /// perfectly conditionable there, and refusing it would be a wrong error on legal
    /// configuration.
    /// </summary>
    RequiredInInitializer,

    /// <summary>
    /// The member's value is a CONSTRUCTOR ARGUMENT. It is settled before the object exists, and
    /// C# has no syntax for conditionally omitting an argument.
    /// </summary>
    ConstructorArgument,
}

/// <summary>
/// One member the developer asked to condition and ShiftMapper will not.
///
/// All three reasons are the same underlying fact — the member's value is settled while the
/// object is being CREATED, so there is nothing to leave untouched. Recorded rather than reported
/// here, because the generator does not talk: <see cref="ShiftMapperAnalyzer"/> turns each into
/// SM0016 at the CreateMap that asked for it.
///
/// The generator still emits the member, UNCONDITIONED. That is deliberate: an analyzer error does
/// not stop the generated file being compiled in the same pass, and a project with analyzers
/// turned off must not get a CS error inside a file it cannot edit. The build fails on SM0016;
/// the generated code stays clean.
/// </summary>
internal sealed class ConditionRefusal
{
    public ConditionRefusal(string member, ConditionRefusalReason reason)
    {
        Member = member;
        Reason = reason;
    }

    public string Member { get; }

    public ConditionRefusalReason Reason { get; }

    /// <summary>The tail of the SM0016 message, which is the whole value of having three reasons.</summary>
    public string Describe() => Reason switch
    {
        ConditionRefusalReason.InitOnly =>
            "it is init-only, so its value is settled when the object is created",
        ConditionRefusalReason.RequiredInInitializer =>
            "it is required and this map builds the destination with an object initializer, " +
            "which C# refuses to leave a required member out of",
        _ =>
            "it is filled by a constructor argument, and an argument cannot be left out",
    };
}
