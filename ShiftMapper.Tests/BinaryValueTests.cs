using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShiftMapper.Tests.Model;
using Xunit;

namespace ShiftMapper.Tests;

/// <summary>
/// A <c>byte[]</c> is binary data: a varbinary column, a hash, a key, a protected secret. It is
/// one value, not a list of bytes, so the null-collection policy does not apply to it. A null
/// stays null in every map, and a non-null array is copied, so the destination never shares the
/// source's array.
///
/// Why it matters: a framework repository reloads an entity after a save and copies it onto the
/// tracked one with the entity's own copy map. When that map turned a null binary column into an
/// empty array, the next save in the same request stored <c>0x</c> where the row had NULL. Code
/// that tests the column for null then read the empty value as a value.
///
/// The maps run under the DEFAULT policy (<c>AllowNullCollections = false</c>), which still turns
/// a null list into an empty one; <see cref="CollectionApiTests"/> covers that half.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class BinaryValueTests
{
    private readonly DatabaseFixture _fixture;

    public BinaryValueTests(DatabaseFixture fixture) => _fixture = fixture;

    private static readonly byte[] SecretBytes = [1, 2, 3, 4];
    private static readonly byte[] HashBytes = [9, 8, 7];

    /// <summary>
    /// The copy map (entity onto entity), which a repository uses to refresh a tracked entity
    /// after a save. Both members are checked: the nullable one and the one declared non-nullable.
    /// </summary>
    [Fact]
    public void The_copy_map_keeps_a_null_byte_array_null_and_copies_the_rest()
    {
        var tracked = new Credential { Secret = null, Hash = null! };
        _fixture.Mapper.Map(new Credential { Secret = null, Hash = null! }, tracked);

        Assert.Null(tracked.Secret);
        Assert.Null(tracked.Hash);

        var fresh = new Credential { Secret = [.. SecretBytes], Hash = [.. HashBytes] };
        _fixture.Mapper.Map(fresh, tracked);

        Assert.Equal(SecretBytes, tracked.Secret);
        Assert.Equal(HashBytes, tracked.Hash);
        Assert.NotSame(fresh.Secret, tracked.Secret);
        Assert.NotSame(fresh.Hash, tracked.Hash);

        // A change to the source's array does not reach the destination.
        fresh.Secret[0] = 42;
        Assert.Equal(SecretBytes, tracked.Secret);

        // An EMPTY array is a value too, and stays one.
        _fixture.Mapper.Map(new Credential { Secret = [], Hash = [] }, tracked);
        Assert.NotNull(tracked.Secret);
        Assert.Empty(tracked.Secret);
    }

    /// <inheritdoc cref="The_copy_map_keeps_a_null_byte_array_null_and_copies_the_rest"/>
    [Fact]
    public void Creating_through_the_copy_map_keeps_a_null_byte_array_null_and_copies_the_rest()
    {
        Credential empty = _fixture.Mapper.Map<Credential>(new Credential { Secret = null, Hash = null! });

        Assert.Null(empty.Secret);
        Assert.Null(empty.Hash);

        var source = new Credential { Secret = [.. SecretBytes], Hash = [.. HashBytes] };
        Credential copy = _fixture.Mapper.Map<Credential>(source);

        Assert.Equal(SecretBytes, copy.Secret);
        Assert.NotSame(source.Secret, copy.Secret);
        Assert.NotSame(source.Hash, copy.Hash);
    }

    /// <summary>The write map (DTO onto entity), the way a save applies a form.</summary>
    [Fact]
    public void The_write_map_keeps_a_null_byte_array_null_and_copies_the_rest()
    {
        var entity = new Credential { Secret = null, Hash = [.. HashBytes] };
        _fixture.Mapper.Map(new CredentialDto { Name = "edited", Secret = null, Hash = null! }, entity);

        Assert.Equal("edited", entity.Name);
        Assert.Null(entity.Secret);
        Assert.Null(entity.Hash);

        var dto = new CredentialDto { Secret = [.. SecretBytes], Hash = [.. HashBytes] };
        Credential created = _fixture.Mapper.Map<Credential>(dto);

        Assert.Equal(SecretBytes, created.Secret);
        Assert.NotSame(dto.Secret, created.Secret);
        Assert.NotSame(dto.Hash, created.Hash);
    }

    /// <summary>The view map (entity to DTO).</summary>
    [Fact]
    public void The_view_map_keeps_a_null_byte_array_null_and_copies_the_rest()
    {
        CredentialDto empty = _fixture.Mapper.Map<CredentialDto>(new Credential { Secret = null, Hash = null! });

        Assert.Null(empty.Secret);
        Assert.Null(empty.Hash);

        var entity = new Credential { Secret = [.. SecretBytes], Hash = [.. HashBytes] };
        CredentialDto dto = _fixture.Mapper.Map<CredentialDto>(entity);

        Assert.Equal(SecretBytes, dto.Secret);
        Assert.NotSame(entity.Secret, dto.Secret);
    }

    /// <summary>
    /// The projections, entity to DTO and entity to entity, against a real database that holds a
    /// NULL. EF builds a new array for every row, so there is nothing to share here.
    /// </summary>
    [Fact]
    public void A_projection_keeps_a_null_binary_column_null()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using CredentialContext context = CredentialContext.Seeded(connection);

        List<CredentialDto> dtos = _fixture.Mapper
            .ProjectTo<CredentialDto>(context.Credentials.OrderBy(c => c.Id))
            .ToList();

        List<Credential> copies = _fixture.Mapper
            .ProjectTo<Credential>(context.Credentials.OrderBy(c => c.Id))
            .ToList();

        Assert.Null(dtos[0].Secret);
        Assert.Equal(SecretBytes, dtos[1].Secret);
        Assert.Null(copies[0].Secret);
        Assert.Equal(SecretBytes, copies[1].Secret);
    }

    /// <summary>No COALESCE in the SQL: the column is read as it is.</summary>
    [Fact]
    public void A_projection_reads_a_binary_column_without_a_guard()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using CredentialContext context = CredentialContext.Seeded(connection);

        string sql = _fixture.Mapper.ProjectTo<CredentialDto>(context.Credentials).ToQueryString();

        Assert.Contains("\"c\".\"Secret\"", sql);
        Assert.DoesNotContain("COALESCE", sql);
    }

    /// <summary>One table of its own, so the shared fixture's database is left as it is.</summary>
    private sealed class CredentialContext(DbContextOptions<CredentialContext> options) : DbContext(options)
    {
        public DbSet<Credential> Credentials => Set<Credential>();

        public static CredentialContext Seeded(SqliteConnection connection)
        {
            var context = new CredentialContext(
                new DbContextOptionsBuilder<CredentialContext>().UseSqlite(connection).Options);

            context.Database.EnsureCreated();
            context.Credentials.AddRange(
                new Credential { Id = 1, Name = "no secret", Secret = null, Hash = [.. HashBytes] },
                new Credential { Id = 2, Name = "with secret", Secret = [.. SecretBytes], Hash = [.. HashBytes] });
            context.SaveChanges();
            context.ChangeTracker.Clear();

            return context;
        }
    }
}
