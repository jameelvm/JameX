import { VideoStatus } from "@/types/video";

const STATUS_LABELS: Record<VideoStatus, string> = {
  [VideoStatus.Uploading]: "still uploading",
  [VideoStatus.Queued]: "queued for processing",
  [VideoStatus.Transcoding]: "processing",
  [VideoStatus.Ready]: "ready",
  [VideoStatus.Failed]: "failed to process",
  [VideoStatus.Rejected]: "rejected",
};

interface ProcessingNoticeProps {
  status: VideoStatus;
}

/**
 * Shown in place of the player for every status except `Ready` — there is no
 * master playlist to point hls.js at until encoding finishes, so this is not
 * an edge case to paper over but a real, expected state a viewer can land on
 * (e.g. sharing a link moments after uploading).
 */
export function ProcessingNotice({ status }: ProcessingNoticeProps) {
  if (status === VideoStatus.Ready) return null;

  return (
    <div
      role="status"
      className="flex aspect-video w-full items-center justify-center rounded-xl bg-neutral-900 text-neutral-300"
    >
      This video is {STATUS_LABELS[status]}. Check back shortly.
    </div>
  );
}
