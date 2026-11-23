// Copyright (C) 2026 Remote Buddy Contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RemoteBuddy.Core.Audio;

public static class AudioLevelMeter
{
    public static float CalculateRms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return 0f;

        double sumOfSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double normalized = samples[i] / 32768.0;
            sumOfSquares += normalized * normalized;
        }

        double rms = Math.Sqrt(sumOfSquares / samples.Length);
        // Map to 0.0 ~ 1.0 with a non-linear perceptual scale
        float level = (float)Math.Clamp(rms * 4.0, 0.0, 1.0);
        return level;
    }
}
