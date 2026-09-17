import { browserApiFetchOrNull } from "@/lib/api/browser-client";
import type { VideoDetail } from "@/types/video";

/**
 * Catalog's own view of a video — the same `VideoDetail` shape the watch
 * page uses, just with `channelName`/`counts`/`viewerReaction` left at
 * Catalog's defaults (null/zeroed) rather than filled in by the Gateway's
 * BFF. Good enough for polling "is it Ready yet", which is all this is for.
 */
export function getVideoStatus(videoId: string): Promise<VideoDetail | null> {
  return browserApiFetchOrNull<VideoDetail>(`/videos/${videoId}`);
}
