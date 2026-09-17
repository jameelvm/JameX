import { browserApiFetch, browserApiMutate } from "@/lib/api/browser-client";
import { VIEWER_HEADER_NAME } from "@/lib/api/headers";
import type {
  CompleteUploadResponse,
  CreateUploadRequest,
  CreateUploadResponse,
  PresignedPart,
  UploadSessionStatus,
} from "@/types/upload";

function authHeaders(viewerId: string): HeadersInit {
  return { "Content-Type": "application/json", [VIEWER_HEADER_NAME]: viewerId };
}

/** Opens an upload — reserves the video id and decides how the file is sliced. No bytes yet. */
export function beginUpload(
  viewerId: string,
  request: CreateUploadRequest,
): Promise<CreateUploadResponse> {
  return browserApiFetch<CreateUploadResponse>("/uploads", {
    method: "POST",
    headers: authHeaders(viewerId),
    body: JSON.stringify(request),
  });
}

/** Which parts have already landed — the call that makes resuming a dropped upload possible. */
export function getUploadStatus(
  uploadId: string,
  viewerId: string,
): Promise<UploadSessionStatus> {
  return browserApiFetch<UploadSessionStatus>(`/uploads/${uploadId}`, {
    headers: { [VIEWER_HEADER_NAME]: viewerId },
  });
}

/** Presigned URLs for a batch of parts, requested together rather than one at a time. */
export async function presignParts(
  uploadId: string,
  viewerId: string,
  partNumbers: number[],
): Promise<PresignedPart[]> {
  const response = await browserApiFetch<{ parts: PresignedPart[] }>(
    `/uploads/${uploadId}/parts/presign`,
    {
      method: "POST",
      headers: authHeaders(viewerId),
      body: JSON.stringify({ partNumbers }),
    },
  );
  return response.parts;
}

/** Reports that one part landed in S3 — carries the ETag, never the bytes themselves. */
export function reportPart(
  uploadId: string,
  viewerId: string,
  partNumber: number,
  eTag: string,
): Promise<void> {
  return browserApiMutate(`/uploads/${uploadId}/parts/${partNumber}`, {
    method: "PUT",
    headers: authHeaders(viewerId),
    body: JSON.stringify({ eTag }),
  });
}

/** Assembles the parts in S3 and publishes `VideoUploaded` — the moment the video actually comes into existence. */
export function completeUpload(
  uploadId: string,
  viewerId: string,
): Promise<CompleteUploadResponse> {
  return browserApiFetch<CompleteUploadResponse>(`/uploads/${uploadId}/complete`, {
    method: "POST",
    headers: authHeaders(viewerId),
    // Ingest already holds every ETag from the report-part calls; sending an
    // empty list tells it to use its own record rather than re-supplying
    // what it was just told a moment ago.
    body: JSON.stringify({ parts: [] }),
  });
}

/** Abandons the upload and discards its parts in S3. */
export function abortUpload(uploadId: string, viewerId: string): Promise<void> {
  return browserApiMutate(`/uploads/${uploadId}`, {
    method: "DELETE",
    headers: { [VIEWER_HEADER_NAME]: viewerId },
  });
}
