import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getProfile,
  profileQueryKey,
  updateDisplayName,
} from "@/features/profile/profile-api";

export function useProfileQuery() {
  return useQuery({
    queryKey: profileQueryKey,
    queryFn: getProfile,
  });
}

export function useUpdateDisplayName() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: updateDisplayName,
    onSuccess: (profile) => {
      queryClient.setQueryData(profileQueryKey, profile);
    },
  });
}
