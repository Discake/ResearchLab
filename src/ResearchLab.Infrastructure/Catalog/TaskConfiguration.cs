using System.Collections.ObjectModel;
using System.Text.Json;
using ResearchLab.Application;
using ResearchLab.Domain;

namespace ResearchLab.Infrastructure.Catalog;

internal static class TaskConfiguration
{
    public static IReadOnlyDictionary<string, TaskSnapshot> Parse(byte[] utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            RequireProperties(root, "schemaVersion", "tasks");
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1)
                throw Invalid("Only schemaVersion 1 is supported.");
            var tasks = root.GetProperty("tasks");
            if (tasks.ValueKind != JsonValueKind.Object)
                throw Invalid("tasks must be an object.");
            var result = new Dictionary<string, TaskSnapshot>(StringComparer.Ordinal);
            foreach (var task in tasks.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(task.Name) || task.Name.Length > 128 || result.ContainsKey(task.Name) || result.Count >= 64)
                    throw Invalid("Task names must be nonblank and unique; at most 64 tasks are allowed.");
                RequireProperties(task.Value, "executable", "args", "timeoutSeconds");
                var executable = Text(task.Value.GetProperty("executable"));
                var args = task.Value.GetProperty("args");
                if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() > 128)
                    throw Invalid("args must be an array of at most 128 strings.");
                if (!task.Value.GetProperty("timeoutSeconds").TryGetInt32(out var timeout) || timeout is < 1 or > 86400)
                    throw Invalid("timeoutSeconds must be an integer between 1 and 86400.");
                result.Add(task.Name, new(task.Name, executable, args.EnumerateArray().Select(Text), TimeSpan.FromSeconds(timeout)));
            }
            if (result.Count == 0)
                throw Invalid("Declare at least one task.");
            return new ReadOnlyDictionary<string, TaskSnapshot>(result);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or DomainException)
        { throw Invalid("Malformed researchlab.json: expected schemaVersion, tasks, executable, args and timeoutSeconds with valid types."); }
    }

    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > 4096 || text.Contains('\0'))
            throw Invalid("Command fields must be strings of at most 4096 characters without NUL.");
        return text;
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("Expected an object.");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name))
                throw Invalid("Unknown or duplicate configuration property.");
        if (remaining.Count != 0)
            throw Invalid("Required configuration property is missing.");
    }

    private static CatalogException Invalid(string message) => new("InvalidConfiguration", message);
}
