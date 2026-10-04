using Atlas.Backoffice.Api;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Web;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAtlasCore("backoffice-api");
builder.Services.AddOptions<StaffDirectoryOptions>().BindConfiguration("StaffDirectory");

// Swap for AddJwtBearer(...) against the bank's identity provider in any real environment.
builder.Services.AddAuthentication(DevStaffAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevStaffAuthenticationHandler>(DevStaffAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(StaffRoles.ComplianceOfficer, p => p.RequireRole(StaffRoles.ComplianceOfficer).RequireClaim(StaffRoles.MarketClaim))
    .AddPolicy(StaffRoles.BranchStaff, p => p.RequireRole(StaffRoles.BranchStaff).RequireClaim(StaffRoles.MarketClaim));

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapBackofficeEndpoints();

app.Run();
