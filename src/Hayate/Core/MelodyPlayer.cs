using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Media;
using System.Text;

namespace Hayate.Core;

/// <summary>
/// 完了を知らせる短いメロディーを鳴らす。
/// 音源ファイルを持たず、その場で 16bit PCM の WAV を組み立てて再生するので、
/// 配布物は実行ファイルだけで済む。
/// </summary>
public static class MelodyPlayer
{
    private const int SampleRate = 44100;

    // 音名 → 周波数（A4 = 440Hz）
    private const double C5 = 523.25, D5 = 587.33, E5 = 659.25, G5 = 783.99;
    private const double A5 = 880.00, C6 = 1046.50, E6 = 1318.51, G4 = 392.00;
    private const double E4 = 329.63, C4 = 261.63;

    /// <summary>成功時。明るく上がっていく 4 音。</summary>
    public static void PlaySuccess() => PlayAsync(new (double, int)[]
    {
        (C5, 110), (E5, 110), (G5, 110), (C6, 380)
    });

    /// <summary>失敗が混じったとき。控えめに下がる 3 音。</summary>
    public static void PlayWarning() => PlayAsync(new (double, int)[]
    {
        (A5, 140), (E5, 140), (C5, 380)
    });

    /// <summary>中止したとき。低く短い 2 音。</summary>
    public static void PlayCanceled() => PlayAsync(new (double, int)[]
    {
        (E4, 130), (C4, 260)
    });

    private static void PlayAsync((double freq, int ms)[] notes)
    {
        // 再生でコピー処理や UI を待たせない
        Task.Run(() =>
        {
            try
            {
                using var stream = BuildWav(notes);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch
            {
                // 音声デバイスが無い環境などでは黙って諦める
                try { SystemSounds.Asterisk.Play(); } catch { }
            }
        });
    }

    private static MemoryStream BuildWav((double freq, int ms)[] notes)
    {
        var samples = new List<short>(SampleRate);

        foreach (var (freq, ms) in notes)
        {
            int count = SampleRate * ms / 1000;
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / SampleRate;

                // 基音に少しだけ倍音を混ぜると電子音っぽさが和らぐ
                double wave = Math.Sin(2 * Math.PI * freq * t)
                            + 0.28 * Math.Sin(4 * Math.PI * freq * t)
                            + 0.12 * Math.Sin(6 * Math.PI * freq * t);
                wave /= 1.40;

                // 立ち上がりと減衰を付けてプチッというノイズを防ぐ
                double progress = (double)i / count;
                double attack = Math.Min(1.0, progress / 0.04);
                double release = Math.Min(1.0, (1.0 - progress) / 0.30);
                double envelope = attack * release * Math.Exp(-1.6 * progress);

                samples.Add((short)(wave * envelope * 8600));
            }

            // 音と音のあいだにわずかな間
            for (int i = 0; i < SampleRate * 18 / 1000; i++) samples.Add(0);
        }

        return WriteWav(samples);
    }

    private static MemoryStream WriteWav(List<short> samples)
    {
        int dataBytes = samples.Count * 2;
        var ms = new MemoryStream(44 + dataBytes);
        var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());

        w.Write("fmt "u8.ToArray());
        w.Write(16);                       // fmt チャンクの長さ
        w.Write((short)1);                 // PCM
        w.Write((short)1);                 // モノラル
        w.Write(SampleRate);
        w.Write(SampleRate * 2);           // バイト/秒
        w.Write((short)2);                 // ブロックサイズ
        w.Write((short)16);                // ビット深度

        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (short s in samples) w.Write(s);

        w.Flush();
        ms.Position = 0;
        return ms;
    }
}
