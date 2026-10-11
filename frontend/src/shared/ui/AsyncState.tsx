import { CircleCheck, CircleDashed, Inbox, TriangleAlert } from "lucide-react";
import { type ReactNode } from "react";
import { Button } from "@/shared/ui/Button";

interface StatePanelProps {
  title: string;
  description: string;
  action?: ReactNode;
}

export function LoadingState({ title = "Đang tải dữ liệu" }: { title?: string }) {
  return (
    <div aria-live="polite" className="state-panel" role="status">
      <CircleDashed aria-hidden="true" className="state-icon state-icon-spin" />
      <p>{title}</p>
    </div>
  );
}

export function EmptyState({ title, description, action }: StatePanelProps) {
  return (
    <div className="state-panel state-panel-large">
      <Inbox aria-hidden="true" className="state-icon" />
      <h2>{title}</h2>
      <p>{description}</p>
      {action}
    </div>
  );
}

interface ErrorStateProps extends StatePanelProps {
  onRetry?: () => void;
}

export function ErrorState({ title, description, onRetry }: ErrorStateProps) {
  return (
    <div aria-live="assertive" className="state-panel" role="alert">
      <TriangleAlert aria-hidden="true" className="state-icon state-icon-error" />
      <h2>{title}</h2>
      <p>{description}</p>
      {onRetry ? <Button onClick={onRetry}>Thử lại</Button> : null}
    </div>
  );
}

export function PendingState({ title, description }: StatePanelProps) {
  return (
    <div aria-live="polite" className="state-panel" role="status">
      <CircleDashed aria-hidden="true" className="state-icon state-icon-spin" />
      <h2>{title}</h2>
      <p>{description}</p>
    </div>
  );
}

export function SuccessState({ title, description }: StatePanelProps) {
  return (
    <div aria-live="polite" className="state-panel" role="status">
      <CircleCheck aria-hidden="true" className="state-icon state-icon-success" />
      <h2>{title}</h2>
      <p>{description}</p>
    </div>
  );
}
