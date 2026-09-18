"use client";

import { useCallback, useEffect, useRef, useState } from "react";

import { useViewer } from "@/components/viewer/viewer-provider";
import {
  abortUpload,
  beginUpload,
  completeUpload,
  getUploadStatus,
  presignParts,
  reportPart,
} from "@/lib/api/uploads-client";
import { ensureMyChannel } from "@/lib/upload/ensure-channel";
import {
  clearPersistedSession,
  readPersistedSession,
  writePersistedSession,
  type PersistedUploadSession,
} from "@/lib/upload/persisted-session";
import type { CreateUploadRequest } from "@/types/upload";
import type { VideoPrivacy } from "@/types/video";

export type PartState = "pending" | "uploading" | "done" | "failed";

export type UploadPhase =
  | "idle"
  | "starting"
  | "uploading"
  | "paused"
  | "completing"
  | "done"
  | "error";

export interface UploadMetadata {
  title: string;
  description: string | null;
  categoryId: string | null;
  tags: string[];
  privacy: VideoPrivacy;
}

export interface UploadState {
  phase: UploadPhase;
  videoId: string | null;
  totalParts: number;
  partStates: PartState[];
  uploadedCount: number;
  errorMessage: string | null;
}

/** How many part uploads run at once — parallel, but bounded, the same trade-off the debug harness made. */
const MAX_CONCURRENCY = 4;

const IDLE_STATE: UploadState = {
  phase: "idle",
  videoId: null,
  totalParts: 0,
  partStates: [],
  uploadedCount: 0,
  errorMessage: null,
};

/** In-flight session details, held outside React state deliberately — see the hook's own remarks. */
interface ActiveSession {
  uploadId: string;
  partSizeBytes: number;
  totalParts: number;
  file: File;
}

/**
 * Ports the resumable multipart upload flow already proven in
 * `web/debug/index.html` into a proper hook: open the upload, ask which
 * parts are still missing, presign and PUT the gaps straight to S3, report
 * each ETag back, complete once every part has landed.
 *
 * `ActiveSession` and the pause flag live in refs, not state — the upload
 * loop below reads them synchronously on every iteration, and React state
 * updates are batched and asynchronous, which is the wrong tool for a value
 * a tight loop needs to see change immediately.
 */
