import { QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useEffect, useState } from "react";
import { ThemeProvider } from "@/app/theme/ThemeProvider";
import { profileQueryKey } from "@/features/profile/profile-api";
import { subscribeToApiUnauthorized } from "@/shared/api/client";
import { createQueryClient } from "@/shared/api/query-client";

interface AppProvidersProps {
  children: ReactNode;
}

export function AppProviders({ children }: AppProvidersProps) {
  const [queryClient] = useState(createQueryClient);

  useEffect(
    () => subscribeToApiUnauthorized(() => {
      queryClient.setQueryData(profileQueryKey, null);
    }),
    [queryClient],
  );

  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider>{children}</ThemeProvider>
    </QueryClientProvider>
  );
}
