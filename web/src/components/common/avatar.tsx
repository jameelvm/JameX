const PALETTE = [
  "bg-red-600",
  "bg-orange-600",
  "bg-amber-600",
  "bg-emerald-600",
  "bg-teal-600",
  "bg-blue-600",
  "bg-indigo-600",
  "bg-purple-600",
  "bg-pink-600",
] as const;

/** A short, deterministic hash — same name always picks the same colour, without pulling in a hashing library for one `% palette.length`. */
function hashToIndex(value: string, modulo: number): number {
  let hash = 0;
  for (let i = 0; i < value.length; i++) {
    hash = (hash * 31 + value.charCodeAt(i)) | 0;
  }
  return Math.abs(hash) % modulo;
}

interface AvatarProps {
  name: string | null;
  size?: number;
}

/**
 * No channel in this system has ever had a real `avatarUrl` — Identity's
 * schema carries the field, but nothing populates it yet. Rather than
 * rendering nothing (reads as a bug) or a generic silhouette (reads as
 * "broken image"), this is what a real platform's own placeholder does:
 * initials on a colour picked deterministically from the name, so the same
 * channel always looks the same without any image at all.
 */
export function Avatar({ name, size = 32 }: AvatarProps) {
  const label = name?.trim() || "?";
  const initial = label.charAt(0).toUpperCase();
  const colorClass = PALETTE[hashToIndex(label, PALETTE.length)];

  return (
    <span
      aria-hidden
      className={`flex shrink-0 items-center justify-center rounded-full font-medium text-white ${colorClass}`}
      style={{ width: size, height: size, fontSize: size * 0.42 }}
    >
      {initial}
    </span>
  );
}
