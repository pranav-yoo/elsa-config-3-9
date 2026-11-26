using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;

namespace IlWorkflow.Api.Workflows;

public sealed class ParallelTaskWorkflowResumer(ParallelTaskCommands commands) : IUserTaskWorkflowResumer
{
    public Task ResumeAsync(UserTask task, UserTaskStimulus stimulus, CancellationToken cancellationToken = default) =>
        commands.ResumeAsync(task, stimulus, cancellationToken);
}
