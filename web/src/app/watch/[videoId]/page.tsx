import type { Metadata } from "next";
import { notFound } from "next/navigation";

import { getComments } from "@/lib/api/comments";
import { getWatchPage } from "@/lib/api/watch";
import { formatPublishedDate } from "@/lib/format";
import { getServerViewerId } from "@/lib/viewer/server-viewer";
import { ChannelByline } from "@/components/video/channel-byline";
import { CommentsSection } from "@/components/comments/comments-section";
import { ProcessingNotice } from "@/components/video/processing-notice";
import { ReactionButtons } from "@/components/video/reaction-buttons";
import { ViewCommentStats } from "@/components/video/view-comment-stats";
import { VideoPlayer } from "@/components/video/video-player";
import { VideoTags } from "@/components/video/video-tags";
import { VideoStatus } from "@/types/video";

interface WatchPageProps {
  params: Promise<{ videoId: string }>;
}

export async function generateMetadata({
  params,
}: WatchPageProps): Promise<Metadata> {
  const { videoId } = await params;
  const viewerId = await getServerViewerId();
  const video = await getWatchPage(videoId, viewerId);

  return { title: video ? video.title : "Video not found" };
}

export default async function WatchPage({ params }: WatchPageProps) {
  const { videoId } = await params;
  const viewerId = await getServerViewerId();

  // Independent reads — fetched together rather than one after another, the
  // same reasoning as the Gateway's own fan-out for this same page.
  const [video, comments] = await Promise.all([
    getWatchPage(videoId, viewerId),
    getComments(videoId),
  ]);

  if (!video) {
    notFound();
  }

  return (
    <article className="mx-auto flex w-full max-w-4xl flex-col gap-4 px-4 py-6">
      {video.status === VideoStatus.Ready && video.masterPlaylistUrl ? (
        <VideoPlayer
          // Forces a clean hls.js teardown/setup rather than trying to
          // re-point an existing instance at a different video.
          key={video.videoId}
          masterPlaylistUrl={video.masterPlaylistUrl}
          posterUrl={video.posterThumbnailUrl}
          title={video.title}
        />
      ) : (
        <ProcessingNotice status={video.status} />
      )}

      <h1 className="text-xl font-semibold text-neutral-900">{video.title}</h1>

      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-neutral-200 pb-4">
        <ChannelByline
          channelName={video.channelName}
          publishedLabel={
            video.publishedAt ? formatPublishedDate(video.publishedAt) : null
          }
        />
        <div className="flex flex-wrap items-center gap-4">
          <ViewCommentStats
            views={video.counts.views}
            comments={video.counts.comments}
          />
          <ReactionButtons
            // Forces a fresh mount (and fresh local state) if the viewer
            // navigates from one video's watch page to another's.
            key={video.videoId}
            videoId={video.videoId}
            initialCounts={{
              likes: video.counts.likes,
              dislikes: video.counts.dislikes,
            }}
            initialViewerReaction={video.viewerReaction}
          />
        </div>
      </div>

      {video.description && (
        <p className="whitespace-pre-line rounded-xl bg-neutral-100 px-4 py-3 text-sm text-neutral-800">
          {video.description}
        </p>
      )}

      <VideoTags tags={video.tags} />

      <CommentsSection videoId={video.videoId} initialComments={comments} />
    </article>
  );
}
