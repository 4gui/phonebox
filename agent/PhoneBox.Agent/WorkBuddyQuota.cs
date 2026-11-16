using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// WorkBuddy keeps every model response in <c>~/.workbuddy/projects/&lt;workspace&gt;/&lt;session&gt;.jsonl</c>, and each of
/// those lines carries the quota window the response was charged against (<c>"quota"</c>: percent used and when it
/// resets) plus what it cost in credits. Only those local files are read; the account balance itself is known to
/// WorkBuddy's server alone, so it cannot be shown here.
/// </summary>
public static class WorkBuddyQuota
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>How much of the end of the newest log is searched for the last quota window (one line is ~10 KB).</summary>
    private const int TailBytes = 400_000;

    /// <summary>What has been counted for one log file, so a growing file is parsed once per appended line.</summary>
    private sealed class Sum
    {
        public long Offset;
        public double Credits;   // the whole file: one WorkBuddy session
        public double Today;     // only the responses stamped after local midnight
        public string Rest = ""; // an unfinished last line, kept for the next increment
    }

    private static readonly Dictionary<string, Sum> Sums = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    // "quota":{"model":"deepseek-v4.1-flash","percent":66,"reset_at":"1794849404","shared_models":[]}
    private static readonly Regex QuotaRx = new(
        "\"quota\"\\s*:\\s*\\{\\s*\"model\"\\s*:\\s*\"(?<model>[^\"]*)\"[^{}]*?\"percent\"\\s*:\\s*(?<pct>[0-9.]+)(?:[^{}]*?\"reset_at\"\\s*:\\s*\"?(?<reset>[0-9]+))?",
        RegexOptions.CultureInvariant);

    // One line of the log: the response timestamp comes first, the credits it cost later in the same line.
    private static readonly Regex LineRx = new(
        "\"timestamp\"\\s*:\\s*(?<ts>[0-9]{13})[\\s\\S]*?\"credit\"\\s*:\\s*(?<credit>[0-9.]+)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The WorkBuddy quota provider, or null when WorkBuddy has never answered on this computer.
    /// <paramref name="nowMs"/> stamps the reading; the caller rolls the window when its reset has passed.
    /// </summary>
    public static QuotaProvider? Read(long nowMs)
    {
        var root = Path.Combine(Home, ".workbuddy", "projects");
        if (!Directory.Exists(root)) return null;

        FileInfo? newest = null;
        var dayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        var today = new List<FileInfo>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                if (IsBackup(path, root)) continue; // .history-fix-backup-*: copies of older logs
                FileInfo f;
                try { f = new FileInfo(path); } catch { continue; }
                if (!f.Exists || f.Length == 0) continue;
                if (newest is null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
                if (new DateTimeOffset(f.LastWriteTime).ToUnixTimeMilliseconds() >= dayStart) today.Add(f);
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (newest is null) return null;

        var window = ReadWindow(newest);
        if (window is null) return null;

        double session;
        double day = 0;
        lock (Gate)
        {
            (session, _) = Accumulate(newest, dayStart);
            foreach (var f in today) day += Accumulate(f, dayStart).Today;
        }

        var percent = Math.Clamp(Math.Round(window.Value.Percent, 1), 0, 100);
        var severity = percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal";
        var limits = new[] { new QuotaLimit("window", percent, window.Value.ResetsAt, null, severity) };
        var extra = session > 0 || day > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{session:0.#} credits this session · {day:0.#} today")
            : null;
        return new QuotaProvider("workbuddy", "WorkBuddy", window.Value.Model, limits, extra, nowMs, null);
    }

    /// <summary>The last quota window in the log: the model, the share of it used, and when it resets (ms).</summary>
    private static (string Model, double Percent, long? ResetsAt)? ReadWindow(FileInfo f)
    {
        try
        {
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            var start = Math.Max(0, f.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (start > 0)
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak < 0) return null;
                text = text[(firstBreak + 1)..]; // the cut line is not a whole record
            }

            (string Model, double Percent, long? ResetsAt)? last = null;
            foreach (Match m in QuotaRx.Matches(text))
            {
                if (!double.TryParse(m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                    continue;
                long? resetsAt = long.TryParse(m.Groups["reset"].Value, out var reset) ? reset * 1000 : null;
                last = (m.Groups["model"].Value, percent, resetsAt);
            }
            return last;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Credits of one log: the whole file for the session, the lines stamped after local midnight for the day.
    /// Reads only what was appended since the last call; a file that shrank is counted again from the start.
    /// </summary>
    private static (double Credits, double Today) Accumulate(FileInfo f, long dayStart)
    {
        if (!Sums.TryGetValue(f.FullName, out var sum)) Sums[f.FullName] = sum = new Sum();
        var length = f.Length;
        if (length < sum.Offset) { sum.Offset = 0; sum.Credits = 0; sum.Today = 0; sum.Rest = ""; }
        if (length == sum.Offset) return (sum.Credits, sum.Today);

        try
        {
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            stream.Seek(sum.Offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024);
            var text = sum.Rest + reader.ReadToEnd();
            sum.Offset = stream.Position;
            var lastBreak = text.LastIndexOf('\n');
            if (lastBreak < 0) { sum.Rest = text; return (sum.Credits, sum.Today); } // no complete line yet
            sum.Rest = text[(lastBreak + 1)..];
            foreach (var line in text[..lastBreak].Split('\n'))
            {
                var m = LineRx.Match(line);
                if (!m.Success) continue;
                if (!double.TryParse(m.Groups["credit"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var credit))
                    continue;
                sum.Credits += credit;
                if (long.TryParse(m.Groups["ts"].Value, out var ts) && ts >= dayStart) sum.Today += credit;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (sum.Credits, sum.Today);
    }

    /// <summary>Copies kept by WorkBuddy's own history repairs: every segment below the projects folder is a real session directory.</summary>
    private static bool IsBackup(string path, string root)
    {
        string relative;
        try { relative = Path.GetRelativePath(root, path); }
        catch (ArgumentException) { return false; }
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment.StartsWith('.')) return true;
        return false;
    }
}
