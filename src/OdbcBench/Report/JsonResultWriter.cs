using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OdbcBench.Config;

namespace OdbcBench.Report;

/// <summary>Writes and reads the full run result (every sample of every series) as JSON.</summary>
public static class JsonResultWriter
{
    public static readonly JsonSerializerOptions Options = new(BenchConfig.JsonOptions)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(RunResult run) => JsonSerializer.Serialize(run, Options);

    public static void Write(RunResult run, string path) => File.WriteAllText(path, Serialize(run), new UTF8Encoding(false));

    public static RunResult Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), Options)
                   ?? throw new ConfigException($"'{path}' does not contain a run result");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"'{path}' is not a valid OdbcBench result file: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ConfigException($"cannot read '{path}': {ex.Message}");
        }
    }
}
