using System.Security.Claims;
using Elsa.Authorization;

namespace IlWorkflow.Infrastructure.Configuration;

public static class ConfigurationAccess
{
    public static bool HasPermission(ClaimsPrincipal user, string resource, string verb) =>
        user.FindAll("permissions").Any(claim => Permission.TryParse(claim.Value, out var grant) && PermissionMatcher.Satisfies(grant, new Permission(resource, verb)));
    public static bool IsConfigurator(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && HasPermission(user, "workflows/definitions", "write");
    public static bool IsSupervisor(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true &&
        (user.IsInRole("admin") || user.IsInRole("Administrator") || HasPermission(user, "user-tasks", "supervise"));
}

