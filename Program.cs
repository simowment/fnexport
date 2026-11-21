using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse_Conversion;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Writers;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;
using CUE4Parse_Conversion.Writers.UEFormat;

namespace fnexport;

public record EmoteEntry(string Id, string DisplayName, string PackagePath, string? AnimationPath);

class Program
{
    static readonly string DefaultPaksDir = @"D:\Games\Fortnite\FortniteGame\Content\Paks";
    static readonly string DefaultCoopRoot = @"C:\Users\got\Downloads\Call.of.Duty.4.Modern.Warfare\cod4-coop";
    static readonly string GameCoopRoot = @"C:\Users\got\Downloads\Call.of.Duty.4.Modern.Warfare\Call of Duty 4-Modern Warfare";
    static readonly string CacheFile = Path.Combine(AppContext.BaseDirectory, "emotes_cache.json");

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
        {
            PrintUsage();
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        string query = args.Length > 1 ? args[1] : "";
        bool install = args.Contains("--install") || args.Contains("-i") || cmd == "install";

        if (cmd == "list" || cmd == "search")
        {
            var provider = await InitProviderAsync();
            var emotes = GetOrBuildEmoteIndex(provider);
            var results = emotes.Values
                .Where(e => string.IsNullOrEmpty(query)
                    || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.DisplayName)
                .ToList();

            Console.WriteLine($"\nFound {results.Count} emotes:");
            foreach (var e in results.Take(50))
            {
                Console.WriteLine($"  [{e.Id}] {e.DisplayName}");
            }
            if (results.Count > 50)
            {
                Console.WriteLine($"  ... and {results.Count - 50} more. Refine your query.");
            }
            return 0;
        }

        if (cmd == "export" || cmd == "install")
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Please provide an emote name or ID to export.");
                Console.ResetColor();
                return 1;
            }

            var provider = await InitProviderAsync();
            var emotes = GetOrBuildEmoteIndex(provider);

