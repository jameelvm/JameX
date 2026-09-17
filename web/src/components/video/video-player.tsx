"use client";

import Hls, { Events, ErrorTypes, type ErrorData, type Level } from "hls.js";
import { useEffect, useRef, useState } from "react";

import { QualitySelector } from "@/components/video/quality-selector";

interface VideoPlayerProps {
  masterPlaylistUrl: string;
  posterUrl: string | null;
  title: string;
}

/**
 * hls.js only exists to make adaptive HLS playback work in browsers that
 * don't support it natively (everything except Safari) — Safari's `<video>`
 * plays an `.m3u8` URL directly, no library involved. Both paths end up
 * driving the same plain `<video>` element; hls.js just feeds it segments
 * MSE couldn't otherwise parse.
 */
export function VideoPlayer({
  masterPlaylistUrl,
  posterUrl,
  title,
}: VideoPlayerProps) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const hlsRef = useRef<Hls | null>(null);
  const [levels, setLevels] = useState<Level[]>([]);
  const [currentLevel, setCurrentLevel] = useState(-1); // hls.js convention: -1 is "Auto"
  const [fatalError, setFatalError] = useState<string | null>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    setLevels([]);
    setCurrentLevel(-1);
    setFatalError(null);

    if (Hls.isSupported()) {
      const hls = new Hls();
      hlsRef.current = hls;

      hls.on(Events.MANIFEST_PARSED, (_event, data) => {
        setLevels(data.levels);
      });

      hls.on(Events.LEVEL_SWITCHED, (_event, data) => {
        setCurrentLevel(data.level);
      });

      // Retry state for the non-fatal path below. A plain closure variable,
      // not state: it must survive across ERROR events without triggering
      // re-renders, and resets whenever this effect re-runs for a new source.
      let nonFatalNetworkRetries = 0;
      const maxNonFatalRetries = 3;

      hls.on(Events.ERROR, (_event, data: ErrorData) => {
        if (!data.fatal) {
          // A level/fragment load can come back as a network error without
          // hls.js ever marking it fatal — hls.js logs it, blames that one
          // level, and otherwise leaves the stream permanently stalled at
          // zero buffered data, even when a sibling level's playlist loaded
          // fine moments earlier. hls.js doesn't retry this case on its own;
          // a fresh startLoad() does, reliably, since the request itself
          // remains valid — only capped so a genuinely unreachable source
          // doesn't retry forever.
          if (
            data.type === ErrorTypes.NETWORK_ERROR &&
            nonFatalNetworkRetries < maxNonFatalRetries
          ) {
            nonFatalNetworkRetries += 1;
            hls.startLoad();
          }
          return;
        }

        // The two recoverable fatal categories hls.js itself documents
        // recovery paths for; anything else genuinely can't continue.
        switch (data.type) {
          case ErrorTypes.NETWORK_ERROR:
            hls.startLoad();
            return;
          case ErrorTypes.MEDIA_ERROR:
            hls.recoverMediaError();
            return;
          default:
            setFatalError("Playback failed. Try reloading the page.");
            hls.destroy();
        }
      });

      hls.loadSource(masterPlaylistUrl);
      hls.attachMedia(video);

      return () => {
        hls.destroy();
        hlsRef.current = null;
      };
    }

    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      // Safari: native HLS, no adaptive-quality UI to offer — the browser
      // handles rung selection internally and exposes no hook into it.
      video.src = masterPlaylistUrl;
      return;
    }

    setFatalError("This browser cannot play HLS video.");
  }, [masterPlaylistUrl]);

  function handleQualityChange(levelIndex: number) {
    if (hlsRef.current) {
      hlsRef.current.currentLevel = levelIndex;
    }
    // hls.js only fires LEVEL_SWITCHED once the switch actually completes;
    // reflecting the choice immediately keeps the selector from looking
    // unresponsive for the fraction of a second that takes.
    setCurrentLevel(levelIndex);
  }

  if (fatalError) {
    return (
      <div className="flex aspect-video w-full items-center justify-center rounded-xl bg-neutral-900 text-sm text-neutral-400">
        {fatalError}
      </div>
    );
  }

  const qualityOptions = levels
    .map((level, levelIndex) => ({ levelIndex, height: level.height }))
    .sort((a, b) => b.height - a.height)
    .map(({ levelIndex, height }) => ({
      levelIndex,
      label: `${height}p`,
    }));

  return (
    <div className="flex flex-col gap-2">
      <video
        ref={videoRef}
        controls
        poster={posterUrl ?? undefined}
        aria-label={title}
        className="aspect-video w-full rounded-xl bg-black"
      />
      {qualityOptions.length > 0 && (
        <div className="flex justify-end">
          <QualitySelector
            options={qualityOptions}
            selectedLevelIndex={currentLevel}
            onChange={handleQualityChange}
          />
        </div>
      )}
    </div>
  );
}
