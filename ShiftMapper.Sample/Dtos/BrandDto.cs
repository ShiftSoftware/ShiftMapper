namespace ShiftMapper.Sample.Dtos;

/// <summary>Read model for a <see cref="Entities.Brand"/>.</summary>
public class BrandDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public int FoundedYear { get; set; }
}
