using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IlWorkflow.Api.ExternalIdentity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace IlWorkflow.Api.Hosting;

/// <summary>Validate a provider's token first, then match its configured claim against that provider's master.</summary>
public static class CompanyAuthentication
{
    public static void AddCompanyAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = new DemoMemberDirectory(configuration, environment);
        services.AddSingleton(directory);
        if (!directory.Enabled || directory.Providers.Count == 0) return;
        var key = configuration["ExternalAuth:DevelopmentTokens:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            // Documentation preview requires neither directory tokens nor an external signing secret.
            if (environment.IsDevelopment() && configuration.GetValue<bool>("SwaggerPreview")) return;
            throw new InvalidOperationException("Run ./New-TestAccessToken.ps1 before startup to configure the development signing key in user secrets.");
        }
        var authentication = services.AddAuthentication();
        foreach (var provider in directory.Providers)
        {
            var scheme = "Master-" + provider.Id;
            authentication.AddJwtBearer(scheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = provider.Issuer,
                    ValidateAudience = true, ValidAudience = provider.Audience,
                    ValidateLifetime = true, RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.FromSeconds(30)
                };
                options.Events.OnTokenValidated = context =>
                {
                    // This executes only after signature/issuer/audience/expiry validation succeeds.
                    // Multiple distinct values for a matching claim are ambiguous, not alternate identities.
                    var identities = context.Principal!.FindAll(provider.MatchClaim).Select(x => x.Value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                    var member = identities.Length == 1 && !string.IsNullOrWhiteSpace(identities[0]) ? directory.FindByClaim(provider, identities[0]) : null;
                    if (member is null)
                    {
                        context.Fail("The token does not identify one member of its trusted master provider.");
                        return Task.CompletedTask;
                    }
                    // External employees enter the process only after an internal user starts the workflow.
                    // They receive approval permissions, never Requester/configuration/execute permissions.
                    // Elsa still requires actual task membership/ownership before accepting a decision.
                    // No token-supplied admin/role/permission claim is copied into the application principal.
                    var claims = new List<Claim>
                    {
                        new("sub", member.ParticipantId), new(ClaimTypes.NameIdentifier, member.ParticipantId),
                        new("name", member.DisplayName), new(ClaimTypes.Name, member.DisplayName),
                        new("external_provider", provider.Id), new("external_master_id", member.Id)
                    };
                    if (member.Email is not null) claims.Add(new Claim(ClaimTypes.Email, member.Email));
                    claims.Add(new Claim(ClaimTypes.Role, "Approver"));
                    foreach (var permission in configuration.GetSection("ExternalAuth:RolePermissions:Approver").Get<string[]>() ?? [])
                        claims.Add(new Claim("permissions", permission));
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, ClaimTypes.Name, ClaimTypes.Role));
                    return Task.CompletedTask;
                };
            });
        }
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            // Elsa's existing Jwt-or-ApiKey scheme still handles local accounts and API keys.
            options.ForwardDefaultSelector = context =>
            {
                var header = context.Request.Headers.Authorization.ToString();
                if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
                try
                {
                    var reader = new JwtSecurityTokenHandler();
                    var token = header[7..].Trim();
                    if (!reader.CanReadToken(token)) return null;
                    var issuer = reader.ReadJwtToken(token).Issuer;
                    var provider = directory.Providers.SingleOrDefault(x => x.Issuer == issuer);
                    // Unvalidated issuer is used ONLY to select a validating handler; it never grants identity.
                    return provider is null ? null : "Master-" + provider.Id;
                }
                catch (ArgumentException) { return null; }
            };
        });
    }
}
