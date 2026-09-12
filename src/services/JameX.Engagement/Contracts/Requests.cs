using JameX.Contracts;

namespace JameX.Engagement.Contracts;

/// <summary>Body of the set-my-reaction endpoint.</summary>
public sealed record ReactionRequest(ReactionKind Kind);

/// <summary>
/// Body of the create-comment endpoint. A null <see cref="ParentCommentId"/>
/// is a top-level comment; a non-null one is a reply.
/// </summary>
public sealed record CreateCommentRequest(string Text, Guid? ParentCommentId);

/// <summary>Body of the edit-comment endpoint.</summary>
public sealed record UpdateCommentRequest(string Text);
