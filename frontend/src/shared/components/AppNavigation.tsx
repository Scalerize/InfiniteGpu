import { NavLink } from "react-router-dom";
import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import {
  LayoutDashboard,
  ClipboardList,
  Send,
  Wallet,
  ChevronDown,
  LogOut,
  UserRoundPen,
  Palette,
  Check,
  MessageSquare,
} from "lucide-react";
import scalerize from "../../assets/logo-blue.png";
import { useThemeStore, type ThemeMode } from "../stores/themeStore";

type NavItem = {
  to: string;
  label: string;
  Icon: typeof LayoutDashboard;
};

const NAV_ITEMS: NavItem[] = [
  { to: "/", label: "Dashboard", Icon: LayoutDashboard },
  { to: "/tasks", label: "Provider Tasks", Icon: ClipboardList },
  { to: "/requests", label: "Requests", Icon: Send },
  { to: "/finance", label: "Payments & Earnings", Icon: Wallet },
];

export type AppNavigationProps = {
  mobileNavOpen: boolean;
  onCloseMobileNav: () => void;
  userPresentation: {
    initials: string;
    label: string;
    badge: string;
  };
  onRequestProfileUpdate: () => void;
  onSignOut: () => void;
};

export const AppNavigation = ({
  mobileNavOpen,
  onCloseMobileNav,
  userPresentation,
  onRequestProfileUpdate,
  onSignOut,
}: AppNavigationProps) => {
  const { mode, setMode } = useThemeStore();

  const themeOptions: { value: ThemeMode; label: string }[] = [
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
    { value: 'system', label: 'System' },
  ];

  return (
  <nav
    className={`bg-white shadow-lg md:flex md:h-auto md:w-64 md:flex-col dark:bg-slate-900 ${
      mobileNavOpen ? "flex flex-col" : "hidden md:flex"
    }`}
  >
    <div className="flex h-full flex-col">
      <div className="border-b border-slate-200 px-6 md:py-5 dark:border-slate-700">
        <div className="items-center gap-3 hidden md:flex">
          <img src={scalerize} alt="logo" className="h-12 w-12 bg-cover"/>
          <div>
            <h1 className="text-2xl font-semibold text-slate-900 dark:text-white">
              InfiniteGPU
            </h1>
            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">Distributed compute</p>
          </div>
        </div>
      </div>

      <ul className="space-y-1 px-3 pb-6 pt-4 md:mt-6">
        {NAV_ITEMS.map(({ to, label, Icon }) => (
          <li key={to}>
            <NavLink
              to={to}
              end={to === "/"}
              onClick={onCloseMobileNav}
              className={({ isActive }) =>
                `flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors duration-150 ${
                  isActive
                    ? "bg-slate-300 dark:text-white hover:bg-slate-300 dark:bg-slate-700 dark:hover:bg-slate-700"
                    : "text-slate-600 hover:bg-slate-100 hover:text-slate-900 dark:text-slate-300 dark:hover:bg-slate-800 dark:hover:text-white"
                }`
              }
            >
              <Icon className="h-5 w-5" />
              {label}
            </NavLink>
          </li>
        ))}
      </ul>

      <div className="mt-auto border-t border-slate-200 p-3 dark:border-slate-700">
        <DropdownMenu.Root>
          <DropdownMenu.Trigger asChild>
            <button
              type="button"
              className="flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left text-slate-600 transition hover:bg-slate-100 focus:outline-none focus:ring-2 focus:ring-indigo-200 focus:ring-offset-2 dark:text-slate-300 dark:hover:bg-slate-800 dark:focus:ring-indigo-800"
            >
              <span className="flex h-9 w-9 items-center justify-center rounded-full bg-slate-200 text-sm font-semibold text-slate-600 dark:bg-slate-700 dark:text-slate-300">
                {userPresentation.initials}
              </span>
              <div className="flex flex-1 flex-col">
                <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                  {userPresentation.label}
                </span>
                <span className="text-xs text-slate-500 dark:text-slate-400">
                  {userPresentation.badge}
                </span>
              </div>
              <ChevronDown className="h-4 w-4 text-slate-400 dark:text-slate-500" />
            </button>
          </DropdownMenu.Trigger>
          <DropdownMenu.Portal>
            <DropdownMenu.Content
              className="z-50 min-w-[220px] rounded-xl border border-slate-200 bg-white p-1.5 text-sm text-slate-700 shadow-xl will-change-[transform,opacity] dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200"
              sideOffset={8}
              align="end"
            >
              <DropdownMenu.Label className="px-3 py-2 text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">
                Profile
              </DropdownMenu.Label>
              <DropdownMenu.Item
                onSelect={() => {
                  onRequestProfileUpdate();
                }}
                className="flex cursor-pointer items-center gap-2 rounded-lg px-3 py-2 outline-none transition hover:bg-indigo-50 hover:text-indigo-600 data-[highlighted]:bg-indigo-50 data-[highlighted]:text-indigo-600 dark:hover:bg-indigo-950/50 dark:data-[highlighted]:bg-indigo-950/50"
              >
                <UserRoundPen className="h-4 w-4" />
                Update profile
              </DropdownMenu.Item>
              
              <DropdownMenu.Separator className="my-1 h-px bg-slate-100 dark:bg-slate-700" />
              
              <DropdownMenu.Label className="px-3 py-2 text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">
                Appearance
              </DropdownMenu.Label>
              
              <DropdownMenu.Sub>
                <DropdownMenu.SubTrigger className="flex cursor-pointer items-center gap-2 rounded-lg px-3 py-2 outline-none transition hover:bg-indigo-50 hover:text-indigo-600 data-[highlighted]:bg-indigo-50 data-[highlighted]:text-indigo-600 data-[state=open]:bg-indigo-50 data-[state=open]:text-indigo-600 dark:hover:bg-indigo-950/50 dark:data-[highlighted]:bg-indigo-950/50 dark:data-[state=open]:bg-indigo-950/50">
                  <Palette className="h-4 w-4" />
                  <span className="flex-1">Theme</span>
                  <ChevronDown className="h-3 w-3 -rotate-90" />
                </DropdownMenu.SubTrigger>
                <DropdownMenu.Portal>
                  <DropdownMenu.SubContent
                    className="z-50 min-w-[160px] rounded-xl border border-slate-200 bg-white p-1.5 text-sm text-slate-700 shadow-xl will-change-[transform,opacity] dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200"
                    sideOffset={8}
                    alignOffset={-5}
                  >
                    {themeOptions.map((option) => (
                      <DropdownMenu.Item
                        key={option.value}
                        onSelect={() => {
                          setMode(option.value);
                        }}
                        className="flex cursor-pointer items-center gap-2 rounded-lg px-3 py-2 outline-none transition hover:bg-indigo-50 hover:text-indigo-600 data-[highlighted]:bg-indigo-50 data-[highlighted]:text-indigo-600 dark:hover:bg-indigo-950/50 dark:data-[highlighted]:bg-indigo-950/50"
                      >
                        <span className="flex h-4 w-4 items-center justify-center">
                          {mode === option.value && <Check className="h-4 w-4" />}
                        </span>
                        {option.label}
                      </DropdownMenu.Item>
                    ))}
                  </DropdownMenu.SubContent>
                </DropdownMenu.Portal>
              </DropdownMenu.Sub>
              
              <DropdownMenu.Separator className="my-1 h-px bg-slate-100 dark:bg-slate-700" />
              
              <DropdownMenu.Item
                onSelect={() => {
                  window.open("https://github.com/Scalerize/InfiniteGpu/issues", "_blank");
                }}
                className="flex cursor-pointer items-center gap-2 rounded-lg px-3 py-2 outline-none transition hover:bg-indigo-50 hover:text-indigo-600 data-[highlighted]:bg-indigo-50 data-[highlighted]:text-indigo-600 dark:hover:bg-indigo-950/50 dark:data-[highlighted]:bg-indigo-950/50"
              >
                <MessageSquare className="h-4 w-4" />
                Send feedback
              </DropdownMenu.Item>

              <DropdownMenu.Item
                onSelect={() => {
                  onSignOut();
                }}
                className="flex cursor-pointer items-center gap-2 rounded-lg px-3 py-2 font-semibold text-rose-600 outline-none transition hover:bg-rose-50 data-[highlighted]:bg-rose-50 dark:hover:bg-rose-950/30 dark:data-[highlighted]:bg-rose-950/30"
              >
                <LogOut className="h-4 w-4" />
                Disconnect
              </DropdownMenu.Item>
              <DropdownMenu.Arrow className="fill-white dark:fill-slate-800" />
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu.Root>
      </div>
    </div>
  </nav>
  );
};
