import { type FormEvent, useId, useMemo, useState } from 'react';
import { Sparkles } from 'lucide-react';

import { DialogShell } from '../../../shared/components/DialogShell';

interface UserProfileUpdateDialogProps {
  open: boolean;
  onDismiss: () => void;
  initialFirstName?: string;
  initialLastName?: string;
  onSubmit?: (values: { firstName: string; lastName: string }) => void;
}

export const UserProfileUpdateDialog = ({
  open,
  onDismiss,
  initialFirstName = '',
  initialLastName = '',
  onSubmit
}: UserProfileUpdateDialogProps) => {
  const firstNameId = useId();
  const lastNameId = useId();
  const helperId = useId();

  const [firstName, setFirstName] = useState(initialFirstName);
  const [lastName, setLastName] = useState(initialLastName);

  const displayName = useMemo(() => {
    const parts = [firstName.trim(), lastName.trim()].filter(Boolean);
    return parts.length > 0 ? parts.join(' ') : 'Your display name';
  }, [firstName, lastName]);

  const initials = useMemo(() => {
    const letters = [firstName, lastName]
      .map((value) => value.trim().charAt(0)?.toUpperCase() ?? '')
      .join('');
    return letters !== '' ? letters : 'YY';
  }, [firstName, lastName]);

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    onSubmit?.({ firstName: firstName.trim(), lastName: lastName.trim() });
    onDismiss();
  };

  return (
    <DialogShell
      open={open}
      onDismiss={onDismiss}
      closeLabel="Close profile update dialog"
      badgeIcon={<Sparkles className="h-3.5 w-3.5" />}
      badgeLabel="Profile"
      title="Update your profile identity"
      helperText="Refine how collaborators see you across task timelines, real-time chat, and audit trails."
      helperId={helperId}
    >
      <form onSubmit={handleSubmit} className="space-y-6">
        <section className="space-y-6">
          <div className="grid gap-6 md:grid-cols-2">
            <div className="space-y-2">
              <label htmlFor={firstNameId} className="text-sm font-semibold text-slate-700 dark:text-slate-300">
                First name
              </label>
              <input
                id={firstNameId}
                name="firstName"
                type="text"
                value={firstName}
                onChange={(event) => setFirstName(event.target.value)}
                placeholder="e.g. Ada"
                className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm transition placeholder:text-slate-400 focus:border-indigo-300 focus:outline-none focus:ring focus:ring-indigo-200/60 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:placeholder:text-slate-500 dark:focus:border-indigo-600 dark:focus:ring-indigo-900/60"
                aria-describedby={helperId}
              />
            </div>

            <div className="space-y-2">
              <label htmlFor={lastNameId} className="text-sm font-semibold text-slate-700 dark:text-slate-300">
                Last name
              </label>
              <input
                id={lastNameId}
                name="lastName"
                type="text"
                value={lastName}
                onChange={(event) => setLastName(event.target.value)}
                placeholder="e.g. Lovelace"
                className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 shadow-sm transition placeholder:text-slate-400 focus:border-indigo-300 focus:outline-none focus:ring focus:ring-indigo-200/60 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:placeholder:text-slate-500 dark:focus:border-indigo-600 dark:focus:ring-indigo-900/60"
              />
            </div>
          </div>

          <div className="grid gap-4 rounded-xl border border-slate-100 bg-slate-50/70 p-4 sm:grid-cols-[auto,1fr] sm:items-center dark:border-slate-700 dark:bg-slate-800/70">
            <div className="flex h-14 w-14 items-center justify-center rounded-full bg-indigo-100 text-base font-semibold uppercase text-indigo-600 shadow-inner dark:bg-indigo-950/50 dark:text-indigo-400">
              {initials}
            </div>
            <div className="space-y-1">
              <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">{displayName}</p>
            </div>
          </div>
        </section>

        <footer className="flex flex-col gap-3 border-t border-slate-100 pt-6 sm:flex-row sm:items-center sm:justify-end dark:border-slate-700">
          <button
            type="button"
            onClick={onDismiss}
            className="w-full rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50 sm:w-auto dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
          >
            Cancel
          </button>
          <button
            type="submit"
            className="w-full rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-500 sm:w-auto dark:bg-indigo-700 dark:hover:bg-indigo-600"
          >
            Save changes
          </button>
        </footer>
      </form>
    </DialogShell>
  );
};