import { cva, type VariantProps } from "class-variance-authority";
import {
  cloneElement,
  type ButtonHTMLAttributes,
  type ReactElement,
  type ReactNode,
} from "react";
import { cn } from "@/shared/lib/cn";

const buttonVariants = cva(
  "inline-flex min-h-11 items-center justify-center gap-2 rounded-full px-5 text-sm font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus)] focus-visible:ring-offset-2 focus-visible:ring-offset-[var(--background)] disabled:pointer-events-none disabled:opacity-50 [&_svg]:size-4",
  {
    variants: {
      variant: {
        primary: "bg-[var(--accent)] text-white hover:bg-[var(--accent-strong)]",
        outline:
          "border border-[var(--border-strong)] bg-transparent text-[var(--text)] hover:bg-[var(--surface-raised)]",
        ghost: "text-[var(--text)] hover:bg-[var(--surface-raised)]",
      },
      size: {
        default: "min-h-11",
        lg: "min-h-12 px-6 text-base",
        icon: "size-11 shrink-0 p-0",
      },
    },
    defaultVariants: {
      variant: "primary",
      size: "default",
    },
  },
);

interface ButtonProps
  extends ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  asChild?: "link";
  children: ReactNode;
}

export function Button({
  asChild,
  className,
  size,
  variant,
  children,
  ...props
}: ButtonProps) {
  const classes = cn(buttonVariants({ size, variant }), className);

  if (asChild === "link") {
    const child = children as ReactElement<{ className?: string }>;
    return cloneElement(child, {
      className: cn(classes, child.props.className),
    });
  }

  return (
    <button className={classes} type="button" {...props}>
      {children}
    </button>
  );
}
