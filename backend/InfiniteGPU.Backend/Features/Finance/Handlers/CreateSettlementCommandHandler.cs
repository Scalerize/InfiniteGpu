using InfiniteGPU.Backend.Data;
using InfiniteGPU.Backend.Data.Entities;
using InfiniteGPU.Backend.Features.Finance.Commands;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Stripe;
using System.Text.Json;

namespace InfiniteGPU.Backend.Features.Finance.Handlers;

public sealed class CreateSettlementCommandHandler : IRequestHandler<CreateSettlementCommand, CreateSettlementResult>
{
    private const decimal MinimumSettlementAmount = 30m;
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CreateSettlementCommandHandler> _logger;

    public CreateSettlementCommandHandler(
        AppDbContext context,
        IConfiguration configuration,
        ILogger<CreateSettlementCommandHandler> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CreateSettlementResult> Handle(CreateSettlementCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate amount
            if (request.Amount < MinimumSettlementAmount)
            {
                return new CreateSettlementResult(false, null, $"Minimum settlement amount is ${MinimumSettlementAmount}");
            }

            // Get user
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

            if (user == null)
            {
                return new CreateSettlementResult(false, null, "User not found");
            }

            // Check sufficient balance
            if (user.Balance < request.Amount)
            {
                return new CreateSettlementResult(false, null, $"Insufficient balance. Available: ${user.Balance:F2}, Required: ${request.Amount:F2}");
            }

            // Initialize Stripe
            var stripeSecretKey = _configuration["Stripe:SecretKey"];
            if (string.IsNullOrWhiteSpace(stripeSecretKey))
            {
                _logger.LogError("Stripe secret key not configured");
                return new CreateSettlementResult(false, null, "Payment system not configured");
            }

            StripeConfiguration.ApiKey = stripeSecretKey;

            // Check if user wants to use existing account
            if (request.UseExistingAccount)
            {
                // Verify user has existing accounts
                if (string.IsNullOrEmpty(user.StripeConnectedAccountId) || string.IsNullOrEmpty(user.StripeExternalAccountId))
                {
                    return new CreateSettlementResult(false, null, "No existing payment account configured. Please provide bank account details.");
                }

                _logger.LogInformation(
                    "Using existing Stripe accounts for user {UserId}: ConnectedAccount={ConnectedAccountId}, ExternalAccount={ExternalAccountId}",
                    request.UserId, user.StripeConnectedAccountId, user.StripeExternalAccountId);

                return await ProcessSettlementWithExistingAccounts(user, request.Amount, cancellationToken);
            }

            // Parse bank account details with case-insensitive deserialization for new account setup
            // If bank details are empty but user has existing accounts, fallback to using existing
            if (string.IsNullOrWhiteSpace(request.BankAccountDetails))
            {
                if (!string.IsNullOrEmpty(user.StripeConnectedAccountId) && !string.IsNullOrEmpty(user.StripeExternalAccountId))
                {
                    _logger.LogInformation(
                        "Bank account details not provided, falling back to existing accounts for user {UserId}",
                        request.UserId);
                    return await ProcessSettlementWithExistingAccounts(user, request.Amount, cancellationToken);
                }
                return new CreateSettlementResult(false, null, "Bank account details are required for new account setup");
            }

            BankAccountInfo? bankInfo;
            try
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                bankInfo = JsonSerializer.Deserialize<BankAccountInfo>(request.BankAccountDetails, jsonOptions);
                if (bankInfo == null)
                {
                    return new CreateSettlementResult(false, null, "Invalid bank account details");
                }

                // Log the parsed values for debugging
                _logger.LogInformation(
                    "Parsed bank account details - IsEU: {IsEU}, HasIban: {HasIban}, IbanLength: {IbanLength}",
                    IsEuropeanUnionCountry(request.Country ?? ""),
                    !string.IsNullOrWhiteSpace(bankInfo.Iban),
                    bankInfo.Iban?.Length ?? 0);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse bank account details: {Details}", request.BankAccountDetails);
                return new CreateSettlementResult(false, null, "Invalid bank account details format");
            }

            // Validate country is provided for new accounts
            if (string.IsNullOrWhiteSpace(request.Country))
            {
                return new CreateSettlementResult(false, null, "Country is required for new account setup");
            }

            // Create settlement entity with Pending status (not yet processing)
            var settlement = new Settlement
            {
                Id = Guid.NewGuid(),
                UserId = request.UserId,
                Amount = request.Amount,
                Status = SettlementStatus.Pending,
                BankAccountDetails = request.BankAccountDetails,
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.Settlements.Add(settlement);
            await _context.SaveChangesAsync(cancellationToken);

            // Now perform Stripe operations (without deducting balance yet)
            try
            {
                // Determine which capabilities to request based on country
                var isEuCountry = IsEuropeanUnionCountry(request.Country);
                var isUsCountry = request.Country.Equals("US", StringComparison.OrdinalIgnoreCase);

                // Create bank account token FIRST
                _logger.LogInformation("Creating bank account token for user {UserId}", request.UserId);

                string bankAccountToken;
                string accountTokenId;
                try
                {
                    var tokenService = new TokenService();
                    TokenCreateOptions tokenOptions;

                    if (isUsCountry && !string.IsNullOrWhiteSpace(bankInfo.AccountNumber) && !string.IsNullOrWhiteSpace(bankInfo.RoutingNumber))
                    {
                        tokenOptions = new TokenCreateOptions
                        {
                            BankAccount = new TokenBankAccountOptions
                            {
                                Country = "US",
                                Currency = "usd",
                                AccountHolderName = bankInfo.AccountHolderName,
                                AccountHolderType = "individual",
                                RoutingNumber = bankInfo.RoutingNumber,
                                AccountNumber = bankInfo.AccountNumber
                            }
                        };
                    }
                    else if (isEuCountry && !string.IsNullOrWhiteSpace(bankInfo.Iban))
                    {
                        tokenOptions = new TokenCreateOptions
                        {
                            BankAccount = new TokenBankAccountOptions
                            {
                                Country = request.Country.ToUpperInvariant(),
                                Currency = "eur",
                                AccountHolderName = bankInfo.AccountHolderName,
                                AccountHolderType = "individual",
                                AccountNumber = bankInfo.Iban
                            }
                        };
                    }
                    else
                    {
                        settlement.Status = SettlementStatus.Failed;
                        settlement.FailureReason = "Invalid bank account details for country";
                        settlement.UpdatedAtUtc = DateTime.UtcNow;
                        await _context.SaveChangesAsync(cancellationToken);

                        return new CreateSettlementResult(false, null, "Invalid bank account details for the selected country");
                    }

                    var token = await tokenService.CreateAsync(tokenOptions, cancellationToken: cancellationToken);
                    bankAccountToken = token.Id;

                    _logger.LogInformation("Created bank account token {TokenId}", bankAccountToken);

                    // Create Account Token (required for FR platforms)
                    // Include individual representative info and business type
                    var individualOptions = new TokenAccountIndividualOptions
                    {
                        FirstName = request.FirstName,
                        LastName = request.LastName,
                        Email = user.Email,
                        Phone = request.Phone,
                        Dob = request.DateOfBirth.HasValue
                            ? new DobOptions
                            {
                                Day = request.DateOfBirth.Value.Day,
                                Month = request.DateOfBirth.Value.Month,
                                Year = request.DateOfBirth.Value.Year
                            }
                            : null
                    };

                    // Add address if provided
                    if (!string.IsNullOrWhiteSpace(request.AddressLine1))
                    {
                        individualOptions.Address = new AddressOptions
                        {
                            Line1 = request.AddressLine1,
                            Line2 = request.AddressLine2,
                            City = request.City,
                            State = request.State,
                            PostalCode = request.PostalCode,
                            Country = request.AddressCountry ?? request.Country?.ToUpperInvariant()
                        };
                    }

                    var accountTokenOptions = new TokenCreateOptions
                    {
                        Account = new TokenAccountOptions
                        {
                            BusinessType = "individual",
                            TosShownAndAccepted = true,
                            Individual = individualOptions
                        }
                    };
                    var accountToken = await tokenService.CreateAsync(accountTokenOptions, cancellationToken: cancellationToken);
                    accountTokenId = accountToken.Id;
                    _logger.LogInformation("Created account token {TokenId} for {FirstName} {LastName}", accountTokenId, request.FirstName, request.LastName);
                }
                catch (StripeException stripeEx)
                {
                    _logger.LogError(stripeEx, "Failed to create tokens");

                    settlement.Status = SettlementStatus.Failed;
                    settlement.FailureReason = $"Failed to create payment tokens: {stripeEx.Message}";
                    settlement.UpdatedAtUtc = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);

                    return new CreateSettlementResult(false, null, $"Failed to create payment tokens: {stripeEx.Message}");
                }

                var accountService = new AccountService();
                string connectedAccountId;
                string externalAccountId;

                // Check if user has a connected account
                if (string.IsNullOrEmpty(user.StripeConnectedAccountId))
                {
                    _logger.LogInformation("Creating Stripe Connect account for user {UserId} in country {Country}", request.UserId, request.Country);

                    // Note: BusinessType and TosAcceptance are provided via the account token
                    // and cannot be set directly on AccountCreateOptions when using an account token
                    var accountOptions = new AccountCreateOptions
                    {
                        Type = "custom", // Always Custom as requested
                        Email = user.Email,
                        Country = request.Country.ToUpperInvariant(),
                        Capabilities = new AccountCapabilitiesOptions
                        {
                            Transfers = new AccountCapabilitiesTransfersOptions { Requested = true },
                            CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true }
                        },
                        BusinessProfile = new AccountBusinessProfileOptions
                        {
                            Url = "https://infinite-gpu.scalerize.fr",
                            Mcc = request.Mcc ?? "5817" // 5817 = Digital Goods: Software Applications
                        },
                        ExternalAccount = bankAccountToken,
                        AccountToken = accountTokenId
                    };

                    // Add country-specific capabilities
                    if (isUsCountry)
                    {
                        accountOptions.Capabilities.UsBankAccountAchPayments = new AccountCapabilitiesUsBankAccountAchPaymentsOptions { Requested = true };
                    }
                    else if (isEuCountry)
                    {
                        accountOptions.Capabilities.SepaDebitPayments = new AccountCapabilitiesSepaDebitPaymentsOptions { Requested = true };
                    }

                    Account account;
                    try
                    {
                        account = await accountService.CreateAsync(accountOptions, cancellationToken: cancellationToken);
                        connectedAccountId = account.Id;
                        connectedAccountId = account.Id;
                        user.StripeConnectedAccountId = connectedAccountId;
                        user.Country = request.Country; // Store country for future settlements

                        // Get the external account ID from the created account
                        if (account.ExternalAccounts?.Data != null && account.ExternalAccounts.Data.Any())
                        {
                            externalAccountId = account.ExternalAccounts.Data.First().Id;
                            user.StripeExternalAccountId = externalAccountId; // Store for future settlements
                        }
                        else
                        {
                            // Fallback if for some reason it wasn't added (shouldn't happen if token is valid)
                            throw new StripeException("External account was not added during creation.");
                        }

                        await _context.SaveChangesAsync(cancellationToken);

                        _logger.LogInformation(
                            "Created Stripe Connect account {AccountId} for user {UserId} in country {Country}",
                            account.Id, request.UserId, request.Country);
                    }
                    catch (StripeException stripeEx)
                    {
                        _logger.LogError(stripeEx, "Failed to create Stripe Connect account for user {UserId} in country {Country}", request.UserId, request.Country);

                        settlement.Status = SettlementStatus.Failed;
                        settlement.FailureReason = $"Failed to create payment account: {stripeEx.Message}";
                        settlement.UpdatedAtUtc = DateTime.UtcNow;
                        await _context.SaveChangesAsync(cancellationToken);

                        return new CreateSettlementResult(false, null, $"Failed to create payment account: {stripeEx.Message}");
                    }
                }
                else
                {
                    connectedAccountId = user.StripeConnectedAccountId;

                    // Retrieve account to check capabilities and ensure they are enabled
                    try
                    {
                        var account = await accountService.GetAsync(connectedAccountId, cancellationToken: cancellationToken);

                        var updateOptions = new AccountUpdateOptions
                        {
                            Capabilities = new AccountCapabilitiesOptions()
                        };
                        bool needsUpdate = false;

                        // Check Transfers capability
                        if (account.Capabilities?.Transfers != "active" && account.Capabilities?.Transfers != "pending")
                        {
                            updateOptions.Capabilities.Transfers = new AccountCapabilitiesTransfersOptions { Requested = true };
                            needsUpdate = true;
                        }

                        // Check CardPayments capability
                        if (account.Capabilities?.CardPayments != "active" && account.Capabilities?.CardPayments != "pending")
                        {
                            updateOptions.Capabilities.CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true };
                            needsUpdate = true;
                        }

                        // Check country-specific capabilities
                        if (isUsCountry && account.Capabilities?.UsBankAccountAchPayments != "active" && account.Capabilities?.UsBankAccountAchPayments != "pending")
                        {
                            updateOptions.Capabilities.UsBankAccountAchPayments = new AccountCapabilitiesUsBankAccountAchPaymentsOptions { Requested = true };
                            needsUpdate = true;
                        }
                        else if (isEuCountry && account.Capabilities?.SepaDebitPayments != "active" && account.Capabilities?.SepaDebitPayments != "pending")
                        {
                            updateOptions.Capabilities.SepaDebitPayments = new AccountCapabilitiesSepaDebitPaymentsOptions { Requested = true };
                            needsUpdate = true;
                        }

                        // Ensure TOS acceptance is recorded if missing (required for Custom accounts)
                        // Only allowed for Custom accounts where the platform handles onboarding
                        if (account.Type == "custom" && account.TosAcceptance?.Date == null)
                        {
                            updateOptions.TosAcceptance = new AccountTosAcceptanceOptions
                            {
                                Date = DateTime.UtcNow,
                                Ip = request.IpAddress ?? "127.0.0.1"
                            };
                            needsUpdate = true;
                        }

                        if (needsUpdate)
                        {
                            _logger.LogInformation("Updating capabilities/TOS for existing account {AccountId}", connectedAccountId);
                            await accountService.UpdateAsync(connectedAccountId, updateOptions, cancellationToken: cancellationToken);
                        }
                    }
                    catch (StripeException stripeEx)
                    {
                        _logger.LogError(stripeEx, "Failed to update existing Stripe account {AccountId}", connectedAccountId);
                        // Continue, as it might work or fail later with a specific error
                    }

                    // Add bank account to connected account
                    _logger.LogInformation(
                        "Adding external bank account to connected account {AccountId}",
                        connectedAccountId);

                    try
                    {
                        // Update the connected account with the external account
                        var accountUpdateOptions = new AccountUpdateOptions
                        {
                            ExternalAccount = bankAccountToken
                        };

                        var updatedAccount = await accountService.UpdateAsync(
                            connectedAccountId,
                            accountUpdateOptions,
                            cancellationToken: cancellationToken);

                        // Get the ID of the newly added external account
                        if (updatedAccount.ExternalAccounts?.Data != null && updatedAccount.ExternalAccounts.Data.Any())
                        {
                            externalAccountId = updatedAccount.ExternalAccounts.Data.Last().Id;
                            user.StripeExternalAccountId = externalAccountId; // Update for future settlements
                            await _context.SaveChangesAsync(cancellationToken);
                        }
                        else
                        {
                            throw new InvalidOperationException("No external account was added to the connected account");
                        }

                        _logger.LogInformation(
                            "Added external bank account {BankAccountId} to connected account {AccountId}",
                            externalAccountId, connectedAccountId);
                    }
                    catch (StripeException stripeEx)
                    {
                        _logger.LogError(stripeEx, "Failed to add external bank account to connected account {AccountId}", connectedAccountId);

                        settlement.Status = SettlementStatus.Failed;
                        settlement.FailureReason = $"Failed to add bank account: {stripeEx.Message}";
                        settlement.UpdatedAtUtc = DateTime.UtcNow;
                        await _context.SaveChangesAsync(cancellationToken);

                        return new CreateSettlementResult(false, null, $"Failed to add bank account: {stripeEx.Message}");
                    }
                }

