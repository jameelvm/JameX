using JameX.Contracts.Dtos;

namespace JameX.Identity.Contracts;

/// <summary>
/// The login response. Kept here rather than in <c>JameX.Contracts</c> for the
/// same reason request bodies are — see the remark on
/// <see cref="CreateUserRequest"/>. The Gateway proxies this straight through
/// to the browser as raw bytes (a YARP route, not a BFF aggregation), so no
/// other service ever needs a C# type to deserialize it.
/// </summary>
public sealed record AuthResponse(string Token, UserDto User);
