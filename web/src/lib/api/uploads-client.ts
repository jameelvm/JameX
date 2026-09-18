import { browserApiFetch, browserApiMutate } from "@/lib/api/browser-client";
import type {
  CompleteUploadResponse,
  CreateUploadRequest,
  CreateUploadResponse,
  PresignedPart,
  UploadSessionStatus,
} from "@/types/upload";

const JSON_HEADERS: HeadersInit = { "Content-Type": "application/json" };

/** Opens an upload — reserves the video id and decides how the file is sliced. No bytes yet. */
export function beginUpload(request: CreateUploadRequest): Promise<CreateUploadResponse> {
  return browserApiFetch<CreateUploadResponse>("/uploads", {
    method: "POST",
    headers: JSON_HEADERS,
    body: JSON.stringify(request),
  });
}

/** Which parts have already landed — the call that makes resuming a dropped upload possible. */
export function getUploadStatus(uploadId: string): Promise<UploadSessionStatus> {
  return browserApiFetch<UploadSessionStatus>(`/uploads/${uploadId}`);
}

/** Presigned URLs for a batch of parts, requested together rather than one at a time. */
export async function presignParts(
  uploadId: string,
  partNumbers: number[],
): Promise<PresignedPart[]> {
  const response = await browserApiFetch<{ parts: PresignedPart[] }>(
    `/uploads/${uploadId}/parts/presign`,
    {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify({ partNumbers }),
    },
  );
  return response.parts;
}

/** Reports that one part landed in S3 — carries the ETag, never the bytes themselves. */
export function reportPart(
  uploadId: string,
  partNumber: number,
  eTag: string,
): Promise<void> {
  return browserApiMutate(`/uploads/${uploadId}/parts/${partNumber}`, {
    method: "PUT",
    headers: JSON_HEADERS,
    body: JSON.stringify({ eTag }),
  });
}

/** Assembles the parts in S3 and publishes `VideoUploaded` — the moment the video actually comes into existence. */
export function completeUpload(uploadId: string): Promise<CompleteUploadResponse> {
  return browserApiFetch<CompleteUploadResponse>(`/uploads/${uploadId}/complete`, {
    method: "POST",
    headers: JSON_HEADERS,
    // Ingest already holds every ETag from the report-part calls; sending an
    // empty list tells it to use its own record rather than re-supplying
    // what it was just told a moment ago.
    body: JSON.stringify({ parts: [] }),
  });
}

/** Abandons the upload and discards its parts in S3. */
export function abortUpload(uploadId: string): Promise<void> {
  return browserApiMutate(`/uploads/${uploadId}`, { method: "DELETE" });
}
