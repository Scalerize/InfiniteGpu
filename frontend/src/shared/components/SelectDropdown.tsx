import { forwardRef, type ReactNode } from 'react';
import * as SelectPrimitive from '@radix-ui/react-select';
import { Check, ChevronDown, ChevronUp } from 'lucide-react';

type SelectValueType = string;

export type SelectDropdownOption<TValue extends SelectValueType> = {
  value: TValue;
  label: ReactNode;
  description?: ReactNode;
  disabled?: boolean;
};

export interface SelectDropdownProps<TValue extends SelectValueType> {
  id?: string;
  name?: string;
  ariaLabel?: string;
  value: TValue;
  placeholder?: string;
  disabled?: boolean;
  onValueChange: (value: TValue) => void;
  options: Array<SelectDropdownOption<TValue>>;
  triggerClassName?: string;
  contentClassName?: string;
  align?: NonNullable<SelectPrimitive.SelectContentProps['align']>;
  sideOffset?: number;
}

const baseTriggerClasses =
  'inline-flex w-full items-center justify-between gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-left text-sm text-slate-700 shadow-sm transition focus:border-indigo-300 focus:outline-none focus:ring focus:ring-indigo-200/60 data-[placeholder]:text-slate-400 disabled:cursor-not-allowed disabled:opacity-60 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:focus:border-indigo-600 dark:focus:ring-indigo-900/60 dark:data-[placeholder]:text-slate-500';
const baseContentClasses = 'z-50 w-auto min-w-[var(--radix-select-trigger-width)] overflow-hidden rounded-lg border border-slate-200 bg-white shadow-xl dark:border-slate-700 dark:bg-slate-800';
const baseItemClasses =
  'relative flex cursor-pointer select-none items-center rounded-md pl-8 pr-3 py-2 text-sm text-slate-700 outline-none transition hover:bg-indigo-50 hover:text-indigo-600 data-[state=checked]:bg-indigo-100 data-[state=checked]:text-indigo-700 data-[disabled]:cursor-not-allowed data-[disabled]:opacity-60 dark:text-slate-200 dark:hover:bg-indigo-950/50 dark:hover:text-indigo-400 dark:data-[state=checked]:bg-indigo-950/70 dark:data-[state=checked]:text-indigo-300';

const SelectItem = forwardRef<HTMLDivElement, SelectPrimitive.SelectItemProps & { className?: string }>(
  ({ children, className, ...props }, forwardedRef) => {
    const combinedClassName = className ? `${baseItemClasses} ${className}` : baseItemClasses;

    return (
      <SelectPrimitive.Item ref={forwardedRef} className={combinedClassName} {...props}>
        <SelectPrimitive.ItemIndicator className="absolute left-2 flex h-4 w-4 items-center justify-center text-indigo-600 dark:text-indigo-400">
          <Check className="h-4 w-4" aria-hidden="true" />
        </SelectPrimitive.ItemIndicator>
        <SelectPrimitive.ItemText asChild>
          <span className="flex flex-col gap-0.5">
            {children}
          </span>
        </SelectPrimitive.ItemText>
      </SelectPrimitive.Item>
    );
  }
);

SelectItem.displayName = 'SelectItem';

export const SelectDropdown = <TValue extends SelectValueType>({
  id,
  name,
  ariaLabel,
  value,
  placeholder,
  disabled,
  onValueChange,
  options,
  triggerClassName,
  contentClassName,
  align,
  sideOffset = 6
}: SelectDropdownProps<TValue>) => {
  const combinedTriggerClassName = triggerClassName ? `${baseTriggerClasses} ${triggerClassName}` : baseTriggerClasses;
  const combinedContentClassName = contentClassName ? `${baseContentClasses} ${contentClassName}` : baseContentClasses;

  return (
    <SelectPrimitive.Root value={value} onValueChange={onValueChange} disabled={disabled}>
      <SelectPrimitive.Trigger
        id={id}
        name={name}
        aria-label={ariaLabel}
        className={combinedTriggerClassName}
      >
        <SelectPrimitive.Value placeholder={placeholder} aria-live="polite" />
        <SelectPrimitive.Icon>
          <ChevronDown className="h-4 w-4 text-slate-400 dark:text-slate-500" aria-hidden="true" />
        </SelectPrimitive.Icon>
      </SelectPrimitive.Trigger>

      <SelectPrimitive.Portal>
        <SelectPrimitive.Content
          className={combinedContentClassName}
          position="popper"
          collisionPadding={12}
          sideOffset={sideOffset}
          align={align}
        >
          <SelectPrimitive.ScrollUpButton className="flex items-center justify-center bg-slate-50 py-2 text-slate-500 dark:bg-slate-700 dark:text-slate-400">
            <ChevronUp className="h-4 w-4" aria-hidden="true" />
          </SelectPrimitive.ScrollUpButton>

          <SelectPrimitive.Viewport className="max-h-64 p-1">
            {options.map((option) => (
              <SelectItem
                key={option.value}
                value={option.value}
                disabled={option.disabled}
                className={option.description ? 'items-start' : undefined}
              >
                <span className="text-sm font-medium leading-5">{option.label}</span>
                {option.description ? (
                  <span className="text-xs font-normal text-slate-500 dark:text-slate-400">{option.description}</span>
                ) : null}
              </SelectItem>
            ))}
          </SelectPrimitive.Viewport>

          <SelectPrimitive.ScrollDownButton className="flex items-center justify-center bg-slate-50 py-2 text-slate-500 dark:bg-slate-700 dark:text-slate-400">
            <ChevronDown className="h-4 w-4" aria-hidden="true" />
          </SelectPrimitive.ScrollDownButton>
        </SelectPrimitive.Content>
      </SelectPrimitive.Portal>
    </SelectPrimitive.Root>
  );
};