                // Create transfer to connected account (not payout, as the account needs balance first)
                _logger.LogInformation(
                    "Creating transfer to connected account {AccountId}",
                    connectedAccountId);

                var transferService = new TransferService();
                var transferOptions = new TransferCreateOptions
                {
                    Amount = (long)(request.Amount * 100), // Convert to cents
                    Currency = "usd",
                    Destination = connectedAccountId,
                    Metadata = new Dictionary<string, string>
                    {
                        { "user_id", request.UserId },
                        { "settlement_id", settlement.Id.ToString() },
                        { "bank_account_holder", bankInfo.AccountHolderName }
                    },
                    Description = $"Settlement transfer for user {request.UserId}"
                };

                Transfer transfer;
                try
                {
                    transfer = await transferService.CreateAsync(transferOptions, cancellationToken: cancellationToken);

                    _logger.LogInformation(
                        "Created transfer {TransferId} for settlement {SettlementId}",
                        transfer.Id, settlement.Id);
                }
                catch (StripeException stripeEx)
                {
                    _logger.LogError(stripeEx, "Stripe transfer failed for settlement {SettlementId}", settlement.Id);

                    string failureReason = stripeEx.Message;
                    if (stripeEx.StripeError?.Code == "account_missing_capabilities" || stripeEx.Message.Contains("capabilities enabled"))
                    {
                        failureReason = "Stripe account requires onboarding. Please complete the verification process sent to your email.";
                    }

                    // Mark settlement as failed (no balance was deducted yet)
                    settlement.Status = SettlementStatus.Failed;
                    settlement.FailureReason = failureReason;
                    settlement.UpdatedAtUtc = DateTime.UtcNow;

                    await _context.SaveChangesAsync(cancellationToken);

                    return new CreateSettlementResult(false, null, $"Transfer failed: {failureReason}");
                }

