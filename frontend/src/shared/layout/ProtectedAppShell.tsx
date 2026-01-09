import { useEffect, useMemo, useState } from "react";
import { Navigate, Outlet, useLocation, useNavigate } from "react-router-dom";
import { Menu, X } from "lucide-react";
import {
  useAuthStore,
  type AuthUser,
} from "../../features/auth/stores/authStore";
import { updateProfile } from "../../features/auth/api";
import { UserProfileUpdateDialog } from "../../features/requestor/components/UserProfileUpdateDialog";
import { AppNavigation } from "../components/AppNavigation";
import scalerize from "../../assets/logo-blue.png";

const capitalize = (value: string) =>
  value.length === 0 ? "" : value[0].toUpperCase() + value.slice(1);

const deriveNameParts = (user: AuthUser | null) => {
  if (user?.firstName && user?.lastName) {
    return { firstName: user.firstName, lastName: user.lastName };
  }

  if (!user?.email) {
    return { firstName: "", lastName: "" };
  }

  const emailName = user.email.split("@")[0] ?? "";
  const segments = emailName.split(/[.\-_]/).filter(Boolean);

  if (segments.length >= 2) {
    return {
      firstName: capitalize(segments[0]),
      lastName: capitalize(segments[1]),
    };
  }

  if (segments.length === 1) {
    return { firstName: capitalize(segments[0]), lastName: "" };
  }

  if (emailName.length >= 2) {
    return {
      firstName: capitalize(emailName.slice(0, emailName.length - 1)),
      lastName: capitalize(emailName.slice(-1)),
    };
  }

  return { firstName: "", lastName: "" };
};

const computeInitials = (
  firstName: string,
  lastName: string,
  fallback: string
) => {
  const letters = [firstName, lastName]
    .map((value) => value.trim().charAt(0).toUpperCase())
    .filter(Boolean);

  if (letters.length === 0) {
    return fallback;
  }

  if (letters.length === 1) {
    const solo = letters[0];
    return `${solo}${fallback.charAt(1) || solo}`;
  }

  return `${letters[0]}${letters[1]}`;
};

const resolveUserPresentation = (user: AuthUser | null) => {
  if (!user) {
    return {
      initials: "IG",
      label: "Guest",
      badge: "Unauthenticated",
    };
  }

  const fallbackId = user.id?.slice(0, 4)?.toUpperCase() ?? "IG";
  
  let label = user.email ?? `User ${fallbackId}`;

  if (user.firstName && user.lastName) {
    label = `${user.firstName} ${user.lastName}`;
  } else if (user.userName) {
    label = user.userName;
  }

  const badge =
    user.role === "Provider"
      ? "Provider account"
      : user.role === "Admin"
      ? "Administrator"
      : "";

  const initials = (() => {
    if (user.firstName && user.lastName) {
      return (user.firstName[0] + user.lastName[0]).toUpperCase();
    }

    if (user.email) {
      const emailName = user.email.split("@")[0];
      const parts = emailName.split(/[.\-_]/).filter(Boolean);
      if (parts.length >= 2) {
        return (parts[0][0] + parts[1][0]).toUpperCase();
      }
      if (emailName.length >= 2) {
        return emailName.slice(0, 2).toUpperCase();
      }
      if (emailName.length === 1) {
        return `${emailName[0].toUpperCase()}${emailName[0].toUpperCase()}`;
      }
    }

    return fallbackId.slice(0, 2).padEnd(2, "X");
  })();

  return { initials, label, badge };
};

export const ProtectedAppShell = () => {
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const [profileDialogOpen, setProfileDialogOpen] = useState(false);
  const [customName, setCustomName] = useState<{
    firstName: string;
    lastName: string;
  } | null>(null);
  const navigate = useNavigate();
  const location = useLocation();
  const user = useAuthStore((state) => state.user);
  const setAuth = useAuthStore((state) => state.setAuth);
  const token = useAuthStore((state) => state.token);
  const clearAuth = useAuthStore((state) => state.clearAuth);

  const derivedNameParts = useMemo(() => deriveNameParts(user), [user]);

  useEffect(() => {
    setCustomName(null);
  }, [user?.id]);

  const currentFirstName = customName?.firstName ?? derivedNameParts.firstName;
  const currentLastName = customName?.lastName ?? derivedNameParts.lastName;

  const userPresentation = useMemo(() => {
    const base = resolveUserPresentation(user);
    // If we have a custom name (optimistic update), use it for display
    const displayName = customName 
      ? `${customName.firstName} ${customName.lastName}`.trim() 
      : base.label;

    return {
      ...base,
      label: displayName !== "" ? displayName : base.label,
      initials: computeInitials(
        currentFirstName,
        currentLastName,
        base.initials
      ),
    };
  }, [currentFirstName, currentLastName, user, customName]);

  if (!user) {
    return <Navigate to="/auth/login" state={{ from: location }} replace />;
  }

  const handleSignOut = () => {
    clearAuth();
    navigate("/auth/login", { replace: true });
  };

  return (
    <div className="flex h-screen flex-col bg-slate-100 md:flex-row dark:bg-slate-950">
      <header className="relative flex items-start justify-between bg-white shadow md:hidden dark:bg-slate-900 dark:shadow-slate-800">
        <div className="flex items-center gap-2 px-4 py-3 ">
          <img src={scalerize} alt="logo" className="h-10 w-10" />
          <button
            type="button"
            onClick={() => setMobileNavOpen((prev) => !prev)}
            className="inline-flex rounded-md items-center justify-center p-2 text-slate-600 transition hover:bg-slate-50 hover:text-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 dark:hover:text-white"
            aria-label="Toggle navigation menu"
          >
            {mobileNavOpen ? (
              <X className="h-5 w-5" />
            ) : (
              <Menu className="h-5 w-5" />
            )}
          </button>
          <span className="text-lg font-semibold text-slate-900 dark:text-white">
            InfiniteGPU
          </span>
        </div>
      </header>

      <AppNavigation
        mobileNavOpen={mobileNavOpen}
        onCloseMobileNav={() => setMobileNavOpen(false)}
        userPresentation={userPresentation}
        onRequestProfileUpdate={() => setProfileDialogOpen(true)}
        onSignOut={handleSignOut}
      />

      <main className="flex-1 overflow-hidden p-6 md:p-8">
        <Outlet />
      </main>

      <UserProfileUpdateDialog
        open={profileDialogOpen}
        onDismiss={() => setProfileDialogOpen(false)}
        initialFirstName={currentFirstName}
        initialLastName={currentLastName}
        onSubmit={async ({ firstName, lastName }) => {
          try {
            const updatedUser = await updateProfile({ firstName, lastName });
            setAuth({
              ...user,
              firstName: updatedUser.firstName,
              lastName: updatedUser.lastName
            }, token);
            setCustomName({ firstName, lastName });
          } catch (error) {
            console.error("Failed to update profile", error);
          }
        }}
      />
    </div>
  );
};
