# Design System

User UI supports Dark and Light toggle (Dark proposed initial default); Admin UI always Light. React/TypeScript/Tailwind/shadcn/ui, semantic CSS design tokens for background/surface/text/border/accent/status. Responsive mobile-first and accessible. Use shared UI primitives with feature-owned composites; no hardcoded payment outcome state.

Typography clear hierarchy; covers 3:4 aspect ratio where possible, images preserve intrinsic aspect ratio in Reader; lazy loading with stable placeholders. Use accessible contrast, keyboard controls, touch targets, loading skeletons, error retry and announcements, unsaved change guard on Admin editors. Admin financial actions require explicit confirmation and visible authoritative backend state.