                // Now create payout from connected account to bank
                _logger.LogInformation(
                    "Creating payout from connected account {AccountId} to bank account {BankAccountId}",
                    connectedAccountId, externalAccountId);

                var payoutService = new PayoutService();
                var payoutOptions = new PayoutCreateOptions
                {
                    Amount = (long)(request.Amount * 100), // Convert to cents
                    Currency = "usd",
                    Destination = externalAccountId,
                    Metadata = new Dictionary<string, string>
                    {
                        { "user_id", request.UserId },
                        { "settlement_id", settlement.Id.ToString() },
                        { "transfer_id", transfer.Id }
                    },
                    Description = $"Settlement payout for user {request.UserId}"
                };

                Payout payout;
                try
                {
                    // Create payout on behalf of the connected account
                    var payoutRequestOptions = new RequestOptions
                    {
                        StripeAccount = connectedAccountId
                    };

                    payout = await payoutService.CreateAsync(payoutOptions, payoutRequestOptions, cancellationToken);

                    _logger.LogInformation(
                        "Created payout {PayoutId} for settlement {SettlementId}",
                        payout.Id, settlement.Id);
                }
                catch (StripeException stripeEx)
                {
                    _logger.LogError(stripeEx, "Stripe payout failed for settlement {SettlementId}", settlement.Id);

                    // Mark settlement as failed (no balance was deducted yet)
                    settlement.Status = SettlementStatus.Failed;
                    settlement.FailureReason = stripeEx.Message;
                    settlement.UpdatedAtUtc = DateTime.UtcNow;

                    await _context.SaveChangesAsync(cancellationToken);

                    return new CreateSettlementResult(false, null, $"Payout failed: {stripeEx.Message}");
                }

