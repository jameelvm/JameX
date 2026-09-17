import type { VideoPrivacy, VideoStatus } from "@/types/video";

/** Mirrors `JameX.Contracts.Dtos.CreateUploadRequest`. */
export interface CreateUploadRequest {
  channelId: string;
  title: string;
  description: string | null;
  categoryId: string | null;
  tags: string[];
  defaultLanguage: string;
  privacy: VideoPrivacy;
  fileName: string;
  contentType: string;
  sizeBytes: number;
}

/** Mirrors `JameX.Contracts.Dtos.CreateUploadResponse`. */
export interface CreateUploadResponse {
  uploadId: string;
  videoId: string;
  objectKey: string;
  partSizeBytes: number;
  totalParts: number;
  expiresAt: string;
}

/** Mirrors `JameX.Contracts.Dtos.PresignedPart`. */
export interface PresignedPart {
  partNumber: number;
  url: string;
  expiresAt: string;
}

/** Mirrors `JameX.Contracts.Dtos.UploadSessionStatus`. */
export interface UploadSessionStatus {
  uploadId: string;
  videoId: string;
  objectKey: string;
  partSizeBytes: number;
  totalParts: number;
  uploadedPartNumbers: number[];
  bytesUploaded: number;
  totalBytes: number;
  createdAt: string;
  expiresAt: string;
  percentComplete: number;
}

/** Mirrors `JameX.Contracts.Dtos.CompleteUploadResponse`. */
export interface CompleteUploadResponse {
  videoId: string;
  status: VideoStatus;
  rawObjectKey: string;
  sizeBytes: number;
}
