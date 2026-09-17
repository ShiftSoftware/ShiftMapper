using ShiftMapper.Sample.Dtos;
using ShiftMapper;
using ShiftMapper.Sample.Mapping;

namespace ShiftMapper.Sample.Endpoints;

/// <summary>
/// DICTIONARIES, which until now were the one collection shape ShiftMapper refused outright
/// (SM0002) and left you to write by hand.
///
/// There is no database in this file on purpose. A dictionary is how a KEYED PAYLOAD arrives —
/// a price per SKU, a note per line number — and that is where it is worth showing, rather than
/// inside a table it would need a value converter to live in.
///
/// One <c>CreateMap&lt;SupplierFeed, SupplierFeedDto&gt;()</c> covers all three properties, and
/// each one demonstrates a different half of the feature. See <see cref="SupplierFeed"/> for the
/// generated code behind each.
/// </summary>
public static class SupplierFeedEndpoints
{
    public static void MapSupplierFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/supplier-feeds").WithTags("Supplier feeds");

        // POST /api/supplier-feeds/preview
        //
        // Try it with a body that leaves "extras" out entirely, and with two "notes" keys — the
        // response tells you what each part of the feature did:
        //
        //   {
        //     "supplier": "Acme Distribution",
        //     "prices": { "APL-IP15P": 999.5, "SNY-PS5S": 499 },
        //     "notes":  { "1": "backordered", "2": "ships Monday" }
        //   }
        //
        //   prices  the VALUES converted:  999.5 -> "999.5", invariant, so it is the same text
        //           in Baghdad and in Berlin
        //   notes   the KEYS converted:    1 -> "1"  (this is the pair the build reports as
        //           SM0008, because converting keys is what can collapse two entries into one)
        //   extras  absent in, {} out:     the null-collection policy, on a dictionary
        group.MapPost("/preview", (SupplierFeed feed, Mapper mapper) =>
        {
            // One call. Every dictionary on the DTO is a NEW dictionary — the DTO owns its own
            // data rather than a second reference to the request object's, which is the same
            // rule every list mapping follows.
            SupplierFeedDto dto = mapper.Map<SupplierFeedDto>(feed);

            return Results.Ok(new
            {
                feed = dto,

                // Proof of the copy: emptying the source afterwards does not touch the DTO.
                ownsItsOwnData = !ReferenceEquals(feed.Extras, dto.Extras),

                // Proof of the policy: the request left "extras" out, and this is still {}.
                extrasWasNull = feed.Extras is null,
                extrasCount = dto.Extras.Count,
            });
        })
        .WithName("PreviewSupplierFeed");

        // POST /api/supplier-feeds/preview-many
        //
        // The collection overloads again, on a type that has nothing to do with the database:
        // they are generated for every declared map, not only for entities.
        group.MapPost("/preview-many", (List<SupplierFeed> feeds, Mapper mapper) =>
            Results.Ok(mapper.Map<List<SupplierFeedDto>>(feeds)))
        .WithName("PreviewSupplierFeeds");
    }
}
