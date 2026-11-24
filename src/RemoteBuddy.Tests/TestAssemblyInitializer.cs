// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.CompilerServices;
using RemoteBuddy.Core.Diagnostics;

namespace RemoteBuddy.Tests;

internal static class TestAssemblyInitializer
{
    internal static string TestLogDirectory { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void InitializeTestLoggingIsolation()
    {
        TestLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "RemoteBuddy_TestLogs",
            $"{Environment.ProcessId}_{Guid.NewGuid():N}");

        AppLogger.DefaultLogDirectoryOverride = TestLogDirectory;
        AppLogger.Initialize(TestLogDirectory);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(TestLogDirectory) && Directory.Exists(TestLogDirectory))
                {
                    Directory.Delete(TestLogDirectory, recursive: true);
                }
            }
            catch
            {
                // Ignore best-effort temp cleanup failures on test host exit.
            }
        };
    }
}
