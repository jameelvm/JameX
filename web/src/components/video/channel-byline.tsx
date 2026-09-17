import { Avatar } from "@/components/common/avatar";

interface ChannelBylineProps {
  channelName: string | null;
  publishedLabel: string | null;
}

/**
 * `channelName` and `publishedLabel` are both nullable on the wire for
 * reasons that are worth surfacing rather than silently blanking: a null
 * channel name means Identity could not be reached when the watch page was
 * assembled (see `IIdentityReadClient`), and a null publish date means the
 * video is not yet public.
 */
export function ChannelByline({
  channelName,
  publishedLabel,
}: ChannelBylineProps) {
  return (
    <div className="flex items-center gap-3">
      <Avatar name={channelName} size={40} />
      <div className="flex flex-col text-sm">
        <span className="font-medium text-neutral-900">
          {channelName ?? "Unknown channel"}
        </span>
        {publishedLabel && (
          <span className="text-xs text-neutral-600">{publishedLabel}</span>
        )}
      </div>
    </div>
  );
}
