using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Elsa.Identity.Contracts;
using Elsa.Identity.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace IlWorkflow.Api.Hosting;

public sealed class ExternalUser
{
    public string Provider { get; set; } = "Company";
    public string Subject { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string[] Roles { get; set; } = [];
}

public static class CompanyAuthentication
{
    public const string DevelopmentIssuer = "ilworkflow-development";
    public const string DevelopmentAudience = "ilworkflow-api";
    public static ExternalUser[] Users(IConfiguration configuration) => configuration.GetSection("ExternalAuth:Users").Get<ExternalUser[]>() ?? [];
    public static bool ProviderEnabled(string provider, IConfiguration configuration, IHostEnvironment environment) => provider switch
    {
        "Development" => environment.IsDevelopment() && configuration.GetValue<bool>("ExternalAuth:DevelopmentTokens:Enabled"),
        "Company" => configuration.GetValue<bool>("ExternalAuth:Enabled"),
        _ => false
    };

    public static void AddCompanyAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var development = ProviderEnabled("Development", configuration, environment);
        var company = ProviderEnabled("Company", configuration, environment);
        if (!development && !company) return;
        var authentication = services.AddAuthentication();
        if (development)
        {
            var key = configuration["ExternalAuth:DevelopmentTokens:SigningKey"];
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                throw new InvalidOperationException("Run ./New-TestAccessToken.ps1 before startup to configure the development signing key in user secrets.");
            authentication.AddJwtBearer("Development", options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = DevelopmentIssuer,
                    ValidateAudience = true, ValidAudience = DevelopmentAudience,
                    ValidateLifetime = true, RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.FromSeconds(30)
                };
                options.Events.OnTokenValidated = context => MapUser(context, "Development", "sub", configuration);
            });
        }
        if (company)
        {
            var authority = configuration["ExternalAuth:Authority"];
            var audience = configuration["ExternalAuth:Audience"];
            if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrWhiteSpace(audience))
                throw new InvalidOperationException("ExternalAuth requires an HTTPS Authority and the registered API Audience.");
            authentication.AddJwtBearer("Company", options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.Events.OnTokenValidated = context => MapUser(context, "Company", configuration["ExternalAuth:SubjectClaim"] ?? "sub", configuration);
            });
        }
        // Elsa's Jwt-or-ApiKey scheme forwards JWTs to Bearer. Preserve local JWT/API-key handling.
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
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
                    // Unvalidated issuer selects a handler only; that handler must validate the whole token.
                    if (development && issuer == DevelopmentIssuer) return "Development";
                    var localIssuer = configuration["Identity:Tokens:Issuer"] ?? "http://elsa.api";
                    if (company && issuer != localIssuer) return "Company";
                }
                catch (ArgumentException) { }
                return null;
            };
        });
    }

    private static async Task MapUser(TokenValidatedContext context, string provider, string subjectClaim, IConfiguration configuration)
    {
        var subject = context.Principal?.FindFirstValue(subjectClaim);
        var matches = Users(configuration).Where(user => user.Provider == provider && user.Subject == subject && user.Enabled).ToArray();
        if (string.IsNullOrWhiteSpace(subject) || matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Id))
        {
            context.Fail("External subject is not registered or is disabled.");
            return;
        }
        var user = matches[0];
        var allUsers = Users(configuration);
        var localUser = await context.HttpContext.RequestServices.GetRequiredService<IUserProvider>()
            .FindAsync(new UserFilter { Id = user.Id }, context.HttpContext.RequestAborted);
        if (allUsers.Count(candidate => candidate.Id == user.Id) != 1 || localUser is not null)
        {
            context.Fail("External user ID must be unique and cannot overlap a local user.");
            return;
        }
        // Construct a clean principal: do not trust upstream admin/permissions/provider/tenant claims.
        var claims = new List<Claim>
        {
            new("sub", user.Id), new(ClaimTypes.NameIdentifier, user.Id),
            new("name", user.Name), new(ClaimTypes.Name, user.Name)
        };
        foreach (var role in user.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            foreach (var permission in configuration.GetSection($"ExternalAuth:RolePermissions:{role}").Get<string[]>() ?? [])
                claims.Add(new Claim("permissions", permission));
        }
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, provider, ClaimTypes.Name, ClaimTypes.Role));
    }
}
