import Link from "next/link";

import { Avatar } from "@/components/common/avatar";
import { VideoThumbnail } from "@/components/video/video-thumbnail";
import { formatCompactNumber, formatDuration, formatRelativeTime } from "@/lib/format";
import { VIDEO_STATUS_BADGE_LABEL } from "@/lib/video-status";
import { VideoStatus } from "@/types/video";

interface VideoCardProps {
  videoId: string;
  title: string;
  thumbnailUrl: string | null;
  durationSeconds: number;
  channelName: string | null;
  publishedAt: string | null;
  /** `null` when the source list doesn't carry a view count at all (search results) — rendered as an absent stat, never a fabricated "0 views". */
  viewCount: number | null;
  /** Only ever passed on "Your videos" (`GetMineAsync`) — every other list is pre-filtered to `Ready`, so there is nothing to badge. `undefined` elsewhere, not `VideoStatus.Ready`, to keep that distinction visible in the type rather than relying on a magic enum value. */
  status?: VideoStatus;
  /** "Your videos" already knows whose videos they are — repeating the viewer's own name and an avatar on every tile is noise a public grid doesn't have. */
  hideChannel?: boolean;
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
  status,
  hideChannel,
}: VideoCardProps) {
  const showStatusBadge = status !== undefined && status !== VideoStatus.Ready;

  return (
    <Link href={`/watch/${videoId}`} className="group flex flex-col gap-3">
      <div className="relative aspect-video w-full overflow-hidden rounded-xl bg-neutral-100">
        <VideoThumbnail src={thumbnailUrl} alt={title} />
        <span
          className={`absolute bottom-1.5 right-1.5 rounded px-1.5 py-0.5 text-xs font-medium text-white ${
            showStatusBadge ? "bg-amber-600" : "bg-black/80"
          }`}
        >
          {showStatusBadge ? VIDEO_STATUS_BADGE_LABEL[status] : formatDuration(durationSeconds)}
        </span>
      </div>

      <div className="flex gap-3">
        {!hideChannel && <Avatar name={channelName} />}
        <div className="flex min-w-0 flex-col">
          <h3 className="line-clamp-2 text-sm font-medium text-neutral-900 group-hover:text-neutral-700">
            {title}
          </h3>
          {!hideChannel && (
            <span className="mt-1 text-xs text-neutral-600">
              {channelName ?? "Unknown channel"}
            </span>
          )}
          <span className={`text-xs text-neutral-600 ${hideChannel ? "mt-1" : ""}`}>
            {viewCount !== null && `${formatCompactNumber(viewCount)} views`}
            {viewCount !== null && publishedAt && " · "}
            {publishedAt && formatRelativeTime(publishedAt)}
          </span>
        </div>
      </div>
    </Link>
  );
}
