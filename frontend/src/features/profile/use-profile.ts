import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getProfile,
  logout,
  profileQueryKey,
  type Profile,
  updateDisplayName,
} from "@/features/profile/profile-api";

export function useProfileQuery() {
  return useQuery<Profile | null>({
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

export function useLogout() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: logout,
    onSuccess: () => {
      queryClient.clear();
    },
  });
}
