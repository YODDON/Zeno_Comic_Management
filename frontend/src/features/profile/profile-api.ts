import { z } from "zod";
import { apiRequest } from "@/shared/api/client";

export const displayNameSchema = z
  .string()
  .trim()
  .min(1, "Vui lòng nhập tên hiển thị.")
  .refine(
    (value) => Array.from(value).length <= 120,
    "Tên hiển thị không được vượt quá 120 ký tự.",
  );

const profileSchema = z.object({
  id: z.string().uuid(),
  displayName: z.string(),
  email: z.string().email().nullable(),
  avatarUrl: z.string().url().nullable(),
  role: z.enum(["User", "Admin"]),
  status: z.enum(["Active", "Disabled"]),
});

export type Profile = z.infer<typeof profileSchema>;

export const profileQueryKey = ["identity", "me"] as const;

export async function getProfile(): Promise<Profile> {
  return profileSchema.parse(await apiRequest<unknown>("/api/v1/me"));
}

export async function updateDisplayName(displayName: string): Promise<Profile> {
  const normalizedDisplayName = displayNameSchema.parse(displayName);
  return profileSchema.parse(
    await apiRequest<unknown>("/api/v1/me", {
      method: "PATCH",
      body: { displayName: normalizedDisplayName },
    }),
  );
}
