import { useState } from "react";
import { Landmark } from "lucide-react";
import { DialogShell } from "../../../shared/components/DialogShell";

interface SettlementDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onSettle: (amount: number, country: string, bankAccountDetails: string, accountToken?: string) => Promise<void>;
  availableBalance: number;
}

const MIN_SETTLEMENT_AMOUNT = 30;

export const SettlementDialog = ({
  isOpen,
  onClose,
  onSettle,
  availableBalance,
}: SettlementDialogProps) => {
  const [amount, setAmount] = useState("");
  const [country, setCountry] = useState("US");
  const [bankName, setBankName] = useState("");
  const [accountHolderName, setAccountHolderName] = useState("");
  
  // US ACH fields
  const [accountNumber, setAccountNumber] = useState("");
  const [routingNumber, setRoutingNumber] = useState("");
  
  // EU SEPA fields
  const [iban, setIban] = useState("");
  const [bic, setBic] = useState("");
  
  const [isProcessing, setIsProcessing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isEuCountry = ["AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
    "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
    "PL", "PT", "RO", "SK", "SI", "ES", "SE"].includes(country);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    const numAmount = parseFloat(amount);
    if (isNaN(numAmount) || numAmount <= 0) {
      setError("Please enter a valid amount");
      return;
    }

    if (numAmount < MIN_SETTLEMENT_AMOUNT) {
      setError(`Minimum settlement amount is $${MIN_SETTLEMENT_AMOUNT}`);
      return;
    }

    if (numAmount > availableBalance) {
      setError(
        `Insufficient balance. Available: $${availableBalance.toFixed(2)}`
      );
      return;
    }

    if (!bankName || !accountHolderName) {
      setError("Please fill in all required fields");
      return;
    }

    // Validate country-specific fields
    if (isEuCountry) {
      if (!iban || !bic) {
        setError("Please fill in IBAN and BIC/SWIFT");
        return;
      }
    } else if (country === "US") {
      if (!accountNumber || !routingNumber) {
        setError("Please fill in account number and routing number");
        return;
      }
    }

    setIsProcessing(true);
    try {
      const bankAccountDetails = JSON.stringify({
        bankName,
        accountHolderName,
        ...(isEuCountry ? { iban, bic } : { accountNumber, routingNumber })
      });

      await onSettle(numAmount, country, bankAccountDetails);

      // Reset form and close
      setAmount("");
      setCountry("US");
      setBankName("");
      setAccountHolderName("");
      setAccountNumber("");
      setRoutingNumber("");
      setIban("");
      setBic("");
      onClose();
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Failed to create settlement"
      );
    } finally {
      setIsProcessing(false);
    }
  };

  const canSettle = availableBalance >= MIN_SETTLEMENT_AMOUNT;

  return (
    <DialogShell
      badgeIcon={<Landmark className="h-5 w-5 text-indigo-600" />}
      badgeLabel="New Settlement"
      title=""
      open={isOpen}
      onDismiss={onClose}
      closeLabel="Close settlement dialog"
    >
      {!canSettle && (
        <div className="mb-4 rounded-lg bg-amber-50 border border-amber-200 p-3 text-sm text-amber-700 dark:bg-amber-950/50 dark:border-amber-900/50 dark:text-amber-400">
          Minimum balance of ${MIN_SETTLEMENT_AMOUNT} required. Current balance:
          ${availableBalance.toFixed(2)}
        </div>
      )}

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
            Settlement Amount ($)
          </label>
          <input
            id="amount"
            type="number"
            step="0.01"
            min={MIN_SETTLEMENT_AMOUNT}
            max={availableBalance}
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
            className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
            placeholder={`Min: ${MIN_SETTLEMENT_AMOUNT}`}
            disabled={isProcessing || !canSettle}
            required
          />
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
            Available: ${availableBalance.toFixed(2)}
          </p>
        </div>

        <div className="border-t border-slate-200 pt-4 dark:border-slate-700">
          <h3 className="text-sm font-medium text-slate-900 mb-3 dark:text-slate-100">
            Bank Account Details
          </h3>

          <div className="space-y-3">
            <div>
              <label
                htmlFor="country"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                Country
              </label>
              <select
                id="country"
                value={country}
                onChange={(e) => setCountry(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                disabled={isProcessing || !canSettle}
                required
              >
                <option value="US">United States</option>
                <option value="FR">France</option>
                <option value="DE">Germany</option>
                <option value="IT">Italy</option>
                <option value="ES">Spain</option>
                <option value="NL">Netherlands</option>
                <option value="BE">Belgium</option>
                <option value="AT">Austria</option>
                <option value="PT">Portugal</option>
                <option value="IE">Ireland</option>
                <option value="PL">Poland</option>
                <option value="SE">Sweden</option>
                <option value="DK">Denmark</option>
                <option value="FI">Finland</option>
                <option value="GR">Greece</option>
              </select>
            </div>

            <div>
              <label
                htmlFor="accountHolderName"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                Account Holder Name
              </label>
              <input
                id="accountHolderName"
                type="text"
                value={accountHolderName}
                onChange={(e) => setAccountHolderName(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                placeholder="John Doe"
                disabled={isProcessing || !canSettle}
                required
              />
            </div>

            <div>
              <label
                htmlFor="bankName"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                Bank Name
              </label>
              <input
                id="bankName"
                type="text"
                value={bankName}
                onChange={(e) => setBankName(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                placeholder={isEuCountry ? "BNP Paribas" : "Bank of America"}
                disabled={isProcessing || !canSettle}
                required
              />
            </div>

            {isEuCountry ? (
              <>
                <div>
                  <label
                    htmlFor="iban"
                    className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
                  >
                    IBAN
                  </label>
                  <input
                    id="iban"
                    type="text"
                    value={iban}
                    onChange={(e) => setIban(e.target.value.toUpperCase())}
                    className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                    placeholder="FR7612345678901234567890123"
                    disabled={isProcessing || !canSettle}
                    required
                  />
                </div>

                <div>
                  <label
                    htmlFor="bic"
                    className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
                  >
                    BIC/SWIFT Code
                  </label>
                  <input
                    id="bic"
                    type="text"
                    value={bic}
                    onChange={(e) => setBic(e.target.value.toUpperCase())}
                    maxLength={11}
                    className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                    placeholder="BNPAFRPP"
                    disabled={isProcessing || !canSettle}
                    required
                  />
                </div>
              </>
            ) : (
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label
                    htmlFor="routingNumber"
                    className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
                  >
                    Routing Number
                  </label>
                  <input
                    id="routingNumber"
                    type="text"
                    value={routingNumber}
                    onChange={(e) =>
                      setRoutingNumber(e.target.value.replace(/\D/g, ""))
                    }
                    maxLength={9}
                    className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                    placeholder="123456789"
                    disabled={isProcessing || !canSettle}
                    required
                  />
                </div>

                <div>
                  <label
                    htmlFor="accountNumber"
                    className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
                  >
                    Account Number
                  </label>
                  <input
                    id="accountNumber"
                    type="text"
                    value={accountNumber}
                    onChange={(e) =>
                      setAccountNumber(e.target.value.replace(/\D/g, ""))
                    }
                    maxLength={17}
                    className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                    placeholder="000123456789"
                    disabled={isProcessing || !canSettle}
                    required
                  />
                </div>
              </div>
            )}
          </div>
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
            className="flex-1 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-emerald-700 disabled:opacity-50 disabled:cursor-not-allowed dark:bg-indigo-700 dark:hover:bg-emerald-600"
            disabled={isProcessing || !canSettle}
          >
            {isProcessing ? "Processing..." : "Create Settlement"}
          </button>
        </div>
      </form>

      <div className="mt-4 rounded-lg bg-slate-50 p-3 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-400">
        <p className="font-medium mb-1">Settlement Processing</p>
        <p>
          Funds will be transferred to your bank account within 1-2 business
          days.
        </p>
      </div>
    </DialogShell>
  );
};
