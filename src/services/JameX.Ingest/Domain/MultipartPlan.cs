namespace JameX.Ingest.Domain;

/// <summary>
/// Decides how a file is sliced for S3 multipart upload.
/// <para>
/// <b>The service decides this, not the client.</b> S3 imposes hard limits, and
/// a client that picked its own part size would discover the violation only at
/// completion — after uploading hundreds of megabytes. Computing it here means
/// an impossible upload is rejected before a single byte moves.
/// </para>
/// <para>
/// The constants below are <i>AWS facts</i>, not our preferences, which is why
/// they are constants rather than configuration. Our preferences — preferred
/// part size, maximum accepted upload — arrive as parameters from
/// <see cref="Configuration.UploadOptions"/>.
/// </para>
/// </summary>
public static class MultipartPlan
{
    /// <summary>
    /// S3's minimum part size. Every part except the final one must be at least
    /// this large, or <c>CompleteMultipartUpload</c> fails with
    /// <c>EntityTooSmall</c> — after the whole transfer has happened.
    /// </summary>
    public const long MinPartSizeBytes = 5L * 1024 * 1024;          // 5 MB

    /// <summary>S3's hard ceiling on parts per upload.</summary>
    public const int MaxParts = 10_000;

    /// <summary>S3's maximum size for a single part.</summary>
    public const long MaxPartSizeBytes = 5L * 1024 * 1024 * 1024;   // 5 GB

    /// <summary>
    /// Chooses a part size satisfying every S3 constraint at once, and returns
    /// the resulting part count.
    /// </summary>
    /// <param name="totalBytes">Size of the file being uploaded.</param>
    /// <param name="preferredPartSizeBytes">Our choice, from configuration.</param>
    public static (long PartSizeBytes, int TotalParts) For(
        long totalBytes, long preferredPartSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalBytes, 1);

        var partSize = Math.Max(preferredPartSizeBytes, MinPartSizeBytes);

        // As files grow, the 10,000-part ceiling binds before the size limit
        // does — so the part size has to grow with the file rather than stay
        // fixed. A 100 GB file at 8 MB parts would need 12,800 parts; S3 would
        // refuse it.
        var smallestThatFits = (long)Math.Ceiling((double)totalBytes / MaxParts);
        if (smallestThatFits > partSize)
        {
            // Round up to a whole megabyte so the number stays legible in logs
            // and in the client's slicing loop.
            const long oneMb = 1024 * 1024;
            partSize = (long)Math.Ceiling((double)smallestThatFits / oneMb) * oneMb;
        }

        if (partSize > MaxPartSizeBytes)
            throw new InvalidOperationException(
                $"A {totalBytes:N0} byte upload cannot be split within S3's limits.");

        var totalParts = (int)Math.Ceiling((double)totalBytes / partSize);

        return (partSize, totalParts);
    }
}
