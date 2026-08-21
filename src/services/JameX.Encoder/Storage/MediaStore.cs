using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using JameX.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace JameX.Encoder.Storage;

/// <summary>
/// Moves bytes between S3 and the worker's scratch disk.
/// <para>
/// Note that Encoder is the one service where video bytes <i>do</i> pass
/// through the process — unavoidably, since transcoding means decoding every
/// frame. That is precisely why it is a separate service: it scales on queue
/// depth and needs CPU and disk, while Ingest scales on connection count and
/// needs neither.
/// </para>
/// </summary>
public interface IMediaStore
{
    /// <summary>Downloads the raw upload to local disk.</summary>
    Task DownloadAsync(string bucket, string key, string destinationPath, CancellationToken ct);

    /// <summary>
    /// Uploads a local directory tree under a key prefix, returning the total
    /// bytes written.
    /// </summary>
    Task<long> UploadDirectoryAsync(string localDirectory, string keyPrefix, CancellationToken ct);

    Task UploadFileAsync(string localPath, string key, CancellationToken ct);
}

internal sealed class MediaStore(
    IAmazonS3 s3,
    IOptions<StorageOptions> storageOptions,
    ILogger<MediaStore> logger) : IMediaStore
{
    private readonly string _mediaBucket = storageOptions.Value.MediaBucket;

    /// <summary>
    /// Segment uploads run concurrently — a ladder is hundreds of small files,
    /// and doing them one at a time makes upload latency dominate the job.
    /// Bounded, so one video cannot saturate the connection pool.
    /// </summary>
    private const int UploadConcurrency = 8;

    public async Task DownloadAsync(string bucket, string key, string destinationPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        // TransferUtility, not GetObject: it downloads in parallel ranges and
        // handles retries, which matters for a 600 MB original.
        var transfer = new TransferUtility(s3);
        await transfer.DownloadAsync(destinationPath, bucket, key, ct);

        var size = new FileInfo(destinationPath).Length;
        logger.LogInformation("Downloaded s3://{Bucket}/{Key} ({Size:N0} bytes)", bucket, key, size);
    }

    public async Task<long> UploadDirectoryAsync(
        string localDirectory, string keyPrefix, CancellationToken ct)
    {
        var files = Directory.GetFiles(localDirectory, "*", SearchOption.AllDirectories);
        long total = 0;

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = UploadConcurrency, CancellationToken = ct },
            async (file, token) =>
            {
                var relative = Path.GetRelativePath(localDirectory, file).Replace('\\', '/');
                await UploadFileAsync(file, $"{keyPrefix.TrimEnd('/')}/{relative}", token);
                Interlocked.Add(ref total, new FileInfo(file).Length);
            });

        logger.LogInformation(
            "Uploaded {Count} files ({Size:N0} bytes) to s3://{Bucket}/{Prefix}",
            files.Length, total, _mediaBucket, keyPrefix);

        return total;
    }

    public async Task UploadFileAsync(string localPath, string key, CancellationToken ct)
    {
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _mediaBucket,
            Key = key,
            FilePath = localPath,

            // Content type matters more than usual here. A browser will refuse
            // to treat a playlist as HLS if it arrives as application/octet-stream,
            // and the player fails with an unhelpful decode error.
            ContentType = ContentTypeFor(localPath),

            // Segments and playlists are immutable once written — a rendition is
            // never edited in place — so they can be cached hard at the edge.
            // This is what makes the CDN tier effective.
            Headers = { CacheControl = "public, max-age=31536000, immutable" }
        }, ct);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };
}
