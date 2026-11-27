using System.Diagnostics;

namespace IlWorkflow.Core.Contracts.SharedContracts;

/// <summary>Standard response body for every /api/* endpoint: { trace_id, status_code, messages, data }.</summary>
public class Result<T>
{
    public string? traceId { get; private set; }
    public int statusCode { get; private set; }
    public IEnumerable<string>? messages { get; private set; }
    public T? data { get; private set; }

    public Result(int statusCode, IEnumerable<string> messages, T? data)
    {
        traceId = Convert.ToString(Activity.Current?.TraceId);
        this.statusCode = statusCode;
        this.messages = messages;
        this.data = data;
    }
}
