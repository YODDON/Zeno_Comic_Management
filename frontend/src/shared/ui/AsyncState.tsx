import {
  CircleCheck,
  CircleDashed,
  Inbox,
  LogIn,
  ShieldX,
  TriangleAlert,
} from "lucide-react";
import { type ReactNode } from "react";
import { ApiError } from "@/shared/api/problem-details";
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

interface AccessStateProps {
  action?: ReactNode;
  description?: string;
  title?: string;
}

export function UnauthorizedState({
  action,
  description = "Vui lòng đăng nhập để tiếp tục.",
  title = "Bạn chưa đăng nhập",
}: AccessStateProps) {
  return (
    <div aria-live="assertive" className="state-panel state-panel-large" role="alert">
      <LogIn aria-hidden="true" className="state-icon state-icon-error" />
      <h2>{title}</h2>
      <p>{description}</p>
      {action}
    </div>
  );
}

export function ForbiddenState({
  action,
  description = "Tài khoản của bạn không có quyền truy cập nội dung này.",
  title = "Không có quyền truy cập",
}: AccessStateProps) {
  return (
    <div aria-live="assertive" className="state-panel state-panel-large" role="alert">
      <ShieldX aria-hidden="true" className="state-icon state-icon-error" />
      <h2>{title}</h2>
      <p>{description}</p>
      {action}
    </div>
  );
}

interface ApiErrorStateProps {
  error: unknown;
  forbiddenAction?: ReactNode;
  onRetry?: () => void;
  unauthorizedAction?: ReactNode;
}

export function ApiErrorState({
  error,
  forbiddenAction,
  onRetry,
  unauthorizedAction,
}: ApiErrorStateProps) {
  if (error instanceof ApiError && error.status === 401) {
    return <UnauthorizedState action={unauthorizedAction} />;
  }

  if (error instanceof ApiError && error.status === 403) {
    return <ForbiddenState action={forbiddenAction} />;
  }

  return (
    <ErrorState
      description="Vui lòng thử lại. Nếu lỗi tiếp diễn, hãy liên hệ hỗ trợ."
      onRetry={onRetry}
      title={error instanceof ApiError ? error.message : "Không tải được dữ liệu"}
    />
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
