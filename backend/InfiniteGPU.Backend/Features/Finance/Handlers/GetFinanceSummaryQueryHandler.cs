using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Finance.Models;
using InfiniteGPU.Backend.Features.Finance.Queries;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace InfiniteGPU.Backend.Features.Finance.Handlers;

public sealed class GetFinanceSummaryQueryHandler : IRequestHandler<GetFinanceSummaryQuery, FinanceSummaryDto>
{
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromHours(24);

    private readonly AppDbContext _context;

    public GetFinanceSummaryQueryHandler(AppDbContext context)
    {
        _context = context;
    }

    public async Task<FinanceSummaryDto> Handle(GetFinanceSummaryQuery request, CancellationToken cancellationToken)
    {
        var userId = request.UserId;
        var now = DateTime.UtcNow;
        var since = now - LookbackWindow;

        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new {
                u.Balance,
                u.FirstName,
                u.LastName,
                u.Phone,
                u.DateOfBirth,
                u.Country,
                u.Address,
                u.StripeConnectedAccountId,
                u.StripeExternalAccountId
            })
            .FirstOrDefaultAsync(cancellationToken);

        var userBalance = user?.Balance ?? 0m;
        
        // Build user info for prefilling settlement form
        UserInfoDto? userInfo = user is not null
            ? new UserInfoDto(
                user.FirstName,
                user.LastName,
                user.Phone,
                user.DateOfBirth,
                user.Country,
                user.Address is not null ? new UserAddressDto(
                    user.Address.Line1,
                    user.Address.Line2,
                    user.Address.City,
                    user.Address.State,
                    user.Address.PostalCode,
                    user.Address.Country
                ) : null,
                user.StripeConnectedAccountId,
                user.StripeExternalAccountId
            )
            : null;

        var creditProjections = await _context.Earnings
            .AsNoTracking()
            .Where(e => e.ProviderUserId == userId)
            .Select(e => new EarningProjection(
                e.Id,
                e.TaskId,
                e.Amount,
                e.Status,
                e.CreatedAtUtc,
                e.UpdatedAtUtc,
                e.PaidAtUtc))
            .ToListAsync(cancellationToken);

        var withdrawalProjections = await _context.Withdrawals
            .AsNoTracking()
            .Where(w => w.RequestorUserId == userId)
            .Select(w => new WithdrawalProjection(
                w.Id,
                w.TaskId,
                w.SubtaskId,
                w.Amount,
                w.Status,
                w.CreatedAtUtc,
                w.UpdatedAtUtc,
                w.SettledAtUtc))
            .ToListAsync(cancellationToken);