            // Find matching emote: exact match first, then substring
            var target = emotes.Values.FirstOrDefault(e => e.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
                      ?? emotes.Values.FirstOrDefault(e => e.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase))
                      ?? emotes.Values.FirstOrDefault(e => e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                      ?? emotes.Values.FirstOrDefault(e => e.Id.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: No emote found matching '{query}'.");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine($"\n========================================================");
            Console.WriteLine($"Selected Emote: {target.DisplayName} ({target.Id})");
            Console.WriteLine($"========================================================");

            string cleanDirName = CleanIdent(target.DisplayName);
            string outDir = Path.Combine(DefaultCoopRoot, "coop_skins", "emotes", cleanDirName);
            Directory.CreateDirectory(outDir);

            if (!ExportEmote(provider, target, outDir, out string? animPath, out string? wavPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nExport failed for {target.DisplayName}.");
                Console.ResetColor();
                return 1;
            }

            if (Directory.Exists(GameCoopRoot))
            {
                string gameOutDir = Path.Combine(GameCoopRoot, "coop_skins", "emotes", cleanDirName);
                Directory.CreateDirectory(gameOutDir);
                foreach (var file in Directory.GetFiles(outDir))
                {
                    File.Copy(file, Path.Combine(gameOutDir, Path.GetFileName(file)), true);
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nExport successful!");
            Console.WriteLine($"  Animation: {animPath}");
            if (!string.IsNullOrEmpty(wavPath))
                Console.WriteLine($"  Audio:     {wavPath}");
            Console.ResetColor();

            if (install)
            {
                Console.WriteLine($"\nInstalling into CoD4 Coop mod wheel...");
                int code = RunAddEmote(animPath!, target.DisplayName, wavPath);
                return code;
            }

            return 0;
        }

        if (cmd == "skins")
        {
            var provider = await InitProviderAsync();
            var skins = GetOrBuildSkinIndex(provider);
            var found = skins.Values
                .Where(s => string.IsNullOrEmpty(query)
                    || s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || s.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.DisplayName).ToList();
            Console.WriteLine($"\nFound {found.Count} skins:");
            foreach (var s in found.Take(50)) Console.WriteLine($"  [{s.Id}] {s.DisplayName}");
            if (found.Count > 50) Console.WriteLine($"  ... and {found.Count - 50} more. Refine your query.");
            return 0;
        }

        if (cmd == "skin" || cmd == "skin-install")
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                Console.WriteLine("Error: Please provide a skin name or ID (see: skins <filter>).");
                return 1;
            }
            var provider = await InitProviderAsync();
            var skins = GetOrBuildSkinIndex(provider);
            var target = skins.Values.FirstOrDefault(s => s.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
                      ?? skins.Values.FirstOrDefault(s => s.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase))
                      ?? skins.Values.FirstOrDefault(s => s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                      ?? skins.Values.FirstOrDefault(s => s.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                Console.WriteLine($"Error: No skin found matching '{query}'.");
                return 1;
            }
            Console.WriteLine($"\nSelected Skin: {target.DisplayName} ({target.Id})");
            string skinDirName = CleanIdent(target.DisplayName);
            string skinOut = Path.Combine(DefaultCoopRoot, "coop_skins", "fortnite_skins", skinDirName);
            Directory.CreateDirectory(skinOut);
            var glbPaths = await ExportSkinAsync(provider, target, skinOut);
            if (glbPaths.Count == 0)
            {
                Console.WriteLine($"Export failed for {target.DisplayName}.");
                return 1;
            }
            Console.WriteLine($"\nExport successful: {glbPaths.Count} part(s) in {skinOut}");
            if (cmd == "skin-install")
            {
                Console.WriteLine("\nInstalling into the CoD4 Coop skin pack...");
                return RunAddSkin(glbPaths, skinDirName);
            }
            return 0;
        }

        PrintUsage();
        return 0;
    }

    static void PrintUsage()
    {
        Console.WriteLine(@"
Fortnite Exporter for CoD4 Coop Mod (fnexport)
===================================================
Usage:
  dotnet run -- list [filter]                  List or search available emotes
  dotnet run -- export <name/id> [--install]   Export .ueanim, .uemodel & .wav
  dotnet run -- install <name/id>              Export and install directly into mod
  dotnet run -- skins [filter]                 List or search available character skins
  dotnet run -- skin <name/id>                 Export a skin's parts as .glb
  dotnet run -- skin-install <name/id>         Export and install as a CoD4 skin
");
    }

    static Dictionary<string, EmoteEntry> GetOrBuildSkinIndex(DefaultFileProvider provider)
    {
        string cache = Path.Combine(AppContext.BaseDirectory, "skins_cache.json");
        if (File.Exists(cache))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, EmoteEntry>>(File.ReadAllText(cache));
                if (cached != null && cached.Count > 0) return cached;
            }
            catch (JsonException) { }
        }

        Console.Write("Building skin cache (one-time index)... ");
        var dict = new Dictionary<string, EmoteEntry>(StringComparer.OrdinalIgnoreCase);
        var files = provider.Files.Keys
            .Where(k => k.Contains("Athena/Items/Cosmetics/Characters/", StringComparison.OrdinalIgnoreCase)
                     && Path.GetFileName(k).StartsWith("CID_", StringComparison.OrdinalIgnoreCase)
                     && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var file in files)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            try
            {
                var pkg = provider.LoadPackage(file);
                var exp = pkg.GetExports().FirstOrDefault(e => e.ExportType == "AthenaCharacterItemDefinition");
                if (exp == null) continue;
                string name = exp.GetOrDefault<FText?>("DisplayName")?.Text
                           ?? exp.GetOrDefault<FText?>("ItemName")?.Text ?? id;
                dict[id] = new EmoteEntry(id, name, file, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException)
            {
                Console.WriteLine($"skip {id}: {ex.Message}");
            }
        }
        File.WriteAllText(cache, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"OK ({dict.Count} skins indexed)");
        return dict;
    }

    static async Task<List<string>> ExportSkinAsync(DefaultFileProvider provider, EmoteEntry entry, string outDir)
    {
        var written = new List<string>();
        var pkg = provider.LoadPackage(entry.PackagePath);
        var item = pkg.GetExports().First(e => e.ExportType == "AthenaCharacterItemDefinition");

        var partObjs = new List<UObject>();
        var bcpProp = item.Properties.FirstOrDefault(p => p.Name.Text == "BaseCharacterParts");
        var arr = bcpProp?.Tag?.GetValue<CUE4Parse.UE4.Assets.Objects.UScriptArray>();
        if (arr != null)
        {
            foreach (var elem in arr.Properties)
            {
                var raw = elem.GetValue<object>()?.ToString();
                var path = ParseObjectPath(raw);
                var rp = string.IsNullOrEmpty(path) ? null : ResolvePath(provider, path);
                if (rp == null)
                {
                    Console.WriteLine($"  missing in local paks (streamed?): {path}");
                    continue;
                }
                var pp = provider.LoadPackage(rp);
                var pe = pp.GetExports().FirstOrDefault(e => e.ExportType.Contains("CharacterPart", StringComparison.OrdinalIgnoreCase));
                if (pe != null) partObjs.Add(pe);
            }
        }

        Console.WriteLine($"Character parts: {partObjs.Count}");
        var options = new ExportOptions(
            meshFormat: EMeshFormat.Gltf2,
            textureFormat: ETextureFormat.Png,
            exportMaterials: true);
        string stage = Path.Combine(Path.GetTempPath(), "fnexport_skin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var session = new ExportSession(null!);
        foreach (var part in partObjs)
        {
            var meshProp = part.Properties.FirstOrDefault(p => p.Name.Text == "SkeletalMesh");
            var meshPath = ParseObjectPath(meshProp?.Tag?.GetValue<object>()?.ToString());
            var rm = string.IsNullOrEmpty(meshPath) ? null : ResolvePath(provider, meshPath);
            var mesh = rm == null ? null : provider.LoadPackage(rm).GetExports().OfType<USkeletalMesh>().FirstOrDefault();
            if (mesh == null)
            {
                Console.WriteLine($"  part {part.Name}: no SkeletalMesh");
                continue;
            }
            if (mesh.LODModels == null || mesh.LODModels.Length == 0)
            {
                Console.WriteLine($"  part {part.Name}: {mesh.Name} has no local LODs (streamed/on-demand asset; enable 'Pre-download Streamed Assets' in Epic Games Launcher)");
                continue;
            }
            Console.WriteLine($"  part {part.Name}: {mesh.Name} ({mesh.LODModels.Length} LODs)");
            session.Add(mesh);
        }
        if (session.HasQueuedItems)
        {
            var res = await session.RunAsync(stage, options, null!, CancellationToken.None);
            foreach (var r in res.Where(x => !x.Success))
                Console.WriteLine($"  export warning ({r.ObjectPath}): {r.Error}");
            foreach (var file in Directory.EnumerateFiles(stage, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
            {
                string dest = Path.Combine(outDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
                written.Add(dest);
            }
        }
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        return written;
    }

    static int RunAddSkin(List<string> glbPaths, string name)
    {
        string script = Path.Combine(DefaultCoopRoot, "tools", "skin", "addskin.py");
        var psi = new ProcessStartInfo("python")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("add");
        psi.ArgumentList.Add(glbPaths[0]);
        psi.ArgumentList.Add(name);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start python");
        while (!proc.StandardOutput.EndOfStream) Console.WriteLine(proc.StandardOutput.ReadLine());
        proc.WaitForExit();
        return proc.ExitCode;
    }

    static async Task<DefaultFileProvider> InitProviderAsync()
    {
        Console.Write("Mounting Fortnite game packages... ");
        var provider = new DefaultFileProvider(new DirectoryInfo(DefaultPaksDir), SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE6_0));
        provider.Initialize();

        // Load mappings
        string usmap = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"FortnitePorting\.data\43.10.usmap");
        if (File.Exists(usmap))
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap);

        // Fetch AES keys (cached locally or online)
        string keysCache = Path.Combine(AppContext.BaseDirectory, "aes_keys.json");
        string jsonStr = "";
        if (File.Exists(keysCache) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(keysCache)).TotalHours < 24)
        {
            jsonStr = File.ReadAllText(keysCache);
        }
        else
        {
            using var http = new HttpClient();
            jsonStr = await http.GetStringAsync("https://api.fortniteporting.app/v2/fortnite/versions/latest");
            File.WriteAllText(keysCache, jsonStr);
        }

        using var doc = JsonDocument.Parse(jsonStr);
        var keysObj = doc.RootElement.GetProperty("keys");
        var mainKey = keysObj.GetProperty("mainKey").GetProperty("key").GetString()!;
        provider.SubmitKey(new FGuid(), new FAesKey(mainKey));

        if (keysObj.TryGetProperty("extraKeys", out var extraKeys))
        {
            foreach (var elem in extraKeys.EnumerateArray())
                provider.SubmitKey(new FGuid(elem.GetProperty("guid").GetString()!), new FAesKey(elem.GetProperty("key").GetString()!));
        }

        provider.LoadVirtualPaths();
        Console.WriteLine($"OK ({provider.Files.Count:N0} files mounted)");
        return provider;
    }

    static Dictionary<string, EmoteEntry> GetOrBuildEmoteIndex(DefaultFileProvider provider)
    {
        if (File.Exists(CacheFile))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, EmoteEntry>>(File.ReadAllText(CacheFile));
                if (cached != null && cached.Count > 0)
                    return cached;
            }
            catch { }
        }

