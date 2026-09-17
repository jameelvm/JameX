import { formatCompactNumber } from "@/lib/format";

interface ViewCommentStatsProps {
  views: number;
  comments: number;
}

/**
 * The two stats on the watch page nobody ever clicks — views and comment
 * count. Split out from reactions deliberately: this stays a plain
 * server-rendered value, while likes/dislikes need viewer identity and
 * client-side state (see `ReactionButtons`).
 */
export function ViewCommentStats({ views, comments }: ViewCommentStatsProps) {
  return (
    <div className="flex items-center gap-4 text-sm text-neutral-700">
      <span>{formatCompactNumber(views)} views</span>
      <span>{formatCompactNumber(comments)} comments</span>
    </div>
  );
}
