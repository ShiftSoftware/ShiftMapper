namespace ShiftMapper.Tests.Model;

/// <summary>
/// A mapper whose only job is to demonstrate a profile WITH A DEPENDENCY.
///
/// <para>It is separate from <see cref="TestMapper"/> for a reason worth stating: a mapper builds
/// ALL its profiles the first time anything is mapped, and one that cannot be built throws. So a
/// single DI-only profile makes the entire mapper DI-only — including maps that have nothing to
/// do with that profile. Much of this suite constructs <c>TestMapper</c> by hand, which is a
/// supported thing to do, and it would all stop working.</para>
///
/// <para>That is the correct behaviour: a profile whose registrations are missing would otherwise
/// leave its <c>MapFrom</c> members quietly unfilled, which is the failure this library exists to
/// prevent. But it is sharp enough to deserve its own mapper rather than a footnote.</para>
/// </summary>
public partial class ProfileMapper : ShiftMapperBase
{
    public ProfileMapper() => AddProfile<NumberedProfile>();
}
