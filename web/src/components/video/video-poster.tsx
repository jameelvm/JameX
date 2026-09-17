import Image from "next/image";

interface VideoPosterProps {
  posterUrl: string | null;
  title: string;
}

/**
 * The poster frame shown above the fold, before playback starts. Falls back
 * to a plain placeholder rather than rendering nothing when a video has no
 * poster yet (e.g. still transcoding) — an empty layout region reads as a
 * bug, a placeholder reads as "nothing to see yet".
 */
export function VideoPoster({ posterUrl, title }: VideoPosterProps) {
  return (
    <div className="relative aspect-video w-full overflow-hidden rounded-xl bg-neutral-900">
      {posterUrl ? (
        <Image
          src={posterUrl}
          alt={title}
          fill
          sizes="(max-width: 1024px) 100vw, 1024px"
          className="object-cover"
          priority
        />
      ) : (
        <div className="flex h-full w-full items-center justify-center text-sm text-neutral-500">
          No preview available yet
        </div>
      )}
    </div>
  );
}
