import { EmptyState } from "@/shared/ui/AsyncState";

interface RoutePlaceholderPageProps {
  eyebrow: string;
  title: string;
  description: string;
}

export function RoutePlaceholderPage({
  eyebrow,
  title,
  description,
}: RoutePlaceholderPageProps) {
  return (
    <div className="shell-container page-section">
      <p className="eyebrow">{eyebrow}</p>
      <EmptyState description={description} title={title} />
    </div>
  );
}
