"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";

import { createGuestUser } from "@/lib/api/users";
import { clearViewerIdCookie, writeViewerIdCookie } from "@/lib/viewer/cookie";
import {
  generateGuestDisplayName,
  generateGuestEmail,
} from "@/lib/viewer/guest-identity";
import {
  clearStoredViewer,
  readStoredViewer,
  writeStoredViewer,
} from "@/lib/viewer/storage";
import type { Viewer } from "@/types/viewer";

interface ViewerContextValue {
  /** `null` while provisioning, or if provisioning failed — see `isLoading`/`error` to tell those apart. */
  viewer: Viewer | null;
  isLoading: boolean;
  error: unknown;
  /** Discards the current guest and provisions a fresh one. Mainly useful for testing multi-viewer interactions locally. */
  resetViewer: () => void;
}

const ViewerContext = createContext<ViewerContextValue | null>(null);

/**
 * Provisions the stand-in viewer identity described in `types/viewer.ts` and
 * makes it available to every interactive client component via
 * {@link useViewer} — reactions and comments read from here rather than each
 * re-implementing "find or create a guest user".
 *
 * Client-only by necessity: `localStorage` does not exist during server
 * rendering, so both server and initial client renders show `isLoading:
 * true, viewer: null`, and the real value only appears after the effect
 * below runs. Nothing reads `viewer` during the initial render, so there is
 * no hydration mismatch — components that need it should treat `isLoading`
 * as "not ready to react yet", not just check `viewer` for null.
 *
 * This context is not how Server Components learn the viewer, and cannot
 * be — they render before any of this runs. That is what the id-only cookie
 * (`lib/viewer/cookie.ts`/`server-viewer.ts`) is for: a Server Component
 * reads the cookie directly, independently of this provider, to ask the
 * Gateway for the viewer's own reaction up front (see the watch page).
 */
export function ViewerProvider({ children }: { children: ReactNode }) {
  const [viewer, setViewer] = useState<Viewer | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    let cancelled = false;

    async function loadOrCreateViewer() {
      setIsLoading(true);
      setError(null);

      const stored = readStoredViewer();
      if (stored) {
        // Re-mirrored on every load, not just once at creation, so a viewer
        // whose cookie was cleared (or who never had one, from before this
        // existed) gets it back without losing their localStorage identity.
        writeViewerIdCookie(stored.userId);
        if (!cancelled) {
          setViewer(stored);
          setIsLoading(false);
        }
        return;
      }

      try {
        const created = await createGuestUser(
          generateGuestDisplayName(),
          generateGuestEmail(),
        );
        writeStoredViewer(created);
        writeViewerIdCookie(created.userId);
        if (!cancelled) setViewer(created);
      } catch (creationError) {
        if (!cancelled) setError(creationError);
      } finally {
        if (!cancelled) setIsLoading(false);
      }
    }

    void loadOrCreateViewer();

    return () => {
      cancelled = true;
    };
  }, [generation]);

  const resetViewer = useCallback(() => {
    clearStoredViewer();
    clearViewerIdCookie();
    setViewer(null);
    setGeneration((count) => count + 1);
  }, []);

  return (
    <ViewerContext.Provider
      value={{ viewer, isLoading, error, resetViewer }}
    >
      {children}
    </ViewerContext.Provider>
  );
}

export function useViewer(): ViewerContextValue {
  const context = useContext(ViewerContext);
  if (!context) {
    throw new Error("useViewer must be used within a ViewerProvider");
  }
  return context;
}
