using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Ships with the Snapshot VR Creator Kit. Snapshot > UGC > Upload Maps to mod.io...
//
// The window for ModioMapUploader, which holds all the logic: sign in, match each built map to
// its mod page, see what is live, try a dry run, upload.
public sealed class ModioMapUploaderWindow : EditorWindow
{
    List<ModioMapUploader.Candidate> _maps = new List<ModioMapUploader.Candidate>();
    readonly HashSet<string> _selected = new HashSet<string>();
    readonly HashSet<string> _platforms = new HashSet<string>(ModioMapUploader.Platforms.Select(p => p.suffix));
    // bundle -> what "Check what is live" last found
    readonly Dictionary<string, string> _liveSummary = new Dictionary<string, string>();
    List<ModioMapUploader.ModSummary> _myMods;

    string _email = "";
    string _code = "";
    string _pastedToken = "";
    string _authMessage = "";
    string _version = "";
    string _changelog = "";
    bool _force;
    bool _ignoreValidation;
    string _lastReport = "";
    Vector2 _scroll;

    [MenuItem("Snapshot/UGC/Upload Maps to mod.io…", false, 41)]
    public static void Open()
    {
        var w = GetWindow<ModioMapUploaderWindow>(false, "Upload to mod.io", true);
        w.minSize = new Vector2(620, 520);
        w.Rescan();
    }

    void OnFocus() => Rescan();

