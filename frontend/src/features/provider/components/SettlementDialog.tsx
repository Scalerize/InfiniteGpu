import { useState, useEffect, useMemo } from "react";
import { Landmark, Edit2, CreditCard, User, MapPin } from "lucide-react";
import { DialogShell } from "../../../shared/components/DialogShell";
import type { SettlementParams, UserInfo } from "../api/financeApi";

interface SettlementDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onSettle: (params: SettlementParams) => Promise<void>;
  availableBalance: number;
  userInfo?: UserInfo | null;
}

const MIN_SETTLEMENT_AMOUNT = 30;

const COUNTRY_NAMES: Record<string, string> = {
  US: "United States",
  FR: "France",
  DE: "Germany",
  IT: "Italy",
  ES: "Spain",
  NL: "Netherlands",
  BE: "Belgium",
  AT: "Austria",
  PT: "Portugal",
  IE: "Ireland",
  PL: "Poland",
  SE: "Sweden",
  DK: "Denmark",
  FI: "Finland",
  GR: "Greece",
};

export const SettlementDialog = ({
  isOpen,
  onClose,
  onSettle,
  availableBalance,
  userInfo,
}: SettlementDialogProps) => {
  const [amount, setAmount] = useState("");
  const [bankName, setBankName] = useState("");
  const [accountHolderName, setAccountHolderName] = useState("");
  
  // Representative info (required for Stripe Connect)
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [phone, setPhone] = useState("");
  const [dateOfBirth, setDateOfBirth] = useState("");
  
  // Address fields
  const [addressLine1, setAddressLine1] = useState("");
  const [addressLine2, setAddressLine2] = useState("");
  const [city, setCity] = useState("");
  const [state, setState] = useState("");
  const [postalCode, setPostalCode] = useState("");
  const [addressCountry, setAddressCountry] = useState("US");
  
  // US ACH fields
  const [accountNumber, setAccountNumber] = useState("");
  const [routingNumber, setRoutingNumber] = useState("");
  
  // EU SEPA fields
  const [iban, setIban] = useState("");
  const [bic, setBic] = useState("");
  
  const [isProcessing, setIsProcessing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  
  // Edit mode - false means using existing account, true means editing/creating new
  const [isEditMode, setIsEditMode] = useState(false);

  // Check if user has configured payment accounts
  const hasExistingAccounts = useMemo(() => {
    return !!(userInfo?.stripeConnectedAccountId && userInfo?.stripeExternalAccountId);
  }, [userInfo]);

  // Prefill form with user info when dialog opens
  useEffect(() => {
    if (isOpen && userInfo) {
      if (userInfo.firstName) setFirstName(userInfo.firstName);
      if (userInfo.lastName) setLastName(userInfo.lastName);
      if (userInfo.phone) setPhone(userInfo.phone);
      if (userInfo.dateOfBirth) setDateOfBirth(userInfo.dateOfBirth);
      if (userInfo.country) setAddressCountry(userInfo.country);
      if (userInfo.address) {
        if (userInfo.address.line1) setAddressLine1(userInfo.address.line1);
        if (userInfo.address.line2) setAddressLine2(userInfo.address.line2);
        if (userInfo.address.city) setCity(userInfo.address.city);
        if (userInfo.address.state) setState(userInfo.address.state);
        if (userInfo.address.postalCode) setPostalCode(userInfo.address.postalCode);
        if (userInfo.address.country) setAddressCountry(userInfo.address.country);
      }
      // Prefill account holder name with first/last name
      const fullName = [userInfo.firstName, userInfo.lastName].filter(Boolean).join(' ');
      if (fullName) setAccountHolderName(fullName);
      
      // If user has existing accounts, start in view mode
      if (userInfo.stripeConnectedAccountId && userInfo.stripeExternalAccountId) {
        setIsEditMode(false);
      } else {
        setIsEditMode(true);
      }
    }
  }, [isOpen, userInfo]);

  const isEuCountry = ["AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
    "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
    "PL", "PT", "RO", "SK", "SI", "ES", "SE"].includes(addressCountry);

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

    // If using existing account, just submit the amount
    if (hasExistingAccounts && !isEditMode) {
      setIsProcessing(true);
      try {
        await onSettle({
          amount: numAmount,
          useExistingAccount: true,
        });

        setAmount("");
        onClose();
      } catch (err) {
        setError(
          err instanceof Error ? err.message : "Failed to create settlement"
        );
      } finally {
        setIsProcessing(false);
      }
      return;
    }

    // Validate new account details
    if (!bankName || !accountHolderName || !firstName || !lastName || !phone || !dateOfBirth) {
      setError("Please fill in all required fields");
      return;
    }

    // Validate address fields
    if (!addressLine1 || !city || !postalCode || !addressCountry) {
      setError("Please fill in address details (street, city, postal code, country)");
      return;
    }

    // Validate country-specific fields
    if (isEuCountry) {
      if (!iban || !bic) {
        setError("Please fill in IBAN and BIC/SWIFT");
        return;
      }
    } else if (addressCountry === "US") {
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

      await onSettle({
        amount: numAmount,
        country: addressCountry,
        bankAccountDetails,
        firstName,
        lastName,
        phone,
        dateOfBirth: dateOfBirth || undefined,
        addressLine1,
        addressLine2: addressLine2 || undefined,
        city,
        state: state || undefined,
        postalCode,
        addressCountry,
        useExistingAccount: false,
      });

      // Reset form and close
      setAmount("");
      setBankName("");
      setAccountHolderName("");
      setFirstName("");
      setLastName("");
      setPhone("");
      setDateOfBirth("");
      setAddressLine1("");
      setAddressLine2("");
      setCity("");
      setState("");
      setPostalCode("");
      setAddressCountry("");
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

  // Render the existing account info card
  const renderExistingAccountCard = () => {
    if (!userInfo) return null;

    const fullName = [userInfo.firstName, userInfo.lastName].filter(Boolean).join(' ');
    const addressParts = [];
    if (userInfo.address?.line1) addressParts.push(userInfo.address.line1);
    if (userInfo.address?.line2) addressParts.push(userInfo.address.line2);
    if (userInfo.address?.city) addressParts.push(userInfo.address.city);
    if (userInfo.address?.state) addressParts.push(userInfo.address.state);
    if (userInfo.address?.postalCode) addressParts.push(userInfo.address.postalCode);
    if (userInfo.address?.country) {
      addressParts.push(COUNTRY_NAMES[userInfo.address.country] || userInfo.address.country);
    }
    const addressString = addressParts.join(', ');

    return (
      <div className="rounded-lg border border-emerald-200 bg-emerald-50 p-4 dark:border-emerald-800 dark:bg-emerald-950/30">
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <CreditCard className="h-5 w-5 text-emerald-600 dark:text-emerald-400" />
            <span className="font-medium text-emerald-800 dark:text-emerald-300">
              Saved Payment Account
            </span>
          </div>
          <button
            type="button"
            onClick={() => setIsEditMode(true)}
            className="flex items-center gap-1 text-sm text-emerald-600 hover:text-emerald-700 dark:text-emerald-400 dark:hover:text-emerald-300 transition"
          >
            <Edit2 className="h-4 w-4" />
            Edit
          </button>
        </div>

        <div className="space-y-3">
          {/* Representative Info */}
          <div className="flex items-start gap-2">
            <User className="h-4 w-4 text-slate-500 dark:text-slate-400 mt-0.5" />
            <div>
              <p className="text-sm font-medium text-slate-800 dark:text-slate-200">
                {fullName || 'Not configured'}
              </p>
              {userInfo.phone && (
                <p className="text-xs text-slate-600 dark:text-slate-400">
                  {userInfo.phone}
                </p>
              )}
              {userInfo.dateOfBirth && (
                <p className="text-xs text-slate-600 dark:text-slate-400">
                  DOB: {userInfo.dateOfBirth}
                </p>
              )}
            </div>
          </div>

          {/* Address */}
          {addressString && (
            <div className="flex items-start gap-2">
              <MapPin className="h-4 w-4 text-slate-500 dark:text-slate-400 mt-0.5" />
              <p className="text-sm text-slate-700 dark:text-slate-300">
                {addressString}
              </p>
            </div>
          )}

          {/* Bank Account Info */}
          <div className="flex items-start gap-2">
            <Landmark className="h-4 w-4 text-slate-500 dark:text-slate-400 mt-0.5" />
            <div>
              <p className="text-sm text-slate-700 dark:text-slate-300">
                Bank account configured
              </p>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                Account ID: ••••{userInfo.stripeExternalAccountId?.slice(-4)}
              </p>
            </div>
          </div>
        </div>

        <div className="mt-3 pt-3 border-t border-emerald-200 dark:border-emerald-800">
          <p className="text-xs text-emerald-700 dark:text-emerald-400">
            ✓ Ready for instant settlement
          </p>
        </div>
      </div>
    );
  };

  // Render the full edit form
  const renderEditForm = () => (
    <>
      <div className="border-t border-slate-200 pt-4 dark:border-slate-700">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-sm font-medium text-slate-900 dark:text-slate-100">
            Representative Information
          </h3>
          {hasExistingAccounts && (
            <button
              type="button"
              onClick={() => setIsEditMode(false)}
              className="text-xs text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
            >
              Cancel Edit
            </button>
          )}
        </div>

        <div className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label
                htmlFor="firstName"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                First Name
              </label>
              <input
                id="firstName"
                type="text"
                value={firstName}
                onChange={(e) => setFirstName(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                placeholder="John"
                disabled={isProcessing || !canSettle}
                required
              />
            </div>

            <div>
              <label
                htmlFor="lastName"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                Last Name
              </label>
              <input
                id="lastName"
                type="text"
                value={lastName}
                onChange={(e) => setLastName(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                placeholder="Doe"
                disabled={isProcessing || !canSettle}
                required
              />
            </div>
          </div>

          <div>
            <label
              htmlFor="phone"
              className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
            >
              Phone Number
            </label>
            <input
              id="phone"
              type="tel"
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
              className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
              placeholder="+1 555 123 4567"
              disabled={isProcessing || !canSettle}
              required
            />
          </div>

          <div>
            <label
              htmlFor="dateOfBirth"
              className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
            >
              Date of Birth
            </label>
            <input
              id="dateOfBirth"
              type="date"
              value={dateOfBirth}
              onChange={(e) => setDateOfBirth(e.target.value)}
              className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
              disabled={isProcessing || !canSettle}
              required
            />
          </div>
        </div>
      </div>

      <div className="border-t border-slate-200 pt-4 dark:border-slate-700">
        <h3 className="text-sm font-medium text-slate-900 mb-3 dark:text-slate-100">
          Address
        </h3>

        <div className="space-y-3">
          <div>
            <label
              htmlFor="addressLine1"
              className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
            >
              Street Address
            </label>
            <input
              id="addressLine1"
              type="text"
              value={addressLine1}
              onChange={(e) => setAddressLine1(e.target.value)}
              className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
              placeholder="123 Main Street"
              disabled={isProcessing || !canSettle}
              required
            />
          </div>

          <div>
            <label
              htmlFor="addressLine2"
              className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
            >
              Address Line 2 <span className="text-slate-400">(optional)</span>
            </label>
            <input
              id="addressLine2"
              type="text"
              value={addressLine2}
              onChange={(e) => setAddressLine2(e.target.value)}
              className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
              placeholder="Apt 4B"
              disabled={isProcessing || !canSettle}
            />
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label
                htmlFor="city"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                City
              </label>
              <input
                id="city"
                type="text"
                value={city}
                onChange={(e) => setCity(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                placeholder="New York"
                disabled={isProcessing || !canSettle}
                required
              />
            </div>

            <div>
              <label
                htmlFor="state"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                State/Province <span className="text-slate-400">(opt.)</span>
              </label>
              <input
                id="state"
                type="text"
                value={state}
                onChange={(e) => setState(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                placeholder="NY"
                disabled={isProcessing || !canSettle}
              />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label
                htmlFor="postalCode"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                Postal Code
              </label>
              <input
                id="postalCode"
                type="text"
                value={postalCode}
                onChange={(e) => setPostalCode(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                placeholder="10001"
                disabled={isProcessing || !canSettle}
                required
              />
            </div>

            <div>
              <label
                htmlFor="addressCountry"
                className="block text-sm font-medium text-slate-700 mb-1 dark:text-slate-300"
              >
                Country
              </label>
              <select
                id="addressCountry"
                value={addressCountry}
                onChange={(e) => setAddressCountry(e.target.value)}
                className="w-full rounded-lg border border-slate-300 px-4 py-2 text-slate-900 focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100 dark:focus:border-emerald-600 dark:focus:ring-emerald-900/60"
                disabled={isProcessing || !canSettle}
                required
              >
                <option value="">Select Country</option>
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
          </div>
        </div>
      </div>

      <div className="border-t border-slate-200 pt-4 dark:border-slate-700">
        <h3 className="text-sm font-medium text-slate-900 mb-3 dark:text-slate-100">
          Bank Account Details
        </h3>

        <div className="space-y-3">
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
    </>
  );

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

        {/* Show existing account card or edit form */}
        {hasExistingAccounts && !isEditMode ? (
          renderExistingAccountCard()
        ) : (
          renderEditForm()
        )}

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
