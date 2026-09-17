interface VideoTagsProps {
  tags: string[];
}

export function VideoTags({ tags }: VideoTagsProps) {
  if (tags.length === 0) return null;

  return (
    <ul className="flex flex-wrap gap-2">
      {tags.map((tag) => (
        <li
          key={tag}
          className="rounded-full bg-neutral-100 px-3 py-1 text-xs text-neutral-700"
        >
          #{tag}
        </li>
      ))}
    </ul>
  );
}
