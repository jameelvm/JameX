/**
 * Wire types for the watch page, mirroring `JameX.Contracts.Dtos` on the
 * backend field-for-field. Enum numeric values must stay in lock-step with
 * `JameX.Contracts.Enums` — they are part of the same wire contract, not
 * independently chosen here.
 */

export enum VideoStatus {
  Uploading = 0,
  Queued = 1,
  Transcoding = 2,
  Ready = 3,
  Failed = 4,
  Rejected = 5,
}

export enum VideoPrivacy {
  Private = 0,
  Unlisted = 1,
  Public = 2,
}

export enum ReactionKind {
  Like = 0,
  Dislike = 1,
}

export interface RenditionInfo {
  label: string;
  width: number;
  height: number;
  bitrateKbps: number;
  codec: string;
  playlistUrl: string;
}

export interface EngagementCounts {
  views: number;
  likes: number;
  dislikes: number;
  comments: number;
}

/** The watch page as the Gateway's BFF assembles it — see `GET /api/watch/{videoId}`. */
export interface VideoDetail {
  videoId: string;
  channelId: string;
  channelName: string | null;
  title: string;
  description: string | null;
  categoryId: string | null;
  tags: string[];
  defaultLanguage: string;
  privacy: VideoPrivacy;
  status: VideoStatus;
  durationSeconds: number;
  masterPlaylistUrl: string | null;
  posterThumbnailUrl: string | null;
  renditions: RenditionInfo[];
  createdAt: string;
  publishedAt: string | null;
  counts: EngagementCounts;
  viewerReaction: ReactionKind | null;
}

/** One row of the home feed / a channel's uploads — see Catalog's `GET /videos`. Deliberately thinner than `VideoDetail`: a grid card never needs a description or the full rendition list. */
export interface VideoSummary {
  videoId: string;
  channelId: string;
  title: string;
  durationSeconds: number;
  thumbnailUrl: string | null;
  status: VideoStatus;
  publishedAt: string | null;
  viewCount: number;
  likeCount: number;
}

/**
 * A `VideoSummary` with its channel's display name attached. Catalog's feed
 * has no notion of channel names — see `hydrateChannelNames` for where this
 * field actually gets filled in, the same "resolve it once, server-side"
 * pattern the watch page's `channelName` already uses.
 */
export interface VideoSummaryWithChannel extends VideoSummary {
  channelName: string | null;
}
