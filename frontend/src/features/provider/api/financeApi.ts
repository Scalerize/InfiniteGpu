import { apiRequest } from '../../../shared/utils/apiClient';

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

export const createSettlement = async (amount: number, country: string, bankAccountDetails: string, accountToken?: string): Promise<{ settlementId: string }> => {
  return apiRequest<{ settlementId: string }>('/api/finance/settlement', {
    method: 'POST',
    body: { amount, country, bankAccountDetails, accountToken }
  });
};