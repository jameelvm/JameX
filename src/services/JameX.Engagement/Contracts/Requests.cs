using JameX.Contracts;

namespace JameX.Engagement.Contracts;

/// <summary>Body of the set-my-reaction endpoint.</summary>
public sealed record ReactionRequest(ReactionKind Kind);