    void Rescan()
    {
        _maps = ModioMapUploader.FindCandidates();
        Repaint();
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Upload built maps to mod.io", EditorStyles.boldLabel);
        DrawSignIn();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.Space();
        DrawMaps();
        EditorGUILayout.Space();
        DrawUpload();

        if (!string.IsNullOrEmpty(_lastReport))
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Last run", EditorStyles.miniBoldLabel);
            EditorGUILayout.TextArea(_lastReport, EditorStyles.wordWrappedLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    // ---------------------------------------------------------------- sign-in

    void DrawSignIn()
    {
        EditorGUILayout.Space();
        if (ModioMapUploader.SignedIn)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Signed in to mod.io as {ModioMapUploader.TokenUser}");
            if (GUILayout.Button("Sign out", GUILayout.Width(80)))
            {
                ModioMapUploader.SignOut();
                _myMods = null;
                _authMessage = "";
            }
            EditorGUILayout.EndHorizontal();
            return;
        }

        EditorGUILayout.HelpBox("Sign in with your mod.io account. You can upload to the mods you own or are on the "
            + "team of. The sign-in stays on this machine; it is never written into the project.", MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        _email = EditorGUILayout.TextField("Email", _email);
        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_email)))
            if (GUILayout.Button("Email me a code", GUILayout.Width(130)))
                _authMessage = ModioMapUploader.RequestEmailCode(_email.Trim(), out string error)
                    ? "Code sent. Check your email." : "Could not request a code: " + error;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        _code = EditorGUILayout.TextField("Code", _code);
        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_code)))
            if (GUILayout.Button("Sign in", GUILayout.Width(130)))
                _authMessage = ModioMapUploader.ExchangeEmailCode(_code, out string error)
                    ? "" : "Sign-in failed: " + error;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        _pastedToken = EditorGUILayout.PasswordField("...or paste a token", _pastedToken);
        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_pastedToken)))
            if (GUILayout.Button("Use token", GUILayout.Width(130)))
            {
                _authMessage = ModioMapUploader.StoreToken(_pastedToken, out string error)
                    ? "" : "Token rejected: " + error;
                _pastedToken = "";
            }
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_authMessage))
            EditorGUILayout.HelpBox(_authMessage, MessageType.None);
    }

    // ---------------------------------------------------------------- maps

    void DrawMaps()
    {
        EditorGUILayout.LabelField("Built maps", EditorStyles.miniBoldLabel);
        if (_maps.Count == 0)
        {
            EditorGUILayout.HelpBox("No built maps found. Build them in Snapshot > UGC > Map Bundles first; it writes the files "
                + "this window uploads to the ModioUploads folder at the top of the project.", MessageType.Info);
            return;
        }

        foreach (var map in _maps)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            bool was = _selected.Contains(map.Bundle);
            bool now = EditorGUILayout.ToggleLeft($"{map.Bundle}   ({map.Source})", was, EditorStyles.boldLabel);
            if (now != was) { if (now) _selected.Add(map.Bundle); else _selected.Remove(map.Bundle); }

            long id = map.ModId;
            if (_myMods != null && _myMods.Count > 0)
            {
                // Pick the mod page by name.
                var names = new[] { "(choose a mod)" }.Concat(_myMods.Select(m => m.Label)).ToArray();
                int current = _myMods.FindIndex(m => m.Id == map.ModId) + 1;
                int picked = EditorGUILayout.Popup(current, names, GUILayout.Width(190));
                if (picked != current && picked > 0) id = _myMods[picked - 1].Id;
            }
            // ...or type its id, the number in the mod page's admin address.
            EditorGUILayout.LabelField("mod id", GUILayout.Width(44));
            long typed = EditorGUILayout.DelayedIntField((int)id, GUILayout.Width(80));
            if (typed != id) id = typed;
            if (id != map.ModId)
            {
                map.ModId = id;
                ModioMapUploader.SetModId(map.Bundle, id);
                _liveSummary.Remove(map.Bundle);
            }
            EditorGUILayout.EndHorizontal();

            string missing = string.Join(", ", ModioMapUploader.Platforms.Where(p => !map.Zips.ContainsKey(p.suffix)).Select(p => p.label));
            EditorGUILayout.LabelField($"built {map.NewestWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
                + (missing.Length > 0 ? $"   NOT built for: {missing}" : "   all three platforms"), EditorStyles.miniLabel);
            if (map.ModId <= 0)
                EditorGUILayout.LabelField("Not matched to a mod page yet. Create the page on mod.io, then press "
                    + "\"List my mods\" and choose it here.", EditorStyles.wordWrappedMiniLabel);
            if (_myMods != null && _myMods.Any(m => m.Id == map.ModId && m.Archived))
                EditorGUILayout.HelpBox("This mod page is archived on mod.io. Choose the live one.", MessageType.Warning);
            if (_liveSummary.TryGetValue(map.Bundle, out string summary))
                EditorGUILayout.LabelField(summary, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Select all", GUILayout.Width(90))) foreach (var m in _maps) _selected.Add(m.Bundle);
        if (GUILayout.Button("Select none", GUILayout.Width(90))) _selected.Clear();
        using (new EditorGUI.DisabledScope(!ModioMapUploader.SignedIn))
        {
            if (GUILayout.Button("List my mods", GUILayout.Width(110))) ListMyMods();
            if (GUILayout.Button("Check what is live", GUILayout.Width(140))) CheckLive();
        }
        EditorGUILayout.EndHorizontal();
    }

    void ListMyMods()
    {
        if (ModioMapUploader.GetMyMods(out var mods, out string error))
        {
            _myMods = mods;
            _lastReport = mods.Count > 0
                ? $"Your account can upload to {mods.Count} Snapshot VR mod(s): {string.Join(", ", mods.Select(m => m.Label))}"
                : "Your account has no Snapshot VR mods yet. Create the mod page on mod.io first.";
        }
        else
            _lastReport = "Could not list your mods: " + error;
    }

    // Compares each matched map's built zips with the file mod.io serves for that platform.
    void CheckLive()
    {
        _liveSummary.Clear();
        foreach (var map in _maps.Where(m => m.ModId > 0))
        {
            if (!ModioMapUploader.GetLive(map.ModId, out string name, out var live, out string error))
            {
                _liveSummary[map.Bundle] = "mod.io: " + error;
                continue;
            }

            var parts = new List<string>();
            foreach (var (suffix, _, label) in ModioMapUploader.Platforms)
            {
                if (!live.TryGetValue(suffix, out var file)) { parts.Add($"{label}: nothing live"); continue; }
                bool same = map.Zips.TryGetValue(suffix, out string zip) && ModioMapUploader.Md5Of(zip) == file.Md5;
                parts.Add($"{label}: v{file.Version} {(same ? "(same as built)" : "(DIFFERENT from built)")}");
            }
            _liveSummary[map.Bundle] = $"\"{name}\" - " + string.Join("; ", parts);
        }
    }

    // ---------------------------------------------------------------- upload

    void DrawUpload()
    {
        EditorGUILayout.LabelField("Upload", EditorStyles.miniBoldLabel);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel("Platforms");
        foreach (var (suffix, _, label) in ModioMapUploader.Platforms)
        {
            bool was = _platforms.Contains(suffix);
            bool now = GUILayout.Toggle(was, label, GUILayout.Width(100));
            if (now != was) { if (now) _platforms.Add(suffix); else _platforms.Remove(suffix); }
        }
        EditorGUILayout.EndHorizontal();

        _version = EditorGUILayout.TextField("Version", _version);
        EditorGUILayout.LabelField("Changelog");
        _changelog = EditorGUILayout.TextArea(_changelog, GUILayout.MinHeight(40));
        _force = EditorGUILayout.ToggleLeft("Upload even if the same file is already on the mod page", _force);
        _ignoreValidation = EditorGUILayout.ToggleLeft("Upload even if Validate Map reports errors", _ignoreValidation);

        var chosen = _maps.Where(m => _selected.Contains(m.Bundle)).ToList();
        bool ready = ModioMapUploader.SignedIn && chosen.Count > 0 && _platforms.Count > 0 && !string.IsNullOrWhiteSpace(_version);
        if (ModioMapUploader.SignedIn && chosen.Count > 0 && string.IsNullOrWhiteSpace(_version))
            EditorGUILayout.LabelField("Give this upload a version, for example 0.3.", EditorStyles.miniLabel);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button("Dry run", GUILayout.Height(28))) Run(chosen, dryRun: true);
            if (GUILayout.Button($"Upload {chosen.Count} map(s)", GUILayout.Height(28))
                && EditorUtility.DisplayDialog("Upload to mod.io",
                    $"Upload {chosen.Count} map(s) for {_platforms.Count} platform(s) as version {_version}?\n\n"
                    + string.Join("\n", chosen.Select(m => $"{m.Bundle} -> mod {m.ModId}"))
                    + "\n\nEach file is added to its mod page for its platform. Players get it once it has passed "
                    + "review and is live.", "Upload", "Cancel"))
                Run(chosen, dryRun: false);
        }
        EditorGUILayout.EndHorizontal();
    }

    void Run(List<ModioMapUploader.Candidate> chosen, bool dryRun)
    {
        var report = new System.Text.StringBuilder();
        int uploaded = 0, skipped = 0, failed = 0;
        foreach (var map in chosen)
        {
            var r = ModioMapUploader.Upload(map, _platforms, _version.Trim(), _changelog, dryRun, _force, _ignoreValidation);
            report.Append(r.Log);
            uploaded += r.Uploaded; skipped += r.Skipped; failed += r.Failed;
        }
        report.AppendLine($"{(dryRun ? "Would upload" : "Uploaded")} {uploaded}, skipped {skipped}, failed {failed}.");

        _lastReport = report.ToString();
        string message = $"{ModioMapUploader.LogPrefix} {(dryRun ? "DRY RUN" : "upload")}\n{_lastReport}";
        if (failed > 0) Debug.LogError(message); else Debug.Log(message);
        if (!dryRun) _liveSummary.Clear();
        Repaint();
    }
}
