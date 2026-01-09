import { useState, useEffect, useRef } from "react";
import { CreditCard } from "lucide-react";
import { DialogShell } from "../../../shared/components/DialogShell";

// Stripe publishable key from environment variables
const STRIPE_PUBLISHABLE_KEY = import.meta.env.VITE_STRIPE_PUBLISHABLE_KEY;

declare global {
  interface Window {
    Stripe: any;
  }
}

interface TopUpDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onTopUp: (amount: number, paymentMethodId: string) => Promise<void>;
}

export const TopUpDialog = ({ isOpen, onClose, onTopUp }: TopUpDialogProps) => {
  const [amount, setAmount] = useState("");
  const [isProcessing, setIsProcessing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stripe, setStripe] = useState<any>(null);
  const [cardElement, setCardElement] = useState<any>(null);
  const cardElementRef = useRef<HTMLDivElement>(null);

  // Initialize Stripe
  useEffect(() => {
    if (!window.Stripe) {
      console.error("Stripe.js not loaded");
      return;
    }

    const stripeInstance = window.Stripe(STRIPE_PUBLISHABLE_KEY);
    setStripe(stripeInstance);

    return () => {
      if (cardElement) {
        cardElement.destroy();
      }
    };
  }, []);

  // Create Stripe Elements when dialog opens
  useEffect(() => {
    if (!stripe || !isOpen || !cardElementRef.current) return;

    // Clear any existing element
    if (cardElement) {
      cardElement.destroy();
    }

    // Create Elements instance
    const elements = stripe.elements();

    // Create Card Element with custom styling
    const card = elements.create("card", {
      style: {
        base: {
          fontSize: "16px",
          color: "#0f172a",
          fontFamily: "system-ui, -apple-system, sans-serif",
          "::placeholder": {
            color: "#94a3b8",
          },
        },
        invalid: {
          color: "#dc2626",
        },
      },
      hidePostalCode: true,
    });

    // Mount the card element
    card.mount(cardElementRef.current);
    setCardElement(card);

    // Listen for validation errors
    card.on("change", (event: any) => {
      if (event.error) {
        setError(event.error.message);
      } else {
        setError(null);
      }
    });

    return () => {
      card.unmount();
    };
  }, [stripe, isOpen]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!stripe || !cardElement) {
      setError("Stripe is not initialized");
      return;
    }

    const numAmount = parseFloat(amount);
    if (isNaN(numAmount) || numAmount <= 0) {
      setError("Please enter a valid amount");
      return;
    }

    setIsProcessing(true);
    try {
      // Create payment method with Stripe Elements
      const { paymentMethod, error: stripeError } =
        await stripe.createPaymentMethod({
          type: "card",
          card: cardElement,
        });

      if (stripeError) {
        throw new Error(stripeError.message);
      }

      if (!paymentMethod) {
        throw new Error("Failed to create payment method");
      }

      // Send to backend - backend will handle 3D Secure confirmation if needed
      await onTopUp(numAmount, paymentMethod.id);

      // Reset form and close
      setAmount("");
      cardElement.clear();
      onClose();
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Failed to process payment"
      );
    } finally {
      setIsProcessing(false);
    }
  };

  return (
    <DialogShell
      badgeIcon={<CreditCard className="h-5 w-5 text-indigo-600" />}
      badgeLabel="Top Up Balance"
      open={isOpen}
      onDismiss={onClose}
      closeLabel="Close top-up dialog"
      title="Add Funds to Your Account"
    >
      <form onSubmit={handleSubmit} className="space-y-4">
        {error && (
          <div className="rounded-lg bg-rose-50 border border-rose-200 p-3 text-sm text-rose-700 dark:bg-rose-950/50 dark:border-rose-900/50 dark:text-rose-400">
            {error}
          </div>
        )}

        <div>
          <label
            htmlFor="amount"
            className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
          >
            Amount ($)
          </label>
          <input
            id="amount"
            type="number"
            step="0.01"
            min="1"
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
            className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-indigo-600 dark:focus:ring-indigo-900/60"
            placeholder="50.00"
            disabled={isProcessing}
            required
          />
        </div>

        <div>
          <label className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300">
            Card Information
          </label>
          <div
            ref={cardElementRef}
            className="w-full rounded-lg border border-slate-300 px-4 py-3 bg-white dark:border-slate-700 dark:bg-slate-800"
            style={{ minHeight: "40px" }}
          />
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
            Supports 3D Secure authentication for enhanced security
          </p>
        </div>

        <div className="pt-4 flex gap-3">
          <button
            type="button"
            onClick={onClose}
            className="flex-1 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            disabled={isProcessing}
          >
            Cancel
          </button>
          <button
            type="submit"
            className="flex-1 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed dark:bg-indigo-700 dark:hover:bg-indigo-600"
            disabled={isProcessing}
          >
            {isProcessing ? "Processing..." : "Top Up"}
          </button>
        </div>
      </form>

      <div className="mt-4 rounded-lg bg-slate-50 p-3 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-400">
        <p className="font-medium mb-1">🔒 Secure Payment with 3D Secure</p>
        <p>
          Your payment information is encrypted and never touches our servers.
          Protected by Stripe with 3D Secure authentication.
        </p>
      </div>
    </DialogShell>
  );
};
