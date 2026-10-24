using System.Globalization;
using ResearchLab.Application;

namespace ResearchLab.Cli;

public enum ConsoleCommand { ProjectList, ExperimentList, RunList, RunShow, RunStart, RunRecover, DatabaseInit, ProjectImport, ExperimentCreate }

public sealed record CommandArguments(ConsoleCommand Command, string DatabasePath, string DataDirectory,
    Guid? Id, RunPageRequest Page, string Parameters = "{}", bool IncludeLogs = false,
    string? Workspace = null, string? Name = null, string? Definition = null)
{
    public static CommandArguments Parse(string[] args)
    {
        var words = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var includeLogs = false;
        foreach (var argument in args)
            ArgumentNullException.ThrowIfNull(argument);
        for (var index = 0; index < args.Length; index++)
        {
            var word = args[index];
            if (!word.StartsWith('-'))
            {
                words.Add(word);
                continue;
            }
            if (word == "--include-logs")
            {
                if (includeLogs) throw new ArgumentException("Option --include-logs was specified more than once.");
                includeLogs = true;
                continue;
            }
            if (word is not ("--database" or "--data-dir" or "--project" or "--experiment" or "--offset" or "--page-size" or
                "--parameters" or "--name" or "--definition"))
                throw new ArgumentException("Unknown option. Use --help for supported options.");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException($"Option {word} requires a value.");
            if (!options.TryAdd(word, args[index]))
                throw new ArgumentException($"Option {word} was specified more than once.");
        }

        var command = words.ToArray() switch
        {
            ["project", "list"] => ConsoleCommand.ProjectList,
            ["experiment", "list"] => ConsoleCommand.ExperimentList,
            ["run", "list"] => ConsoleCommand.RunList,
            ["run", "show", _] => ConsoleCommand.RunShow,
            ["run", "start", _] => ConsoleCommand.RunStart,
            ["run", "recover"] => ConsoleCommand.RunRecover,
            ["database", "init"] => ConsoleCommand.DatabaseInit,
            ["project", "import", _] => ConsoleCommand.ProjectImport,
            ["experiment", "create"] => ConsoleCommand.ExperimentCreate,
            _ => throw new ArgumentException("Unknown or incomplete command. Use --help for supported commands.")
        };
        foreach (var option in options.Keys)
        {
            var allowed = option is "--database" or "--data-dir" ||
                (command is ConsoleCommand.ExperimentList or ConsoleCommand.ExperimentCreate && option == "--project") ||
                (command == ConsoleCommand.RunList && option is "--experiment" or "--offset" or "--page-size") ||
                (command == ConsoleCommand.RunStart && option == "--parameters") ||
                (command == ConsoleCommand.ProjectImport && option == "--name") ||
                (command == ConsoleCommand.ExperimentCreate && option == "--definition");
            if (!allowed)
                throw new ArgumentException($"Option {option} is not supported for this command.");
        }
        if (includeLogs && command != ConsoleCommand.RunShow)
            throw new ArgumentException("Option --include-logs is supported only for run show.");
        var database = FullPath(Required("--database"));
        var directory = FullPath(Required("--data-dir"));
        Guid? id = command switch
        {
            ConsoleCommand.ExperimentList or ConsoleCommand.ExperimentCreate => Identifier(Required("--project")),
            ConsoleCommand.RunList => Identifier(Required("--experiment")),
            ConsoleCommand.RunShow or ConsoleCommand.RunStart => Identifier(words[2]),
            _ => null
        };
        var defaults = new RunPageRequest();
        var offset = Integer("--offset", defaults.Offset);
        var size = Integer("--page-size", defaults.PageSize);
        // Pagination limits belong to Application, not to the console adapter.
        return new(command, database, directory, id, new RunPageRequest(offset, size),
            options.GetValueOrDefault("--parameters", "{}"), includeLogs,
            command == ConsoleCommand.ProjectImport ? FullPath(words[2]) : null,
            command == ConsoleCommand.ProjectImport ? Required("--name") : null,
            command == ConsoleCommand.ExperimentCreate ? FullPath(Required("--definition")) : null);

        string Required(string option) => options.TryGetValue(option, out var value) ? value :
            throw new ArgumentException($"Required option {option} is missing.");

        int Integer(string option, int fallback) => !options.TryGetValue(option, out var value) ? fallback :
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number :
            throw new ArgumentException($"Option {option} requires a nonnegative integer.");
    }

    private static Guid Identifier(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id :
        throw new ArgumentException("Identifiers must be nonempty GUIDs.");

    private static string FullPath(string value)
    {
        try { return Path.GetFullPath(value); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Invalid configured filesystem path."); }
    }
}
