import {
  Activity,
  BookOpen,
  HardDrive,
  KeyRound,
  LayoutDashboard,
  Settings,
  type LucideIcon,
} from "lucide-react"

/** A page in the sidebar. `count` names the list whose size shows on the right (design). */
export type NavItem = { title: string; icon: LucideIcon; to: string; count?: "buckets" | "keys" }

export const navigation: NavItem[] = [
  { title: "Dashboard", icon: LayoutDashboard, to: "/" },
  { title: "Buckets", icon: HardDrive, to: "/buckets", count: "buckets" },
  { title: "Access Keys", icon: KeyRound, to: "/access-keys", count: "keys" },
  { title: "Metrics", icon: Activity, to: "/metrics" },
  { title: "Guide", icon: BookOpen, to: "/guide" },
  { title: "Settings", icon: Settings, to: "/settings" },
]
