"use client";

import { useViewer } from "@/components/viewer/viewer-provider";
import { PipelineStatus } from "@/components/upload/pipeline-status";
import { UploadForm, type UploadFormValues } from "@/components/upload/upload-form";
import { UploadProgress } from "@/components/upload/upload-progress";
import { useResumableUpload } from "@/hooks/use-resumable-upload";

/**
 * Decides which of the three upload stages to render — the picker, live
 * progress, or the post-upload pipeline status — purely from
 * `useResumableUpload`'s state. No upload logic lives in this component;
 * it only routes between the pieces that do.
 */
export function UploadPageClient() {
  const { viewer, isLoading: isViewerLoading } = useViewer();
  const { state, persistedSession, start, pause, resume, resumeFromReload, abort } =
    useResumableUpload();

  function handleSubmit(values: UploadFormValues) {
    if (persistedSession) {
      void resumeFromReload(values.file);
      return;
    }
    void start(values.file, {
      title: values.title,
      description: values.description,
      categoryId: null,
      tags: values.tags,
      privacy: values.privacy,
    });
  }

  if (state.phase === "done" && state.videoId) {
    return <PipelineStatus videoId={state.videoId} />;
  }

  if (state.phase !== "idle") {
    return (
      <UploadProgress state={state} onPause={pause} onResume={resume} onAbort={abort} />
    );
  }

  return (
    <UploadForm
      onSubmit={handleSubmit}
      disabled={isViewerLoading || viewer === null}
      disabledReason="Signing you in…"
      resumeMode={persistedSession ? { fileName: persistedSession.fileName } : undefined}
    />
  );
}
