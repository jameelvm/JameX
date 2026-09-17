/** Mirrors `JameX.Contracts.Dtos.CommentDto` field-for-field. */
export interface Comment {
  commentId: string;
  videoId: string;
  userId: string;
  userDisplayName: string | null;
  parentCommentId: string | null;
  text: string;
  createdAt: string;
  isEdited: boolean;
}