        var settlementProjections = await _context.Settlements
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => new SettlementProjection(
                s.Id,
                s.Amount,
                s.Status,
                s.CreatedAtUtc,
                s.UpdatedAtUtc,
                s.CompletedAtUtc))
            .ToListAsync(cancellationToken);

        var paymentProjections = await _context.Payments
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new PaymentProjection(
                p.Id,
                p.Amount,
                p.Status,
                p.CreatedAtUtc,
                p.UpdatedAtUtc,
                p.SettledAtUtc))
            .ToListAsync(cancellationToken);

        var relatedTaskIds = creditProjections.Select(c => c.TaskId)
            .Concat(withdrawalProjections.Select(d => d.TaskId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        var taskLookup = relatedTaskIds.Length == 0
            ? new Dictionary<Guid, TaskSnapshot>()
            : await _context.Tasks
                .AsNoTracking()
                .Where(t => relatedTaskIds.Contains(t.Id))
                .Select(t => new TaskSnapshot(t.Id, t.OnnxModelBlobUri))
                .ToDictionaryAsync(t => t.Id, cancellationToken);

        // Exclude failed settlements from calculations and ledger display
        var nonFailedSettlements = settlementProjections.Where(s => s.Status != SettlementStatus.Failed).ToList();

        var totalCredits = creditProjections.Sum(c => c.Amount) + paymentProjections.Sum(p => p.Amount);
        var totalDebits = withdrawalProjections.Sum(d => d.Amount) + nonFailedSettlements.Sum(s => s.Amount);
        var creditsLast24h = creditProjections.Where(c => c.CreatedAtUtc >= since).Sum(c => c.Amount)
            + paymentProjections.Where(p => p.CreatedAtUtc >= since).Sum(p => p.Amount);
        var debitsLast24h = withdrawalProjections.Where(d => d.CreatedAtUtc >= since).Sum(d => d.Amount)
            + nonFailedSettlements.Where(s => s.CreatedAtUtc >= since).Sum(s => s.Amount);
        var pendingBalance = creditProjections.Where(c => c.Status == EarningStatus.Pending).Sum(c => c.Amount);

        var ledgerEntries = BuildLedgerEntries(creditProjections, withdrawalProjections, nonFailedSettlements, paymentProjections, taskLookup);
        var chronological = ledgerEntries
            .OrderBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.EntryId, StringComparer.Ordinal)
            .ToList();

        var ledger = chronological
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ThenByDescending(entry => entry.EntryId, StringComparer.Ordinal)
            .Take(100)
            .Select(entry => new FinanceLedgerEntryDto(
                entry.EntryId,
                entry.Kind,
                entry.Title,
                entry.Detail,
                entry.Amount,
                entry.OccurredAtUtc,
                entry.BalanceAfter,
                entry.TaskId,
                entry.Source))
            .ToList();

        var nextPayout = SelectPayoutSnapshot(
            settlementProjections,
            SettlementStatus.Pending,
            g => g.Min(x => x.CreatedAtUtc),
            ascending: true);

        var previousPayout = SelectPayoutSnapshot(
            settlementProjections,
            SettlementStatus.Completed,
            g => g.Max(x => x.CompletedAtUtc ?? x.UpdatedAtUtc ?? x.CreatedAtUtc),
            ascending: false);

        return new FinanceSummaryDto(
            Balance: userBalance,
            TotalCredits: totalCredits,
            TotalDebits: totalDebits,
            CreditsLast24Hours: creditsLast24h,
            DebitsLast24Hours: debitsLast24h,
            PendingBalance: pendingBalance,
            NextPayout: nextPayout,
            PreviousPayout: previousPayout,
            GeneratedAtUtc: now,
            LedgerEntries: ledger,
            UserInfo: userInfo);
    }

    private static List<LedgerProjection> BuildLedgerEntries(
        IEnumerable<EarningProjection> credits,
        IEnumerable<WithdrawalProjection> withdrawals,
        IEnumerable<SettlementProjection> settlements,
        IEnumerable<PaymentProjection> payments,
        IReadOnlyDictionary<Guid, TaskSnapshot> taskLookup)
    {
        var result = new List<LedgerProjection>();

        foreach (var credit in credits)
        {
            var occurredAt = credit.PaidAtUtc ?? credit.UpdatedAtUtc ?? credit.CreatedAtUtc;
            taskLookup.TryGetValue(credit.TaskId, out var taskSnapshot);

            result.Add(new LedgerProjection
            {
                EntryId = credit.Id.ToString(),
                Kind = FinanceLedgerEntryKind.Credit,
                Title = $"Earning for task {credit.TaskId:N}",
                Detail = taskSnapshot?.ModelUrl,
                Amount = credit.Amount,
                OccurredAtUtc = occurredAt,
                TaskId = credit.TaskId,
                Source = "escrow"
            });
        }

        foreach (var withdrawal in withdrawals)
        {
            var occurredAt = withdrawal.SettledAtUtc ?? withdrawal.UpdatedAtUtc ?? withdrawal.CreatedAtUtc;
            taskLookup.TryGetValue(withdrawal.TaskId, out var taskSnapshot);

            result.Add(new LedgerProjection
            {
                EntryId = withdrawal.Id.ToString(),
                Kind = FinanceLedgerEntryKind.Debit,
                Title = $"Charge for subtask {withdrawal.SubtaskId:N}",
                Detail = taskSnapshot?.ModelUrl,
                Amount = withdrawal.Amount,
                OccurredAtUtc = occurredAt,
                TaskId = withdrawal.TaskId,
                Source = "escrow"
            });
        }

        foreach (var settlement in settlements)
        {
            var occurredAt = settlement.CompletedAtUtc ?? settlement.UpdatedAtUtc ?? settlement.CreatedAtUtc;

            result.Add(new LedgerProjection
            {
                EntryId = settlement.Id.ToString(),
                Kind = FinanceLedgerEntryKind.Debit,
                Title = "Settlement payout",
                Detail = $"Bank transfer ({settlement.Status})",
                Amount = settlement.Amount,
                OccurredAtUtc = occurredAt,
                TaskId = null,
                Source = "settlement"
            });
        }

        foreach (var payment in payments)
        {
            var occurredAt = payment.SettledAtUtc ?? payment.UpdatedAtUtc ?? payment.CreatedAtUtc;

            result.Add(new LedgerProjection
            {
                EntryId = payment.Id.ToString(),
                Kind = FinanceLedgerEntryKind.Credit,
                Title = "Account top-up",
                Detail = $"Payment ({payment.Status})",
                Amount = payment.Amount,
                OccurredAtUtc = occurredAt,
                TaskId = null,
                Source = "topup"
            });
        }

        return result;
    }

    private static FinancePayoutSnapshotDto? SelectPayoutSnapshot(
        IEnumerable<SettlementProjection> entries,
        SettlementStatus status,
        Func<IGrouping<string, SettlementProjection>, DateTime?> orderingSelector,
        bool ascending)
    {
        var groups = entries
            .Where(entry => entry.Status == status)
            .GroupBy(entry => $"settlement:{entry.Id}");

        var orderedGroups = ascending
            ? groups.OrderBy(g => orderingSelector(g) ?? DateTime.MaxValue)
            : groups.OrderByDescending(g => orderingSelector(g) ?? DateTime.MinValue);

        var selectedGroup = orderedGroups.FirstOrDefault();
        if (selectedGroup is null)
        {
            return null;
        }

        var reference = string.IsNullOrWhiteSpace(selectedGroup.Key)
            ? $"settlement:{selectedGroup.First().Id}"
            : selectedGroup.Key;

        var initiatedAt = selectedGroup.Min(entry => entry.CreatedAtUtc);
        var settledAt = status == SettlementStatus.Completed
            ? selectedGroup.Max(entry => entry.CompletedAtUtc ?? entry.UpdatedAtUtc ?? entry.CreatedAtUtc)
            : (DateTime?)null;

        return new FinancePayoutSnapshotDto(
            reference,
            selectedGroup.Sum(entry => entry.Amount),
            initiatedAt,
            settledAt,
            selectedGroup.Count(),
            status);
    }

    private sealed record EarningProjection(
        Guid Id,
        Guid TaskId,
        decimal Amount,
        EarningStatus Status,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc,
        DateTime? PaidAtUtc);

    private sealed record WithdrawalProjection(
        Guid Id,
        Guid TaskId,
        Guid SubtaskId,
        decimal Amount,
        WithdrawalStatus Status,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc,
        DateTime? SettledAtUtc);

    private sealed record SettlementProjection(
        Guid Id,
        decimal Amount,
        SettlementStatus Status,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc,
        DateTime? CompletedAtUtc);

    private sealed record PaymentProjection(
        Guid Id,
        decimal Amount,
        PaymentStatus Status,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc,
        DateTime? SettledAtUtc);

    private sealed record TaskSnapshot(Guid Id, string? ModelUrl);

    private sealed class LedgerProjection
    {
        public required string EntryId { get; init; }

        public required FinanceLedgerEntryKind Kind { get; init; }

        public required string Title { get; init; }

        public string? Detail { get; init; }

        public required decimal Amount { get; init; }

        public required DateTime OccurredAtUtc { get; init; }

        public Guid? TaskId { get; init; }

        public required string Source { get; init; }

        public decimal BalanceAfter { get; set; }
    }
}
