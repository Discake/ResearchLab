using System.Globalization;
using System.Text.Json;

// A real local execution example, not a benchmark or recorded research evidence.
var input = Environment.GetEnvironmentVariable("RESEARCHLAB_PARAMETERS_JSON");
if (input is null)
{
    Console.Error.WriteLine("Launch this example through researchlab run start.");
    return 2;
}
using var document = JsonDocument.Parse(input);
var parameters = document.RootElement;
var iterations = parameters.GetProperty("iterations").GetInt32();
var scale = parameters.GetProperty("scale").GetDouble();
Console.WriteLine($"Local execution demo: {parameters.GetProperty("label").GetString()}");
Console.Error.WriteLine("Demo diagnostic; no benchmark measurements or research metrics are claimed.");
await Task.Delay(parameters.GetProperty("delayMilliseconds").GetInt32());
var sum = Enumerable.Range(1, iterations).Sum(value => value * scale);
Console.WriteLine("Sum: " + sum.ToString("G17", CultureInfo.InvariantCulture));
if (parameters.GetProperty("verbose").GetBoolean())
    Console.WriteLine($"Iterations: {iterations.ToString(CultureInfo.InvariantCulture)}");
return parameters.GetProperty("fail").GetBoolean() ? 7 : 0;
