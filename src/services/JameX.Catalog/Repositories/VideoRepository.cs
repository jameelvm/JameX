using JameX.Catalog.Data;
using JameX.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace JameX.Catalog.Repositories;

/// <summary>
/// Data access for <c>videos</c> and <c>renditions</c>.
/// <para>
/// Note what is missing: there is no <c>SaveChangesAsync</c> here. These methods
/// only <i>stage</i> work. Committing belongs to
/// <see cref="ServiceDefaults.Data.IInboxUnitOfWork"/>, because the whole point
/// is that the change and the inbox claim commit in one transaction. A
/// repository that saved on its own would split them into two, and the
/// idempotency guarantee would be gone.
/// </para>
/// </summary>
public interface IVideoRepository
{
    /// <summary>
    /// Loads a video for modification — deliberately <b>tracked</b>, unlike the
    /// read paths, because the caller is about to change it and EF needs to know
    /// which columns moved.
    /// </summary>
    Task<Video?> FindForUpdateAsync(Guid videoId, CancellationToken ct);

    Task<bool> ExistsAsync(Guid videoId, CancellationToken ct);

    void Add(Video video);

    void AddRenditions(IEnumerable<Rendition> renditions);

    /// <summary>
    /// Ladder rungs already recorded for this video, so a partially applied
    /// encode is not re-inserted. The unique index on (video_id, label) is the
    /// real guard; this just avoids provoking it.
    /// </summary>
    Task<HashSet<string>> GetRenditionLabelsAsync(Guid videoId, CancellationToken ct);
}

internal sealed class VideoRepository(CatalogDbContext db) : IVideoRepository
{
    public Task<Video?> FindForUpdateAsync(Guid videoId, CancellationToken ct) =>
        db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);

    public Task<bool> ExistsAsync(Guid videoId, CancellationToken ct) =>
        db.Videos.AsNoTracking().AnyAsync(v => v.Id == videoId, ct);

    public void Add(Video video) => db.Videos.Add(video);

    public void AddRenditions(IEnumerable<Rendition> renditions) =>
        db.Renditions.AddRange(renditions);

    public async Task<HashSet<string>> GetRenditionLabelsAsync(Guid videoId, CancellationToken ct) =>
        (await db.Renditions.AsNoTracking()
            .Where(r => r.VideoId == videoId)
            .Select(r => r.Label)
            .ToListAsync(ct))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
