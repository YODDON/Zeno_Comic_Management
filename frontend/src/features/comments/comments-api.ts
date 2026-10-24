import { z } from "zod";
import { apiRequest } from "@/shared/api/client";

export const commentSchema = z.object({
  id: z.string().uuid(), userId: z.string().uuid(), displayName: z.string(), avatarUrl: z.string().nullable(),
  comicId: z.string().uuid().nullable(), chapterId: z.string().uuid().nullable(), parentId: z.string().uuid().nullable(),
  body: z.string(), status: z.enum(["Visible", "Hidden", "Deleted"]), createdAt: z.iso.datetime({ offset: true }),
  updatedAt: z.iso.datetime({ offset: true }), version: z.number().int().positive(), replyCount: z.number().int().nonnegative(),
});
export const commentPageSchema = z.object({ items: z.array(commentSchema), page: z.number().int().positive(), pageSize: z.literal(20), totalItems: z.number().int().nonnegative(), totalPages: z.number().int().nonnegative() });
export const adminCommentSchema = commentSchema.extend({
  targetTitle: z.string().min(1), targetSlug: z.string().nullable(),
  parentBody: z.string().nullable(), parentAuthorDisplayName: z.string().nullable(),
});
export const adminCommentPageSchema = commentPageSchema.extend({ items: z.array(adminCommentSchema) });
export type CommentItem = z.infer<typeof commentSchema>;
export type AdminCommentItem = z.infer<typeof adminCommentSchema>;
export type CommentPage = z.infer<typeof commentPageSchema>;
export type CommentTarget = { comicId: string; chapterId?: never } | { comicId?: never; chapterId: string };

function targetPath(target: CommentTarget) { return "comicId" in target ? `/api/v1/comics/${target.comicId}/comments` : `/api/v1/chapters/${target.chapterId}/comments`; }
export async function getComments(target: CommentTarget, page: number) { return commentPageSchema.parse(await apiRequest<unknown>(`${targetPath(target)}?page=${page}`)); }
export async function getReplies(parentId: string, page = 1) { return commentPageSchema.parse(await apiRequest<unknown>(`/api/v1/comments/${parentId}/replies?page=${page}`)); }
export async function createComment(target: CommentTarget, body: string, parentId?: string) { return commentSchema.parse(await apiRequest<unknown>("/api/v1/comments", { method: "POST", body: { ...target, parentId, body } })); }
export async function editComment(id: string, body: string, expectedVersion: number) { return commentSchema.parse(await apiRequest<unknown>(`/api/v1/comments/${id}`, { method: "PATCH", body: { body, expectedVersion } })); }
export async function deleteComment(id: string, expectedVersion: number) { return commentSchema.parse(await apiRequest<unknown>(`/api/v1/comments/${id}`, { method: "DELETE", body: { expectedVersion } })); }
export async function getAdminComments(status: string, page: number) { const query = new URLSearchParams({ page: String(page) }); if (status) query.set("status", status); return adminCommentPageSchema.parse(await apiRequest<unknown>(`/api/v1/admin/comments?${query}`)); }
export async function moderateComment(id: string, action: "hide" | "delete" | "restore", expectedVersion: number, reason: string) { return commentSchema.parse(await apiRequest<unknown>(`/api/v1/admin/comments/${id}/${action}`, { method: "POST", body: { expectedVersion, reason } })); }
