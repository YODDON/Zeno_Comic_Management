import { hashKey, useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { profileQueryKey, type Profile } from "@/features/profile/profile-api";

function viewerIdentity(profile: Profile | null | undefined): string {
  if (profile === undefined) return "unresolved";
  return profile?.id ?? "anonymous";
}

export function PersonalCacheIsolation() {
  const queryClient = useQueryClient();

  useEffect(() => {
    const profileQueryHash = hashKey(profileQueryKey);
    let previousIdentity = viewerIdentity(
      queryClient.getQueryData<Profile | null>(profileQueryKey),
    );

    return queryClient.getQueryCache().subscribe((event) => {
      if (event.query.queryHash !== profileQueryHash) {
        return;
      }

      const nextIdentity = viewerIdentity(
        queryClient.getQueryData<Profile | null>(profileQueryKey),
      );
      if (nextIdentity === previousIdentity) return;

      previousIdentity = nextIdentity;
      queryClient.removeQueries({ queryKey: ["interaction", "rating"] });
      queryClient.removeQueries({ queryKey: ["catalog", "comic"] });
    });
  }, [queryClient]);

  return null;
}
