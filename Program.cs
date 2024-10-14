﻿using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

class Program
{
    public static Config config = new();
    static string homeUrl = "https://mods.vintagestory.at";
    static string modApi = "http://mods.vintagestory.at/api/mod";

    static void Separator()
    {
        Console.WriteLine(new string('█', 50));
    }

    static List<string> GetModList(string modPath)
    {
        return Directory.GetFiles(modPath, "*.zip").ToList();
    }

    static List<Dictionary<string, object>> ExtractModInfoFromZips(List<string> modlist)
    {
        List<Dictionary<string, object>> modInfoList = new List<Dictionary<string, object>>();
        foreach (var mod in modlist)
        {
            using (ZipArchive archive = ZipFile.OpenRead(mod))
            {
                var modInfoEntry = archive.GetEntry("modinfo.json");
                if (modInfoEntry != null)
                {
                    using (var reader = new StreamReader(modInfoEntry.Open()))
                    {
                        string json = reader.ReadToEnd();
                        var options = new JsonSerializerOptions
                        {
                            AllowTrailingCommas = true,
                            ReadCommentHandling = JsonCommentHandling.Skip,
                            Converters = { new LowercaseDictionaryConverter() }
                        };

                        var modInfo = JsonSerializer.Deserialize<Dictionary<string, object>>(json, options);

                        if (modInfo != null)
                        {
                            modInfoList.Add(modInfo);
                            Console.WriteLine(modInfo["name"]);
                        }
                    }
                }
            }
        }
        return modInfoList;
    }



