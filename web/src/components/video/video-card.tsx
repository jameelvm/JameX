import Link from "next/link";

import { Avatar } from "@/components/common/avatar";
import { VideoThumbnail } from "@/components/video/video-thumbnail";
import { formatCompactNumber, formatDuration, formatRelativeTime } from "@/lib/format";

interface VideoCardProps {
  videoId: string;
  title: string;
  thumbnailUrl: string | null;
  durationSeconds: number;
  channelName: string | null;
  publishedAt: string | null;
  /** `null` when the source list doesn't carry a view count at all (search results) — rendered as an absent stat, never a fabricated "0 views". */
  viewCount: number | null;
}

/**
 * One grid tile — the browsing unit both the home feed and search results
 * share. `publishedAt` is nullable on the wire the same way `VideoDetail`'s
 * is (a video can be Ready but not yet public); the meta line simply omits
 * the "· 3 days ago" half rather than showing a fabricated date.
 */
export function VideoCard({
  videoId,
  title,
  thumbnailUrl,
  durationSeconds,
  channelName,
  publishedAt,
  viewCount,
}: VideoCardProps) {
  return (
    <Link href={`/watch/${videoId}`} className="group flex flex-col gap-3">
      <div className="relative aspect-video w-full overflow-hidden rounded-xl bg-neutral-100">
        <VideoThumbnail src={thumbnailUrl} alt={title} />
        <span className="absolute bottom-1.5 right-1.5 rounded bg-black/80 px-1.5 py-0.5 text-xs font-medium text-white">
          {formatDuration(durationSeconds)}
        </span>
      </div>

      <div className="flex gap-3">
        <Avatar name={channelName} />
        <div className="flex min-w-0 flex-col">
          <h3 className="line-clamp-2 text-sm font-medium text-neutral-900 group-hover:text-neutral-700">
            {title}
          </h3>
          <span className="mt-1 text-xs text-neutral-600">
            {channelName ?? "Unknown channel"}
          </span>
          <span className="text-xs text-neutral-600">
            {viewCount !== null && `${formatCompactNumber(viewCount)} views`}
            {viewCount !== null && publishedAt && " · "}
            {publishedAt && formatRelativeTime(publishedAt)}
          </span>
        </div>
      </div>
    </Link>
  );
}
