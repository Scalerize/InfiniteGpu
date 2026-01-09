using InfiniteGPU.Backend.Data.Entities;

namespace InfiniteGPU.Backend.Features.Finance.Models;

public enum FinanceLedgerEntryKind
{
    Credit = 0,
    Debit = 1
}

public sealed record FinanceSummaryDto(
    decimal Balance,
    decimal TotalCredits,
    decimal TotalDebits,
    decimal CreditsLast24Hours,
    decimal DebitsLast24Hours,
    decimal PendingBalance,
    FinancePayoutSnapshotDto? NextPayout,
    FinancePayoutSnapshotDto? PreviousPayout,
    DateTime GeneratedAtUtc,
    IReadOnlyList<FinanceLedgerEntryDto> LedgerEntries,
    UserInfoDto? UserInfo = null);

public sealed record UserInfoDto(
    string? FirstName,
    string? LastName,
    string? Phone,
    DateOnly? DateOfBirth,
    string? Country,
    UserAddressDto? Address,
    string? StripeConnectedAccountId,
    string? StripeExternalAccountId);

public sealed record UserAddressDto(
    string? Line1,
    string? Line2,
    string? City,
    string? State,
    string? PostalCode,
    string? Country);

public sealed record FinancePayoutSnapshotDto(
    string Reference,
    decimal Amount,
    DateTime? InitiatedAtUtc,
    DateTime? SettledAtUtc,
    int EntryCount,
    SettlementStatus Status);

public sealed record FinanceLedgerEntryDto(
    string EntryId,
    FinanceLedgerEntryKind Kind,
    string Title,
    string? Detail,
    decimal Amount,
    DateTime OccurredAtUtc,
    decimal BalanceAfter,
    Guid? TaskId,
    string Source);
