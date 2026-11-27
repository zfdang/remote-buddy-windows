// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using RemoteBuddy.Core.Diagnostics;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.Core.Config;

public sealed class CommonPhraseStore
{
    public const int MaximumImportBytes = 4_000_000;
    public const int MaximumEntries = 500;
    public const string ExportWebsite = "https://remote.zfdang.com";
    public const string ExportGithub = "https://github.com/zfdang/remote-buddy-windows";

    public static readonly IReadOnlyList<RemoteButton> BaseButtons =
    [
        RemoteButton.Ok,
        RemoteButton.Left,
        RemoteButton.Up,
        RemoteButton.Right,
        RemoteButton.Down
    ];

    public static readonly IReadOnlyList<RemoteButton?[]> ChromecastExtraRows =
    [
        [RemoteButton.Home, RemoteButton.Mute],
        [RemoteButton.YouTube, RemoteButton.Netflix],
        [RemoteButton.Power, RemoteButton.Input]
    ];

    public static readonly IReadOnlyList<RemoteButton?[]> Gy3leExtraRows =
    [
        [RemoteButton.Home, RemoteButton.Mute],
        [RemoteButton.YouTube, RemoteButton.Netflix],
        [RemoteButton.Power, RemoteButton.Custom]
    ];

    public static readonly IReadOnlyList<RemoteButton?[]> XiaomiExtraRows =
    [
        [null, RemoteButton.VolumeUp],
        [RemoteButton.Home, RemoteButton.VolumeDown],
        [RemoteButton.Menu, RemoteButton.LiveTv]
    ];

    public static readonly IReadOnlyList<RemoteButton?[]> T1ExtraRows =
    [
        [RemoteButton.Mute, RemoteButton.Home],
        [null, RemoteButton.Menu]
    ];

    public static readonly IReadOnlyList<RemoteButton> AllExtraButtons =
    [
        RemoteButton.Home,
        RemoteButton.Mute,
        RemoteButton.Menu,
        RemoteButton.LiveTv,
        RemoteButton.VolumeUp,
        RemoteButton.VolumeDown,
        RemoteButton.YouTube,
        RemoteButton.Netflix,
        RemoteButton.Power,
        RemoteButton.Input,
        RemoteButton.Custom
    ];

    public static readonly IReadOnlyList<RemoteButton> AllSupportedButtons =
        BaseButtons.Concat(AllExtraButtons).ToArray();

    private static readonly Dictionary<string, RemoteButton> KeyToButton = BuildKeyToButtonMap();
    private static readonly Dictionary<RemoteButton, string> ButtonToKey = BuildButtonToKeyMap();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyList<CommonPhrase> BuiltInEntries =
    [
        new() { Id = "builtin-1", Text = "好的", Label = "好的" },
        new() { Id = "builtin-2", Text = "继续", Label = "继续" },
        new() { Id = "builtin-3", Text = "按这个方案执行", Label = "按方案执行" },
        new() { Id = "builtin-4", Text = "解释清楚一点", Label = "解释清楚" },
        new() { Id = "builtin-5", Text = "先给我一个计划", Label = "先给计划" },
        new() { Id = "builtin-6", Text = "检查一下有没有问题", Label = "检查问题" },
        new() { Id = "builtin-7", Text = "简短一点", Label = "简短一点" },
        new() { Id = "builtin-8", Text = "总结一下", Label = "总结一下" },
        new() { Id = "builtin-9", Text = "举个例子", Label = "举个例子" },
        new() { Id = "builtin-10", Text = "还有其他方案吗？", Label = "其他方案" },
        new() { Id = "builtin-11", Text = "不对，再检查一下", Label = "不对重查" },
        new() { Id = "builtin-12", Text = "思路可以，我补充一点：", Label = "我补充一点" }
    ];

