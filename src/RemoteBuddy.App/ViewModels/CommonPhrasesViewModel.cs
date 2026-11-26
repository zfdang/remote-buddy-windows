// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteBuddy.Core.Config;
using RemoteBuddy.Core.Models;

namespace RemoteBuddy.App.ViewModels;

public sealed record CommonPhraseButtonBadge(RemoteButton Button, string ShortText);

public sealed class CommonPhraseItemViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public IReadOnlyList<CommonPhraseButtonBadge> AssignedBadges { get; init; } = [];
}

public sealed class CommonPhrasePositionItem
{
    public RemoteButton Button { get; init; }
    public string ShortTitle { get; init; } = string.Empty;
    public string AssignedLabel { get; init; } = string.Empty;
    public bool IsAssigned { get; init; }
    public bool IsSelected { get; init; }
}

public partial class CommonPhrasesViewModel : ObservableObject
{
    private readonly CommonPhraseStore _store;
    private readonly Func<RemoteModelType?> _getRemoteModel;
    private readonly Action _returnToMapping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignInstructionText))]
    private RemoteButton? _selectedButton;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanMoveSelected))]
    private string? _selectedPhraseId;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CharacterCountText))]
    [NotifyPropertyChangedFor(nameof(IsOverCharacterLimit))]
    [NotifyPropertyChangedFor(nameof(WhitespaceHint))]
    [NotifyPropertyChangedFor(nameof(HasWhitespaceHint))]
    private string _editText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    public ObservableCollection<CommonPhraseItemViewModel> FilteredPhrases { get; } = new();
    public ObservableCollection<CommonPhrasePositionItem> ExtraButtons { get; } = new();

    public CommonPhrasePositionItem? PadOk { get; private set; }
    public CommonPhrasePositionItem? PadUp { get; private set; }
    public CommonPhrasePositionItem? PadDown { get; private set; }
    public CommonPhrasePositionItem? PadLeft { get; private set; }
    public CommonPhrasePositionItem? PadRight { get; private set; }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public bool CanDelete => !string.IsNullOrEmpty(SelectedPhraseId);
    public bool CanMoveSelected => !string.IsNullOrEmpty(SelectedPhraseId);
    public bool IsOverCharacterLimit => EditText.Length > CommonPhrase.MaximumTextLength;
    public string CharacterCountText => $"{EditText.Length}/{CommonPhrase.MaximumTextLength} 个字符";

    public string WhitespaceHint => CommonPhrase.GetBoundaryWhitespaceState(EditText) switch
    {
        CommonPhraseBoundaryWhitespace.TrailingOnly => "末尾包含不可见字符（空格或制表符等，已保留原样输入）",
        CommonPhraseBoundaryWhitespace.LeadingOnly => "开头包含不可见字符（空格或制表符等，已保留原样输入）",
        CommonPhraseBoundaryWhitespace.Both => "首尾均包含不可见字符（空格或制表符等，已保留原样输入）",
        _ => string.Empty
    };

    public bool HasWhitespaceHint => !string.IsNullOrEmpty(WhitespaceHint);

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(EditText) &&
        !string.IsNullOrWhiteSpace(EditLabel) &&
        EditText.Length <= CommonPhrase.MaximumTextLength &&
        EditLabel.Length <= CommonPhrase.MaximumLabelLength;

    public string AssignInstructionText => SelectedButton == null
        ? "请先选择一个键位。"
        : "从常用语库选择内容，立即保存分配。";

    public CommonPhrasesViewModel(CommonPhraseStore store, Func<RemoteModelType?> getRemoteModel, Action returnToMapping)
    {
        _store = store;
        _getRemoteModel = getRemoteModel;
        _returnToMapping = returnToMapping;
        _store.Changed += Refresh;

        var first = _store.Archive.Entries.FirstOrDefault();
        if (first != null)
        {
            SelectedPhraseId = first.Id;
            EditLabel = first.Label;
            EditText = first.Text;
        }
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => RebuildLibrary();

    public void Refresh()
    {
        var model = _getRemoteModel();
        if (SelectedButton is { } btn && !CommonPhraseStore.GetButtons(model).Contains(btn))
        {
            SelectedButton = null;
        }

        PadOk = BuildPositionItem(RemoteButton.Ok);
        PadUp = BuildPositionItem(RemoteButton.Up);
        PadDown = BuildPositionItem(RemoteButton.Down);
        PadLeft = BuildPositionItem(RemoteButton.Left);
        PadRight = BuildPositionItem(RemoteButton.Right);
        OnPropertyChanged(nameof(PadOk));
        OnPropertyChanged(nameof(PadUp));
        OnPropertyChanged(nameof(PadDown));
        OnPropertyChanged(nameof(PadLeft));
        OnPropertyChanged(nameof(PadRight));

        ExtraButtons.Clear();
        foreach (var extra in CommonPhraseStore.GetExtraButtons(model))
        {
            ExtraButtons.Add(BuildPositionItem(extra));
        }

        RebuildLibrary();
    }

    [RelayCommand]
    public void SelectPosition(RemoteButton button)
    {
        var model = _getRemoteModel();
        if (!CommonPhraseStore.GetButtons(model).Contains(button))
        {
            SelectedButton = null;
            Refresh();
            return;
        }

        SelectedButton = button;
        var assigned = _store.GetPhrase(button);
        LoadIntoEditor(assigned);
        StatusMessage = string.Empty;
        Refresh();
    }

    [RelayCommand]
    public void ChoosePhrase(string phraseId)
    {
        var phrase = _store.Archive.Entries.FirstOrDefault(e => string.Equals(e.Id, phraseId, StringComparison.Ordinal));
        if (phrase == null) return;

        LoadIntoEditor(phrase);
        if (SelectedButton is { } button && CommonPhraseStore.GetButtons(_getRemoteModel()).Contains(button))
        {
            try
            {
                _store.Assign(button, phrase.Id);
                SetStatus("已保存。", isError: false);
            }
            catch
            {
                SetStatus("无法保存分配，请检查常用语内容。", isError: true);
            }
        }
        else
        {
            StatusMessage = string.Empty;
        }
        Refresh();
    }

    [RelayCommand]
    public void NewPhrase()
    {
        SelectedButton = null;
        LoadIntoEditor(null);
        StatusMessage = string.Empty;
        Refresh();
    }

    [RelayCommand]
    public void SavePhrase()
    {
        if (!CanSave) return;
        try
        {
            var phrase = new CommonPhrase
            {
                Id = SelectedPhraseId ?? ("user-" + Guid.NewGuid().ToString("N")),
                Label = EditLabel.Trim(),
                Text = EditText
            };
            string savedId = _store.SavePhrase(phrase, SelectedPhraseId);
            var saved = _store.Archive.Entries.FirstOrDefault(e => e.Id == savedId);
            LoadIntoEditor(saved);
            SetStatus("已保存。", isError: false);
            Refresh();
        }
        catch
        {
            SetStatus("无法保存。请检查短语内容长度。", isError: true);
        }
    }

    [RelayCommand]
    public void DeletePhrase()
    {
        if (string.IsNullOrEmpty(SelectedPhraseId)) return;
        try
        {
            _store.Delete(SelectedPhraseId);
            LoadIntoEditor(_store.Archive.Entries.FirstOrDefault());
            SetStatus("已删除。", isError: false);
            Refresh();
        }
        catch
        {
            SetStatus("删除失败。", isError: true);
        }
    }

    [RelayCommand]
    public void MoveUp()
    {
        if (string.IsNullOrEmpty(SelectedPhraseId)) return;
        _store.Move(SelectedPhraseId, -1);
        SetStatus("已保存。", isError: false);
    }

    [RelayCommand]
    public void MoveDown()
    {
        if (string.IsNullOrEmpty(SelectedPhraseId)) return;
        _store.Move(SelectedPhraseId, 1);
        SetStatus("已保存。", isError: false);
    }

    public bool TryImportJson(string json)
    {
        try
        {
            _store.ImportJson(json);
            SelectedButton = null;
            LoadIntoEditor(_store.Archive.Entries.FirstOrDefault());
            SetStatus("已导入常用语备份。", isError: false);
            Refresh();
            return true;
        }
        catch
        {
            SetStatus("无法导入。请检查 JSON 文件格式；原有数据未被修改。", isError: true);
            return false;
        }
    }

    public string ExportJson()
    {
        string json = _store.ExportJson();
        SetStatus("已导出常用语备份。", isError: false);
        return json;
    }

    [RelayCommand]
    public void Return() => _returnToMapping();

    public static string GetShortButtonGlyph(RemoteButton button) => button switch
    {
        RemoteButton.Ok => "OK",
        RemoteButton.Up => "▲",
        RemoteButton.Down => "▼",
        RemoteButton.Left => "◀",
        RemoteButton.Right => "▶",
        RemoteButton.Home => "⌂",
        RemoteButton.Mute => "🔇",
        RemoteButton.VolumeUp => "🔊+",
        RemoteButton.VolumeDown => "🔉−",
        RemoteButton.Menu => "≡",
        RemoteButton.LiveTv => "TV",
        RemoteButton.YouTube => "YT",
        RemoteButton.Netflix => "NF",
        RemoteButton.Power => "⏻",
        RemoteButton.Input => "⏏",
        RemoteButton.Custom => "★",
        _ => button.ToString()
    };

    private CommonPhrasePositionItem BuildPositionItem(RemoteButton button)
    {
        var phrase = _store.GetPhrase(button);
        return new CommonPhrasePositionItem
        {
            Button = button,
            ShortTitle = GetShortButtonGlyph(button),
            AssignedLabel = phrase?.Label ?? "未分配",
            IsAssigned = phrase != null,
            IsSelected = SelectedButton == button
        };
    }

    private void RebuildLibrary()
    {
        var buttons = CommonPhraseStore.GetButtons(_getRemoteModel());
        string query = SearchText.Trim();
        FilteredPhrases.Clear();

        foreach (var entry in _store.Archive.Entries)
        {
            if (!string.IsNullOrEmpty(query) &&
                !entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var badges = buttons
                .Where(btn => _store.GetPhrase(btn)?.Id == entry.Id)
                .Select(btn => new CommonPhraseButtonBadge(btn, GetShortButtonGlyph(btn)))
                .ToArray();

            FilteredPhrases.Add(new CommonPhraseItemViewModel
            {
                Id = entry.Id,
                Label = entry.Label,
                Text = entry.Text,
                IsSelected = string.Equals(SelectedPhraseId, entry.Id, StringComparison.Ordinal),
                AssignedBadges = badges
            });
        }
    }

    private void LoadIntoEditor(CommonPhrase? phrase)
    {
        SelectedPhraseId = phrase?.Id;
        EditLabel = phrase?.Label ?? string.Empty;
        EditText = phrase?.Text ?? string.Empty;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
