namespace ShiftMapper.Sample.Entities;

/// <summary>
/// A catalogue item, and the base of a TABLE-PER-HIERARCHY family — one table, a Discriminator
/// column, and a base type you can query without knowing which row is which.
///
/// <b>THIS IS WHY STEP 10 EXISTS.</b> A TPH table is where polymorphism stops being a language
/// feature and becomes a mapping problem: <c>db.CatalogItems</c> is an
/// <c>IQueryable&lt;CatalogItem&gt;</c> whose rows are really physical and digital items, and a
/// mapper that only ever sees the STATIC type throws away everything the derived types added.
///
/// <para>Seeded with four rows — two of each kind — by <see cref="Data.SeedData"/>.</para>
/// </summary>
public class CatalogItem
{
    public int Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>Something with a weight, which only physical things have.</summary>
public class PhysicalItem : CatalogItem
{
    public decimal WeightKg { get; set; }
}

/// <summary>Something with a download size, which only digital things have.</summary>
public class DigitalItem : CatalogItem
{
    public int SizeMb { get; set; }
}
