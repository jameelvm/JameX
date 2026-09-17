const STORAGE_KEY = "jamex.upload";

/**
 * Just enough to resume after a reload — never the file itself, which
 * `localStorage` cannot hold anyway. The browser has no way to re-read bytes
 * from a `File` object across a page reload, so resuming still requires the
 * viewer to re-select the same file; this is what lets the upload recognise
 * it as a continuation rather than starting over.
 */
export interface PersistedUploadSession {
  uploadId: string;
  videoId: string;
  partSizeBytes: number;
  totalParts: number;
  fileName: string;
}

export function readPersistedSession(): PersistedUploadSession | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as PersistedUploadSession) : null;
  } catch {
    return null;
  }
}

export function writePersistedSession(session: PersistedUploadSession): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  } catch {
    // An unresumable upload after a reload is a worse experience, not a
    // broken one — the upload itself already succeeded or is in progress.
  }
}

export function clearPersistedSession(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Best effort.
  }
}
