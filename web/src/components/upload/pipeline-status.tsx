"use client";

import Link from "next/link";
import { useEffect, useState } from "react";

import { getVideoStatus } from "@/lib/api/video-status-client";
import { VideoStatus } from "@/types/video";

const STATUS_LABEL: Record<VideoStatus, string> = {
  [VideoStatus.Uploading]: "Uploading",
  [VideoStatus.Queued]: "Queued for processing",
  [VideoStatus.Transcoding]: "Processing",
  [VideoStatus.Ready]: "Ready",
  [VideoStatus.Failed]: "Processing failed",
  [VideoStatus.Rejected]: "Rejected",
};

const POLL_INTERVAL_MS = 3_000;
/** ~6 minutes — generous for a real encode, but not an unbounded background poll. */
const MAX_POLLS = 120;

interface PipelineStatusProps {
  videoId: string;
}

/**
 * Watches Catalog until the encoder finishes. A real UI would use a
 * websocket or SSE; polling keeps this dependency-free, the same trade-off
 * `web/debug/index.html` made — this is a straight port of that logic.
 *
 * Recursive `setTimeout` rather than `setInterval`: a slow request can never
 * overlap with the next tick this way, since the next poll is only
 * scheduled after the previous one resolves.
 */
export function PipelineStatus({ videoId }: PipelineStatusProps) {
  const [status, setStatus] = useState<VideoStatus>(VideoStatus.Queued);
  const [renditionLabels, setRenditionLabels] = useState<string[]>([]);
  const [gaveUp, setGaveUp] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let pollCount = 0;

    async function poll() {
      if (cancelled) return;

      const video = await getVideoStatus(videoId).catch(() => null);
      if (cancelled) return;

      if (video) {
        setStatus(video.status);
        setRenditionLabels(video.renditions.map((rendition) => rendition.label));

        if (video.status === VideoStatus.Ready || video.status === VideoStatus.Failed) {
          return;
        }
      }

      pollCount += 1;
      if (pollCount >= MAX_POLLS) {
        setGaveUp(true);
        return;
      }

      setTimeout(() => void poll(), POLL_INTERVAL_MS);
    }

    void poll();

    return () => {
      cancelled = true;
    };
  }, [videoId]);

  return (
    <div className="flex flex-col gap-2 rounded-lg bg-neutral-100 p-4 text-sm">
      <div className="flex items-center justify-between text-neutral-800">
        <span>Pipeline status</span>
        <span>{STATUS_LABEL[status]}</span>
      </div>

      {renditionLabels.length > 0 && (
        <p className="text-neutral-600">
          Renditions: {renditionLabels.join(", ")}
        </p>
      )}

      {status === VideoStatus.Ready && (
        <Link
          href={`/watch/${videoId}`}
          className="self-start rounded-full bg-blue-600 px-4 py-1 text-sm font-medium text-white hover:bg-blue-700"
        >
          Watch it
        </Link>
      )}

      {status === VideoStatus.Failed && (
        <p className="text-red-600">Encoding failed for this video.</p>
      )}

      {gaveUp && (
        <p className="text-neutral-500">
          Still processing after a while — check back on the watch page
          later.
        </p>
      )}
    </div>
  );
}
