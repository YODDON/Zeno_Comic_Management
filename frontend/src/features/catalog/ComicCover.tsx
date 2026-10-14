import { BookOpen } from "lucide-react";

export function ComicCover({ title }: { title: string }) {
  return (
    <div aria-hidden="true" className="comic-cover comic-cover-placeholder">
      <span>{title.slice(0, 1).toLocaleUpperCase("vi")}</span>
      <BookOpen />
    </div>
  );
}
