using JameX.Contracts.Dtos;
using JameX.Identity.Domain;

namespace JameX.Identity.Mapping;

/// <summary>
/// Entity to DTO. The projection is explicit and deliberate: entities are this
/// service's private shape and may change freely, while the DTOs in
/// <c>JameX.Contracts</c> are a published contract the Gateway depends on.
/// Returning entities directly would weld the two together and leak columns
/// (a password hash, a moderation flag) the moment one is added.
/// </summary>
public static class EntityMappings
{
    public static UserDto ToDto(this User user) =>
        new(user.Id, user.Email, user.DisplayName, user.CreatedAt);

    public static ChannelDto ToDto(this Channel channel) =>
        new(channel.Id,
            channel.OwnerUserId,
            channel.Name,
            channel.Handle,
            channel.AvatarUrl,
            channel.SubscriberCount,
            channel.CreatedAt);
}
