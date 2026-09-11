namespace ShiftFramework;

/// <summary>
/// ShiftFramework's select DTO: an id and something to show a human.
///
/// <para><b>WHY A CONVERSION CANNOT FILL THIS.</b> A type-pair conversion is handed ONE value and
/// asked what it becomes. This needs two source members, and which two depends on the DESTINATION
/// MEMBER'S NAME — <c>ProductListDto.Brand</c> is filled from <c>Product.BrandId</c> and
/// <c>Product.Brand.Name</c>. That is a different question, which is why the member convention sits
/// on top of <c>CreateConversion</c> rather than replacing it.</para>
///
/// <para><b>AND IT HAS TO REACH THE PROJECTION.</b> Doing this reflectively in an <c>AfterMap</c> —
/// which is what ShiftFramework does today — works in memory and cannot appear in a list query at
/// all, so lists need a second, hand-inlined code path. The convention resolves to an ordinary
/// inline member-init at compile time, so there is one code path and one answer.</para>
/// </summary>
public class ShiftEntitySelectDTO
{
    /// <summary>The id, as text. Filled from <c>{Member}ID</c>.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>What to show. Filled from the related entity's nominated name member.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// What an entity uses to nominate its own key and display-name members.
///
/// <para>THIS ATTRIBUTE IS THE INDIRECTION THE WHOLE RULE TURNS ON. The convention says
/// <c>Text = {Member}.{NameOf}</c>, and <c>{NameOf}</c> means "whatever member the type I have
/// reached nominates here". So the framework never lists the entities, and an entity that calls its
/// display member <c>Title</c> is served by the same rule as one that calls it <c>Name</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ShiftEntityKeyAndNameAttribute : Attribute
{
    public ShiftEntityKeyAndNameAttribute(string value, string text)
    {
        Value = value;
        Text = text;
    }

    /// <summary>The member holding the key.</summary>
    public string Value { get; }

    /// <summary>The member holding the text a human reads.</summary>
    public string Text { get; }
}
