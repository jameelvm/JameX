"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";

import { logIn, signUp as signUpRequest } from "@/lib/api/users";
import {
  clearViewerTokenCookie,
  writeViewerTokenCookie,
} from "@/lib/viewer/cookie";
import {
  clearStoredViewer,
  readStoredViewer,
  writeStoredViewer,
} from "@/lib/viewer/storage";
import type { Viewer } from "@/types/viewer";

interface ViewerContextValue {
  /** `null` means genuinely signed out — a real, browsable state, not a loading placeholder. See `isLoading` to tell those apart. */
  viewer: Viewer | null;
  /** True only for the brief window before the initial `localStorage` read completes. */
  isLoading: boolean;
  login: (email: string, password: string) => Promise<void>;
  /** Registers the account, then signs it straight in — see the implementation's remark on why that's two backend calls behind one function. */
  signUp: (email: string, displayName: string, password: string) => Promise<void>;
  logout: () => void;
}

const ViewerContext = createContext<ViewerContextValue | null>(null);

/**
 * The real signed-in session, backed by a real password login against
 * Identity — see `types/viewer.ts`. Makes it available to every interactive
 * client component via {@link useViewer}: reactions, comments and uploads
 * all read from here rather than each re-implementing "is someone signed
 * in".
 *
 * Client-only by necessity: `localStorage` does not exist during server
 * rendering, so both server and initial client renders show `isLoading:
 * true, viewer: null`, and the real value only appears after the effect
 * below runs. Nothing reads `viewer` during the initial render, so there is
 * no hydration mismatch — components that need it should treat `isLoading`
 * as "haven't checked yet", not read `viewer` as final until it settles.
 *
 * This context is not how Server Components learn the viewer, and cannot
 * be — they render before any of this runs. That is what the token cookie
 * (`lib/viewer/cookie.ts`/`server-viewer.ts`) is for: a Server Component
 * reads it directly, independently of this provider, to ask the Gateway for
 * the viewer's own reaction up front (see the watch page).
 */
export function ViewerProvider({ children }: { children: ReactNode }) {
  // One state value, not two — the initial check below has exactly one
  // outcome to report ("here's the session" or "there is none"), and a
  // single setState call for it avoids the effect triggering two separate
  // renders on mount for what is really one transition.
  const [session, setSession] = useState<{ viewer: Viewer | null; isLoading: boolean }>({
    viewer: null,
    isLoading: true,
  });

  useEffect(() => {
    // Deferred to a microtask rather than read and set directly in the
    // effect body: `localStorage` is a synchronous external source, but
    // resolving it through a promise tick is what keeps this from reading
    // as "derive state during render" (which `readStoredViewer` genuinely
    // cannot do — `localStorage` does not exist during server rendering, so
    // the read has to happen after mount, not before).
    async function loadStoredSession() {
      const stored = readStoredViewer();
      // Re-mirrored on every load, not just once at sign-in, so a session
      // whose cookie was cleared (or who never had one, from before this
      // existed) gets it back without losing the localStorage session.
      if (stored) writeViewerTokenCookie(stored.token);
      setSession({ viewer: stored, isLoading: false });
    }

    void loadStoredSession();
  }, []);

  const applySession = useCallback((viewer: Viewer) => {
    writeStoredViewer(viewer);
    writeViewerTokenCookie(viewer.token);
    setSession({ viewer, isLoading: false });
  }, []);

  const login = useCallback(
    async (email: string, password: string) => {
      applySession(await logIn(email, password));
    },
    [applySession],
  );

  const signUp = useCallback(
    async (email: string, displayName: string, password: string) => {
      // Identity's POST /users (registration) returns a plain user record,
      // not a token — a real registration and a real login are two
      // different actions on the backend, matching how a production auth
      // flow usually keeps them separate. Chaining them here is a frontend
      // convenience so a caller only ever awaits one function; if the login
      // half fails (it shouldn't, immediately after a successful signup with
      // the same credentials) it surfaces as this call rejecting, same as
      // any other auth failure.
      await signUpRequest(email, displayName, password);
      applySession(await logIn(email, password));
    },
    [applySession],
  );

  const logout = useCallback(() => {
    clearStoredViewer();
    clearViewerTokenCookie();
    setSession({ viewer: null, isLoading: false });
  }, []);

  return (
    <ViewerContext.Provider
      value={{ viewer: session.viewer, isLoading: session.isLoading, login, signUp, logout }}
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
