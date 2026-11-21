using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Elsa.Identity.Contracts;
using Elsa.Identity.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace IlWorkflow.Infrastructure.Authentication;

/// <summary>
/// Accepts an HS256 token signed with UserToken:SigningKey whose "uid" claim is an Elsa Identity user ID.
/// The token only says WHO the caller is. Roles and permissions always come from that Elsa user;
/// claims such as "admin" or "roles" inside the token are ignored.
/// </summary>
public static class UserTokenAuthentication
{
    public const string Scheme = "UserToken";
    private const string UserIdClaim = "uid";

    public static void AddUserTokenAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("UserToken");
        var key = section["SigningKey"];
        if (string.IsNullOrWhiteSpace(key)) return;
        if (Encoding.UTF8.GetByteCount(key) < 32) throw new InvalidOperationException("UserToken:SigningKey must be at least 32 bytes for HS256.");

        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuer = !string.IsNullOrWhiteSpace(section["Issuer"]), ValidIssuer = section["Issuer"],
                ValidateAudience = !string.IsNullOrWhiteSpace(section["Audience"]), ValidAudience = section["Audience"],
                // Tokens without "exp" never expire. Keep this true outside quick local tests.
                RequireExpirationTime = section.GetValue("RequireExpiration", true), ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var services = context.HttpContext.RequestServices;
                    var ct = context.HttpContext.RequestAborted;
                    var uid = context.Principal!.FindFirst(UserIdClaim)?.Value;
                    var user = string.IsNullOrWhiteSpace(uid) ? null : await services.GetRequiredService<IUserProvider>().FindAsync(new UserFilter { Id = uid }, ct);
                    if (user is null) { context.Fail("Token uid does not match an Elsa user."); return; }
                    var roles = await services.GetRequiredService<IRoleProvider>().FindManyAsync(new RoleFilter { Ids = user.Roles }, ct);
                    var claims = new List<Claim>
                    {
                        new("sub", user.Id), new(ClaimTypes.NameIdentifier, user.Id),
                        new("name", user.Name), new(ClaimTypes.Name, user.Name)
                    };
                    foreach (var role in user.Roles) claims.Add(new(ClaimTypes.Role, role));
                    foreach (var permission in roles.SelectMany(x => x.Permissions).Distinct(StringComparer.Ordinal)) claims.Add(new("permissions", permission));
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
                }
            };
        });

        // Tokens carrying "uid" go to this scheme; everything else stays with Elsa's local handler.
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var previous = options.ForwardDefaultSelector;
            options.ForwardDefaultSelector = context =>
            {
                var header = context.Request.Headers.Authorization.ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var handler = new JwtSecurityTokenHandler();
                    var raw = header[7..].Trim();
                    try { if (handler.CanReadToken(raw) && handler.ReadJwtToken(raw).Claims.Any(x => x.Type == UserIdClaim)) return Scheme; }
                    catch (ArgumentException) { }
                }
                return previous?.Invoke(context);
            };
        });
    }
}
