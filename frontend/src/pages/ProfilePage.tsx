import {
  BadgeCheck,
  Crown,
  LogOut,
  Mail,
  MoonStar,
  PencilLine,
  Sun,
  UserRound,
  WalletCards,
} from "lucide-react";
import { useId, useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { ZodError } from "zod";
import { useTheme } from "@/app/theme/theme-context";
import { displayNameSchema, type Profile } from "@/features/profile/profile-api";
import {
  useLogout,
  useProfileQuery,
  useUpdateDisplayName,
} from "@/features/profile/use-profile";
import { ApiError } from "@/shared/api/problem-details";
import { ApiErrorState, LoadingState, UnauthorizedState } from "@/shared/ui/AsyncState";
import { Button } from "@/shared/ui/Button";

export function ProfilePage() {
  const profileQuery = useProfileQuery();

  if (profileQuery.isPending) {
    return (
      <div className="shell-container page-section">
        <LoadingState title="Đang tải hồ sơ" />
      </div>
    );
  }

  if (profileQuery.isError) {
    return (
      <div className="shell-container page-section">
        <ApiErrorState
          error={profileQuery.error}
          onRetry={() => void profileQuery.refetch()}
          unauthorizedAction={
            <Button asChild="link">
              <a href="/api/v1/auth/google/login?returnUrl=%2Fprofile">
                Đăng nhập với Google
              </a>
            </Button>
          }
        />
      </div>
    );
  }

  if (!profileQuery.data) {
    return (
      <div className="shell-container page-section">
        <UnauthorizedState
          action={
            <Button asChild="link">
              <a href="/api/v1/auth/google/login?returnUrl=%2Fprofile">
                Đăng nhập lại với Google
              </a>
            </Button>
          }
        />
      </div>
    );
  }

  return <ProfileDetails profile={profileQuery.data} />;
}

function ProfileDetails({ profile }: { profile: Profile }) {
  const inputId = useId();
  const navigate = useNavigate();
  const { setTheme, theme } = useTheme();
  const logoutMutation = useLogout();
  const updateMutation = useUpdateDisplayName();
  const [displayName, setDisplayName] = useState(profile.displayName);
  const [validationError, setValidationError] = useState<string>();
  const [savedMessage, setSavedMessage] = useState<string>();

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSavedMessage(undefined);

    const parsed = displayNameSchema.safeParse(displayName);
    if (!parsed.success) {
      setValidationError(parsed.error.issues[0]?.message);
      return;
    }

    setValidationError(undefined);
    try {
      const updated = await updateMutation.mutateAsync(parsed.data);
      setDisplayName(updated.displayName);
      setSavedMessage("Tên hiển thị đã được cập nhật.");
    } catch (error) {
      if (error instanceof ZodError) {
        setValidationError("Phản hồi từ máy chủ không hợp lệ. Vui lòng thử lại.");
      }
    }
  }

  async function handleLogout() {
    try {
      await logoutMutation.mutateAsync();
      await navigate("/", { replace: true });
    } catch {
      // The mutation error is presented next to the action below.
    }
  }

  const serverError = updateMutation.error;
  if (serverError instanceof ApiError && (serverError.status === 401 || serverError.status === 403)) {
    return (
      <div className="shell-container page-section">
        <ApiErrorState
          error={serverError}
          unauthorizedAction={
            <Button asChild="link">
              <a href="/api/v1/auth/google/login?returnUrl=%2Fprofile">
                Đăng nhập lại với Google
              </a>
            </Button>
          }
        />
      </div>
    );
  }

  const serverErrorMessage = getUpdateErrorMessage(serverError);
  const normalizedValue = displayName.trim();
  const isUnchanged = normalizedValue === profile.displayName;

  return (
    <section className="profile-page shell-container page-section" aria-labelledby="profile-title">
      <header className="profile-heading">
        <p className="eyebrow">
          <PencilLine aria-hidden="true" /> Dấu tên người đọc
        </p>
        <h1 id="profile-title">Hồ sơ của bạn</h1>
        <p>
          Chọn tên sẽ xuất hiện trong trải nghiệm đọc. Thông tin Google bên dưới chỉ để đối chiếu
          và không thể chỉnh sửa tại đây.
        </p>
      </header>

      <div className="profile-card">
        <div className="registration-corners" aria-hidden="true" />
        <div className="profile-identity">
          {profile.avatarUrl ? (
            <img alt="" className="profile-avatar" src={profile.avatarUrl} />
          ) : (
            <span className="profile-avatar profile-avatar-fallback" aria-hidden="true">
              <UserRound />
            </span>
          )}
          <div>
            <p className="profile-registration">READER / {profile.id.slice(0, 8).toUpperCase()}</p>
            <strong>{profile.displayName}</strong>
            <span className="profile-status">
              <BadgeCheck aria-hidden="true" /> Đang hoạt động
            </span>
          </div>
        </div>

        <form className="profile-form" noValidate onSubmit={(event) => void handleSubmit(event)}>
          <div className="form-field">
            <label htmlFor={inputId}>Tên hiển thị</label>
            <input
              aria-describedby={`${inputId}-help ${inputId}-error`}
              aria-invalid={Boolean(validationError || serverErrorMessage)}
              autoComplete="nickname"
              id={inputId}
              onChange={(event) => {
                setDisplayName(event.target.value);
                setValidationError(undefined);
                setSavedMessage(undefined);
                updateMutation.reset();
              }}
              value={displayName}
            />
            <span className="field-help" id={`${inputId}-help`}>
              Tối đa 120 ký tự. Khoảng trắng ở đầu và cuối sẽ được loại bỏ.
            </span>
            <span className="field-error" id={`${inputId}-error`} role="alert">
              {validationError ?? serverErrorMessage}
            </span>
          </div>

          <div className="readonly-field" aria-label="Email Google">
            <Mail aria-hidden="true" />
            <span>
              <small>Email Google · chỉ đọc</small>
              <strong>{profile.email ?? "Google không cung cấp email"}</strong>
            </span>
          </div>

          <div className="profile-form-footer">
            <span aria-live="polite" className="save-message" role="status">
              {savedMessage}
            </span>
            <Button
              disabled={updateMutation.isPending || isUnchanged}
              type="submit"
            >
              {updateMutation.isPending ? "Đang lưu…" : "Lưu tên hiển thị"}
            </Button>
          </div>
        </form>

        <div className="profile-supporting-sections">
          <section aria-labelledby="profile-links-title" className="profile-supporting-section">
            <div>
              <p className="profile-section-kicker">Tài khoản</p>
              <h2 id="profile-links-title">Truy cập nhanh</h2>
            </div>
            <div className="profile-action-links">
              <Button asChild="link" variant="outline">
                <Link to="/wallet"><WalletCards aria-hidden="true" /> Xem ví</Link>
              </Button>
              <Button asChild="link" variant="outline">
                <Link to="/vip"><Crown aria-hidden="true" /> Quyền lợi VIP</Link>
              </Button>
            </div>
          </section>

          <section aria-labelledby="profile-theme-title" className="profile-supporting-section">
            <div>
              <p className="profile-section-kicker">Tùy chọn trình duyệt</p>
              <h2 id="profile-theme-title">Giao diện đọc</h2>
            </div>
            <div aria-label="Chọn giao diện" className="profile-theme-options" role="group">
              <Button
                aria-pressed={theme === "light"}
                onClick={() => setTheme("light")}
                variant={theme === "light" ? "primary" : "outline"}
              >
                <Sun aria-hidden="true" /> Sáng
              </Button>
              <Button
                aria-pressed={theme === "dark"}
                onClick={() => setTheme("dark")}
                variant={theme === "dark" ? "primary" : "outline"}
              >
                <MoonStar aria-hidden="true" /> Tối
              </Button>
            </div>
          </section>

          <section aria-labelledby="profile-session-title" className="profile-supporting-section">
            <div>
              <p className="profile-section-kicker">Phiên đăng nhập</p>
              <h2 id="profile-session-title">Đăng xuất khỏi các thiết bị</h2>
              <p className="profile-section-description">
                Đăng xuất sẽ thu hồi tất cả bản sao phiên hiện tại của tài khoản này.
              </p>
            </div>
            <Button
              className="profile-logout-button"
              disabled={logoutMutation.isPending}
              onClick={() => void handleLogout()}
              variant="outline"
            >
              <LogOut aria-hidden="true" />
              {logoutMutation.isPending ? "Đang đăng xuất…" : "Đăng xuất"}
            </Button>
            {logoutMutation.isError ? (
              <p aria-live="assertive" className="field-error" role="alert">
                Không thể đăng xuất. Vui lòng thử lại.
              </p>
            ) : null}
          </section>
        </div>
      </div>

    </section>
  );
}

function getUpdateErrorMessage(error: unknown): string | undefined {
  if (error instanceof ApiError) {
    if (error.status === 400) {
      return "Tên hiển thị chưa hợp lệ. Hãy kiểm tra lại nội dung đã nhập.";
    }

    if (error.status === 409) {
      return "Hồ sơ vừa được cập nhật ở nơi khác. Hãy tải lại trang rồi thử lại.";
    }
  }

  return error ? "Không thể cập nhật hồ sơ. Vui lòng thử lại." : undefined;
}
