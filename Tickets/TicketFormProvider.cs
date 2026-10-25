using System.Text.Json;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;

namespace IlWorkflow.Api.Tickets;

public sealed class TicketFormProvider : IUserTaskFormProvider
{
    public string Name => "ticket-json";

    public Task<ResolvedUserTaskForm?> ResolveAsync(UserTaskFormReference reference, CancellationToken cancellationToken = default)
    {
        var configuration = TicketJson.Read<TicketConfiguration>(reference.Binding!);
        return Task.FromResult<ResolvedUserTaskForm?>(new(reference, configuration.Version.ToString(), new Dictionary<string, object?>())
        {
            Fields = reference.Key == "correction"
                ? configuration.Definition.Attributes.Select(f => new UserTaskFormFieldDescriptor(f.Key, f.Key, f.Type, f.Required)).ToArray()
                : [new UserTaskFormFieldDescriptor("reason", "Reason (required for rejection)")]
        });
    }

    public Task<UserTaskFormValidationResult> ValidateAndNormalizeAsync(ResolvedUserTaskForm form, string actionKey, JsonElement data, CancellationToken cancellationToken = default)
    {
        try
        {
            TicketJson.Require(data.ValueKind == JsonValueKind.Object, "Supply a JSON data object.");
            if (form.Requested.Key == "correction")
            {
                TicketJson.Require(actionKey == "Resubmit" && data.TryGetProperty("attributes", out _), "Supply attributes with Resubmit.");
                TicketJson.Require(data.EnumerateObject().All(p => p.Name == "attributes"), "Unknown correction field.");
                var configuration = TicketJson.Read<TicketConfiguration>(form.Requested.Binding!);
                TicketJson.ValidateAttributes(configuration.Definition, data.GetProperty("attributes").Deserialize<Dictionary<string, JsonElement>>(TicketJson.Options)!);
            }
            else
            {
                TicketJson.Require(actionKey is "Approve" or "Reject", "Unknown review action.");
                TicketJson.Require(data.EnumerateObject().All(p => p.Name is "reason" or "comment"), "Only reason/comment are accepted during review; attributes are frozen.");
                if (actionKey == "Reject")
                    TicketJson.Require(data.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reason.GetString()), "Rejection requires a reason.");
                foreach (var field in data.EnumerateObject()) TicketJson.Require(field.Value.ValueKind == JsonValueKind.String, "Reason/comment must be strings.");
            }
            return Task.FromResult(new UserTaskFormValidationResult(true, data.Clone()));
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        {
            return Task.FromResult(new UserTaskFormValidationResult(false, Errors: [error.Message]));
        }
    }
}
