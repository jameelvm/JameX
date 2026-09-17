import type { PartState, UploadState } from "@/hooks/use-resumable-upload";

const PART_COLOR: Record<PartState, string> = {
  pending: "bg-neutral-200",
  uploading: "bg-amber-500",
  done: "bg-emerald-500",
  failed: "bg-red-500",
};

const PHASE_LABEL: Record<UploadState["phase"], string> = {
  idle: "",
  starting: "Opening the upload…",
  uploading: "Uploading…",
  paused: "Paused",
  completing: "Assembling the file…",
  done: "Upload complete — waiting for encoding to start.",
  error: "Something went wrong.",
};

interface UploadProgressProps {
  state: UploadState;
  onPause: () => void;
  onResume: () => void;
  onAbort: () => void;
}

/**
 * Purely presentational — every number here comes straight from
 * `useResumableUpload`'s state. The per-part grid mirrors the one already
 * proven useful in `web/debug/index.html`: it makes a stalled or failed part
 * visible at a glance, which a single aggregate percentage cannot.
 */
export function UploadProgress({
  state,
  onPause,
  onResume,
  onAbort,
}: UploadProgressProps) {
  if (state.phase === "idle") return null;

  const percent =
    state.totalParts === 0
      ? 0
      : Math.round((state.uploadedCount / state.totalParts) * 100);

  const canPause = state.phase === "uploading";
  const canResume = state.phase === "paused" || state.phase === "error";
  const canAbort = state.phase !== "done" && state.phase !== "completing";

  return (
    <div className="flex flex-col gap-3 rounded-lg bg-neutral-100 p-4">
      <div className="flex items-center justify-between text-sm text-neutral-800">
        <span>{PHASE_LABEL[state.phase]}</span>
        {state.totalParts > 0 && (
          <span>
            {state.uploadedCount} of {state.totalParts} parts — {percent}%
          </span>
        )}
      </div>

      {state.errorMessage && (
        <p className="text-sm text-red-600">{state.errorMessage}</p>
      )}

      {state.totalParts > 0 && (
        <>
          <div className="h-2 overflow-hidden rounded-full bg-neutral-200">
            <div
              className="h-full bg-neutral-900 transition-[width]"
              style={{ width: `${percent}%` }}
            />
          </div>

          <div className="flex flex-wrap gap-1">
            {state.partStates.map((partState, index) => (
              <div
                key={index}
                title={`Part ${index + 1}: ${partState}`}
                className={`h-3 w-3 rounded-sm ${PART_COLOR[partState]}`}
              />
            ))}
          </div>
        </>
      )}

      <div className="flex gap-3">
        {canPause && (
          <button
            type="button"
            onClick={onPause}
            className="rounded-full bg-neutral-200 px-3 py-1 text-xs text-neutral-800 hover:bg-neutral-300"
          >
            Pause
          </button>
        )}
        {canResume && (
          <button
            type="button"
            onClick={onResume}
            className="rounded-full bg-neutral-200 px-3 py-1 text-xs text-neutral-800 hover:bg-neutral-300"
          >
            Resume
          </button>
        )}
        {canAbort && (
          <button
            type="button"
            onClick={onAbort}
            className="rounded-full bg-neutral-200 px-3 py-1 text-xs text-neutral-800 hover:bg-neutral-300"
          >
            Abort
          </button>
        )}
      </div>
    </div>
  );
}
