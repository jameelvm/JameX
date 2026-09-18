"use client";

import { useState } from "react";

import { useViewer } from "@/components/viewer/viewer-provider";
import { removeMyReaction, setMyReaction } from "@/lib/api/reactions";
import { formatCompactNumber } from "@/lib/format";
import { ReactionKind } from "@/types/video";

interface ReactionCounts {
  likes: number;
  dislikes: number;
}

interface ReactionButtonsProps {
  videoId: string;
  initialCounts: ReactionCounts;
  initialViewerReaction: ReactionKind | null;
}

/**
 * The only interactive piece of the engagement row — everything the viewer
 * identity module exists to unblock. Seeded from the server-rendered
 * `VideoDetail` (`initialCounts`/`initialViewerReaction`) and then manages
 * its own state from there; see the data-patterns skill's "pass from Server
 * Component" pattern for why this beats re-fetching on the client.
 *
 * Render with `key={videoId}` from the parent — this component's state must
 * not survive a navigation to a different video.
 */
export function ReactionButtons({
  videoId,
  initialCounts,
  initialViewerReaction,
}: ReactionButtonsProps) {
  const { viewer } = useViewer();
  const [counts, setCounts] = useState(initialCounts);
  const [reaction, setReaction] = useState(initialViewerReaction);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const canReact = viewer !== null && !isSubmitting;

  async function handleClick(kind: ReactionKind) {
    if (!viewer || isSubmitting) return;

    const previousReaction = reaction;
    const previousCounts = counts;
    const nextReaction = previousReaction === kind ? null : kind;

    // Optimistic: the UI reflects the intended end state immediately: an
    // API round trip on every click would make the button feel laggy for
    // what is, in the overwhelming common case, a guaranteed success.
    setCounts(applyReactionChange(previousCounts, previousReaction, nextReaction));
    setReaction(nextReaction);
    setIsSubmitting(true);

    try {
      if (nextReaction === null) {
        await removeMyReaction(videoId);
      } else {
        await setMyReaction(videoId, nextReaction);
      }
    } catch {
      // Roll back to exactly what was on screen before the click — no
      // partial-failure state to reconcile.
      setCounts(previousCounts);
      setReaction(previousReaction);
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <div className="flex items-center gap-3 text-sm">
      <ReactionButton
        label="Like"
        count={counts.likes}
        active={reaction === ReactionKind.Like}
        disabled={!canReact}
        disabledReason={viewer ? undefined : "Sign in to react"}
        onClick={() => handleClick(ReactionKind.Like)}
      />
      <ReactionButton
        label="Dislike"
        count={counts.dislikes}
        active={reaction === ReactionKind.Dislike}
        disabled={!canReact}
        disabledReason={viewer ? undefined : "Sign in to react"}
        onClick={() => handleClick(ReactionKind.Dislike)}
      />
    </div>
  );
}

/** Pure so the optimistic-update logic above is easy to reason about (and test) in isolation from any React state. */
function applyReactionChange(
  counts: ReactionCounts,
  from: ReactionKind | null,
  to: ReactionKind | null,
): ReactionCounts {
  const next = { ...counts };

  if (from === ReactionKind.Like) next.likes -= 1;
  if (from === ReactionKind.Dislike) next.dislikes -= 1;
  if (to === ReactionKind.Like) next.likes += 1;
  if (to === ReactionKind.Dislike) next.dislikes += 1;

  return next;
}

interface ReactionButtonProps {
  label: string;
  count: number;
  active: boolean;
  disabled: boolean;
  disabledReason?: string;
  onClick: () => void;
}

function ReactionButton({
  label,
  count,
  active,
  disabled,
  disabledReason,
  onClick,
}: ReactionButtonProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-pressed={active}
      title={disabled ? disabledReason : active ? `You reacted: ${label}` : label}
      className={`rounded-full px-3 py-1 transition-colors disabled:cursor-not-allowed disabled:opacity-60 ${
        active
          ? "bg-neutral-900 font-semibold text-white"
          : "bg-neutral-100 text-neutral-800 hover:bg-neutral-200"
      }`}
    >
      {label} {formatCompactNumber(count)}
    </button>
  );
}
