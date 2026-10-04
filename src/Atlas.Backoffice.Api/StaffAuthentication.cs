using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Atlas.Backoffice.Api;

public static class StaffRoles
{
    public const string ComplianceOfficer = "ComplianceOfficer";
    public const string BranchStaff = "BranchStaff";
    public const string MarketClaim = "atlas:market";
}

public sealed class StaffMember
{
    public string Id { get; set; } = default!;
    public string Role { get; set; } = default!;
    public string Market { get; set; } = default!;
}

public sealed class StaffDirectoryOptions
{
    public List<StaffMember> Staff { get; set; } = [];
}

/// <summary>
/// LOCAL DEVELOPMENT ONLY. Authenticates a member of staff from an <c>X-Staff-Id</c> header checked
/// against a configured directory.
///
/// It exists so the rest of the back office can be written against real claims — a named individual,
/// a role, and the market they work in — which is what GC §2 needs. In production this scheme is
/// replaced by the bank's identity provider (OIDC/JWT bearer); no endpoint code changes, because the
/// endpoints only read <see cref="ClaimsPrincipal"/>.
/// </summary>
public sealed class DevStaffAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<StaffDirectoryOptions> directory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevStaffHeader";
    public const string Header = "X-Staff-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var staffId = Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(staffId)) return Task.FromResult(AuthenticateResult.NoResult());

        var member = directory.Value.Staff.FirstOrDefault(s => string.Equals(s.Id, staffId, StringComparison.OrdinalIgnoreCase));
        if (member is null) return Task.FromResult(AuthenticateResult.Fail("Unknown staff member."));

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, member.Id),
            new Claim(ClaimTypes.Role, member.Role),
            new Claim(StaffRoles.MarketClaim, member.Market.ToUpperInvariant()),
        }, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class StaffPrincipalExtensions
{
    public static string StaffId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No staff id claim.");

    public static string StaffMarket(this ClaimsPrincipal user) =>
        user.FindFirstValue(StaffRoles.MarketClaim) ?? throw new InvalidOperationException("No market claim.");
}
