import { VideoCard } from "@/components/video/video-card";
import type { VideoStatus } from "@/types/video";

interface VideoGridItem {
  videoId: string;
  title: string;
  thumbnailUrl: string | null;
  durationSeconds: number;
  channelName: string | null;
  publishedAt: string | null;
  viewCount: number | null;
  status?: VideoStatus;
  hideChannel?: boolean;
}

interface VideoGridProps {
  videos: VideoGridItem[];
  emptyMessage: string;
}

/** The responsive tile layout the home feed and search results both share — one column on a phone, up to five on a wide desktop, matching a real video platform's own breakpoints rather than stretching a handful of tiles unnaturally wide. */
export function VideoGrid({ videos, emptyMessage }: VideoGridProps) {
  if (videos.length === 0) {
    return <p className="text-sm text-neutral-600">{emptyMessage}</p>;
  }

  return (
    <div className="grid grid-cols-1 gap-x-4 gap-y-8 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
      {videos.map((video) => (
        <VideoCard key={video.videoId} {...video} />
      ))}
    </div>
  );
}
