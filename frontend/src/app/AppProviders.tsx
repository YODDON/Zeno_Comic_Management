import { QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { ThemeProvider } from "@/app/theme/ThemeProvider";
import { createQueryClient } from "@/shared/api/query-client";

interface AppProvidersProps {
  children: ReactNode;
}

export function AppProviders({ children }: AppProvidersProps) {
  const [queryClient] = useState(createQueryClient);

  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider>{children}</ThemeProvider>
    </QueryClientProvider>
  );
}