export function useResumableUpload() {
  const { viewer } = useViewer();
  const [state, setState] = useState<UploadState>(IDLE_STATE);
  const [persistedSession, setPersistedSession] =
    useState<PersistedUploadSession | null>(null);

  const sessionRef = useRef<ActiveSession | null>(null);
  const pausedRef = useRef(false);

  useEffect(() => {
    setPersistedSession(readPersistedSession());
  }, []);

  const setPartState = useCallback((partNumber: number, next: PartState) => {
    setState((current) => {
      const partStates = [...current.partStates];
      partStates[partNumber - 1] = next;
      return { ...current, partStates };
    });
  }, []);

  const finishUpload = useCallback(async (session: ActiveSession) => {
    setState((current) => ({ ...current, phase: "completing" }));
    await completeUpload(session.uploadId);
    clearPersistedSession();
    setPersistedSession(null);
    setState((current) => ({ ...current, phase: "done" }));
  }, []);

  const uploadMissingParts = useCallback(async () => {
    const session = sessionRef.current;
    if (!session) return;

    setState((current) => ({ ...current, phase: "uploading", errorMessage: null }));

    // The client never assumes what already landed — it asks. This one call
    // is what makes both "resume after a pause" and "resume after a reload"
    // the same code path as a fresh upload.
    const status = await getUploadStatus(session.uploadId);
    const alreadyUploaded = new Set(status.uploadedPartNumbers);

    setState((current) => {
      const partStates = [...current.partStates];
      alreadyUploaded.forEach((partNumber) => {
        partStates[partNumber - 1] = "done";
      });
      return { ...current, partStates, uploadedCount: alreadyUploaded.size };
    });

    const missing: number[] = [];
    for (let partNumber = 1; partNumber <= session.totalParts; partNumber++) {
      if (!alreadyUploaded.has(partNumber)) missing.push(partNumber);
    }

    if (missing.length === 0) {
      await finishUpload(session);
      return;
    }

    const presigned = await presignParts(session.uploadId, missing);
    const urlByPart = new Map(presigned.map((part) => [part.partNumber, part.url]));

    const queue = [...missing];
    let anyFailed = false;

    const uploadOnePart = async (partNumber: number) => {
      setPartState(partNumber, "uploading");

      const start = (partNumber - 1) * session.partSizeBytes;
      const end = Math.min(start + session.partSizeBytes, session.file.size);
      const blob = session.file.slice(start, end);

      const url = urlByPart.get(partNumber);
      if (!url) throw new Error(`No presigned URL was issued for part ${partNumber}.`);

      try {
        // The bytes go straight to S3 — this is the whole reason a 600 MB
        // upload never makes Ingest the bottleneck.
        const putResponse = await fetch(url, { method: "PUT", body: blob });
        if (!putResponse.ok) throw new Error(`S3 returned ${putResponse.status}`);

        // Readable only because the raw bucket's CORS config exposes ETag —
        // without that this is null and the upload can never complete.
        const eTag = putResponse.headers.get("ETag");
        if (!eTag) throw new Error("ETag not readable from the S3 response.");

        await reportPart(session.uploadId, partNumber, eTag);

        setPartState(partNumber, "done");
        setState((current) => ({ ...current, uploadedCount: current.uploadedCount + 1 }));
      } catch {
        anyFailed = true;
        setPartState(partNumber, "failed");
      }
    };

    async function worker() {
      while (queue.length > 0) {
        if (pausedRef.current) return;
        const partNumber = queue.shift();
        if (partNumber !== undefined) await uploadOnePart(partNumber);
      }
    }

    const workerCount = Math.min(MAX_CONCURRENCY, missing.length);
    await Promise.all(Array.from({ length: workerCount }, worker));

    if (pausedRef.current) {
      setState((current) => ({ ...current, phase: "paused" }));
      return;
    }
    if (anyFailed) {
      setState((current) => ({
        ...current,
        phase: "error",
        errorMessage: "Some parts failed to upload. Press Resume to retry them.",
      }));
      return;
    }
    await finishUpload(session);
  }, [finishUpload, setPartState]);

  const runUploadLoop = useCallback(async () => {
    try {
      await uploadMissingParts();
    } catch (error) {
      setState((current) => ({
        ...current,
        phase: "error",
        errorMessage: describeError(error),
      }));
    }
  }, [uploadMissingParts]);

  const start = useCallback(
    async (file: File, metadata: UploadMetadata) => {
      if (!viewer) return;

      pausedRef.current = false;
      setState({ ...IDLE_STATE, phase: "starting" });

      try {
        const channelId = await ensureMyChannel(viewer);

        const request: CreateUploadRequest = {
          channelId,
          title: metadata.title,
          description: metadata.description,
          categoryId: metadata.categoryId,
          tags: metadata.tags,
          defaultLanguage: "en",
          privacy: metadata.privacy,
          fileName: file.name,
          contentType: file.type || "application/octet-stream",
          sizeBytes: file.size,
        };

        const created = await beginUpload(request);

        sessionRef.current = {
          uploadId: created.uploadId,
          partSizeBytes: created.partSizeBytes,
          totalParts: created.totalParts,
          file,
        };

        writePersistedSession({
          uploadId: created.uploadId,
          videoId: created.videoId,
          partSizeBytes: created.partSizeBytes,
          totalParts: created.totalParts,
          fileName: file.name,
        });

        setState({
          phase: "uploading",
          videoId: created.videoId,
          totalParts: created.totalParts,
          partStates: Array<PartState>(created.totalParts).fill("pending"),
          uploadedCount: 0,
          errorMessage: null,
        });

        await runUploadLoop();
      } catch (error) {
        setState((current) => ({
          ...current,
          phase: "error",
          errorMessage: describeError(error),
        }));
      }
    },
    [viewer, runUploadLoop],
  );

  const resumeFromReload = useCallback(
    async (file: File) => {
      if (!viewer || !persistedSession) return;

      pausedRef.current = false;
      sessionRef.current = {
        uploadId: persistedSession.uploadId,
        partSizeBytes: persistedSession.partSizeBytes,
        totalParts: persistedSession.totalParts,
        file,
      };

      setState({
        phase: "uploading",
        videoId: persistedSession.videoId,
        totalParts: persistedSession.totalParts,
        partStates: Array<PartState>(persistedSession.totalParts).fill("pending"),
        uploadedCount: 0,
        errorMessage: null,
      });

      await runUploadLoop();
    },
    [viewer, persistedSession, runUploadLoop],
  );

  const pause = useCallback(() => {
    pausedRef.current = true;
  }, []);

  const resume = useCallback(async () => {
    if (!sessionRef.current) return;
    pausedRef.current = false;
    await runUploadLoop();
  }, [runUploadLoop]);

  const abort = useCallback(async () => {
    const session = sessionRef.current;
    if (!session) return;

    pausedRef.current = true;
    try {
      await abortUpload(session.uploadId);
    } finally {
      sessionRef.current = null;
      clearPersistedSession();
      setPersistedSession(null);
      setState(IDLE_STATE);
    }
  }, []);

  return { state, persistedSession, start, pause, resume, resumeFromReload, abort };
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : "Something went wrong.";
}
