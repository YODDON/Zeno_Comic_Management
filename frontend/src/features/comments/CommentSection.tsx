import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { createComment, deleteComment, editComment, getComments, getReplies, type CommentItem, type CommentTarget } from "@/features/comments/comments-api";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const key = (target: CommentTarget, page: number) => ["interaction", "comments", target, page] as const;

export function CommentSection({ target }: { target: CommentTarget }) {
  const [page, setPage] = useState(1);
  const [body, setBody] = useState("");
  const profile = useProfileQuery();
  const client = useQueryClient();
  const comments = useQuery({ queryKey: key(target, page), queryFn: () => getComments(target, page) });
  const create = useMutation({
    mutationFn: () => createComment(target, body.trim()),
    onSuccess: async () => { setBody(""); await client.invalidateQueries({ queryKey: ["interaction", "comments"] }); },
  });

  return (
    <section aria-labelledby="comments-heading" className="comment-section">
      <div className="section-heading-row"><div><p className="section-kicker">Thảo luận</p><h2 id="comments-heading">Bình luận</h2></div>{comments.data ? <span>{comments.data.totalItems} bình luận</span> : null}</div>
      {profile.data ? (
        <form onSubmit={(event) => { event.preventDefault(); if (body.trim()) create.mutate(); }}>
          <label htmlFor="new-comment">Viết bình luận</label>
          <textarea id="new-comment" maxLength={2000} onChange={(event) => setBody(event.target.value)} required rows={3} value={body} />
          <Button disabled={create.isPending || !body.trim()} type="submit">{create.isPending ? "Đang gửi…" : "Gửi bình luận"}</Button>
          {create.isError ? <p className="field-error" role="alert">Không thể gửi bình luận. Hãy thử lại.</p> : null}
        </form>
      ) : profile.isPending ? <p aria-busy="true">Đang kiểm tra đăng nhập…</p> : <p className="inline-notice">Đăng nhập với Google để bình luận.</p>}
      {comments.isPending ? <LoadingState title="Đang tải bình luận" /> : null}
      {comments.isError ? <ApiErrorState error={comments.error} onRetry={() => void comments.refetch()} /> : null}
      {comments.data?.items.length === 0 ? <p>Chưa có bình luận. Hãy bắt đầu cuộc trò chuyện.</p> : null}
      <ol className="comment-list">{comments.data?.items.map((comment) => <CommentCard comment={comment} currentUserId={profile.data?.id} key={comment.id} target={target} />)}</ol>
      {comments.data && comments.data.totalPages > 1 ? <nav aria-label="Phân trang bình luận" className="comment-pagination"><Button disabled={page === 1} onClick={() => setPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {page}/{comments.data.totalPages}</span><Button disabled={page >= comments.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau</Button></nav> : null}
    </section>
  );
}

function CommentCard({ comment, currentUserId, target }: { comment: CommentItem; currentUserId?: string; target: CommentTarget }) {
  const client = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [replying, setReplying] = useState(false);
  const [showReplies, setShowReplies] = useState(false);
  const [text, setText] = useState(comment.body);
  const [reply, setReply] = useState("");
  const replies = useInfiniteQuery({
    queryKey: ["interaction", "comment-replies", comment.id],
    queryFn: ({ pageParam }) => getReplies(comment.id, pageParam),
    initialPageParam: 1,
    getNextPageParam: (lastPage) => lastPage.page < lastPage.totalPages ? lastPage.page + 1 : undefined,
    enabled: showReplies,
  });
  const refresh = async () => { await client.invalidateQueries({ queryKey: ["interaction", "comments"] }); await client.invalidateQueries({ queryKey: ["interaction", "comment-replies", comment.id] }); };
  const edit = useMutation({ mutationFn: () => editComment(comment.id, text.trim(), comment.version), onSuccess: async () => { setEditing(false); await refresh(); } });
  const remove = useMutation({ mutationFn: () => deleteComment(comment.id, comment.version), onSuccess: refresh });
  const createReply = useMutation({ mutationFn: () => createComment(target, reply.trim(), comment.id), onSuccess: async () => { setReply(""); setReplying(false); setShowReplies(true); await refresh(); } });
  const pending = edit.isPending || remove.isPending || createReply.isPending;
  const replyItems = replies.data?.pages.flatMap((replyPage) => replyPage.items) ?? [];
  return <li className="comment-card"><article><header><strong>{comment.displayName}</strong><time dateTime={comment.createdAt}>{new Date(comment.createdAt).toLocaleString("vi-VN")}</time></header>{editing ? <form onSubmit={(event) => { event.preventDefault(); if (text.trim()) edit.mutate(); }}><label className="sr-only" htmlFor={`edit-${comment.id}`}>Sửa bình luận</label><textarea id={`edit-${comment.id}`} maxLength={2000} onChange={(event) => setText(event.target.value)} value={text} /><Button disabled={pending || !text.trim()} type="submit">Lưu</Button><Button onClick={() => { setText(comment.body); setEditing(false); }} type="button" variant="ghost">Hủy</Button></form> : <p>{comment.body}</p>}<div className="comment-actions">{currentUserId ? <Button disabled={pending} onClick={() => setReplying((value) => !value)} variant="ghost">Trả lời</Button> : null}{currentUserId === comment.userId ? <><Button disabled={pending} onClick={() => setEditing(true)} variant="ghost">Sửa</Button><Button disabled={pending} onClick={() => remove.mutate()} variant="ghost">Xóa</Button></> : null}{comment.replyCount > 0 ? <Button onClick={() => setShowReplies((value) => !value)} variant="ghost">{showReplies ? "Ẩn phản hồi" : `Xem ${comment.replyCount} phản hồi`}</Button> : null}</div>{(edit.isError || remove.isError || createReply.isError) ? <p className="field-error" role="alert">Không thể cập nhật bình luận. Hãy tải lại và thử lại.</p> : null}{replying ? <form onSubmit={(event) => { event.preventDefault(); if (reply.trim()) createReply.mutate(); }}><label htmlFor={`reply-${comment.id}`}>Phản hồi {comment.displayName}</label><textarea autoFocus id={`reply-${comment.id}`} maxLength={2000} onChange={(event) => setReply(event.target.value)} value={reply} /><Button disabled={pending || !reply.trim()} type="submit">Gửi phản hồi</Button></form> : null}</article>{showReplies ? <div className="comment-replies">{replies.isPending ? <p aria-busy="true">Đang tải phản hồi…</p> : null}{replies.isError ? <div role="alert"><p>Không thể tải phản hồi.</p><Button onClick={() => void replies.refetch()} variant="outline">Thử lại phản hồi</Button></div> : null}<ol>{replyItems.map((item) => <ReplyRow currentUserId={currentUserId} item={item} key={item.id} onChanged={refresh} />)}</ol>{replies.hasNextPage ? <Button disabled={replies.isFetchingNextPage} onClick={() => void replies.fetchNextPage()} variant="outline">{replies.isFetchingNextPage ? "Đang tải thêm…" : "Tải thêm phản hồi"}</Button> : null}</div> : null}</li>;
}

function ReplyRow({ item, currentUserId, onChanged }: { item: CommentItem; currentUserId?: string; onChanged: () => Promise<void> }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(item.body);
  const edit = useMutation({ mutationFn: () => editComment(item.id, text.trim(), item.version), onSuccess: async () => { setEditing(false); await onChanged(); } });
  const remove = useMutation({ mutationFn: () => deleteComment(item.id, item.version), onSuccess: onChanged });
  const pending = edit.isPending || remove.isPending;
  return <li><strong>{item.displayName}</strong>{editing ? <form onSubmit={(event) => { event.preventDefault(); if (text.trim()) edit.mutate(); }}><label className="sr-only" htmlFor={`edit-reply-${item.id}`}>Sửa phản hồi</label><textarea id={`edit-reply-${item.id}`} maxLength={2000} onChange={(event) => setText(event.target.value)} value={text} /><Button disabled={pending || !text.trim()} type="submit">Lưu</Button><Button onClick={() => { setText(item.body); setEditing(false); }} type="button" variant="ghost">Hủy</Button></form> : <p>{item.body}</p>}{currentUserId === item.userId ? <div className="comment-actions"><Button disabled={pending} onClick={() => setEditing(true)} variant="ghost">Sửa phản hồi</Button><Button disabled={pending} onClick={() => remove.mutate()} variant="ghost">Xóa phản hồi</Button></div> : null}{edit.isError || remove.isError ? <p className="field-error" role="alert">Không thể cập nhật phản hồi.</p> : null}</li>;
}
