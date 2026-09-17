import type { Viewer } from "@/types/viewer";

const STORAGE_KEY = "jamex.viewer";

/**
 * Every `localStorage` access is wrapped: it can throw in a private window,
 * with site data blocked, or with storage full, and none of those should
 * crash the app — they should just mean "no stored viewer this time".
 */
export function readStoredViewer(): Viewer | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;

    const parsed: unknown = JSON.parse(raw);
    if (!isViewer(parsed)) return null;

    return parsed;
  } catch {
    return null;
  }
}

export function writeStoredViewer(viewer: Viewer): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(viewer));
  } catch {
    // A viewer created but not persisted just gets re-created next visit —
    // degraded, not broken.
  }
}

export function clearStoredViewer(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to do — worst case the next read still sees the old viewer.
  }
}

function isViewer(value: unknown): value is Viewer {
  return (
    typeof value === "object" &&
    value !== null &&
    typeof (value as Viewer).userId === "string" &&
    typeof (value as Viewer).displayName === "string"
  );
}
