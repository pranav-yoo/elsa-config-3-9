using Elsa.Identity.Contracts;
using Elsa.Identity.Models;
using IlWorkflow.Api.Hosting;

namespace IlWorkflow.Api.Tickets;

// JSON is the research directory; production can query the application's employee directory here.
public sealed class TicketIdentity(IUserProvider localUsers, IConfiguration configuration, IHostEnvironment environment)
{
    public async Task<bool> HasRoleAsync(string id, string role, CancellationToken cancellationToken)
    {
        var local = await localUsers.FindAsync(new UserFilter { Id = id }, cancellationToken);
        if (local is not null) return local.Roles.Contains(role);
        return CompanyAuthentication.Users(configuration).Any(user => user.Id == id && user.Enabled
            && user.Roles.Contains(role) && CompanyAuthentication.ProviderEnabled(user.Provider, configuration, environment));
    }
}
