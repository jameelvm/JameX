"use client";

import { useState, type FormEvent } from "react";

import { VideoPrivacy } from "@/types/video";

export interface UploadFormValues {
  file: File;
  title: string;
  description: string | null;
  tags: string[];
  privacy: VideoPrivacy;
}

interface UploadFormProps {
  onSubmit: (values: UploadFormValues) => void;
  disabled: boolean;
  disabledReason?: string;
  /** Swaps the submit label/behaviour for continuing an upload found in `localStorage` after a reload. */
  resumeMode?: { fileName: string };
}

/**
 * Pure form state — no upload logic here at all. Submitting hands a plain
 * `UploadFormValues` up to whatever owns `useResumableUpload`; this
 * component would look identical wired to a completely different upload
 * mechanism.
 */
export function UploadForm({
  onSubmit,
  disabled,
  disabledReason,
  resumeMode,
}: UploadFormProps) {
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [tagsInput, setTagsInput] = useState("");
  const [privacy, setPrivacy] = useState<VideoPrivacy>(VideoPrivacy.Public);

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (!file) return;

    if (resumeMode) {
      onSubmit({ file, title: "", description: null, tags: [], privacy });
      return;
    }

    onSubmit({
      file,
      title: title.trim() || file.name,
      description: description.trim() || null,
      tags: tagsInput
        .split(",")
        .map((tag) => tag.trim())
        .filter(Boolean),
      privacy,
    });
  }

  const canSubmit = !disabled && file !== null;

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-4">
      {resumeMode && (
        <p className="rounded-lg bg-neutral-100 px-3 py-2 text-sm text-neutral-800">
          An unfinished upload for <strong>{resumeMode.fileName}</strong> was
          found. Choose that same file again to resume it.
        </p>
      )}

      <div className="flex flex-col gap-1">
        <label htmlFor="upload-file" className="text-sm text-neutral-600">
          Video file
        </label>
        <input
          id="upload-file"
          type="file"
          accept="video/*"
          onChange={(event) => setFile(event.target.files?.[0] ?? null)}
          className="text-sm text-neutral-700 file:mr-3 file:rounded-full file:border-0 file:bg-neutral-100 file:px-3 file:py-1.5 file:text-neutral-800 file:hover:bg-neutral-200"
        />
      </div>

      {!resumeMode && (
        <>
          <div className="flex flex-col gap-1">
            <label htmlFor="upload-title" className="text-sm text-neutral-600">
              Title
            </label>
            <input
              id="upload-title"
              value={title}
              onChange={(event) => setTitle(event.target.value)}
              placeholder={file?.name ?? "Untitled"}
              maxLength={200}
              className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 placeholder:text-neutral-500 focus:border-neutral-900 focus:outline-none"
            />
          </div>

          <div className="flex flex-col gap-1">
            <label htmlFor="upload-description" className="text-sm text-neutral-600">
              Description
            </label>
            <textarea
              id="upload-description"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              rows={3}
              maxLength={5000}
              className="resize-none rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
            />
          </div>

          <div className="flex gap-4">
            <div className="flex flex-1 flex-col gap-1">
              <label htmlFor="upload-tags" className="text-sm text-neutral-600">
                Tags (comma separated)
              </label>
              <input
                id="upload-tags"
                value={tagsInput}
                onChange={(event) => setTagsInput(event.target.value)}
                className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
              />
            </div>

            <div className="flex flex-col gap-1">
              <label htmlFor="upload-privacy" className="text-sm text-neutral-600">
                Privacy
              </label>
              <select
                id="upload-privacy"
                value={privacy}
                onChange={(event) => setPrivacy(Number(event.target.value) as VideoPrivacy)}
                className="rounded-lg border border-neutral-300 px-3 py-2 text-sm text-neutral-900 focus:border-neutral-900 focus:outline-none"
              >
                <option value={VideoPrivacy.Public}>Public</option>
                <option value={VideoPrivacy.Unlisted}>Unlisted</option>
                <option value={VideoPrivacy.Private}>Private</option>
              </select>
            </div>
          </div>
        </>
      )}

      <button
        type="submit"
        disabled={!canSubmit}
        title={disabled ? disabledReason : undefined}
        className="self-start rounded-full bg-blue-600 px-5 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-neutral-200 disabled:text-neutral-400"
      >
        {resumeMode ? "Resume upload" : "Start upload"}
      </button>
    </form>
  );
}
