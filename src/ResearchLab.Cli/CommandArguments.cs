using System.Globalization;
using ResearchLab.Application;

namespace ResearchLab.Cli;

public enum ReadCommand { ProjectList, ExperimentList, RunList, RunShow }

public sealed record CommandArguments(ReadCommand Command, string DatabasePath, string DataDirectory,
    Guid? Id, RunPageRequest Page)
{
    public static CommandArguments Parse(string[] args)
    {
        var words = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
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
            if (word is not ("--database" or "--data-dir" or "--project" or "--experiment" or "--offset" or "--page-size"))
                throw new ArgumentException("Unknown option. Use --help for supported options.");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException($"Option {word} requires a value.");
            if (!options.TryAdd(word, args[index]))
                throw new ArgumentException($"Option {word} was specified more than once.");
        }

        var command = words.ToArray() switch
        {
            ["project", "list"] => ReadCommand.ProjectList,
            ["experiment", "list"] => ReadCommand.ExperimentList,
            ["run", "list"] => ReadCommand.RunList,
            ["run", "show", _] => ReadCommand.RunShow,
            _ => throw new ArgumentException("Unknown or incomplete command. Use --help for supported commands.")
        };
        foreach (var option in options.Keys)
        {
            var allowed = option is "--database" or "--data-dir" ||
                (command == ReadCommand.ExperimentList && option == "--project") ||
                (command == ReadCommand.RunList && option is "--experiment" or "--offset" or "--page-size");
            if (!allowed)
                throw new ArgumentException($"Option {option} is not supported for this command.");
        }
        var database = FullPath(Required("--database"));
        var directory = FullPath(Required("--data-dir"));
        Guid? id = command switch
        {
            ReadCommand.ExperimentList => Identifier(Required("--project")),
            ReadCommand.RunList => Identifier(Required("--experiment")),
            ReadCommand.RunShow => Identifier(words[2]),
            _ => null
        };
        var defaults = new RunPageRequest();
        var offset = Integer("--offset", defaults.Offset);
        var size = Integer("--page-size", defaults.PageSize);
        // Pagination limits belong to Application, not to the console adapter.
        return new(command, database, directory, id, new RunPageRequest(offset, size));

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
