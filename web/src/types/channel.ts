/** Mirrors `JameX.Contracts.Dtos.ChannelDto`. */
export interface Channel {
  channelId: string;
  ownerUserId: string;
  name: string;
  handle: string;
  avatarUrl: string | null;
  subscriberCount: number;
  createdAt: string;
}
