// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace RemoteBuddy.Core.Models;

public enum CommonPhraseBoundaryWhitespace
{
    None = 0,
    LeadingOnly = 1,
    TrailingOnly = 2,
    Both = 3
}

public class CommonPhrase
{
    public const int MaximumTextLength = 4_000;
    public const int MaximumLabelLength = 80;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonIgnore]
    public string InsertionText => Text;

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(Text) &&
        !string.IsNullOrWhiteSpace(Label) &&
        Text.Length <= MaximumTextLength &&
        Label.Length <= MaximumLabelLength;

    public static CommonPhraseBoundaryWhitespace GetBoundaryWhitespaceState(string? text)
    {
        if (string.IsNullOrEmpty(text)) return CommonPhraseBoundaryWhitespace.None;
        bool hasLeading = char.IsWhiteSpace(text[0]);
        bool hasTrailing = char.IsWhiteSpace(text[^1]);
        return (hasLeading, hasTrailing) switch
        {
            (true, true) => CommonPhraseBoundaryWhitespace.Both,
            (true, false) => CommonPhraseBoundaryWhitespace.LeadingOnly,
            (false, true) => CommonPhraseBoundaryWhitespace.TrailingOnly,
            _ => CommonPhraseBoundaryWhitespace.None
        };
    }

    public CommonPhrase Clone() => new()
    {
        Id = Id,
        Text = Text,
        Label = Label
    };
}

public class CommonPhraseArchive
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("website")]
    public string? Website { get; set; }

    [JsonPropertyName("github")]
    public string? Github { get; set; }

    [JsonPropertyName("entries")]
    public List<CommonPhrase> Entries { get; set; } = new();

    [JsonPropertyName("bindings")]
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("builtInIDs")]
    public List<string>? BuiltInIDs { get; set; }

    [JsonPropertyName("builtInBindingKeys")]
    public List<string>? BuiltInBindingKeys { get; set; }
}

public enum CommonPhraseRoutingAction
{
    Unhandled = 0,
    Consumed = 1,
    Insert = 2,
    Close = 3
}

public readonly record struct CommonPhraseRoutingDecision(CommonPhraseRoutingAction Action, RemoteButton Button = RemoteButton.None);

/// <summary>
/// Claims only panel controls from its owning input source. Held releases remain
/// consumed after closing, so exit keys never leak into the foreground application.
/// </summary>
public sealed class CommonPhraseRouting
{
    private static readonly HashSet<RemoteButton> ExitButtons = [RemoteButton.Back, RemoteButton.Power];
    private readonly HashSet<(string Source, RemoteButton Button)> _held = new();
    private readonly HashSet<RemoteButton> _phraseButtons = new();

    public string? Owner { get; private set; }

    public void Open(string source, IEnumerable<RemoteButton> buttons)
    {
        Owner = source;
        _phraseButtons.Clear();
        foreach (var btn in buttons) _phraseButtons.Add(btn);
    }

    public void Close()
    {
        Owner = null;
    }

    public void Reset(string source)
    {
        if (string.Equals(Owner, source, StringComparison.Ordinal))
        {
            Owner = null;
        }
        _held.RemoveWhere(item => string.Equals(item.Source, source, StringComparison.Ordinal));
    }

    public bool Claims(RemoteButton button, string source) =>
        _held.Contains((source, button)) ||
        (string.Equals(Owner, source, StringComparison.Ordinal) &&
         (_phraseButtons.Contains(button) || ExitButtons.Contains(button)));

    public CommonPhraseRoutingDecision Handle(RemoteButton button, bool isDown, string source)
    {
        var input = (source, button);
        if (!isDown)
        {
            return _held.Remove(input)
                ? new(CommonPhraseRoutingAction.Consumed)
                : new(CommonPhraseRoutingAction.Unhandled);
        }

        if (_held.Contains(input))
        {
            return new(CommonPhraseRoutingAction.Consumed);
        }

        if (!string.Equals(Owner, source, StringComparison.Ordinal) ||
            (!_phraseButtons.Contains(button) && !ExitButtons.Contains(button)))
        {
            return new(CommonPhraseRoutingAction.Unhandled);
        }

        _held.Add(input);
        Owner = null;
        return _phraseButtons.Contains(button)
            ? new(CommonPhraseRoutingAction.Insert, button)
            : new(CommonPhraseRoutingAction.Close, button);
    }
}
