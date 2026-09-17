interface QualityOption {
  /** The hls.js level index this option switches to — not a label. */
  levelIndex: number;
  label: string;
}

interface QualitySelectorProps {
  options: QualityOption[];
  /** `-1` means "Auto" — hls.js's own convention for adaptive selection, kept as-is rather than remapped. */
  selectedLevelIndex: number;
  onChange: (levelIndex: number) => void;
}

/** A plain controlled `<select>` — no hls.js knowledge here, just rendering options and reporting a choice. */
export function QualitySelector({
  options,
  selectedLevelIndex,
  onChange,
}: QualitySelectorProps) {
  return (
    <select
      value={selectedLevelIndex}
      onChange={(event) => onChange(Number(event.target.value))}
      aria-label="Video quality"
      className="rounded border border-neutral-300 bg-white px-2 py-1 text-xs text-neutral-700"
    >
      <option value={-1}>Auto</option>
      {options.map((option) => (
        <option key={option.levelIndex} value={option.levelIndex}>
          {option.label}
        </option>
      ))}
    </select>
  );
}
