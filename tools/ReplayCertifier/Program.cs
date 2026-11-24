using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MonsterPathPredictor.ReplayCertifier
{
    internal sealed class CliOptions
    {
        public bool SelfTest;
        public string ProtocolDir;
        public List<string> MapFiles = new();
        public string CorpusFile;
        public string CacheDir;
        public string CdnBase = "https://sereplaycn.feimogames.com/prod/";
        public string JsonOut;
        public string MdOut;
        public int Concurrency = 24;
        public int Limit;
        public bool SaveCache;
        public GateThresholds Thresholds = new();
    }

    internal static class Program
    {
        private const string UsageText =
            "Usage: ReplayCertifier --protocol-dir <dir> --maps <maps.json> [--maps <extra.json>] [--corpus <ids.txt>] [--cache-dir <dir>] [--save-cache] [--concurrency 24] [--limit N] [--json-out report.json] [--md-out report.md] | --self-test";

        public static bool TryParseArgs(string[] args, out CliOptions opts, out string error)
        {
            opts = new CliOptions();
            error = null;
            if (args == null || args.Length == 0)
            {
                error = UsageText;
                return false;
            }

            string parseErr = null;
            for (int i = 0; i < args.Length; i++)
            {
                string tok = args[i];
                if (string.Equals(tok, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    opts.SelfTest = true;
                    continue;
                }
                if (string.Equals(tok, "--save-cache", StringComparison.OrdinalIgnoreCase))
                {
                    opts.SaveCache = true;
                    continue;
                }

                string ReadValue(string optName)
                {
                    if (i + 1 >= args.Length)
                    {
                        parseErr = $"ERROR: Option {optName} requires a non-empty value.";
                        return null;
                    }
                    string val = args[++i];
                    if (string.IsNullOrWhiteSpace(val) || val.StartsWith("--", StringComparison.Ordinal))
                    {
                        parseErr = $"ERROR: Option {optName} requires a non-empty value (got \"{val}\").";
                        return null;
                    }
                    return val;
                }

                if (string.Equals(tok, "--protocol-dir", StringComparison.OrdinalIgnoreCase))
                {
                    opts.ProtocolDir = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                }
                else if (string.Equals(tok, "--maps", StringComparison.OrdinalIgnoreCase))
                {
                    string mf = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    opts.MapFiles.Add(mf);
                }
                else if (string.Equals(tok, "--corpus", StringComparison.OrdinalIgnoreCase))
                {
                    opts.CorpusFile = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                }
                else if (string.Equals(tok, "--cache-dir", StringComparison.OrdinalIgnoreCase))
                {
                    opts.CacheDir = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                }
                else if (string.Equals(tok, "--cdn", StringComparison.OrdinalIgnoreCase))
                {
                    opts.CdnBase = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                }
                else if (string.Equals(tok, "--json-out", StringComparison.OrdinalIgnoreCase))
                {
                    opts.JsonOut = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                }
                else if (string.Equals(tok, "--md-out", StringComparison.OrdinalIgnoreCase))
                {
                    opts.MdOut = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                }
                else if (string.Equals(tok, "--concurrency", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!int.TryParse(v, out int c)) { error = $"ERROR: Invalid integer for {tok}: \"{v}\""; return false; }
                    opts.Concurrency = Math.Clamp(c, 1, 64);
                }
                else if (string.Equals(tok, "--limit", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!int.TryParse(v, out int lim) || lim < 0) { error = $"ERROR: Invalid non-negative integer for {tok}: \"{v}\""; return false; }
                    opts.Limit = lim;
                }
                else if (string.Equals(tok, "--min-det-accuracy", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) { error = $"ERROR: Invalid number for {tok}: \"{v}\""; return false; }
                    opts.Thresholds.MinDetAccuracy = d;
                }
                else if (string.Equals(tok, "--min-thief-accuracy", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) { error = $"ERROR: Invalid number for {tok}: \"{v}\""; return false; }
                    opts.Thresholds.MinThiefAccuracy = d;
                }
                else if (string.Equals(tok, "--max-abs-z", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) { error = $"ERROR: Invalid number for {tok}: \"{v}\""; return false; }
                    opts.Thresholds.MaxAbsZ = d;
                }
                else if (string.Equals(tok, "--min-aggro-samples", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!long.TryParse(v, out long n) || n < 0) { error = $"ERROR: Invalid integer for {tok}: \"{v}\""; return false; }
                    opts.Thresholds.MinAggroSamples = n;
                }
                else if (string.Equals(tok, "--min-thief-samples", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!long.TryParse(v, out long n) || n < 0) { error = $"ERROR: Invalid integer for {tok}: \"{v}\""; return false; }
                    opts.Thresholds.MinThiefSamples = n;
                }
                else if (string.Equals(tok, "--min-calib-bins", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!int.TryParse(v, out int n) || n < 0) { error = $"ERROR: Invalid integer for {tok}: \"{v}\""; return false; }
                    opts.Thresholds.MinCalibratedBins = n;
                }
                else if (string.Equals(tok, "--max-download-fail-rate", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) { error = $"ERROR: Invalid number for {tok}: \"{v}\""; return false; }
                    opts.Thresholds.MaxDownloadFailRatio = d;
                }
                else
                {
                    error = $"ERROR: Unknown argument: \"{tok}\"";
                    return false;
                }
            }

            if (!opts.SelfTest &&
                (string.IsNullOrWhiteSpace(opts.ProtocolDir) ||
                 opts.MapFiles.Count == 0 ||
                 (string.IsNullOrWhiteSpace(opts.CorpusFile) && string.IsNullOrWhiteSpace(opts.CacheDir))))
            {
                error = UsageText;
                return false;
            }

            return true;
        }

        public static async Task<int> Main(string[] args)
        {
            if (!TryParseArgs(args, out var opts, out string parseError))
            {
                Console.Error.WriteLine(parseError);
                return 1;
            }

            if (opts.SelfTest)
            {
                return SelfTests.RunAll();
            }

            string protocolDir = opts.ProtocolDir;
            var mapFiles = opts.MapFiles;
            string corpusFile = opts.CorpusFile;
            string cacheDir = opts.CacheDir;
            string cdnBase = opts.CdnBase;
            string jsonOut = opts.JsonOut;
            string mdOut = opts.MdOut;
            int concurrency = opts.Concurrency;
            int limit = opts.Limit;
            bool saveCache = opts.SaveCache;
            var thresholds = opts.Thresholds;

            var decoder = new ProtocolDecoder(protocolDir);
            var catalog = new BoardCatalog();
            foreach (var mf in mapFiles) catalog.LoadJsonFile(mf);
            if (catalog.MapCount == 0)
            {
                Console.Error.WriteLine("ERROR: No maps loaded from --maps arguments: " + string.Join(", ", mapFiles));
                return 1;
            }

            var replayIds = new List<string>();
            if (!string.IsNullOrWhiteSpace(corpusFile))
            {
                if (!File.Exists(corpusFile))
                {
                    Console.Error.WriteLine("ERROR: Specified --corpus file not found: " + corpusFile);
                    return 1;
                }
                string[] lines = File.ReadAllLines(corpusFile);
                for (int li = 0; li < lines.Length; li++)
                {
                    string trimmed = lines[li].Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (!IsValidReplayId(trimmed))
                    {
                        Console.Error.WriteLine($"ERROR: Invalid replay ID at {corpusFile}:{li + 1}: \"{trimmed}\"");
                        return 1;
                    }
                    replayIds.Add(trimmed);
                }
            }
            else if (!string.IsNullOrWhiteSpace(cacheDir))
            {
                if (!Directory.Exists(cacheDir))
                {
                    Console.Error.WriteLine("ERROR: Specified --cache-dir not found: " + cacheDir);
                    return 1;
                }
                replayIds.AddRange(DiscoverCachedReplayIds(cacheDir));
            }

            replayIds = replayIds.Distinct(StringComparer.Ordinal).ToList();
            if (limit > 0 && replayIds.Count > limit) replayIds = replayIds.Take(limit).ToList();

            if (saveCache && !string.IsNullOrWhiteSpace(cacheDir)) Directory.CreateDirectory(cacheDir);

            Console.WriteLine($"Loaded {catalog.MapCount} official PVE maps. Certifying {replayIds.Count:N0} replays (concurrency={concurrency}, stream-only={!saveCache})...");

            using var handler = new HttpClientHandler { UseProxy = false, MaxConnectionsPerServer = concurrency + 8 };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };

            var sw = Stopwatch.StartNew();
            var perReplay = new ConcurrentBag<CertificationSummary>();
            int processed = 0, cacheHits = 0, streamed = 0;

            await Parallel.ForEachAsync(replayIds, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (id, ct) =>
            {
                byte[] bytes = null;
                bool hadCorruptLocalFile = false;
                string cachedPath = ResolveCachedReplayPath(cacheDir, id);
                if (cachedPath != null)
                {
                    var fi = new FileInfo(cachedPath);
                    if (fi.Length > 1000)
                    {
                        bytes = await File.ReadAllBytesAsync(cachedPath, ct);
                        Interlocked.Increment(ref cacheHits);
                    }
                    else
                    {
                        hadCorruptLocalFile = true;
                    }
                }
                string savePath = ResolveSaveCachePath(cacheDir, id, cachedPath);

                if (bytes == null)
                {
                    string[] cdns = cdnBase.Contains("sereplaycn", StringComparison.OrdinalIgnoreCase)
                        ? new[] { cdnBase, "https://sereplayjp.feimogames.com/prod/" }
                        : new[] { cdnBase };

                    foreach (string baseUrl in cdns)
                    {
                        for (int attempt = 0; attempt < 2; attempt++)
                        {
                            try
                            {
                                byte[] fetched = await client.GetByteArrayAsync(baseUrl + id, ct);
                                if (fetched.Length > 1000)
                                {
                                    bytes = fetched;
                                    Interlocked.Increment(ref streamed);
                                    break;
                                }
                            }
                            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                break;
                            }
                            catch
                            {
                                if (attempt < 1) await Task.Delay(250 * (attempt + 1), ct);
                            }
                        }
                        if (bytes != null) break;
                    }

                    // Persist downloaded replay back to cache in an isolated try/catch so a cache write error
                    // NEVER clears the downloaded bytes or misclassifies a valid download as DownloadFailed.
                    if (bytes != null && saveCache && savePath != null)
                    {
                        try
                        {
                            string parentDir = Path.GetDirectoryName(savePath);
                            if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
                            await File.WriteAllBytesAsync(savePath, bytes, ct);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"WARN: Failed to write cache file {savePath}: {ex.Message}");
                        }
                    }
                }

                CertificationSummary local;
                if (bytes == null)
                {
                    local = hadCorruptLocalFile
                        ? new CertificationSummary { ReplaysAttempted = 1, TruncatedOrCorrupt = 1 }
                        : new CertificationSummary { ReplaysAttempted = 1, DownloadFailed = 1 };
                }
                else
                {
                    try { local = ReplayEvaluator.EvaluateBytes(id, bytes, decoder, catalog); }
                    catch { local = new CertificationSummary { ReplaysAttempted = 1, ParseErrors = 1 }; }
                }

                perReplay.Add(local);
                int done = Interlocked.Increment(ref processed);
                if (done % 250 == 0 || done == replayIds.Count)
                    Console.WriteLine($"  progress: {done:N0}/{replayIds.Count:N0} ({sw.Elapsed.TotalSeconds:F1}s | cached={cacheHits:N0}, streamed={streamed:N0})");
            });

            sw.Stop();
            var total = new CertificationSummary();
            foreach (var item in perReplay) total.MergeFrom(item);

            bool passed = ReportWriter.PrintAndExport(total, catalog, sw.Elapsed.TotalSeconds, thresholds, jsonOut, mdOut);
            return passed ? 0 : 2;
        }

        private static bool IsValidReplayId(string s)
        {
            return !string.IsNullOrWhiteSpace(s) && s.Length >= 10 && s.All(char.IsDigit);
        }

        public static List<string> DiscoverCachedReplayIds(string cacheDir)
        {
            var ids = new List<string>();
            if (string.IsNullOrWhiteSpace(cacheDir) || !Directory.Exists(cacheDir)) return ids;

            foreach (string file in Directory.GetFiles(cacheDir))
            {
                string name = Path.GetFileName(file);
                if (IsValidReplayId(name)) ids.Add(name);
            }

            // Also support official game & AstralParty.Toys nested layout: <root>/<id>/<id>
            foreach (string subDir in Directory.GetDirectories(cacheDir))
            {
                string dirName = Path.GetFileName(subDir);
                if (!IsValidReplayId(dirName)) continue;
                string nested = Path.Combine(subDir, dirName);
                if (File.Exists(nested)) ids.Add(dirName);
            }

            return ids.Distinct(StringComparer.Ordinal).ToList();
        }

        public static string ResolveCachedReplayPath(string cacheDir, string replayId)
        {
            if (string.IsNullOrWhiteSpace(cacheDir) || string.IsNullOrWhiteSpace(replayId)) return null;
            string flat = Path.Combine(cacheDir, replayId);
            if (File.Exists(flat)) return flat;
            string nested = Path.Combine(cacheDir, replayId, replayId);
            if (File.Exists(nested)) return nested;
            return null;
        }

        public static string ResolveSaveCachePath(string cacheDir, string replayId, string existingCachedPath)
        {
            if (string.IsNullOrWhiteSpace(cacheDir) || string.IsNullOrWhiteSpace(replayId)) return null;
            if (!string.IsNullOrWhiteSpace(existingCachedPath)) return existingCachedPath;
            string flat = Path.Combine(cacheDir, replayId);
            if (Directory.Exists(flat)) return Path.Combine(flat, replayId);
            return flat;
        }
    }
}
