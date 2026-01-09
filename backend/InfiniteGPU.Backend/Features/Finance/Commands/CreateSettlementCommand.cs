namespace InfiniteGPU.Backend.Features.Finance.Commands;

public sealed record CreateSettlementCommand(
    string UserId,
    decimal Amount,
    string? Country,
    string? BankAccountDetails,
    string? IpAddress,
    string? FirstName,
    string? LastName,
    string? Phone,
    DateOnly? DateOfBirth,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? AddressCountry,
    string? Mcc = "5817",
    bool UseExistingAccount = false) : MediatR.IRequest<CreateSettlementResult>;

public sealed record CreateSettlementResult(
    bool Success,
    string? SettlementId,
    string? ErrorMessage,
    bool RequiresIdentityVerification = false,
    string? IdentityVerificationUrl = null);