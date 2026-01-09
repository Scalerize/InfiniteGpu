using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Generic;

namespace InfiniteGPU.Backend.Data.Entities;

/// <summary>
/// Owned entity representing an address for a user
/// </summary>
public class UserAddress
{
    [Column(TypeName = "nvarchar(256)")]
    public string? Line1 { get; set; }

    [Column(TypeName = "nvarchar(256)")]
    public string? Line2 { get; set; }

    [Column(TypeName = "nvarchar(128)")]
    public string? City { get; set; }

    [Column(TypeName = "nvarchar(128)")]
    public string? State { get; set; }

    [Column(TypeName = "nvarchar(20)")]
    public string? PostalCode { get; set; }

    [Column(TypeName = "nvarchar(2)")]
    public string? Country { get; set; }
}

public class ApplicationUser : IdentityUser
{
    [Column(TypeName = "nvarchar(100)")]
    public string? FirstName { get; set; }

    [Column(TypeName = "nvarchar(100)")]
    public string? LastName { get; set; }

    [Column(TypeName = "nvarchar(20)")]
    public string? Phone { get; set; }

    [Column(TypeName = "date")]
    public DateOnly? DateOfBirth { get; set; }

    public bool IsActive { get; set; } = true;

    [Column(TypeName = "nvarchar(max)")]
    public string? ResourceCapabilities { get; set; }

    [Column(TypeName = "decimal(18,6)")]
    public decimal Balance { get; set; } = 0m;

    [Column(TypeName = "nvarchar(255)")]
    public string? StripeConnectedAccountId { get; set; }

    [Column(TypeName = "nvarchar(255)")]
    public string? StripeExternalAccountId { get; set; }

    [Column(TypeName = "nvarchar(2)")]
    public string? Country { get; set; }

    /// <summary>
    /// User's address for settlement/payout purposes
    /// </summary>
    public UserAddress? Address { get; set; }

    public virtual ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}