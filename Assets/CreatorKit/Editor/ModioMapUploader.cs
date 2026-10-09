using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Ships with the Snapshot VR Creator Kit. Snapshot > UGC > Upload Maps to mod.io...
//
// Uploads a built map to its mod.io page, so you don't have to upload three files by hand and
// tag each with the right platform. It sends exactly what Build Map Bundle wrote:
//   ModioUploads/<bundle>/<bundle>_<platform>.zip
// Each zip becomes its own modfile, tagged with its one platform (the android zip is the
// Meta Quest file), because the game picks its file by platform.
//
// It adds files to a mod page that already exists. Create the page on mod.io first (name, logo,
// the Arena or Map tag); which page a bundle belongs to is then remembered in
// ProjectSettings/ModioMapUploads.json.
//
// You sign in with your own mod.io account, and you can only upload to mods you own or are on
// the team of. The sign-in token is kept in this machine's editor preferences, never in the
// project. A new file still goes through the game's normal review before players see it.
//
// Every request is made synchronously: the editor waits for mod.io, with a progress bar on
// uploads. That keeps the tool usable from a script or the command line as well as the window.
public static class ModioMapUploader
{
    public const string LogPrefix = "[ModioUpload]";

    // Snapshot VR on mod.io. The API key identifies the game, not you: it can read public data
    // and start a sign-in, and nothing else. It is the same key every copy of the game carries.
    public const long GameId = 11905;
    public const string ServerUrl = "https://g-11905.modapi.io/v1";
    const string ApiKey = "3179f5f6ca29993d9eace330858912ab";

    // Which mod each bundle uploads to. Outside Assets/CreatorKit, so a kit upgrade keeps it.
    public static string ConfigPath = "ProjectSettings/ModioMapUploads.json";

    // More folders of upload zips to list beside this project's own ModioUploads folder, as
    // (folder, label). Evaluated on every scan.
    public static Func<IEnumerable<(string root, string label)>> ExtraUploadRoots;

    const string PrefToken = "SnapshotVR.ModioMapUploader.AccessToken";
    const string PrefTokenUser = "SnapshotVR.ModioMapUploader.AccessTokenUser";

    // Bundle file suffix -> the platform name mod.io uses. The android bundle is the Meta Quest
    // one, and mod.io calls that platform "oculus".
    public static readonly (string suffix, string modioPlatform, string label)[] Platforms =
    {
        ("windows", "windows", "Windows"),
        ("android", "oculus",  "Meta Quest"),
        ("linux",   "linux",   "Linux"),
    };

    [Serializable] class MapEntry { public string bundle; public long modId; }
    [Serializable] class Config { public List<MapEntry> maps = new List<MapEntry>(); }

    public sealed class Candidate
    {
        public string Bundle;
        public string Source;          // where the zips were found
        public long ModId;             // 0 = not matched to a mod page yet
        // suffix -> zip path, only for the platforms that are built
        public Dictionary<string, string> Zips = new Dictionary<string, string>();
        public DateTime NewestWriteUtc;
    }

    public sealed class ModFile
    {
        public long ModfileId;
        public string Version;
        public string Md5;
    }

    public struct ModSummary
    {
        public long Id;
        public string Name;
        public bool Archived;   // mod.io status 3: taken down, kept on the account
        public string Label => Archived ? Name + " (archived)" : Name;
    }

    // ---------------------------------------------------------------- bundle -> mod mapping

