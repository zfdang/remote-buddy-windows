// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Platform.Windows.Audio;

public static class VbCableInstaller
{
    private const string FileName = "VBCABLE_Driver_Pack46.zip";
    private const string Sha256 = "BC44E8974191A5A2427D6C979E2DAB08DBB09925F3F435DFA2F0BA3E45B6EB90";
    private const int MaxPackageBytes = 2 * 1024 * 1024;
    private static readonly Uri Mirror = new($"https://remote.zfdang.com/downloads/drivers/{FileName}");
    private static readonly Uri Official = new($"https://download.vb-audio.com/Download_CABLE/{FileName}");
    private static readonly SemaphoreSlim InstallGate = new(1);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });

    public static async Task<string> InstallAsync(IProgress<string> progress)
    {
        if (!await InstallGate.WaitAsync(0))
            return "VB-CABLE 安装正在进行。";

        DirectoryInfo? work = null;
        try
        {
            if (VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true) != DriverReadinessStatus.NotInstalled)
                return "检测到已有 VB-CABLE，请查看驱动状态。";

            work = Directory.CreateTempSubdirectory("remote-buddy-vbcable-");
            byte[] package = await DownloadVerifiedAsync(Client, [Mirror, Official], Sha256, progress)
                .ConfigureAwait(false);
            using var zip = new ZipArchive(new MemoryStream(package));
            zip.ExtractToDirectory(work.FullName);

            string installer = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 or Architecture.Arm64 => Path.Combine(work.FullName, "VBCABLE_Setup_x64.exe"),
                Architecture.X86 => Path.Combine(work.FullName, "VBCABLE_Setup.exe"),
                _ => throw new PlatformNotSupportedException("This Windows architecture is unsupported.")
            };
            if (!File.Exists(installer))
                throw new InvalidDataException("The package has no matching VB-CABLE installer.");

            progress.Report("请确认 Windows 授权，并在 VB-CABLE 安装程序中选择 Install Driver。");
            using var process = Process.Start(new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installer)!
            }) ?? throw new InvalidOperationException("Could not start the VB-CABLE installer.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode is not (0 or 1641 or 3010))
            {
                AppLogger.Warn($"VB-CABLE installer exit code {process.ExitCode}.", nameof(VbCableInstaller));
                return "VB-CABLE 安装程序未成功结束。请查看其提示后重试。";
            }

            var status = VirtualAudioAuditor.AuditCableInputDevice(forceRefresh: true);
            return status == DriverReadinessStatus.Ready
                ? "已检测到 VB-CABLE。请按官方安装说明重启 Windows。"
                : "安装程序已退出。若已完成安装，请重启 Windows，然后重新检测。";
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return "已取消管理员授权。";
        }
        catch (Exception error)
        {
            AppLogger.Error("VB-CABLE installation failed", error, nameof(VbCableInstaller));
            return "无法下载或启动 VB-CABLE 安装程序。请检查网络后重试。";
        }
        finally
        {
            if (work != null)
            {
                try { work.Delete(recursive: true); }
                catch (IOException error) { AppLogger.Warn(error.Message, nameof(VbCableInstaller)); }
                catch (UnauthorizedAccessException error) { AppLogger.Warn(error.Message, nameof(VbCableInstaller)); }
            }
            InstallGate.Release();
        }
    }

    internal static async Task<byte[]> DownloadVerifiedAsync(
        HttpClient client, IReadOnlyList<Uri> sources, string expectedSha256, IProgress<string>? progress = null)
    {
        Exception? lastError = null;
        foreach (var source in sources)
        {
            progress?.Report($"正在从 {source.Host} 下载 VB-CABLE…");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(MaxPackageBytes, timeout.Token).ConfigureAwait(false);
                byte[] data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("VB-CABLE package SHA-256 did not match.");

                return data;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                AppLogger.Warn($"VB-CABLE download from {source.Host} failed: {error.Message}", nameof(VbCableInstaller));
                lastError = error;
            }
        }

        throw new IOException("All VB-CABLE download sources failed.", lastError);
    }
}
