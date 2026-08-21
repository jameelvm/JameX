using Amazon.S3;
using Amazon.S3.Model;
using JameX.ServiceDefaults.Aws;
using JameX.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;

namespace JameX.Ingest.Storage;

/// <summary>
/// Every S3 operation the upload feature performs. The AWS SDK stops here, the
/// same way EF stops at a repository.
/// </summary>
public interface IRawUploadStore
{
    /// <summary>
    /// Opens a multipart upload and returns S3's id for it. Nothing is stored
    /// yet — this reserves the job so parts have somewhere to go.
    /// </summary>
    Task<string> BeginAsync(string objectKey, string contentType, CancellationToken ct);

    /// <summary>
    /// Signs a URL the browser can PUT one part to, directly.
    /// </summary>
    string PresignPart(string objectKey, string s3UploadId, int partNumber, DateTimeOffset expiresAt);

    /// <summary>
    /// Asks S3 to assemble the parts. S3 validates every ETag; a missing or
    /// mismatched one fails the whole call, which is what stops a half-uploaded
    /// video ever becoming a real object.
    /// </summary>
    Task<long> CompleteAsync(
        string objectKey, string s3UploadId, IReadOnlyList<(int PartNumber, string ETag)> parts,
        CancellationToken ct);

    /// <summary>
    /// Discards the upload and its parts. Without this the uploaded parts stay
    /// in S3, invisible in listings but billable, until a lifecycle rule sweeps
    /// them.
    /// </summary>
    Task AbortAsync(string objectKey, string s3UploadId, CancellationToken ct);
}

internal sealed class RawUploadStore(
    IAmazonS3 s3,
    [FromKeyedServices(AwsClientKeys.PublicS3)] IAmazonS3 presigningS3,
    IOptions<StorageOptions> storageOptions,
    IOptions<AwsOptions> awsOptions,
    ILogger<RawUploadStore> logger) : IRawUploadStore
{
    private readonly string _bucket = storageOptions.Value.RawBucket;
    private readonly AwsOptions _aws = awsOptions.Value;

    public async Task<string> BeginAsync(string objectKey, string contentType, CancellationToken ct)
    {
        var response = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            ContentType = contentType
        }, ct);

        logger.LogInformation(
            "Opened multipart upload {S3UploadId} for {Bucket}/{Key}",
            response.UploadId, _bucket, objectKey);

        return response.UploadId;
    }

    /// <summary>
    /// Signed with the <b>public</b> client, not the internal one.
    /// <para>
    /// A presigned URL is signed including its host. A URL signed for
    /// <c>http://localstack:4566</c> — reachable only inside the compose
    /// network — is useless to a browser on the host machine, and fails with a
    /// signature mismatch rather than a helpful error. In production the same
    /// rule applies: sign for the public domain the client will actually call.
    /// </para>
    /// </summary>
    public string PresignPart(
        string objectKey, string s3UploadId, int partNumber, DateTimeOffset expiresAt)
    {
        var url = presigningS3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,

            // These two turn a plain object PUT into a *part* PUT. Without them
            // the browser would overwrite the whole object with one 8 MB slice.
            UploadId = s3UploadId,
            PartNumber = partNumber
        });

        return MatchEndpointScheme(url);
    }

    /// <summary>
    /// Forces the presigned URL's scheme to match the configured public
    /// endpoint.
    /// <para>
    /// The SDK generates presigned URLs as <c>https</c> regardless of the
    /// endpoint's scheme, and <c>AmazonS3Config.UseHttp</c> does not change
    /// that in SDK v4. Against LocalStack — plain HTTP on 4566 — the browser
    /// then fails the TLS handshake and reports a bare network error with no
    /// hint that the scheme is the problem.
    /// </para>
    /// <para>
    /// The scheme is <b>not</b> part of the signature, so rewriting it is safe;
    /// the signed host, path and query are untouched. In real AWS the endpoint
    /// is https and this is a no-op.
    /// </para>
    /// </summary>
    private string MatchEndpointScheme(string url)
    {
        var publicEndpoint = _aws.PublicServiceUrl ?? _aws.ServiceUrl;

        if (string.IsNullOrWhiteSpace(publicEndpoint)) return url;

        if (publicEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat("http://", url.AsSpan("https://".Length));
        }

        return url;
    }

    public async Task<long> CompleteAsync(
        string objectKey, string s3UploadId, IReadOnlyList<(int PartNumber, string ETag)> parts,
        CancellationToken ct)
    {
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            UploadId = s3UploadId,
            // Order matters: S3 assembles the object in the order given, so the
            // parts must be sorted by part number or the video is scrambled.
            PartETags = parts
                .OrderBy(p => p.PartNumber)
                .Select(p => new PartETag(p.PartNumber, p.ETag))
                .ToList()
        }, ct);

        // Ask S3 how large the assembled object actually is, rather than
        // trusting the size the client declared at initiation.
        var metadata = await s3.GetObjectMetadataAsync(_bucket, objectKey, ct);

        logger.LogInformation(
            "Completed {Bucket}/{Key} from {Parts} parts, {Bytes:N0} bytes",
            _bucket, objectKey, parts.Count, metadata.ContentLength);

        return metadata.ContentLength;
    }

    public async Task AbortAsync(string objectKey, string s3UploadId, CancellationToken ct)
    {
        await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            UploadId = s3UploadId
        }, ct);

        logger.LogInformation("Aborted multipart upload {S3UploadId} for {Key}", s3UploadId, objectKey);
    }
}
