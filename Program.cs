using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

class Program
{
    public static Config config = new();
    static string homeUrl = "https://mods.vintagestory.at";
    static string modApi = "http://mods.vintagestory.at/api/mod";
    static string oldPathName = $"Old-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
    static string oldModPathName = $"Old-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
    static bool olderVersion = false;

    static void ClearConsole()
    {
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // vsc terminal scary debug fact
        }
    }

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
            using ZipArchive archive = ZipFile.OpenRead(mod);
            var modInfoEntry = archive.GetEntry("modinfo.json");
            if (modInfoEntry == null)
            {
                continue;
            }
            using var reader = new StreamReader(modInfoEntry.Open());
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
                modInfo["uniquekeyformodpath"] = Path.GetFullPath(mod).Replace('\\', '/');
                modInfoList.Add(modInfo);
            }
        }
        return modInfoList;
    }



    static async Task CheckForUpdatesAsync(List<Dictionary<string, object>> modInfoList)
    {
        int CheckedMods = 0;
        int DownloadedMods = 0;
        using HttpClient client = new HttpClient();
        foreach (var mod in modInfoList)
        {
            Separator();
            CheckedMods++;
            if (!mod.TryGetValue("modid", out var modid))
            {
                Console.WriteLine($"Failed to find modid property in modinfo.json for {mod["name"]}");
                continue;
            }
            string apiReq = modApi + "/" + modid;
            HttpResponseMessage modRes = await client.GetAsync(apiReq);
            if (!modRes.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to check for {mod["name"]}");
                continue;
            }
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

            // exit if skipped inside of compareversions
            if (release.TryGetProperty("skipped", out _))
                continue;

            int modComparison = CompareVersionParts(release.GetProperty("modversion").ToString(), mod["version"].ToString() ?? "0.0.0");

            bool updateMod = modComparison > 0 || (config.CanDowngrade ?? false) && (modComparison < 0) || (config.AlwaysDownload ?? false);
            if (!updateMod)
            {
                Console.WriteLine($"{mod["name"]} is on latest ({mod["version"]})!");
                continue;
            }

            DownloadedMods++;
            if (config.AlwaysDownload ?? false)
                Console.WriteLine($"Updating {mod["name"]}, always download is enabled");
            else if (modComparison > 0)
                Console.WriteLine($"Updating {mod["name"]}, has a newer version.");
            else if (modComparison < 0)
                Console.WriteLine($"Updating {mod["name"]}, has an older version and downgrading is allowed");
            int fileId = release.GetProperty("fileid").GetInt32();
            string downlink = homeUrl + "/download?fileid=" + fileId;

            using HttpResponseMessage response = await client.GetAsync(downlink);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to download {mod["name"]} - {release.GetProperty("modversion")} - {release.GetProperty("tags")[0]}");
                continue;
            }
            if (!Directory.Exists(config.ModPath))
            {
                throw new Exception("Mods directory is not set.");
            }
            if (mod["uniquekeyformodpath"].ToString() != null)
            {
                string oldPath = Path.Combine(config.ModPath, oldPathName);
                if (!Directory.Exists(oldPath))
                {
                    Directory.CreateDirectory(oldPath);
                }
                string oldFileName = Path.GetFileName(mod["uniquekeyformodpath"]?.ToString() ?? string.Empty);
                string oldOutputPath = Path.Combine(oldPath, oldFileName);
                if (File.Exists(oldOutputPath))
                {
                    File.Delete(oldOutputPath);
                }
                File.Move(mod["uniquekeyformodpath"]?.ToString() ?? string.Empty, oldOutputPath);
            }

            string fileNameBase = $"{mod["name"]} - ({mod["modid"]}) - {release.GetProperty("modversion")} - {release.GetProperty("tags")[0]}"
                .Replace(":", ";")
                .Replace("/", "-")
                .Replace("\\", "-");

            bool olderVer = false;
            if (olderVersion && (config.MoveOlder ?? false)) olderVer = true;

            string OutputPath = Path.Combine(config.ModPath, $"{fileNameBase}.zip");
            if (olderVer) OutputPath = Path.Combine(config.ModPath, oldModPathName, $"{fileNameBase}.zip");

            // Handle duplicates
            int duplicateCount = 0;
            while (File.Exists(OutputPath))
            {
                duplicateCount++;
                OutputPath = Path.Combine(config.ModPath, $"{fileNameBase} ({duplicateCount++}).zip");
                if (olderVer) OutputPath = Path.Combine(config.ModPath, oldModPathName, $"{fileNameBase} ({duplicateCount++}).zip");
            }

            // Save mod
            await File.WriteAllBytesAsync(OutputPath, await response.Content.ReadAsByteArrayAsync());
        }
        Separator();
        Console.WriteLine($"Checked {CheckedMods} mods. Downloaded {DownloadedMods} mods");
    }

    static JsonElement CompareVersions(JsonElement releases, string GameVersion)
    {
        foreach (JsonElement release in releases.EnumerateArray())
        {
            olderVersion = false;
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
                    Console.WriteLine($"Exact Release found: {tag.GetString()}");
                    return release;
                }

                if ((comparisonResult < 0) && (config.MissingVersion == 2)) // if tagVersion is less than GameVersion and we use one version older
                {
                    Console.WriteLine($"Older Release found: {tag.GetString()}");
                    olderVersion = true;
                    return release;
                }
            }
        }

        if (config.MissingVersion == 3)
        {
            Console.WriteLine($"No exact version found, skipping mod");
            return JsonDocument.Parse("{\"skipped\": true}").RootElement;
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
            oldPathName = $"Old-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            oldModPathName = $"OldVersion-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            ClearConsole();
            Separator();
            Console.WriteLine("Welcome to VSMD!");
            Console.WriteLine("Please choose an option:");
            Console.WriteLine("1. Check for updates");
            Console.WriteLine($"2. Set Mods Directory (Current: {config.ModPath})");
            Console.WriteLine($"3. Set Game Version (Current: {config.GameVersion})");
            Console.WriteLine($"4. Always update mods and ignore versions? (Current: {config.AlwaysUpdate})");
            Console.WriteLine($"5. Downgrade mods? (Current: {config.CanDowngrade})");
            Console.WriteLine($"6. Always download even if it's the same version? (Current: {config.AlwaysDownload})");
            Console.WriteLine($"7. Missing release decision? (Current: {config.MissingVersion})");
            Console.WriteLine($"8. Move mods made for older versions to {oldModPathName}? (Current: {config.MoveOlder})");
            Console.WriteLine($"0. Exit");
            Separator();

            int option = GetMainInput();
            switch (option)
            {
                case 1:
                    ClearConsole();
                    // Get mod list
                    if (config.ModPath == null)
                    {
                        throw new Exception("Mods directory is not set.");
                    }
                    List<string> modList = GetModList(config.ModPath);
                    // dont log since we log in updates anyway
                    // Console.WriteLine("Mods in your folder: \n");
                    // Extract mod info from ZIPs
                    List<Dictionary<string, object>> modInfoList = ExtractModInfoFromZips(modList);
                    Separator();
                    Console.WriteLine("\nChecking for updates...\n");
                    // Check for updates asynchronously
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
                    ClearConsole();
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
                    ClearConsole();
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
                        config.SaveConfig();
                        Console.WriteLine($"Game version updated to: {config.GameVersion}");
                    }
                    break;
                case 4:
                    ClearConsole();
                    Console.WriteLine("Current setting: " + config.AlwaysUpdate);
                    Console.WriteLine("If this is true it will make us always update to latest version. No exceptions");
                    Console.WriteLine("1) Always update and ignore older versions");
                    Console.WriteLine("2) Update only if there's an update specifically for your version");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.AlwaysUpdate = true;
                            config.SaveConfig();
                            break;
                        case 2:
                            config.AlwaysUpdate = false;
                            config.SaveConfig();
                            break;
                        case 3:
                            Console.WriteLine("Update cancelled.");
                            break;
                        default:
                            Console.WriteLine("Invalid option.");
                            break;
                    }
                    break;
                case 5:
                    ClearConsole();
                    Console.WriteLine("Current setting: " + config.CanDowngrade);
                    Console.WriteLine("Dictates what happens if the mod versions are lower than installed.");
                    Console.WriteLine("1) Downgrade currently installed");
                    Console.WriteLine("2) Skip the mod");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.CanDowngrade = true;
                            config.SaveConfig();
                            break;
                        case 2:
                            config.CanDowngrade = false;
                            config.SaveConfig();
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
                    ClearConsole();
                    Console.WriteLine("Current setting: " + config.AlwaysDownload);
                    Console.WriteLine("Dictates what happens if the mod versions are the same as installed.");
                    Console.WriteLine("1) Download anyway");
                    Console.WriteLine("2) Don't Download");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.AlwaysDownload = true;
                            config.SaveConfig();
                            break;
                        case 2:
                            config.AlwaysDownload = false;
                            config.SaveConfig();
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
                    ClearConsole();
                    Console.WriteLine("Current setting: " + config.MissingVersion);
                    Console.WriteLine("Dictates what happens if the mod releases don't feature our version.");
                    Console.WriteLine("1) Use latest release");
                    Console.WriteLine("2) Use one release below current");
                    Console.WriteLine("3) Skip the mod");
                    Console.WriteLine("4) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.MissingVersion = 1;
                            config.SaveConfig();
                            break;
                        case 2:
                            config.MissingVersion = 2;
                            config.SaveConfig();
                            break;
                        case 3:
                            config.MissingVersion = 3;
                            config.SaveConfig();
                            break;
                        case 4:
                            Console.WriteLine("Update cancelled.");
                            break;
                        default:
                            Console.WriteLine("Invalid option.");
                            break;
                    }
                    break;
                case 8:
                    ClearConsole();
                    Console.WriteLine($"Current setting: (Move older mods? {config.MoveOlder})");
                    Console.WriteLine($"Whether or not to move mods older than current game version to this folder: {oldModPathName}.");
                    Console.WriteLine("1) Move older mods");
                    Console.WriteLine("2) Don't move older mods");
                    Console.WriteLine("3) Cancel:");
                    option = GetMainInput();
                    switch (option)
                    {
                        case 1:
                            config.MoveOlder = true;
                            config.SaveConfig();
                            break;
                        case 2:
                            config.MoveOlder = false;
                            config.SaveConfig();
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
                    ClearConsole();
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
            try
            {
                // Attempt to read key input
                ConsoleKeyInfo cki = Console.ReadKey(true);
                if (int.TryParse(cki.KeyChar.ToString(), out int option))
                {
                    return option; // Return the parsed integer
                }
            }
            catch
            {
                // Fallback to reading a line
                string? input = Console.ReadLine();
                if (int.TryParse(input, out int option))
                {
                    return option; // Return the parsed integer
                }
            }
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
    public bool? AlwaysUpdate { get; set; }
    public bool? CanDowngrade { get; set; }
    public bool? AlwaysDownload { get; set; }
    public int? MissingVersion { get; set; }
    public bool? MoveOlder { get; set; }

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
                else if (line.StartsWith("AlwaysUpdate"))
                {
                    if (bool.TryParse(line.Split('=')[1].Trim(), out bool alwaysUpdateValue))
                        AlwaysUpdate = alwaysUpdateValue;
                    else
                        AlwaysUpdate = false;
                }
                else if (line.StartsWith("CanDowngrade"))
                {
                    if (bool.TryParse(line.Split('=')[1].Trim(), out bool canDowngradeValue))
                        CanDowngrade = canDowngradeValue;
                    else
                        CanDowngrade = true;
                }
                else if (line.StartsWith("AlwaysDownload"))
                {
                    if (bool.TryParse(line.Split('=')[1].Trim(), out bool AlwaysDownloadValue))
                        AlwaysDownload = AlwaysDownloadValue;
                    else
                        AlwaysDownload = true;
                }
                else if (line.StartsWith("MissingVersion"))
                {
                    if (int.TryParse(line.Split('=')[1].Trim(), out int MissingVersionValue))
                        MissingVersion = MissingVersionValue;
                    else
                        MissingVersion = 2;
                }
                else if (line.StartsWith("MoveOlder"))
                {
                    if (bool.TryParse(line.Split('=')[1].Trim(), out bool MoveOlderValue))
                        MoveOlder = MoveOlderValue;
                    else
                        MoveOlder = false;
                }
            }
        }
        else
        {
            ModPath = "./Mods";
            GameVersion = "1.19.8";
            AlwaysUpdate = false;
            CanDowngrade = true;
            AlwaysDownload = true;
            MissingVersion = 2;
            MoveOlder = false;
            SaveConfig();
        }
    }

    public void SaveConfig()
    {
        File.WriteAllText(configPath,
            $"ModPath = {ModPath}\n" +
            $"GameVersion = {GameVersion}\n" +
            $"AlwaysUpdate = {AlwaysUpdate}\n" +
            $"CanDowngrade = {CanDowngrade}\n" +
            $"AlwaysDownload = {AlwaysDownload}\n" +
            $"MissingVersion = {MissingVersion}\n" +
            $"MoveOlder = {MoveOlder}\n"
            );
    }
}