namespace ReTweak.Core;

/// <summary>What the GPU is being used for, as far as Auto can tell.</summary>
public enum ActivityKind
{
    /// <summary>Not enough information (no load readings): Auto uses its general target and tuning.</summary>
    Unknown,
    Idle,
    Desktop,
    Video,
    Gaming,
    Compute,
}

/// <summary>How Auto behaves for one activity. Editable in Settings.</summary>
/// <param name="TargetHotspotC">Hotspot to hold; the core target is 8 °C lower when there's no hotspot.</param>
/// <param name="LeadSeconds">How far ahead to project a rising power trend, so fans move before the heat arrives.</param>
public sealed record ActivityProfile(double TargetHotspotC, SmartPriority Priority, double LeadSeconds);

/// <summary>One second of the readings activity detection uses; any may be missing.</summary>
public readonly record struct ActivitySample(double? GpuLoad, double? MemoryLoad, double? DecoderLoad, double? EncoderLoad,
                                             double? PowerFraction, bool Fullscreen, string? ProcessName);

/// <summary>
/// Works out what the user is doing from the last ~10 s of GPU readings and the foreground app.
/// Rules, first match wins:
/// <list type="bullet">
/// <item>Compute: a known render/AI/encode program with real GPU load; or the encoder busy outside a
/// fullscreen app; or very high, steady load (power varies little) outside a fullscreen app.</item>
/// <item>Video: the decode engine busy while the 3D load stays moderate.</item>
/// <item>Gaming: a fullscreen app with real GPU load (not a browser or media player); or high load whose
/// power swings frame to frame.</item>
/// <item>Idle: next to no load or power.</item>
/// <item>Desktop: anything else.</item>
/// </list>
/// Hysteresis: a heavier activity takes over after 3 consistent seconds, a lighter one after 15, so a
/// loading screen doesn't drop a game to Desktop.
/// </summary>
public sealed class ActivityClassifier
{
    private const int Window = 10, HeavierAfter = 3, LighterAfter = 15;

    /// <summary>Programs whose GPU use is long, steady work: renderers, AI, video encoders.</summary>
    private static readonly string[] ComputeApps =
    [
        "blender", "python", "ffmpeg", "handbrake", "resolve", "comfyui", "ollama", "lmstudio", "stable",
        "octane", "redshift", "vray", "keyshot", "unreal", "furmark", "cinebench", "octanebench", "tensor",
        "topaz", "premiere", "afterfx", "mediaencoder", "obs64", "kdenlive", "whisper",
    ];

    /// <summary>
    /// Programs that go fullscreen without being games: browsers and media players. Fullscreen Chrome at
    /// ~25% load (a video whose decoding barely shows on the decoder) was counted as Gaming (diagnostic
    /// log, 2026-10-20). They can still count as Gaming through the swinging-load rule (a browser game).
    /// </summary>
    private static readonly string[] FullscreenNotGames =
    [
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "librewolf", "waterfox",
        "vlc", "mpc-hc", "mpc-be", "mpv", "potplayer", "wmplayer", "video.ui", "media.player", "kodi", "plex",
        "netflix", "spotify", "powerpnt", "acrobat",
    ];

    private readonly Queue<ActivitySample> _recent = new();
    private ActivityKind _current = ActivityKind.Unknown, _candidate = ActivityKind.Unknown;
    private int _candidateFor;

    public ActivityKind Current => _current;
    /// <summary>Why <see cref="Current"/> was picked, for the dashboard and the diagnostic log.</summary>
    public string Reason { get; private set; } = "";

    public ActivityKind Update(ActivitySample s)
    {
        _recent.Enqueue(s);
        while (_recent.Count > Window) _recent.Dequeue();

        var (raw, why) = Classify();
        if (raw == _current) { _candidateFor = 0; Reason = why; return _current; }
        if (raw != _candidate) { _candidate = raw; _candidateFor = 0; }
        _candidateFor++;
        bool heavier = Rank(raw) > Rank(_current);
        // Unknown -> anything is immediate: there's nothing to protect yet.
        if (_current == ActivityKind.Unknown || _candidateFor >= (heavier ? HeavierAfter : LighterAfter))
        {
            _current = raw;
            _candidateFor = 0;
            Reason = why;
        }
        return _current;
    }

    private static int Rank(ActivityKind a) => a switch
    {
        ActivityKind.Gaming or ActivityKind.Compute => 4,
        ActivityKind.Video => 3,
        ActivityKind.Desktop => 2,
        ActivityKind.Idle => 1,
        _ => 0,
    };

