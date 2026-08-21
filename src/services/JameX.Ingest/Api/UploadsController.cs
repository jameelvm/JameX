using JameX.Contracts.Dtos;
using JameX.Ingest.Contracts;
using JameX.Ingest.Services;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace JameX.Ingest.Api;

/// <summary>
/// The upload control plane.
/// <para>
/// Every action here exchanges a few kilobytes of JSON. The video bytes never
/// appear — the browser PUTs them straight to S3 using the presigned URLs this
/// controller hands out. That split is the entire reason this service can sit
/// in front of ~480 Gbps of ingest without becoming the bottleneck.
/// </para>
/// </summary>
[ApiController]
[Route("uploads")]
[Produces("application/json")]
public sealed class UploadsController(
    IUploadService uploadService,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Opens an upload: reserves the video id, opens the multipart upload in S3,
    /// and tells the client how to slice the file.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<CreateUploadResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Begin(CreateUploadRequest request, CancellationToken ct) =>
        (await uploadService.BeginAsync(currentUser.RequireUserId(), request, ct))
            .ToActionResult(created => Created($"/uploads/{created.UploadId}", created));

    /// <summary>
    /// Issues presigned URLs for a batch of parts. Requested in batches so a
    /// 600 MB upload does not need hundreds of round trips before it can start.
    /// </summary>
    [HttpPost("{uploadId:guid}/parts/presign")]
    [ProducesResponseType<PresignPartsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Presign(
        Guid uploadId, PresignPartsRequest request, CancellationToken ct) =>
        (await uploadService.PresignAsync(uploadId, currentUser.RequireUserId(), request.PartNumbers, ct))
            .ToActionResult();

    /// <summary>
    /// Reports that one part landed, with the ETag S3 returned. Carries no
    /// bytes — this is what makes the upload resumable.
    /// </summary>
    [HttpPut("{uploadId:guid}/parts/{partNumber:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReportPart(
        Guid uploadId, int partNumber, ReportPartRequest request, CancellationToken ct) =>
        (await uploadService.ReportPartAsync(
            uploadId, currentUser.RequireUserId(), partNumber, request.ETag, ct))
            .ToActionResult(_ => NoContent());

    /// <summary>
    /// Progress and, crucially, which parts are still missing — the call a
    /// client makes after a dropped connection so it re-sends only the gaps.
    /// </summary>
    [HttpGet("{uploadId:guid}")]
    [ProducesResponseType<UploadSessionStatus>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(Guid uploadId, CancellationToken ct) =>
        (await uploadService.GetStatusAsync(uploadId, currentUser.RequireUserId(), ct))
            .ToActionResult();

    /// <summary>
    /// Asks S3 to assemble the parts, then publishes <c>VideoUploaded</c>.
    /// <para>
    /// This goes through Ingest rather than the browser calling S3 directly,
    /// because completion is the moment the video comes into existence — if S3
    /// assembled the object and nobody told us, the file would sit in the bucket
    /// forever, unencoded and invisible.
    /// </para>
    /// </summary>
    [HttpPost("{uploadId:guid}/complete")]
    [ProducesResponseType<CompleteUploadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(
        Guid uploadId, CompleteUploadRequest request, CancellationToken ct) =>
        (await uploadService.CompleteAsync(uploadId, currentUser.RequireUserId(), request.Parts, ct))
            .ToActionResult();

    /// <summary>
    /// Abandons the upload and discards its parts in S3, which would otherwise
    /// remain billable but invisible.
    /// </summary>
    [HttpDelete("{uploadId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Abort(Guid uploadId, CancellationToken ct) =>
        (await uploadService.AbortAsync(uploadId, currentUser.RequireUserId(), ct))
            .ToActionResult(_ => NoContent());
}