    private static readonly IReadOnlyDictionary<string, string> BuiltInDefaultBindings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"] = "builtin-1",
            ["left"] = "builtin-2",
            ["right"] = "builtin-3",
            ["up"] = "builtin-4",
            ["down"] = "builtin-5",
            ["home"] = "builtin-6",
            ["mute"] = "builtin-7",
            ["volume_up"] = "builtin-7",
            ["menu"] = "builtin-8",
            ["youtube"] = "builtin-8",
            ["volume_down"] = "builtin-9",
            ["netflix"] = "builtin-9",
            ["tv"] = "builtin-10",
            ["input"] = "builtin-10",
            ["power"] = "builtin-11"
        };

    private readonly object _lock = new();
    private readonly string _filePath;

    public CommonPhraseArchive Archive { get; private set; }
    public event Action? Changed;

    public CommonPhraseStore(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "RemoteBuddy");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "common-phrases.json");
        }

        Archive = LoadOrInitialize();
    }

    public static string ResolveDefaultPathForConfigDirectory(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "RemoteBuddy", "common-phrases.json");
        }

        string dir = Path.GetDirectoryName(configPath) ?? Path.GetTempPath();
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(configPath) + ".phrases.json");
    }

    public static IReadOnlyList<RemoteButton?[]> GetExtraButtonRows(RemoteModelType? model) => model switch
    {
        RemoteModelType.GoogleG9N9N => ChromecastExtraRows,
        RemoteModelType.GoogleGY3LE => Gy3leExtraRows,
        RemoteModelType.T1Remote => T1ExtraRows,
        _ => XiaomiExtraRows
    };

    public static bool SupportsBaseButtons(RemoteModelType? model) =>
        model is RemoteModelType.GoogleG9N9N or RemoteModelType.GoogleGY3LE;

    public static IReadOnlyList<RemoteButton> GetExtraButtons(RemoteModelType? model) =>
        GetExtraButtonRows(model).SelectMany(row => row).Where(b => b.HasValue).Select(b => b!.Value).ToArray();

    public static IReadOnlyList<RemoteButton> GetButtons(RemoteModelType? model) =>
        SupportsBaseButtons(model)
            ? BaseButtons.Concat(GetExtraButtons(model)).ToArray()
            : GetExtraButtons(model);

    public static string? ToKey(RemoteButton button) =>
        ButtonToKey.TryGetValue(button, out string? key) ? key : null;

    public static bool TryFromKey(string? key, out RemoteButton button)
    {
        if (!string.IsNullOrWhiteSpace(key) && KeyToButton.TryGetValue(key.Trim(), out button))
        {
            return true;
        }
        button = RemoteButton.None;
        return false;
    }

    public CommonPhrase? GetPhrase(RemoteButton button)
    {
        lock (_lock)
        {
            string? key = ToKey(button);
            if (key == null || !Archive.Bindings.TryGetValue(key, out string? id)) return null;
            return Archive.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }
    }

    public string SavePhrase(CommonPhrase phrase, string? replacingId)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        lock (_lock)
        {
            var candidate = CloneArchive(Archive);
            var saved = new CommonPhrase
            {
                Id = string.IsNullOrWhiteSpace(phrase.Id) ? "user-" + Guid.NewGuid().ToString("N") : phrase.Id,
                Text = phrase.Text,
                Label = phrase.Label
            };
            if (!saved.IsValid) throw new ArgumentException("Invalid common phrase.", nameof(phrase));

            if (!string.IsNullOrWhiteSpace(replacingId))
            {
                int idx = candidate.Entries.FindIndex(e => string.Equals(e.Id, replacingId, StringComparison.Ordinal));
                if (idx >= 0)
                {
                    if (replacingId.StartsWith("builtin-", StringComparison.Ordinal))
                    {
                        saved.Id = "user-" + Guid.NewGuid().ToString("N");
                        candidate.Entries[idx] = saved;
                        foreach (string key in candidate.Bindings.Keys.ToArray())
                        {
                            if (string.Equals(candidate.Bindings[key], replacingId, StringComparison.Ordinal))
                            {
                                candidate.Bindings[key] = saved.Id;
                            }
                        }
                    }
                    else
                    {
                        saved.Id = replacingId;
                        candidate.Entries[idx] = saved;
                    }
                }
                else
                {
                    candidate.Entries.Add(saved);
                }
            }
            else
            {
                candidate.Entries.Add(saved);
            }

            PersistLocked(candidate);
            return saved.Id;
        }
    }

    public void Assign(RemoteButton button, string phraseId)
    {
        lock (_lock)
        {
            string? key = ToKey(button) ?? throw new ArgumentException($"Unsupported phrase button: {button}", nameof(button));
            var candidate = CloneArchive(Archive);
            candidate.Bindings[key] = phraseId;
            PersistLocked(candidate);
        }
    }

    public void Unassign(RemoteButton button)
    {
        lock (_lock)
        {
            string? key = ToKey(button);
            if (key == null) return;
            var candidate = CloneArchive(Archive);
            if (candidate.Bindings.Remove(key))
            {
                PersistLocked(candidate);
            }
        }
    }

    public void Delete(string phraseId)
    {
        lock (_lock)
        {
            var candidate = CloneArchive(Archive);
            candidate.Entries.RemoveAll(e => string.Equals(e.Id, phraseId, StringComparison.Ordinal));
            foreach (string key in candidate.Bindings.Keys.ToArray())
            {
                if (string.Equals(candidate.Bindings[key], phraseId, StringComparison.Ordinal))
                {
                    candidate.Bindings.Remove(key);
                }
            }
            PersistLocked(candidate);
        }
    }

    public void Move(string phraseId, int offset)
    {
        lock (_lock)
        {
            var candidate = CloneArchive(Archive);
            int index = candidate.Entries.FindIndex(e => string.Equals(e.Id, phraseId, StringComparison.Ordinal));
            int target = index + offset;
            if (index < 0 || target < 0 || target >= candidate.Entries.Count || index == target) return;

            var item = candidate.Entries[index];
            candidate.Entries.RemoveAt(index);
            candidate.Entries.Insert(target, item);
            PersistLocked(candidate);
        }
    }

    public string ExportJson()
    {
        lock (_lock)
        {
            var exported = CloneArchive(Archive);
            exported.Website = ExportWebsite;
            exported.Github = ExportGithub;
            return JsonSerializer.Serialize(exported, JsonOptions);
        }
    }

    public void ImportJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumImportBytes)
        {
            throw new InvalidDataException("Common phrases import payload is empty or too large.");
        }

        var imported = JsonSerializer.Deserialize<CommonPhraseArchive>(json, JsonOptions)
            ?? throw new InvalidDataException("Invalid common phrases JSON.");
        ValidateArchive(imported);

        lock (_lock)
        {
            var candidate = CloneArchive(imported);
            foreach (var entry in candidate.Entries)
            {
                if (!entry.Id.StartsWith("builtin-", StringComparison.Ordinal)) continue;
                var builtIn = BuiltInEntries.FirstOrDefault(b => string.Equals(b.Id, entry.Id, StringComparison.Ordinal));
                if (builtIn != null && builtIn.Text == entry.Text && builtIn.Label == entry.Label) continue;

                string oldId = entry.Id;
                entry.Id = "user-" + Guid.NewGuid().ToString("N");
                foreach (string key in candidate.Bindings.Keys.ToArray())
                {
                    if (string.Equals(candidate.Bindings[key], oldId, StringComparison.Ordinal))
                    {
                        candidate.Bindings[key] = entry.Id;
                    }
                }
            }

            PersistLocked(candidate);
        }
    }

    public static void ValidateArchive(CommonPhraseArchive archive)
    {
        if (archive.SchemaVersion != 2 || archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("Unsupported archive schema or entry count.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entry.IsValid || !ids.Add(entry.Id))
            {
                throw new InvalidDataException($"Invalid or duplicate common phrase entry: {entry.Id}");
            }
        }

        foreach (var (key, targetId) in archive.Bindings)
        {
            if (!KeyToButton.ContainsKey(key) || !ids.Contains(targetId))
            {
                throw new InvalidDataException($"Invalid common phrase binding: {key} -> {targetId}");
            }
        }
    }

    private CommonPhraseArchive LoadOrInitialize()
    {
        var defaultArchive = CreateDefaultArchive();
        if (!File.Exists(_filePath))
        {
            return defaultArchive;
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            var saved = JsonSerializer.Deserialize<CommonPhraseArchive>(json, JsonOptions);
            if (saved == null) return defaultArchive;
            ValidateArchive(saved);

            var knownBuiltInIds = new HashSet<string>(
                saved.BuiltInIDs ?? saved.Entries.Where(e => e.Id.StartsWith("builtin-", StringComparison.Ordinal)).Select(e => e.Id),
                StringComparer.Ordinal);
            var knownBindingKeys = new HashSet<string>(
                saved.BuiltInBindingKeys ?? BaseButtons.Select(b => ToKey(b)!),
                StringComparer.OrdinalIgnoreCase);

            var reconciledEntries = new List<CommonPhrase>();
            foreach (var phrase in saved.Entries)
            {
                if (phrase.Id.StartsWith("builtin-", StringComparison.Ordinal))
                {
                    var match = BuiltInEntries.FirstOrDefault(b => string.Equals(b.Id, phrase.Id, StringComparison.Ordinal));
                    if (match != null) reconciledEntries.Add(match.Clone());
                }
                else
                {
                    reconciledEntries.Add(phrase.Clone());
                }
            }

            foreach (var builtIn in BuiltInEntries)
            {
                if (!knownBuiltInIds.Contains(builtIn.Id) &&
                    !reconciledEntries.Any(e => string.Equals(e.Id, builtIn.Id, StringComparison.Ordinal)))
                {
                    reconciledEntries.Add(builtIn.Clone());
                }
            }

            var validIds = reconciledEntries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var reconciledBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, id) in saved.Bindings)
            {
                if (KeyToButton.ContainsKey(key) && validIds.Contains(id))
                {
                    reconciledBindings[key] = id;
                }
            }

            foreach (var (key, defaultId) in BuiltInDefaultBindings)
            {
                if (!knownBindingKeys.Contains(key) && !reconciledBindings.ContainsKey(key) && validIds.Contains(defaultId))
                {
                    reconciledBindings[key] = defaultId;
                }
            }

            var result = new CommonPhraseArchive
            {
                SchemaVersion = 2,
                Entries = reconciledEntries,
                Bindings = reconciledBindings,
                BuiltInIDs = BuiltInEntries.Select(e => e.Id).ToList(),
                BuiltInBindingKeys = BuiltInDefaultBindings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
            };
            ValidateArchive(result);
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to load common phrases from {_filePath}; using defaults: {ex.Message}", "CommonPhraseStore");
            return defaultArchive;
        }
    }

    private void PersistLocked(CommonPhraseArchive candidate)
    {
        candidate.SchemaVersion = 2;
        candidate.BuiltInIDs = BuiltInEntries.Select(e => e.Id).ToList();
        candidate.BuiltInBindingKeys = BuiltInDefaultBindings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        ValidateArchive(candidate);

        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(candidate, JsonOptions);
        string temp = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }

        Archive = candidate;
        Changed?.Invoke();
    }

    private static CommonPhraseArchive CreateDefaultArchive() => new()
    {
        SchemaVersion = 2,
        Entries = BuiltInEntries.Select(e => e.Clone()).ToList(),
        Bindings = new Dictionary<string, string>(BuiltInDefaultBindings, StringComparer.OrdinalIgnoreCase),
        BuiltInIDs = BuiltInEntries.Select(e => e.Id).ToList(),
        BuiltInBindingKeys = BuiltInDefaultBindings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
    };

    private static CommonPhraseArchive CloneArchive(CommonPhraseArchive source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Website = source.Website,
        Github = source.Github,
        Entries = source.Entries.Select(e => e.Clone()).ToList(),
        Bindings = new Dictionary<string, string>(source.Bindings, StringComparer.OrdinalIgnoreCase),
        BuiltInIDs = source.BuiltInIDs?.ToList(),
        BuiltInBindingKeys = source.BuiltInBindingKeys?.ToList()
    };

    private static Dictionary<string, RemoteButton> BuildKeyToButtonMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ok"] = RemoteButton.Ok,
        ["left"] = RemoteButton.Left,
        ["up"] = RemoteButton.Up,
        ["right"] = RemoteButton.Right,
        ["down"] = RemoteButton.Down,
        ["home"] = RemoteButton.Home,
        ["mute"] = RemoteButton.Mute,
        ["menu"] = RemoteButton.Menu,
        ["tv"] = RemoteButton.LiveTv,
        ["volume_up"] = RemoteButton.VolumeUp,
        ["volume_down"] = RemoteButton.VolumeDown,
        ["youtube"] = RemoteButton.YouTube,
        ["netflix"] = RemoteButton.Netflix,
        ["power"] = RemoteButton.Power,
        ["input"] = RemoteButton.Input,
        ["custom"] = RemoteButton.Custom
    };

    private static Dictionary<RemoteButton, string> BuildButtonToKeyMap() =>
        BuildKeyToButtonMap().ToDictionary(pair => pair.Value, pair => pair.Key);
}