        Console.Write("Building emote cache (one-time index)... ");
        var dict = new Dictionary<string, EmoteEntry>(StringComparer.OrdinalIgnoreCase);

        var danceFiles = provider.Files.Keys
            .Where(k => (k.Contains("Athena/Items/Cosmetics/Dances/", StringComparison.OrdinalIgnoreCase) || k.Contains("/Dances/EID_", StringComparison.OrdinalIgnoreCase))
                     && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in danceFiles)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            try
            {
                var pkg = provider.LoadPackage(file);
                var exp = pkg.GetExports().FirstOrDefault(e => e.ExportType == "AthenaDanceItemDefinition");
                if (exp == null) continue;

                string name = exp.GetOrDefault<FText?>("DisplayName")?.Text
                           ?? exp.GetOrDefault<FText?>("ItemName")?.Text
                           ?? id.Replace("EID_", "");

                string? animPath = null;
                foreach (var prop in exp.Properties)
                {
                    if (prop.Name.Text == "Animation")
                    {
                        var val = prop.Tag?.GetValue<object>()?.ToString();
                        animPath = ParseObjectPath(val);
                        break;
                    }
                }

                dict[id] = new EmoteEntry(id, name, file, animPath);
            }
            catch { }
        }

        File.WriteAllText(CacheFile, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"OK ({dict.Count} emotes indexed)");
        return dict;
    }

    static bool ExportEmote(DefaultFileProvider provider, EmoteEntry entry, string outDir, out string? animFilePath, out string? wavFilePath)
    {
        animFilePath = null;
        wavFilePath = null;

        // Resolve montage or sequence path
        string? animPkgPath = null;
        if (!string.IsNullOrEmpty(entry.AnimationPath))
        {
            animPkgPath = ResolvePath(provider, entry.AnimationPath);
        }

        // Fallback search if animation path was empty or unresolved
        if (string.IsNullOrEmpty(animPkgPath))
        {
            string baseName = entry.Id.Replace("EID_", "");
            animPkgPath = provider.Files.Keys.FirstOrDefault(k => !k.Contains("/Juno/", StringComparison.OrdinalIgnoreCase)
                                                               && !k.Contains("/FigureCosmetics/", StringComparison.OrdinalIgnoreCase)
                                                               && (k.Contains($"Emote_{baseName}_CMM", StringComparison.OrdinalIgnoreCase) || k.Contains($"Emote_{baseName}", StringComparison.OrdinalIgnoreCase))
                                                               && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrEmpty(animPkgPath))
        {
            Console.WriteLine("Could not locate animation package for emote.");
            return false;
        }

        Console.WriteLine($"Loading animation package: {animPkgPath}");
        var animPackage = provider.LoadPackage(animPkgPath);
        var montage = animPackage.GetExports().OfType<UAnimMontage>().FirstOrDefault();
        var sequence = animPackage.GetExports().OfType<UAnimSequence>().FirstOrDefault();

        USkeleton? skeleton = null;
        CAnimSet? animSet = null;

        if (montage != null)
        {
            skeleton = montage.Skeleton.Load<USkeleton>();
            animSet = AnimConverter.ConvertAnims(skeleton!, montage);
        }
        else if (sequence != null)
        {
            skeleton = sequence.Skeleton.Load<USkeleton>();
            animSet = AnimConverter.ConvertAnims(skeleton!, sequence);
        }

        if (skeleton == null || animSet == null || animSet.Sequences.Count == 0)
        {
            Console.WriteLine("Failed to extract skeleton or animation sequence.");
            return false;
        }

        Console.WriteLine($"Found {animSet.Sequences.Count} animation sequence(s) in animSet:");
        for (int sIdx = 0; sIdx < animSet.Sequences.Count; sIdx++)
        {
            var s = animSet.Sequences[sIdx];
            Console.WriteLine($"  [{sIdx}] {s.Name}: {s.NumFrames} frames");
        }

        var exportOptions = new ExportOptions();

        // 1. Save Skeleton .uemodel
        var skelDto = new SkeletonDto(skeleton);
        var ueModel = new UEModel(skeleton.Name, skeleton.GetPathName(), skelDto, exportOptions);
        var skelWriter = new FArchiveWriter();
        ueModel.Save(skelWriter);
        string skelFile = Path.Combine(outDir, $"{skeleton.Name}.uemodel");
        File.WriteAllBytes(skelFile, skelWriter.GetBuffer());
        Console.WriteLine($"Wrote skeleton: {Path.GetFileName(skelFile)}");

        // 2. Save Animation .ueanim
        CAnimSequence targetSeq;
        var loopSeqs = animSet.Sequences.Where(s => s.Name.Contains("loop", StringComparison.OrdinalIgnoreCase)).ToList();
        if (loopSeqs.Count > 0)
        {
            targetSeq = loopSeqs
                .OrderBy(s => s.Name.Contains("traversal", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.NumFrames)
                .First();
        }
        else
        {
            var nonIntro = animSet.Sequences
                .Where(s => !s.Name.Contains("intro", StringComparison.OrdinalIgnoreCase)
                         && !s.Name.Contains("start", StringComparison.OrdinalIgnoreCase)
                         && !s.Name.Contains("end", StringComparison.OrdinalIgnoreCase)
                         && !s.Name.Contains("outro", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nonIntro.Count > 0)
            {
                targetSeq = nonIntro.OrderByDescending(s => s.NumFrames).First();
            }
            else
            {
                targetSeq = animSet.Sequences.OrderByDescending(s => s.NumFrames).First();
            }
        }
        int targetSeqIndex = animSet.Sequences.IndexOf(targetSeq);
        Console.WriteLine($"Selected animation sequence: [{targetSeqIndex}] {targetSeq.Name} ({targetSeq.NumFrames} frames)");

        var ueAnim = new UEAnim(targetSeq.Name, targetSeq.Name, animSet, targetSeqIndex, exportOptions);
        var animWriter = new FArchiveWriter();
        ueAnim.Save(animWriter);
        animFilePath = Path.Combine(outDir, $"{targetSeq.Name}.ueanim");
        File.WriteAllBytes(animFilePath, animWriter.GetBuffer());
        Console.WriteLine($"Wrote animation: {Path.GetFileName(animFilePath)}");

        // 3. Audio Extraction
        wavFilePath = ExtractAudio(provider, animPackage, entry, outDir);

        return true;
    }

    static string? ExtractAudio(DefaultFileProvider provider, IPackage animPkg, EmoteEntry entry, string outDir)
    {
        try
        {
            // Find sound cue or wave referenced in montage notify exports
            string? soundPath = null;

            // Check montage notifies first
            foreach (var exp in animPkg.GetExports())
            {
                if (exp.ExportType.Contains("Notify") || exp.ExportType.Contains("Sound"))
                {
                    foreach (var prop in exp.Properties)
                    {
                        var str = prop.Tag?.GetValue<object>()?.ToString();
                        Console.WriteLine($"[AudioNotify] {exp.ExportType} -> {prop.Name.Text}: {str}");
                        if (prop.Name.Text == "EmoteSound1P" || prop.Name.Text == "EmoteSound3P" || prop.Name.Text == "Sound")
                        {
                            var parsed = ParseObjectPath(str);
                            if (!string.IsNullOrEmpty(parsed) && !parsed.Contains("Foley", StringComparison.OrdinalIgnoreCase))
                            {
                                soundPath = parsed;
                                break;
                            }
                        }
                    }
                    if (soundPath != null) break;
                }
            }

            // Fallback: Check dance item definition package
            if (string.IsNullOrEmpty(soundPath) && !string.IsNullOrEmpty(entry.PackagePath))
            {
                try
                {
                    var itemPkg = provider.LoadPackage(entry.PackagePath);
                    foreach (var exp in itemPkg.GetExports())
                    {
                        foreach (var prop in exp.Properties)
                        {
                            if (prop.Name.Text.Contains("Sound") || prop.Name.Text.Contains("Audio"))
                            {
                                var parsed = ParseObjectPath(prop.Tag?.GetValue<object>()?.ToString());
                                if (!string.IsNullOrEmpty(parsed) && (parsed.Contains("Sound") || parsed.Contains("Cue") || parsed.Contains("Audio")))
                                {
                                    soundPath = parsed;
                                    break;
                                }
                            }
                        }
                        if (soundPath != null) break;
                    }
                }
                catch { }
            }

            // Fallback 2: Search by emote name in Athena/Sounds/Emotes/
            if (string.IsNullOrEmpty(soundPath))
            {
                string baseName = entry.Id.Replace("EID_", "");
                var candidates = provider.Files.Keys.Where(k => (k.Contains("Sounds/Emotes/", StringComparison.OrdinalIgnoreCase) || k.Contains("Athena/Sounds/", StringComparison.OrdinalIgnoreCase))
                                                                 && (k.Contains(baseName, StringComparison.OrdinalIgnoreCase) || k.Contains(CleanIdent(entry.DisplayName), StringComparison.OrdinalIgnoreCase))
                                                                 && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).ToList();
                Console.WriteLine($"Found {candidates.Count} candidate sound assets for {entry.DisplayName}:");
                foreach (var c in candidates)
                    Console.WriteLine("  " + c);

                soundPath = candidates
                    .OrderByDescending(k => k.Contains("Music", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(k => k.Contains("Foley", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            }

            if (string.IsNullOrEmpty(soundPath))
            {
                Console.WriteLine("No audio notification found in animation montage.");
                return null;
            }

            string? realSoundPkgPath = ResolvePath(provider, soundPath);
            if (string.IsNullOrEmpty(realSoundPkgPath))
            {
                Console.WriteLine($"Could not locate sound package on disk for {soundPath}.");
                return null;
            }
            Console.WriteLine($"Loading sound package: {realSoundPkgPath}");
            var soundPkg = provider.LoadPackage(realSoundPkgPath);

            USoundWave? soundWave = soundPkg.GetExports().OfType<USoundWave>().FirstOrDefault();

            // If it's a SoundCue, look for SoundNodeWavePlayer exports
            if (soundWave == null)
            {
                var candidateWaves = new List<USoundWave>();
                foreach (var exp in soundPkg.GetExports())
                {
                    foreach (var prop in exp.Properties)
                    {
                        if (prop.Name.Text == "SoundWaveAssetPtr" || prop.Name.Text == "SoundWave")
                        {
                            var wavePath = ParseObjectPath(prop.Tag?.GetValue<object>()?.ToString());
                            if (!string.IsNullOrEmpty(wavePath))
                            {
                                var realWavePkgPath = ResolvePath(provider, wavePath);
                                if (!string.IsNullOrEmpty(realWavePkgPath))
                                {
                                    var wavePkg = provider.LoadPackage(realWavePkgPath);
                                    var waves = wavePkg.GetExports().OfType<USoundWave>();
                                    candidateWaves.AddRange(waves);
                                }
                            }
                        }
                    }
                }

                if (candidateWaves.Count > 0)
                {
                    soundWave = candidateWaves
                        .OrderByDescending(w => w.Name.Contains("Loop", StringComparison.OrdinalIgnoreCase)
                                             || w.Name.Contains("Music", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(w => w.Name.Contains("Intro", StringComparison.OrdinalIgnoreCase)
                                  || w.Name.Contains("Start", StringComparison.OrdinalIgnoreCase)
                                  || w.Name.Contains("Foley", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                }
            }

            if (soundWave == null)
            {
                Console.WriteLine("Could not locate USoundWave asset.");
                return null;
            }

            Console.WriteLine($"Decoding audio stream: {soundWave.Name}...");
            SoundDecoder.Decode(soundWave, true, out var format, out var rawData);

            if (rawData == null || rawData.Length == 0)
            {
                Console.WriteLine("Sound decoder returned empty data.");
                return null;
            }

            string cleanName = CleanIdent(entry.DisplayName);
            string tempRaw = Path.Combine(outDir, $"sound_raw.{format.ToLowerInvariant()}");
            string outWav = Path.Combine(outDir, $"{cleanName}.wav");
            File.WriteAllBytes(tempRaw, rawData);

            bool decoded = ConvertRawAudioToWav(tempRaw, format, outWav);
            try { File.Delete(tempRaw); } catch { }

            if (decoded && File.Exists(outWav))
            {
                Console.WriteLine($"Successfully produced WAV: {Path.GetFileName(outWav)} ({new FileInfo(outWav).Length:N0} bytes)");
                return outWav;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Audio extraction warning: {ex.Message}");
        }

        return null;
    }

    static bool ConvertRawAudioToWav(string rawFile, string format, string wavFile)
    {
        string baseDir = AppContext.BaseDirectory;
        string radadec = FindTool("radadec.exe");
        string binkadec = FindTool("binkadec.exe");
        string vgmstream = FindTool("vgmstream-cli.exe");

        ProcessStartInfo psi;

        if (format.Equals("RADA", StringComparison.OrdinalIgnoreCase) && File.Exists(radadec))
        {
            psi = new ProcessStartInfo(radadec, $"-i \"{rawFile}\" -o \"{wavFile}\"") { UseShellExecute = false, CreateNoWindow = true };
        }
        else if ((format.Equals("BINKA", StringComparison.OrdinalIgnoreCase) || format.Equals("UEBA", StringComparison.OrdinalIgnoreCase)) && File.Exists(binkadec))
        {
            psi = new ProcessStartInfo(binkadec, $"-i \"{rawFile}\" -o \"{wavFile}\"") { UseShellExecute = false, CreateNoWindow = true };
        }
        else if (File.Exists(vgmstream))
        {
            psi = new ProcessStartInfo(vgmstream, $"-o \"{wavFile}\" \"{rawFile}\"") { UseShellExecute = false, CreateNoWindow = true };
        }
        else
        {
            Console.WriteLine($"No compatible audio decoder found for {format}.");
            return false;
        }

        using var proc = Process.Start(psi);
        proc?.WaitForExit();
        return proc?.ExitCode == 0;
    }

    static string FindTool(string exeName)
    {
        // 1. tools/fnexport/decoders
        string local1 = Path.Combine(AppContext.BaseDirectory, "decoders", exeName);
        if (File.Exists(local1)) return local1;

        string local2 = Path.Combine(DefaultCoopRoot, "tools", "fnexport", "decoders", exeName);
        if (File.Exists(local2)) return local2;

        // 2. FortnitePorting .data
        string appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"FortnitePorting\.data");
        string fp1 = Path.Combine(appdata, "rada", exeName);
        if (File.Exists(fp1)) return fp1;
        string fp2 = Path.Combine(appdata, "binka", exeName);
        if (File.Exists(fp2)) return fp2;
        string fp3 = Path.Combine(appdata, "vgmstream", exeName);
        if (File.Exists(fp3)) return fp3;

        return exeName;
    }

    static int RunAddEmote(string ueAnimPath, string displayName, string? wavPath)
    {
        string script = Path.Combine(DefaultCoopRoot, "tools", "skin", "addemote.py");
        string args = $"\"{script}\" add \"{ueAnimPath}\" \"{displayName}\"";
        if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
        {
            args += $" --music \"{wavPath}\"";
        }

        var psi = new ProcessStartInfo("python", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return 1;

        while (!proc.StandardOutput.EndOfStream)
        {
            Console.WriteLine(proc.StandardOutput.ReadLine());
        }

        proc.WaitForExit();
        return proc.ExitCode;
    }

    static string? ResolvePath(DefaultFileProvider provider, string virtualPath)
    {
        try
        {
            var fixedPath = provider.FixPath(virtualPath);
            if (provider.Files.ContainsKey(fixedPath)) return fixedPath;
            var found = provider.Files.Keys.FirstOrDefault(k => k.Equals(fixedPath, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
        }
        catch { }
        string fileName = Path.GetFileName(virtualPath);
        return provider.Files.Keys.FirstOrDefault(k => !k.Contains("/Juno/", StringComparison.OrdinalIgnoreCase)
                                                    && !k.Contains("/FigureCosmetics/", StringComparison.OrdinalIgnoreCase)
                                                    && (k.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) || k.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)));
    }

    static string ParseObjectPath(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return "";
        var match = Regex.Match(val, @"'([^']+)'");
        string p = match.Success ? match.Groups[1].Value : val.Trim();
        if (p.Contains('.'))
            p = p.Substring(0, p.IndexOf('.'));
        if (!p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            p += ".uasset";
        return p;
    }

    static string CleanIdent(string name)
    {
        return Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]", "_").Trim('_');
    }
}
