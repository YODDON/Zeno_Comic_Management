import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { getAdminComments, moderateComment, type AdminCommentItem } from "@/features/comments/comments-api";
import { Link } from "react-router-dom";
import { useProfileQuery } from "@/features/profile/use-profile";
import { ApiErrorState, EmptyState, LoadingState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

const statuses = ["", "Visible", "Hidden", "Deleted"] as const;

export function AdminCommentsPage() {
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const profile = useProfileQuery();
  const comments = useQuery({ queryKey: ["admin", "comments", status, page], queryFn: () => getAdminComments(status, page) });

  return <div className="admin-page"><div className="admin-page-heading"><p className="eyebrow">Kiểm duyệt</p><h1>Bình luận</h1><p>Ẩn, xóa hoặc phục hồi bình luận ẩn. Bình luận đã xóa không thể phục hồi.</p></div><div className="admin-comment-toolbar"><label htmlFor="comment-status">Trạng thái</label><select id="comment-status" onChange={(event) => { setStatus(event.target.value); setPage(1); }} value={status}>{statuses.map((value) => <option key={value || "all"} value={value}>{value || "Tất cả"}</option>)}</select></div>{comments.isPending ? <LoadingState title="Đang tải bình luận" /> : null}{comments.isError ? <ApiErrorState error={comments.error} onRetry={() => void comments.refetch()} /> : null}{comments.data?.items.length === 0 ? <EmptyState description="Không có bình luận phù hợp bộ lọc." title="Danh sách trống" /> : null}<ol className="admin-comment-list">{comments.data?.items.map((comment) => <AdminCommentCard canMutate={profile.data?.role === "Admin"} comment={comment} key={comment.id} />)}</ol>{comments.data && comments.data.totalPages > 1 ? <nav aria-label="Phân trang kiểm duyệt bình luận" className="comment-pagination"><Button disabled={page === 1} onClick={() => setPage((value) => value - 1)} variant="outline">Trang trước</Button><span>Trang {page}/{comments.data.totalPages}</span><Button disabled={page >= comments.data.totalPages} onClick={() => setPage((value) => value + 1)} variant="outline">Trang sau</Button></nav> : null}</div>;
}

function AdminCommentCard({ comment, canMutate }: { comment: AdminCommentItem; canMutate: boolean }) {
  const [action, setAction] = useState<"hide" | "delete" | "restore" | null>(null);
  const [reason, setReason] = useState("");
  const client = useQueryClient();
  const mutation = useMutation({ mutationFn: () => moderateComment(comment.id, action!, comment.version, reason.trim()), onSuccess: async () => { setAction(null); setReason(""); await client.invalidateQueries({ queryKey: ["admin", "comments"] }); } });
  const buttons: Array<{ action: "hide" | "delete" | "restore"; label: string }> = comment.status === "Visible" ? [{ action: "hide", label: "Ẩn" }, { action: "delete", label: "Xóa" }] : comment.status === "Hidden" ? [{ action: "restore", label: "Phục hồi" }, { action: "delete", label: "Xóa" }] : [];
  const targetHref = comment.comicId ? `/comics/${comment.targetSlug}` : `/chapters/${comment.chapterId}/read`;
  return <li className="admin-comment-card"><header><strong>{comment.displayName}</strong><span>{comment.status}</span></header><p>{comment.body}</p>{comment.parentBody ? <blockquote><strong>Phản hồi {comment.parentAuthorDisplayName}</strong><p>{comment.parentBody}</p></blockquote> : null}<small><Link to={targetHref}>{comment.targetTitle}</Link> · <time dateTime={comment.createdAt}>{new Date(comment.createdAt).toLocaleString("vi-VN")}</time> · v{comment.version}</small><div className="comment-actions">{buttons.map((button) => <Button disabled={!canMutate || mutation.isPending} key={button.action} onClick={() => setAction(button.action)} variant={button.action === "delete" ? "outline" : "primary"}>{button.label}</Button>)}</div>{action ? <form onSubmit={(event) => { event.preventDefault(); if (reason.trim()) mutation.mutate(); }}><label htmlFor={`reason-${comment.id}`}>Lý do {action}</label><textarea autoFocus id={`reason-${comment.id}`} maxLength={500} onChange={(event) => setReason(event.target.value)} required value={reason} /><Button disabled={!reason.trim() || mutation.isPending} type="submit">{mutation.isPending ? "Đang lưu…" : "Xác nhận"}</Button><Button disabled={mutation.isPending} onClick={() => { setAction(null); setReason(""); }} type="button" variant="ghost">Hủy</Button></form> : null}{mutation.isError ? <p className="field-error" role="alert">Không thể cập nhật. Hãy tải lại trạng thái bình luận và thử lại.</p> : null}</li>;
}
