using FluentValidation;

namespace IlWorkflow.Api.ExternalIdentity;

public enum DemoMasterKind { Username, Email }

/// <summary>Trust and match configuration belongs to the provider, not to a manually registered employee.</summary>
public sealed class ExternalProvider
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DemoMasterKind Master { get; set; }
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "ilworkflow-api";
    public string MatchClaim { get; set; } = "name";
    public string MemberField { get; set; } = "username";
    public bool IgnoreCase { get; set; } = true;
}

/// <summary>Hard-coded API record. ParticipantId is computed, not registered in a user table.</summary>
public sealed record MasterMember(string Id, string DisplayName, string ParticipantId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Username = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Email = null)
{
    public string? Field(string field) => field.ToLowerInvariant() switch
    {
        "id" => Id, "username" => Username, "email" => Email, "displayname" => DisplayName, _ => null
    };
}

public sealed class ExternalProviderValidator : AbstractValidator<ExternalProvider>
{
    public ExternalProviderValidator()
    {
        RuleFor(x => x.Id).Matches("^[a-z][a-z0-9-]{0,39}$");
        RuleFor(x => x.DisplayName).NotEmpty();
        RuleFor(x => x.Master).IsInEnum();
        RuleFor(x => x.Issuer).NotEmpty();
        RuleFor(x => x.Audience).NotEmpty();
        RuleFor(x => x.MatchClaim).NotEmpty().Must(claim => !new[]
        { "iss", "aud", "exp", "iat", "nbf", "jti", "token_use", "roles", "permissions", "admin" }.Contains(claim))
            .WithMessage("Use a user-specific claim, not issuer/protocol/permission metadata.");
        RuleFor(x => x.MemberField).Must(field => !string.IsNullOrWhiteSpace(field) &&
            new[] { "id", "username", "email", "displayname" }.Contains(field.ToLowerInvariant()))
            .WithMessage("MemberField must identify an available master response field.");
        RuleFor(x => x).Must(provider => provider.MemberField != "username" || provider.Master == DemoMasterKind.Username)
            .WithMessage("The email master has no username field.");
        RuleFor(x => x).Must(provider => provider.MemberField != "email" || provider.Master == DemoMasterKind.Email)
            .WithMessage("The username master has no email field.");
    }
}

/// <summary>
/// One shared, read-only source backs the two sample APIs and server-side identity matching.
/// No loopback HTTP, database registration, hidden cache, passwords or first-login writes are needed.
/// A real HTTP master adapter can replace this source when its contract is available.
/// </summary>
public sealed class DemoMemberDirectory
{
    private readonly ExternalProvider[] _providers;
    public bool Enabled { get; }
    public IReadOnlyList<ExternalProvider> Providers => _providers;
    public DemoMemberDirectory(IConfiguration configuration, IHostEnvironment environment)
    {
        Enabled = environment.IsDevelopment() && configuration.GetValue<bool>("ExternalAuth:DevelopmentTokens:Enabled");
        _providers = Enabled ? configuration.GetSection("ExternalAuth:Providers").Get<ExternalProvider[]>()?.Where(x => x.Enabled).ToArray() ?? [] : [];
        var validator = new ExternalProviderValidator();
        foreach (var provider in _providers) validator.ValidateAndThrow(provider);
        if (_providers.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != _providers.Length ||
            _providers.Select(x => x.Issuer).Distinct(StringComparer.Ordinal).Count() != _providers.Length)
            throw new InvalidOperationException("External demo provider IDs and token issuers must be unique.");
        var localIssuer = configuration["Identity:Tokens:Issuer"] ?? "http://elsa.api";
        if (_providers.Any(x => x.Issuer == localIssuer)) throw new InvalidOperationException("An external issuer cannot shadow Elsa's local issuer.");
        foreach (var provider in _providers)
            if (Members(provider).Any(member => string.IsNullOrWhiteSpace(member.Field(provider.MemberField))))
                throw new InvalidOperationException($"Match field is unavailable in provider {provider.Id}.");
    }
    public ExternalProvider? Provider(string id) => _providers.SingleOrDefault(x => x.Id == id);
    public IReadOnlyList<MasterMember> Members(ExternalProvider provider)
    {
        // Exactly five fictional employees per API. IDs are stable within their provider.
        var names = new[] { ("john", "John Doe"), ("alice", "Alice Smith"), ("bob", "Bob Jones"), ("clara", "Clara Brown"), ("dan", "Dan Wilson") };
        return names.Select((name, index) =>
        {
            var masterId = (provider.Master == DemoMasterKind.Username ? "U" : "E") + (index + 1).ToString("D3");
            // Do not use names/emails as task keys: edits to matching/display fields must not reassign history.
            // Prefixing with provider ID also prevents the two Johns from inheriting each other's tasks.
            var participantId = $"external:{provider.Id}:{masterId}";
            return new MasterMember(masterId, name.Item2, participantId,
                provider.Master == DemoMasterKind.Username ? name.Item1 : null,
                provider.Master == DemoMasterKind.Email ? name.Item1 + "@example.test" : null);
        }).ToArray();
    }
    public MasterMember? FindByClaim(ExternalProvider provider, string value)
    {
        var comparison = provider.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = Members(provider).Where(member => string.Equals(member.Field(provider.MemberField)?.Trim(), value.Trim(), comparison)).ToArray();
        // Never choose the first record when a configured matching field is ambiguous.
        return matches.Length == 1 ? matches[0] : null;
    }
    public MasterMember? FindByParticipantId(string participantId) => _providers.SelectMany(Members).SingleOrDefault(x => x.ParticipantId == participantId);
}
