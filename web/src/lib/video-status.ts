import { VideoStatus } from "@/types/video";

/** Short, standalone labels for a status badge — see `components/video/processing-notice.tsx` and `components/upload/pipeline-status.tsx` for the same statuses phrased to fit inside a sentence instead. */
export const VIDEO_STATUS_BADGE_LABEL: Record<VideoStatus, string> = {
  [VideoStatus.Uploading]: "Uploading",
  [VideoStatus.Queued]: "Queued",
  [VideoStatus.Transcoding]: "Processing",
  [VideoStatus.Ready]: "Ready",
  [VideoStatus.Failed]: "Failed",
  [VideoStatus.Rejected]: "Rejected",
};