    static async Task CheckForUpdatesAsync(List<Dictionary<string, object>> modInfoList)
    {
        int CheckedMods = 0;
        int DownloadedMods = 0;
        using (HttpClient client = new HttpClient())
        {
            foreach (var mod in modInfoList)
            {
                CheckedMods++;
                Separator();
                string apiReq = modApi + "/" + mod["modid"];
                HttpResponseMessage modRes = await client.GetAsync(apiReq);
                if (modRes.IsSuccessStatusCode)
                {
                    var resModInfo = JsonSerializer.Deserialize<Dictionary<string, object>>(await modRes.Content.ReadAsStringAsync());
                    if (resModInfo == null)
                    {
                        Console.WriteLine($"Failed to check for {mod["name"]}");
                        continue;
                    }
                    var releases = (JsonElement)resModInfo["mod"];

                    if (config.GameVersion == null)
                    {
                        Console.WriteLine("Game version is not set. Exiting");
                        return;
                    }
                    var release = CompareVersions(releases.GetProperty("releases"), config.GameVersion);

                    int modComparison = CompareVersionParts(release.GetProperty("modversion").ToString(), mod["version"].ToString() ?? "0.0.0");

                    if (modComparison > 0 || (config.CanDowngrade ?? false) && (modComparison < 0) || (config.AlwaysDownload ?? false))
                    {
                        DownloadedMods++;
                        if (config.AlwaysDownload ?? false)
                            Console.WriteLine($"Updating {mod["name"]}, always download is enabled");
                        else if (modComparison > 0)
                            Console.WriteLine($"Updating {mod["name"]}, has a newer version.");
                        else if (modComparison < 0)
                            Console.WriteLine($"Updating {mod["name"]}, has an older version and downgrading is allowed");
                        int fileId = release.GetProperty("fileid").GetInt32();
                        string downlink = homeUrl + "/download?fileid=" + fileId;
                        //Console.WriteLine("Downloading from: " + downlink);

                        using (HttpResponseMessage response = await client.GetAsync(downlink))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                if (!Directory.Exists(config.DownloadPath))
                                {
                                    Console.WriteLine("Output folder doesn't exist. Exiting");
                                    return;
                                }
                                string fileName = $"{mod["name"]} - {release.GetProperty("modversion").ToString()}".Replace(":", ";").Replace("/", "-").Replace("\\", "-") + ".zip";
                                string outputPath = Path.Combine(config.DownloadPath, Path.GetFileName(fileName));
                                await File.WriteAllBytesAsync(outputPath, await response.Content.ReadAsByteArrayAsync());
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"{mod["name"]} is on latest ({mod["version"]})!");
                    }
                }
            }
        }
    }

    static JsonElement CompareVersions(JsonElement releases, string GameVersion)
    {
        foreach (JsonElement release in releases.EnumerateArray())
        {
            JsonElement tags = release.GetProperty("tags");
            foreach (JsonElement tag in tags.EnumerateArray())
            {
                string? tagVersion = tag.GetString()?.TrimStart('v'); // Remove 'v' from tag version

                // Shouldn't ever happen
                if (tagVersion == null)
                {
                    Console.WriteLine("Tag version is null. Skipping");
                    continue;
                }

                // Compare version parts
                int comparisonResult = CompareVersionParts(tagVersion, GameVersion);

                if (comparisonResult > 0 && (config.AlwaysUpdate ?? true)) // If tagVersion is greater than GameVersion
                {
                    Console.WriteLine($"Newer Release found: {tag.GetString()}");
                    return release;
                }

                if (comparisonResult == 0) // if tagVersion is equal to GameVersion
                {
                    Console.WriteLine($"Equal Release found: {tag.GetString()}");
                    return release;
                }

                if ((comparisonResult < 0) && (config.MissingVersion == "Use One Version Below")) // if tagVersion is less than GameVersion
                {
                    Console.WriteLine($"Older Release found: {tag.GetString()}");
                    return release;
                }
            }
        }
        Console.WriteLine($"No version found. Using Latest: {releases[0].GetProperty("tags")[0].GetString()}");
        return releases[0];
    }

    static int CompareVersionParts(string versionA, string versionB)
    {
        // Split the versions into parts (handle pre-release parts as well)
        string[] partsA = versionA.Split('.', '-');
        string[] partsB = versionB.Split('.', '-');

        // Compare each part numerically or alphabetically
        int maxLength = Math.Max(partsA.Length, partsB.Length);

        for (int i = 0; i < maxLength; i++)
        {
            // Get the current part for each version
            string partA = i < partsA.Length ? partsA[i] : "0";
            string partB = i < partsB.Length ? partsB[i] : "0";

            // Try to compare numerically first
            if (int.TryParse(partA, out int numA) && int.TryParse(partB, out int numB))
            {
                if (numA > numB) return 1;  // partA is greater
                if (numA < numB) return -1; // partA is smaller
            }
            else
            {
                // Compare alphabetically if not numeric
                int stringComparison = string.Compare(partA, partB, StringComparison.Ordinal);
                if (stringComparison != 0) return stringComparison;
            }
        }

        // If all parts are equal, return 0
        return 0;
    }

    static async Task Main(string[] args)
    {
        while (true)
        {
            Separator();
            Console.WriteLine("Welcome to VSMD!");
            Console.WriteLine("Please choose an option:");
            Console.WriteLine("1. Check for updates");
            Console.WriteLine($"2. Set Mods Directory (Current: {config.ModPath})");
            Console.WriteLine($"3. Set Download Directory (Current: {config.DownloadPath})");
            Console.WriteLine($"4. Set Game Version (Current: {config.GameVersion})");
            Console.WriteLine($"5. Set if to always update mods? (Current: {config.AlwaysUpdate})");
            Console.WriteLine($"6. Set if can downgrade mods? (Current: {config.CanDowngrade})");
            Console.WriteLine($"7. Always download anyway? (Current: {config.AlwaysDownload})");
            Console.WriteLine($"8. Missing release decision? (Current: {config.MissingVersion})");
            Console.WriteLine($"0. Exit");
            Separator();

            int option = GetMainInput();
            switch (option)
            {
                case 1:
                    // Get mod list
                    if (config.ModPath == null)
                    {
                        Console.WriteLine("Mods directory is not set.");
                        break;
                    }
                    List<string> modList = GetModList(config.ModPath);
                    Console.WriteLine("Mods in your folder: \n");
                    // Extract mod info from ZIPs
                    List<Dictionary<string, object>> modInfoList = ExtractModInfoFromZips(modList);
                    Separator();
                    Console.WriteLine("\nChecking for updates...\n");
                    // Check for updates asynchronously
                    if (config.DownloadPath == null)
                    {
                        Console.WriteLine("Output path is not set.");
                        break;
                    }
                    if (config.GameVersion == null)
                    {
                        Console.WriteLine("Game version is not set.");
                        break;
                    }
                    await CheckForUpdatesAsync(modInfoList);
                    Separator();
                    Console.WriteLine("\n\nDownload complete, press Enter to exit!");
                    Console.ReadLine();
                    break;
                case 2:
                    Console.WriteLine("Enter the new mods directory:");
                    string newModPath = Console.ReadLine() ?? string.Empty;
                    if (Directory.Exists(newModPath))
                    {
                        config.ModPath = newModPath;
                        config.SaveConfig();
                        Console.WriteLine($"Mods Directory updated to: {config.ModPath}");
                    }
                    else
                    {
                        Console.WriteLine("Directory doesn't exist, no changes made.");
                    }
                    break;
                case 3:
                    Console.WriteLine("Enter the new download directory:");
                    string newDownloadPath = Console.ReadLine() ?? string.Empty;
                    if (Directory.Exists(newDownloadPath))
                    {
                        config.DownloadPath = newDownloadPath;
                        config.SaveConfig();
                        Console.WriteLine($"Mods Directory updated to: {config.DownloadPath}");
                    }
                    else
                    {
                        Console.WriteLine("Directory doesn't exist, no changes made.");
                    }
                    break;
                case 4:
                    Console.WriteLine("Current game version: " + config.GameVersion);
                    Console.WriteLine("Note that this doesn't check if the version exists.");
                    Console.WriteLine("This will be used for checking for which game version to download your mods");
                    Console.WriteLine("Enter the new game version:");
                    string newGameVersion;
                    newGameVersion = Console.ReadLine() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(newGameVersion))
                    {
                        Console.WriteLine("Please enter a valid game version.");
                    }
                    else
                    {
                        config.GameVersion = newGameVersion;
                        Console.WriteLine($"Game version updated to: {config.GameVersion}");
                    }
                    break;
                case 5:
                    Console.WriteLine("Current always update setting: " + config.AlwaysUpdate);
                    Console.WriteLine("1) Always update mods");
                    Console.WriteLine("2) Update only if there's an update specifically for your version");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.AlwaysUpdate = true;
                            config.SaveConfig();
                            Console.WriteLine($"Always update setting updated to: {config.AlwaysUpdate}");
                            break;
                        case 2:
                            config.AlwaysUpdate = false;
                            config.SaveConfig();
                            Console.WriteLine($"Always update setting updated to: {config.AlwaysUpdate}");
                            break;
                        case 3:
                            Console.WriteLine("Update cancelled.");
                            break;
                        default:
                            Console.WriteLine("Invalid option.");
                            break;
                    }
                    break;
                case 6:
                    Console.WriteLine("Current can downgrade setting: " + config.CanDowngrade);
                    Console.WriteLine("1) Allow downgrading mods");
                    Console.WriteLine("2) Do not allow downgrading mods");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.CanDowngrade = true;
                            config.SaveConfig();
                            Console.WriteLine($"Can downgrade setting updated to: {config.CanDowngrade}");
                            break;
                        case 2:
                            config.CanDowngrade = false;
                            config.SaveConfig();
                            Console.WriteLine($"Can downgrade setting updated to: {config.CanDowngrade}");
                            break;
                        case 3:
                            Console.WriteLine("Update cancelled.");
                            break;
                        default:
                            Console.WriteLine("Invalid option.");
                            break;
                    }
                    break;
                case 7:
                    Console.WriteLine("Current can downgrade setting: " + config.AlwaysDownload);
                    Console.WriteLine("1) Always download anyway");
                    Console.WriteLine("2) Do not download always");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.AlwaysDownload = true;
                            config.SaveConfig();
                            Console.WriteLine($"Can downgrade setting updated to: {config.AlwaysDownload}");
                            break;
                        case 2:
                            config.AlwaysDownload = false;
                            config.SaveConfig();
                            Console.WriteLine($"Can downgrade setting updated to: {config.AlwaysDownload}");
                            break;
                        case 3:
                            Console.WriteLine("Update cancelled.");
                            break;
                        default:
                            Console.WriteLine("Invalid option.");
                            break;
                    }
                    break;
                case 8:
                    Console.WriteLine("Current can missing release setting: " + config.MissingVersion);
                    Console.WriteLine("Dictates what happens if the mod releases don't feature our version.");
                    Console.WriteLine("1) Use latest release");
                    Console.WriteLine("2) Use one release below current");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.MissingVersion = "Use Latest";
                            config.SaveConfig();
                            Console.WriteLine($"Can downgrade setting updated to: {config.MissingVersion}");
                            break;
                        case 2:
                            config.MissingVersion = "Use One Version Below";
                            config.SaveConfig();
                            Console.WriteLine($"Can downgrade setting updated to: {config.MissingVersion}");
                            break;
                        case 3:
                            Console.WriteLine("Update cancelled.");
                            break;
                        default:
                            Console.WriteLine("Invalid option.");
                            break;
                    }
                    break;
                case 0:
                    Console.WriteLine("Exiting");
                    Environment.Exit(0);
                    break;
                default:
                    Console.WriteLine("Invalid option, exiting.");
                    break;
            }

            Separator();
        }
    }

    static int GetMainInput()
    {
        // Loop until a valid integer is provided
        while (true)
        {
            // Check if the application can read keys
            if (IsConsoleAvailable())
            {
                // Attempt to read key input
                ConsoleKeyInfo cki = Console.ReadKey(true);
                if (int.TryParse(cki.KeyChar.ToString(), out int option))
                {
                    return option; // Return the parsed integer
                }
                else
                {
                    Console.WriteLine($"'{cki.KeyChar}' is not a valid option.");
                }
            }
            else
            {
                // Fallback to reading a line
                string? input = Console.ReadLine();
                if (int.TryParse(input, out int option))
                {
                    return option; // Return the parsed integer
                }
                else
                {
                    Console.WriteLine($"'{input}' is not a valid option.");
                }
            }
        }
    }


    static bool IsConsoleAvailable()
    {
        try
        {
            // Try to read from the console to see if it's available
            return Console.KeyAvailable;
        }
        catch (InvalidOperationException)
        {
            // If we catch an InvalidOperationException, then the console is not available
            return false;
        }
    }
}