                // SUCCESS: Now deduct balance, save user info, and update settlement
                // Use a transaction to ensure atomicity
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    user.Balance -= request.Amount;

                    // Save user info for future settlements
                    user.FirstName = request.FirstName;
                    user.LastName = request.LastName;
                    user.Phone = request.Phone;
                    user.DateOfBirth = request.DateOfBirth;

                    // Save address if provided
                    if (!string.IsNullOrWhiteSpace(request.AddressLine1))
                    {
                        user.Address = new UserAddress
                        {
                            Line1 = request.AddressLine1,
                            Line2 = request.AddressLine2,
                            City = request.City,
                            State = request.State,
                            PostalCode = request.PostalCode,
                            Country = request.AddressCountry ?? request.Country?.ToUpperInvariant()
                        };
                    }

                    settlement.StripeTransferId = payout.Id;
                    settlement.Status = SettlementStatus.Processing;
                    settlement.UpdatedAtUtc = DateTime.UtcNow;

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (Exception dbEx)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _logger.LogError(dbEx, "Failed to update database after successful Stripe payout for settlement {SettlementId}", settlement.Id);

                    // Payout was created but we couldn't update the database
                    // This is a critical error that needs manual intervention
                    return new CreateSettlementResult(false, null, "Settlement processing error. Please contact support.");
                }

                _logger.LogInformation(
                    "Settlement created successfully. UserId: {UserId}, Amount: {Amount}, SettlementId: {SettlementId}, StripePayoutId: {PayoutId}",
                    request.UserId, request.Amount, settlement.Id, payout.Id);

                return new CreateSettlementResult(true, settlement.Id.ToString(), null);
            }
            catch (StripeException stripeEx)
            {
                _logger.LogError(stripeEx, "Stripe error during settlement processing for settlement {SettlementId}", settlement.Id);

                // Mark settlement as failed (balance was never deducted)
                settlement.Status = SettlementStatus.Failed;
                settlement.FailureReason = stripeEx.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                return new CreateSettlementResult(false, null, $"Settlement processing failed: {stripeEx.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during settlement processing for settlement {SettlementId}", settlement.Id);

                // Mark settlement as failed (balance was never deducted)
                settlement.Status = SettlementStatus.Failed;
                settlement.FailureReason = ex.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                return new CreateSettlementResult(false, null, $"Settlement processing failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating settlement for user {UserId}", request.UserId);
            return new CreateSettlementResult(false, null, $"Settlement creation failed: {ex.Message}");
        }
    }

    private async Task<CreateSettlementResult> ProcessSettlementWithExistingAccounts(
        ApplicationUser user,
        decimal amount,
        CancellationToken cancellationToken)
    {
        var connectedAccountId = user.StripeConnectedAccountId!;
        var externalAccountId = user.StripeExternalAccountId!;

        // Create settlement entity with Pending status
        var settlement = new Settlement
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Amount = amount,
            Status = SettlementStatus.Pending,
            BankAccountDetails = "Using existing account", // No new bank details provided
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.Settlements.Add(settlement);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            // Create transfer to connected account
            _logger.LogInformation(
                "Creating transfer to existing connected account {AccountId}",
                connectedAccountId);

            var transferService = new TransferService();
            var transferOptions = new TransferCreateOptions
            {
                Amount = (long)(amount * 100), // Convert to cents
                Currency = "usd",
                Destination = connectedAccountId,
                Metadata = new Dictionary<string, string>
                {
                    { "user_id", user.Id },
                    { "settlement_id", settlement.Id.ToString() },
                    { "using_existing_account", "true" }
                },
                Description = $"Settlement transfer for user {user.Id}"
            };

            Transfer transfer;
            try
            {
                transfer = await transferService.CreateAsync(transferOptions, cancellationToken: cancellationToken);

                _logger.LogInformation(
                    "Created transfer {TransferId} for settlement {SettlementId}",
                    transfer.Id, settlement.Id);
            }
            catch (StripeException stripeEx)
            {
                _logger.LogError(stripeEx, "Stripe transfer failed for settlement {SettlementId}", settlement.Id);

                settlement.Status = SettlementStatus.Failed;
                settlement.FailureReason = stripeEx.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                return new CreateSettlementResult(false, null, $"Transfer failed: {stripeEx.Message}");
            }

            // Create payout from connected account to bank
            _logger.LogInformation(
                "Creating payout from connected account {AccountId} to existing bank account {BankAccountId}",
                connectedAccountId, externalAccountId);

            var payoutService = new PayoutService();
            var payoutOptions = new PayoutCreateOptions
            {
                Amount = (long)(amount * 100), // Convert to cents
                Currency = "usd",
                Destination = externalAccountId,
                Metadata = new Dictionary<string, string>
                {
                    { "user_id", user.Id },
                    { "settlement_id", settlement.Id.ToString() },
                    { "transfer_id", transfer.Id }
                },
                Description = $"Settlement payout for user {user.Id}"
            };

            Payout payout;
            try
            {
                var payoutRequestOptions = new RequestOptions
                {
                    StripeAccount = connectedAccountId
                };

                payout = await payoutService.CreateAsync(payoutOptions, payoutRequestOptions, cancellationToken);

                _logger.LogInformation(
                    "Created payout {PayoutId} for settlement {SettlementId}",
                    payout.Id, settlement.Id);
            }
            catch (StripeException stripeEx)
            {
                _logger.LogError(stripeEx, "Stripe payout failed for settlement {SettlementId}", settlement.Id);

                settlement.Status = SettlementStatus.Failed;
                settlement.FailureReason = stripeEx.Message;
                settlement.UpdatedAtUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                return new CreateSettlementResult(false, null, $"Payout failed: {stripeEx.Message}");
            }

            // SUCCESS: Deduct balance and update settlement
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                user.Balance -= amount;

                settlement.StripeTransferId = payout.Id;
                settlement.Status = SettlementStatus.Processing;
                settlement.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception dbEx)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(dbEx, "Failed to update database after successful Stripe payout for settlement {SettlementId}", settlement.Id);

                return new CreateSettlementResult(false, null, "Settlement processing error. Please contact support.");
            }

            _logger.LogInformation(
                "Settlement created successfully using existing account. UserId: {UserId}, Amount: {Amount}, SettlementId: {SettlementId}, StripePayoutId: {PayoutId}",
                user.Id, amount, settlement.Id, payout.Id);

            return new CreateSettlementResult(true, settlement.Id.ToString(), null);
        }
        catch (StripeException stripeEx)
        {
            _logger.LogError(stripeEx, "Stripe error during settlement processing for settlement {SettlementId}", settlement.Id);

            settlement.Status = SettlementStatus.Failed;
            settlement.FailureReason = stripeEx.Message;
            settlement.UpdatedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            return new CreateSettlementResult(false, null, $"Settlement processing failed: {stripeEx.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during settlement processing for settlement {SettlementId}", settlement.Id);

            settlement.Status = SettlementStatus.Failed;
            settlement.FailureReason = ex.Message;
            settlement.UpdatedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            return new CreateSettlementResult(false, null, $"Settlement processing failed: {ex.Message}");
        }
    }

    private static bool IsEuropeanUnionCountry(string countryCode)
    {
        // List of EU countries (ISO 3166-1 alpha-2 codes)
        var euCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
            "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
            "PL", "PT", "RO", "SK", "SI", "ES", "SE"
        };

        return euCountries.Contains(countryCode);
    }

    private sealed class BankAccountInfo
    {
        public string BankName { get; set; } = string.Empty;
        public string AccountHolderName { get; set; } = string.Empty;

        // US ACH fields
        public string? AccountNumber { get; set; }
        public string? RoutingNumber { get; set; }

        // SEPA fields (EU)
        public string? Iban { get; set; }
        public string? Bic { get; set; }
    }
}