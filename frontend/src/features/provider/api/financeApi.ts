import { apiRequest } from '../../../shared/utils/apiClient';

export interface UserAddress {
  line1: string | null;
  line2: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  country: string | null;
}

export interface UserInfo {
  firstName: string | null;
  lastName: string | null;
  phone: string | null;
  dateOfBirth: string | null;
  country: string | null;
  address: UserAddress | null;
  stripeConnectedAccountId: string | null;
  stripeExternalAccountId: string | null;
}

export interface FinanceSummary {
  balance: number;
  netBalance: number;
  totalCredits: number;
  totalDebits: number;
  creditsLast24Hours: number;
  debitsLast24Hours: number;
  pendingBalance: number;
  nextPayout: PayoutSnapshot | null;
  previousPayout: PayoutSnapshot | null;
  generatedAtUtc: string;
  ledgerEntries: LedgerEntry[];
  userInfo: UserInfo | null;
}

export interface PayoutSnapshot {
  reference: string;
  amount: number;
  initiatedAtUtc: string | null;
  settledAtUtc: string | null;
  entryCount: number;
  status: 'Pending' | 'Processing' | 'Completed' | 'Failed';
}

export interface LedgerEntry {
  entryId: string;
  kind: 'Credit' | 'Debit';
  title: string;
  detail: string | null;
  amount: number;
  occurredAtUtc: string;
  balanceAfter: number;
  taskId: string | null;
  source: string;
}

export const getFinanceSummary = async (): Promise<FinanceSummary> => {
  return apiRequest<FinanceSummary>('/api/finance/summary');
};

export const processTopUp = async (amount: number, stripePaymentMethodId: string): Promise<{ paymentId: string }> => {
  return apiRequest<{ paymentId: string }>('/api/finance/topup', {
    method: 'POST',
    body: { amount, stripePaymentMethodId }
  });
};

export interface SettlementParams {
  amount: number;
  country?: string;
  bankAccountDetails?: string;
  firstName?: string;
  lastName?: string;
  phone?: string;
  dateOfBirth?: string;
  addressLine1?: string;
  addressLine2?: string;
  city?: string;
  state?: string;
  postalCode?: string;
  addressCountry?: string;
  mcc?: string;
  useExistingAccount?: boolean;
}

export const createSettlement = async (params: SettlementParams): Promise<{ settlementId: string }> => {
  return apiRequest<{ settlementId: string }>('/api/finance/settlement', {
    method: 'POST',
    body: params
  });
};