// Custom converter to lowercase dictionary keys
public class LowercaseDictionaryConverter : JsonConverter<Dictionary<string, object>>
{
    public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Read the JSON object
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return dict;
            }

            // Read the key
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a property name.");
            }

            string? key = reader.GetString()?.ToLower(); // Convert the key to lowercase, allow for null

            // Read the value
            reader.Read();
            object? value = JsonSerializer.Deserialize<object?>(ref reader, options);

            // Add to dictionary
            if (key != null && value != null)
            {
                dict[key] = value;
            }
        }

        throw new JsonException("Expected the end of the JSON object.");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kvp in value)
        {
            writer.WritePropertyName(kvp.Key.ToLower()); // Write key as lowercase
            JsonSerializer.Serialize(writer, kvp.Value, options);
        }
        writer.WriteEndObject();
    }
}

public class Config
{
    private string configPath;
    public string? ModPath { get; set; }
    public string? GameVersion { get; set; }
    public string? DownloadPath { get; set; }
    public bool? AlwaysUpdate { get; set; }
    public bool? CanDowngrade { get; set; }
    public bool? AlwaysDownload { get; set; }
    public string? MissingVersion { get; set; }

    public Config()
    {
        configPath = Path.Combine(Directory.GetCurrentDirectory(), "vsmd.cfg");
        LoadConfig();
    }