    private (ActivityKind, string) Classify()
    {
        var latest = _recent.Last();
        double? Avg(Func<ActivitySample, double?> f) =>
            _recent.Select(f).OfType<double>().DefaultIfEmpty(double.NaN).Average() is var a && !double.IsNaN(a) ? a : null;

        double? gpu = Avg(x => x.GpuLoad), dec = Avg(x => x.DecoderLoad), enc = Avg(x => x.EncoderLoad), power = Avg(x => x.PowerFraction);
        if (gpu is not double load) return (ActivityKind.Unknown, "no load readings");
        var powers = _recent.Select(x => x.PowerFraction).OfType<double>().ToList();
        double swing = powers.Count >= 3 ? StdDev(powers) : 0;
        string app = latest.ProcessName ?? "";
        bool fullscreen = latest.Fullscreen;
        string? computeApp = ComputeApps.FirstOrDefault(a => app.Contains(a, StringComparison.OrdinalIgnoreCase));

        if (computeApp is not null && load >= 20) return (ActivityKind.Compute, $"{app} with {load:F0}% GPU load");
        if (enc >= 20 && !fullscreen) return (ActivityKind.Compute, $"video encoder at {enc:F0}%");
        if (load >= 85 && swing < 0.03 && !fullscreen) return (ActivityKind.Compute, $"steady {load:F0}% load");
        if (dec >= 5 && load < 50) return (ActivityKind.Video, $"video decoder at {dec:F0}%");
        bool notGame = FullscreenNotGames.Any(a => app.Contains(a, StringComparison.OrdinalIgnoreCase));
        if (fullscreen && load >= 25 && !notGame) return (ActivityKind.Gaming, $"fullscreen {app} at {load:F0}% load");
        if (load >= 40 && swing >= 0.03) return (ActivityKind.Gaming, $"{load:F0}% load, power swinging ±{swing * 100:F0}%");
        if (load < 5 && (power ?? 0) < 0.15 && (dec ?? 0) < 2) return (ActivityKind.Idle, $"{load:F0}% load");
        return (ActivityKind.Desktop, $"{load:F0}% load");
    }

    private static double StdDev(List<double> v)
    {
        double mean = v.Average();
        return Math.Sqrt(v.Average(x => (x - mean) * (x - mean)));
    }
}

/// <summary>
/// How much airflow this card needs at each power level, learned while Auto holds its target.
/// The proportional-integral loop ends up carrying a steady offset when the built-in estimate
/// (25% + 55% x power) is off for this card and case; that offset is moved slowly into this table,
/// so next time the fans start at the right speed. Twelve buckets over 0-120% of the power limit.
/// </summary>
public sealed class AirflowModel
{
    public const int Buckets = 12;
    private const double MaxCorrection = 30;
    private readonly double[] _correction = new double[Buckets];

    public IReadOnlyList<double> Corrections => _correction;

    public void Load(IReadOnlyList<double>? saved)
    {
        if (saved is null || saved.Count != Buckets) return;
        for (int i = 0; i < Buckets; i++) _correction[i] = Math.Clamp(saved[i], -MaxCorrection, MaxCorrection);
    }

    /// <summary>Fan % the card needs at this power, before temperature feedback.</summary>
    public double Estimate(double powerFraction)
    {
        double p = Math.Clamp(powerFraction, 0, 1.2);
        return 25 + 55 * Math.Min(p, 1) + Interpolate(p);
    }

    /// <summary>
    /// Raises <see cref="Estimate"/> at this power by exactly <paramref name="amount"/>, spread over the two
    /// neighbouring buckets, so the controller can take the same amount out of its integral with no jump.
    /// Returns how much was actually moved (less when a bucket hits its limit).
    /// </summary>
    public double Learn(double powerFraction, double amount)
    {
        double p = Math.Clamp(powerFraction, 0, 1.2);
        double x = p / 1.2 * (Buckets - 1);
        int i = Math.Min((int)Math.Floor(x), Buckets - 1);
        double f = i + 1 < Buckets ? x - i : 0;
        double before = Interpolate(p);
        // Interpolate(p) = c[i]·(1-f) + c[i+1]·f, so adding k·(1-f) and k·f raises it by k·((1-f)² + f²).
        double k = amount / ((1 - f) * (1 - f) + f * f);
        _correction[i] = Math.Clamp(_correction[i] + k * (1 - f), -MaxCorrection, MaxCorrection);
        if (i + 1 < Buckets) _correction[i + 1] = Math.Clamp(_correction[i + 1] + k * f, -MaxCorrection, MaxCorrection);
        return Interpolate(p) - before;
    }

    private double Interpolate(double p)
    {
        double x = p / 1.2 * (Buckets - 1);
        int i = Math.Clamp((int)Math.Floor(x), 0, Buckets - 1);
        double f = x - i;
        return i + 1 < Buckets ? _correction[i] * (1 - f) + _correction[i + 1] * f : _correction[i];
    }
}
