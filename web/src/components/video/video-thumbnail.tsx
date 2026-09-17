"use client";

import Image from "next/image";
import { useState } from "react";

interface VideoThumbnailProps {
  src: string | null;
  alt: string;
}

/**
 * The thumbnail slot on a grid tile. `src` being non-null is not a guarantee
 * the image actually loads — a video can have a `thumbnailUrl` on the wire
 * pointing at an object that was never really written (see PROGRESS.md's
 * environment notes on S3 not surviving a stack restart, or a video whose
 * encode never produced a poster frame) — so this needs its own "did this
 * fail" state, not just a null check. Falls back to the same placeholder
 * either way, so a broken image and a genuinely missing one look identical
 * rather than one being a broken-image icon and the other a blank box.
 */
export function VideoThumbnail({ src, alt }: VideoThumbnailProps) {
  const [failed, setFailed] = useState(false);

  if (!src || failed) {
    return <ThumbnailPlaceholder />;
  }

  return (
    <Image
      src={src}
      alt={alt}
      fill
      sizes="(max-width: 640px) 100vw, (max-width: 1024px) 50vw, 25vw"
      className="object-cover"
      onError={() => setFailed(true)}
    />
  );
}

function ThumbnailPlaceholder() {
  return (
    <div className="flex h-full w-full items-center justify-center bg-neutral-200">
      <svg
        aria-hidden
        viewBox="0 0 24 24"
        className="h-10 w-10 fill-none stroke-neutral-400 stroke-[1.5]"
      >
        <rect x="2.5" y="5" width="19" height="14" rx="2.5" />
        <path d="M10 9.5l5 2.5-5 2.5z" fill="currentColor" stroke="none" />
      </svg>
    </div>
  );
}
