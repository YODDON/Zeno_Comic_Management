import { ArrowRight, BookMarked, Sparkles } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "@/shared/ui/Button";

export function HomePage() {
  return (
    <div className="home-page shell-container">
      <section className="hero-section" aria-labelledby="home-heading">
        <div className="hero-copy">
          <p className="eyebrow">
            <Sparkles aria-hidden="true" /> Khung đọc truyện tiếng Việt
          </p>
          <h1 id="home-heading">Đọc theo nhịp của bạn.</h1>
          <p className="hero-description">
            Một kệ truyện gọn, rõ và yên tĩnh để bạn quay lại đúng trang đang dở.
          </p>
          <Button asChild="link" size="lg">
            <Link to="/comics">
              Khám phá kho truyện <ArrowRight aria-hidden="true" />
            </Link>
          </Button>
        </div>

        <div aria-label="Minh họa nhịp đọc" className="reading-card">
          <div className="registration-corners" aria-hidden="true" />
          <p className="reading-card-kicker">Trang đang chờ bạn</p>
          <div className="reading-card-art" aria-hidden="true">
            <span />
            <span />
            <span />
          </div>
          <div className="reading-card-meta">
            <BookMarked aria-hidden="true" />
            <span>Tiến độ được lưu sau khi bạn đăng nhập.</span>
          </div>
        </div>
      </section>

      <section className="foundation-strip" aria-label="Nền tảng giao diện">
        <p>Giao diện sáng / tối</p>
        <p>Đọc tốt trên mọi màn hình</p>
        <p>Trạng thái rõ ràng, không phỏng đoán thanh toán</p>
      </section>
    </div>
  );
}