    private void LoadConfig()
    {
        if (File.Exists(configPath))
        {
            string[] lines = File.ReadAllLines(configPath);
            foreach (var line in lines)
            {
                if (line.StartsWith("ModPath"))
                {
                    ModPath = line.Split('=')[1].Trim();
                }
                else if (line.StartsWith("GameVersion"))
                {
                    GameVersion = line.Split('=')[1].Trim();
                }
                else if (line.StartsWith("DownloadPath"))
                {
                    DownloadPath = line.Split('=')[1].Trim();
                }
                else if (line.StartsWith("AlwaysUpdate"))
                {
                    if (bool.TryParse(line.Split('=')[1].Trim(), out bool alwaysUpdateValue))
                        AlwaysUpdate = alwaysUpdateValue;
                    else
                        AlwaysUpdate = true;
                }
                else if (line.StartsWith("CanDowngrade"))
                {
                    if (bool.TryParse(line.Split('=')[1].Trim(), out bool canDowngradeValue))
                        CanDowngrade = canDowngradeValue;
                    else
                        CanDowngrade = false;
                }
                else if (line.StartsWith("AlwaysDownload"))
                {
                    if (bool.TryParse(line.Split('=')[1].Trim(), out bool AlwaysDownloadValue))
                        AlwaysDownload = AlwaysDownloadValue;
                    else
                        AlwaysDownload = false;
                }
                else if (line.StartsWith("MissingVersion"))
                {
                    MissingVersion = line.Split('=')[1].Trim();
                }
            }
        }
        else
        {
            // Handle config creation or re-ask for values as needed
            ModPath = AskDirectory("Please enter the path to your mods folder: ", "Please enter a valid path.");
            GameVersion = "0.0.0";
            DownloadPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods");
            AlwaysUpdate = true;
            CanDowngrade = false;
            AlwaysDownload = false;
            MissingVersion = "Use Latest";
            SaveConfig();
        }
    }

    public void SaveConfig()
    {
        File.WriteAllText(configPath,
            $"ModPath = {ModPath}\n" +
            $"GameVersion = {GameVersion}\n" +
            $"DownloadPath = {DownloadPath}\n" +
            $"AlwaysUpdate = {AlwaysUpdate}\n" +
            $"CanDowngrade = {CanDowngrade}\n" +
            $"AlwaysDownload = {AlwaysDownload}\n" +
            $"MissingVersion = {MissingVersion}\n"
            );
    }

    private string AskDirectory(string prompt, string errorMsg)
    {
        string? input;
        do
        {
            Console.WriteLine(prompt);
            input = Console.ReadLine();
            if (!Directory.Exists(input))
            {
                Console.WriteLine(errorMsg);
            }
        } while (!Directory.Exists(input));
        return input;
    }
}