    static Config LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return new Config();
        return JsonUtility.FromJson<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
    }

    public static long GetModId(string bundle) =>
        LoadConfig().maps.FirstOrDefault(m => m.bundle == bundle)?.modId ?? 0;

    public static void SetModId(string bundle, long modId)
    {
        var config = LoadConfig();
        var entry = config.maps.FirstOrDefault(m => m.bundle == bundle);
        if (entry == null) config.maps.Add(entry = new MapEntry { bundle = bundle });
        if (entry.modId == modId && File.Exists(ConfigPath)) return;

        entry.modId = modId;
        config.maps = config.maps.OrderBy(m => m.bundle, StringComparer.Ordinal).ToList();
        File.WriteAllText(ConfigPath, JsonUtility.ToJson(config, true) + "\n");
        if (ConfigPath.StartsWith("Assets/")) AssetDatabase.ImportAsset(ConfigPath);
    }

    // ---------------------------------------------------------------- what is built

    // Every map with at least one upload zip.
    public static List<Candidate> FindCandidates()
    {
        var result = new List<Candidate>();
        AddFrom(BuildMapBundle.UploadsRoot, "This project", result);
        if (ExtraUploadRoots != null)
            foreach (var (root, label) in ExtraUploadRoots())
                AddFrom(root, label, result);
        return result;
    }

    static void AddFrom(string root, string source, List<Candidate> result)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string bundle = Path.GetFileName(dir);
            if (bundle.StartsWith("kitcheck_")) continue;   // a build check, not a map
            var c = new Candidate { Bundle = bundle, Source = source, ModId = GetModId(bundle) };
            foreach (var (suffix, _, _) in Platforms)
            {
                string zip = Path.Combine(dir, $"{bundle}_{suffix}.zip");
                if (!File.Exists(zip)) continue;
                c.Zips[suffix] = zip;
                var written = File.GetLastWriteTimeUtc(zip);
                if (written > c.NewestWriteUtc) c.NewestWriteUtc = written;
            }
            if (c.Zips.Count > 0) result.Add(c);
        }
    }

    public static string Md5Of(string path)
    {
        using (var md5 = MD5.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    // ---------------------------------------------------------------- validation

    // Runs Validate Map on the map prefab(s) behind a bundle, if this project has them. Returns
    // the number of errors, or -1 when there is nothing here to validate (the zips came from
    // another project). `summary` lists the errors.
    public static int ValidateBundle(string bundle, out string summary)
    {
        summary = "";
        var assets = AssetDatabase.GetAssetPathsFromAssetBundle(bundle);
        if (assets.Length == 0)
        {
            // The kit's reference maps carry no bundle name; they are listed in a file instead.
            var reference = BuildMapBundle.LoadReferenceMapBuilds(BuildMapBundle.ReferenceMapListPath, quiet: true);
            if (reference != null)
                foreach (var build in reference)
                    if (build.assetBundleName == bundle) assets = build.assetNames;
        }

        var prefabs = BuildMapBundle.TopLevelAssets(assets)
            .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefabs.Count == 0) return -1;

        var errors = new List<string>();
        foreach (var prefab in prefabs)
            foreach (var finding in UGCMapValidator.ValidatePrefabAsset(prefab))
                if (finding.Level.ToString() == "Error")
                    errors.Add(finding.Message);

        summary = string.Join("\n", errors.Select(e => "      - " + Truncate(e, 240)));
        return errors.Count;
    }

    // ---------------------------------------------------------------- sign-in

    public static string Token => EditorPrefs.GetString(PrefToken, "");
    public static string TokenUser => EditorPrefs.GetString(PrefTokenUser, "");
    public static bool SignedIn => !string.IsNullOrEmpty(Token);

    public static void SignOut()
    {
        EditorPrefs.DeleteKey(PrefToken);
        EditorPrefs.DeleteKey(PrefTokenUser);
    }

    // Step 1 of mod.io's email sign-in: mod.io mails a five-character code to that address.
    public static bool RequestEmailCode(string email, out string error)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["api_key"] = ApiKey, ["email"] = email });
        return Ok(Send(HttpMethod.Post, ServerUrl + "/oauth/emailrequest", form, null), out _, out error);
    }

    // Step 2: trade the code for a token for your account, and remember it on this machine.
    public static bool ExchangeEmailCode(string code, out string error)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["api_key"] = ApiKey, ["security_code"] = code.Trim() });
        if (!Ok(Send(HttpMethod.Post, ServerUrl + "/oauth/emailexchange", form, null), out var json, out error))
            return false;
        return StoreToken((string)json["access_token"], out error);
    }

    // For a token made by hand at mod.io/me/access (it needs write scope).
    public static bool StoreToken(string token, out string error)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0) { error = "no token"; return false; }

        if (!Ok(Send(HttpMethod.Get, ServerUrl + "/me", null, token), out var me, out error))
            return false;

        EditorPrefs.SetString(PrefToken, token);
        EditorPrefs.SetString(PrefTokenUser, (string)me["username"] ?? "?");
        return true;
    }

    // ---------------------------------------------------------------- reading mod.io

    // The Snapshot VR mods your account owns or is on the team of.
    public static bool GetMyMods(out List<ModSummary> mods, out string error)
    {
        mods = new List<ModSummary>();
        if (!Ok(Send(HttpMethod.Get, $"{ServerUrl}/me/mods?game_id={GameId}&_limit=100", null, Token), out var json, out error))
            return false;
        foreach (var mod in json["data"] as JArray ?? new JArray())
            mods.Add(new ModSummary
            {
                Id = (long?)mod["id"] ?? 0,
                Name = (string)mod["name"],
                Archived = ((int?)mod["status"] ?? 1) == 3,
            });
        // Archived mods last: two mods can share a name, and the live one is the one you want.
        mods = mods.OrderBy(m => m.Archived).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    // The mod's name, and the file each platform currently serves to players (suffix -> file).
    public static bool GetLive(long modId, out string modName, out Dictionary<string, ModFile> live, out string error)
    {
        modName = null;
        live = new Dictionary<string, ModFile>();
        string modUrl = $"{ServerUrl}/games/{GameId}/mods/{modId}";

        if (!Ok(Send(HttpMethod.Get, modUrl, null, Token), out var mod, out error))
            return false;
        modName = (string)mod["name"];

        foreach (var p in mod["platforms"] as JArray ?? new JArray())
        {
            string platform = (string)p["platform"];
            long fileId = (long?)p["modfile_live"] ?? 0;
            string suffix = Platforms.FirstOrDefault(x => x.modioPlatform == platform).suffix;
            if (suffix == null || fileId == 0) continue;

            if (!Ok(Send(HttpMethod.Get, $"{modUrl}/files/{fileId}", null, Token), out var file, out error))
                return false;
            live[suffix] = new ModFile
            {
                ModfileId = fileId,
                Version = (string)file["version"],
                Md5 = (string)file["filehash"]?["md5"],
            };
        }
        return true;
    }

    // The mod's newest files, live or not, keyed "<suffix>:<md5>". A file waiting for review is
    // not live, so GetLive does not see it, and without this a second run would upload it
    // again. Empty if the list can't be read: the upload then just isn't skipped.
    static Dictionary<string, ModFile> GetRecentUploads(long modId)
    {
        var result = new Dictionary<string, ModFile>();
        string url = $"{ServerUrl}/games/{GameId}/mods/{modId}/files?_sort=-id&_limit=30";
        if (!Ok(Send(HttpMethod.Get, url, null, Token), out var json, out _)) return result;

        foreach (var file in json["data"] as JArray ?? new JArray())
        {
            string md5 = (string)file["filehash"]?["md5"];
            foreach (var p in file["platforms"] as JArray ?? new JArray())
            {
                string platform = (string)p["platform"];
                string suffix = Platforms.FirstOrDefault(x => x.modioPlatform == platform).suffix;
                string key = suffix + ":" + md5;
                if (suffix == null || md5 == null || result.ContainsKey(key)) continue;
                result[key] = new ModFile { ModfileId = (long?)file["id"] ?? 0, Version = (string)file["version"], Md5 = md5 };
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- upload

    public sealed class UploadResult
    {
        public int Uploaded, Skipped, Failed;
        public readonly StringBuilder Log = new StringBuilder();
    }

    // Uploads the chosen platforms of one map, each as its own modfile.
    //   - A platform whose file is already on the mod page (same md5, live or waiting for
    //     review) is skipped unless `force`.
    //   - A map that fails Validate Map is not uploaded unless `ignoreValidation`.
    //   - With `dryRun`, nothing is sent: the report says what would be.
    public static UploadResult Upload(Candidate map, IEnumerable<string> suffixes, string version, string changelog,
        bool dryRun, bool force = false, bool ignoreValidation = false)
    {
        var result = new UploadResult();
        var log = result.Log;
        log.AppendLine($"{map.Bundle} -> mod {map.ModId}{(dryRun ? "  (DRY RUN, nothing sent)" : "")}");

        if (map.ModId <= 0)
        {
            log.AppendLine("   not matched to a mod page yet - skipped");
            result.Failed++;
            return result;
        }
        if (!SignedIn)
        {
            log.AppendLine("   not signed in to mod.io - skipped");
            result.Failed++;
            return result;
        }
        if (!GetLive(map.ModId, out string modName, out var live, out string error))
        {
            log.AppendLine("   could not read the mod: " + error);
            result.Failed++;
            return result;
        }
        log.AppendLine($"   mod: \"{modName}\"");

        int validationErrors = ValidateBundle(map.Bundle, out string validation);
        if (validationErrors < 0)
            log.AppendLine("   Validate Map: not run - this project does not hold the map's prefab");
        else if (validationErrors == 0)
            log.AppendLine("   Validate Map: no errors");
        else
        {
            log.AppendLine($"   Validate Map: {validationErrors} error(s)\n{validation}");
            if (!ignoreValidation)
            {
                log.AppendLine("   not uploaded - fix the errors (Snapshot > UGC > Validate Map), rebuild, and try again");
                result.Failed++;
                return result;
            }
            log.AppendLine("   uploading anyway, as asked");
        }

        var uploaded = GetRecentUploads(map.ModId);
        foreach (var (suffix, modioPlatform, label) in Platforms)
        {
            if (!suffixes.Contains(suffix)) continue;
            if (!map.Zips.TryGetValue(suffix, out string zip))
            {
                log.AppendLine($"   {label}: no zip built - skipped");
                result.Failed++;
                continue;
            }

            string md5 = Md5Of(zip);
            float mb = new FileInfo(zip).Length / 1024f / 1024f;
            if (!force && live.TryGetValue(suffix, out var current) && current.Md5 == md5)
            {
                log.AppendLine($"   {label}: identical to the live file ({current.ModfileId}, v{current.Version}) - skipped");
                result.Skipped++;
                continue;
            }
            if (!force && uploaded.TryGetValue(suffix + ":" + md5, out var earlier))
            {
                log.AppendLine($"   {label}: already uploaded as modfile {earlier.ModfileId} (v{earlier.Version}), not live yet - skipped");
                result.Skipped++;
                continue;
            }
            if (dryRun)
            {
                log.AppendLine($"   {label}: WOULD upload {Path.GetFileName(zip)} ({mb:F2} MB) as v{version}");
                result.Uploaded++;
                continue;
            }

            EditorUtility.DisplayProgressBar("Uploading to mod.io", $"{map.Bundle} - {label} ({mb:F1} MB)", 0.5f);
            FileStream stream = null;
            try
            {
                // No `using` on the form: Send hands it to the request, and the request disposes
                // it (and the stream inside it). Mono's MultipartContent throws a
                // NullReferenceException when it is disposed a second time.
                stream = File.OpenRead(zip);
                var form = new MultipartFormDataContent();
                var file = new StreamContent(stream);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                form.Add(file, "filedata", Path.GetFileName(zip));
                form.Add(new StringContent(version ?? ""), "version");
                form.Add(new StringContent(changelog ?? ""), "changelog");
                form.Add(new StringContent("true"), "active");
                form.Add(new StringContent(md5), "filehash");
                form.Add(new StringContent(modioPlatform), "platforms[]");

                var response = Send(HttpMethod.Post, $"{ServerUrl}/games/{GameId}/mods/{map.ModId}/files", form, Token, timeoutSeconds: 900);
                if (Ok(response, out var json, out error))
                {
                    log.AppendLine($"   {label}: uploaded as modfile {(long?)json["id"]} (v{version}, {mb:F2} MB)");
                    result.Uploaded++;
                }
                else
                {
                    log.AppendLine($"   {label}: FAILED - {error}");
                    result.Failed++;
                }
            }
            catch (Exception e)
            {
                // One platform failing must not stop the others, or hide what already went up.
                log.AppendLine($"   {label}: FAILED - {e.GetType().Name}: {e.Message}");
                result.Failed++;
            }
            finally
            {
                stream?.Dispose();
                EditorUtility.ClearProgressBar();
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- http

    static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    struct Response { public int Status; public string Body; }

    static Response Send(HttpMethod method, string url, HttpContent content, string bearer, int timeoutSeconds = 60)
    {
        // Task.Run so the request never waits on Unity's main-thread synchronization context,
        // which this call is blocking.
        return Task.Run(async () =>
        {
            using (var request = new HttpRequestMessage(method, url) { Content = content })
            using (var cancel = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrEmpty(bearer))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                try
                {
                    using (var response = await Http.SendAsync(request, cancel.Token).ConfigureAwait(false))
                        return new Response
                        {
                            Status = (int)response.StatusCode,
                            Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                        };
                }
                catch (OperationCanceledException)
                {
                    return new Response { Status = 0, Body = $"timed out after {timeoutSeconds} s" };
                }
                catch (HttpRequestException e)
                {
                    return new Response { Status = 0, Body = e.Message };
                }
            }
        }).GetAwaiter().GetResult();
    }

    // True on a 2xx. Otherwise `error` is mod.io's own message where it sent one.
    static bool Ok(Response response, out JObject json, out string error)
    {
        json = null;
        error = null;
        try { json = string.IsNullOrEmpty(response.Body) ? new JObject() : JObject.Parse(response.Body); }
        catch (Exception) { /* not JSON: reported below as the raw body */ }

        if (response.Status >= 200 && response.Status < 300 && json != null) return true;

        string message = (string)json?["error"]?["message"];
        error = response.Status == 0
            ? response.Body
            : $"HTTP {response.Status}: {message ?? Truncate(response.Body, 300)}";
        return false;
    }

    static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
}
