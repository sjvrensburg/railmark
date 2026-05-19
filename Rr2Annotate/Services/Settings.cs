using System.Text.Json;

namespace Rr2Annotate.Services;

public class Settings
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "rr2annotate");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "settings.json");

    public string CliCommand { get; set; } = "";

    public static Settings Load()
    {
        if (!File.Exists(ConfigFile))
            return new Settings();

        var json = File.ReadAllText(ConfigFile);
        return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFile, json);
    }

    public static async Task<Settings> EnsureConfigured(bool forceReconfigure = false)
    {
        var settings = Load();

        if (!forceReconfigure && !string.IsNullOrWhiteSpace(settings.CliCommand))
            return settings;

        Console.WriteLine("=== Rr2Annotate Configuration ===");
        Console.WriteLine();
        Console.WriteLine("Enter the command or path to the RailReader2 CLI.");
        Console.WriteLine("Examples:");
        Console.WriteLine("  railreader2-cli");
        Console.WriteLine("  /home/user/bin/railreader2-cli");
        Console.WriteLine("  /opt/railreader2-cli/railreader2-cli");
        Console.WriteLine();

        while (true)
        {
            Console.Write("RailReader2 CLI command: ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Command cannot be empty.");
                continue;
            }

            // Test the command
            Console.Write("Testing... ");
            if (await TestCliCommand(input))
            {
                Console.WriteLine("OK");
                settings.CliCommand = input;
                settings.Save();
                Console.WriteLine($"Saved to {ConfigFile}");
                return settings;
            }

            Console.WriteLine("FAILED — could not run the command. Please check the path and try again.");
        }
    }

    private static async Task<bool> TestCliCommand(string command)
    {
        try
        {
            var psi = CliRunner.BuildProcessStartInfo(command, "--help");
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output.Contains("railreader2-cli");
        }
        catch
        {
            return false;
        }
    }
}
