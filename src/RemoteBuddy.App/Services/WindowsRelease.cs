// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace RemoteBuddy.App.Services;

internal sealed record WindowsRelease(string Tag, long PackageSize, Version Version)
{
    internal const string DownloadsUrl = "https://remote.zfdang.com/downloads/#platform-windows";
    private const string ManifestUrl = "https://remote.zfdang.com/downloads/windows-latest.json";
    private const int MaxManifestBytes = 64 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };

    public string PackageDescription => $"Windows x64 · {PackageSize / 1048576.0:0.0} MB";

    internal static string FormatTag(Version version) =>
        $"v{version.Major:D2}-{version.Minor:D2}-{version.Build:D2}.{version.Revision:D3}";

    internal static async Task<WindowsRelease> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    internal static WindowsRelease Parse(JsonElement manifest)
    {
        string? tag = manifest.GetProperty("tag").GetString();
        if (tag is not { Length: 13 } ||
            tag[0] is not ('v' or 'V') ||
            tag[3] != '-' ||
            tag[6] != '-' ||
            tag[9] != '.' ||
            ParseVersion(tag) is not { } version)
        {
            throw new InvalidDataException("Invalid Windows release tag.");
        }

        var package = manifest.GetProperty("packages").GetProperty("x64");
        string? name = package.GetProperty("name").GetString();
        long size = package.GetProperty("size").GetInt64();
        if (name != $"Remote_Buddy_Windows_{tag}_x64.zip" || size <= 0)
        {
            throw new InvalidDataException("Invalid Windows x64 package metadata.");
        }

        return new WindowsRelease(tag, size, version);
    }

    internal static Version? ParseVersion(string value)
    {
        string normalized = value.TrimStart('v', 'V').Replace('-', '.');
        if (!Version.TryParse(normalized, out var version) ||
            version.Revision < 0 ||
            version.Major > 99 ||
            !DateOnly.TryParseExact(
                $"{2000 + version.Major:D4}-{version.Minor:D2}-{version.Build:D2}",
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return null;
        }

        // The public Windows release tag uses the last three digits of the build commit count.
        return new Version(version.Major, version.Minor, version.Build, version.Revision % 1000);
    }
}
