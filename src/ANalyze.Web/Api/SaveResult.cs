using System.Text.Json;
using System.Text.Json.Nodes;
using ANalyze.Infrastructure.Jobs;
using Microsoft.AspNetCore.Http.Json;

namespace ANalyze.Web.Api;

/// <summary>Response of a settings save: the saved value plus <c>savedLater</c>, the job it waits for when the database was busy.</summary>
public static class SaveResult
{
    public static IResult Ok(object value, bool written, JobLock jobLock, JsonOptions json)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), json.SerializerOptions)!.AsObject();
        node["savedLater"] = written ? null : jobLock.Holder ?? "the running job";
        return Results.Ok(node);
    }
}
