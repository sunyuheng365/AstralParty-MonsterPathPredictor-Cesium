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
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d) || d < 0.0 || d > 1.0)
                    {
                        error = $"ERROR: Invalid probability in [0, 1] for {tok}: \"{v}\"";
                        return false;
                    }
                    opts.Thresholds.MinDetAccuracy = d;
                }
                else if (string.Equals(tok, "--min-thief-accuracy", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d) || d < 0.0 || d > 1.0)
                    {
                        error = $"ERROR: Invalid probability in [0, 1] for {tok}: \"{v}\"";
                        return false;
                    }
                    opts.Thresholds.MinThiefAccuracy = d;
                }
                else if (string.Equals(tok, "--max-abs-z", StringComparison.OrdinalIgnoreCase))
                {
                    string v = ReadValue(tok);
                    if (parseErr != null) { error = parseErr; return false; }
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d) || d < 0.0)
                    {
                        error = $"ERROR: Invalid non-negative finite number for {tok}: \"{v}\"";
                        return false;
                    }
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
                    if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d) || d < 0.0 || d > 1.0)
                    {
                        error = $"ERROR: Invalid rate in [0, 1] for {tok}: \"{v}\"";
                        return false;
                    }
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

            if (!opts.SelfTest && opts.SaveCache && string.IsNullOrWhiteSpace(opts.CacheDir))
            {
                error = "ERROR: --save-cache requires --cache-dir <dir>.";
                return false;
            }

            if (!opts.SelfTest && ReportWriter.HaveConflictingOutputPaths(opts.JsonOut, opts.MdOut))
            {
                error = "ERROR: --json-out and --md-out must not resolve to the same path or ancestor/descendant paths.";
                return false;
            }

            if (!opts.SelfTest && HasInputOutputPathConflict(opts, out string conflictErr))
            {
                error = conflictErr;
                return false;
            }

            return true;
        }

        private static bool HasInputOutputPathConflict(CliOptions opts, out string error)
        {
            error = null;
            string[] outputs = { opts.JsonOut, opts.MdOut };
            string[] outputFlags = { "--json-out", "--md-out" };
            for (int i = 0; i < outputs.Length; i++)
            {
                string outPath = outputs[i];
                if (string.IsNullOrWhiteSpace(outPath)) continue;
                if (ReportWriter.AreSameOutputPath(outPath, opts.CorpusFile))
                {
                    error = $"ERROR: {outputFlags[i]} must not overwrite --corpus file \"{opts.CorpusFile}\".";
                    return true;
                }
                for (int m = 0; m < opts.MapFiles.Count; m++)
                {
                    if (ReportWriter.AreSameOutputPath(outPath, opts.MapFiles[m]))
                    {
                        error = $"ERROR: {outputFlags[i]} must not overwrite --maps file \"{opts.MapFiles[m]}\".";
                        return true;
                    }
                }
                if (ReportWriter.IsPathInsideDirectory(outPath, opts.ProtocolDir))
                {
                    error = $"ERROR: {outputFlags[i]} must not write into --protocol-dir \"{opts.ProtocolDir}\".";
                    return true;
                }
                if (ReportWriter.IsPathInsideDirectory(outPath, opts.CacheDir))
                {
                    error = $"ERROR: {outputFlags[i]} must not write into --cache-dir \"{opts.CacheDir}\".";
                    return true;
                }
            }
            return false;
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
            foreach (var mf in mapFiles)
            {
                if (!File.Exists(mf))
                {
                    Console.Error.WriteLine("ERROR: Specified --maps file not found: " + mf);
                    return 1;
                }
                try
                {
                    int loaded = catalog.LoadJsonFile(mf);
                    if (loaded <= 0)
                    {
                        Console.Error.WriteLine("ERROR: Specified --maps file contained no valid map definitions: " + mf);
                        return 1;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR: Failed to load --maps file \"{mf}\": {ex.Message}");
                    return 1;
                }
            }
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

            if (!string.IsNullOrWhiteSpace(cacheDir) &&
                HasCachePathConflict(replayIds, cacheDir, corpusFile, mapFiles, jsonOut, mdOut, out string cacheConflictErr))
            {
                Console.Error.WriteLine(cacheConflictErr);
                return 1;
            }

            if (saveCache && !string.IsNullOrWhiteSpace(cacheDir)) Directory.CreateDirectory(cacheDir);

            Console.WriteLine($"Loaded {catalog.MapCount} official PVE maps. Certifying {replayIds.Count:N0} replays (concurrency={concurrency}, stream-only={!saveCache})...");

            using var handler = new HttpClientHandler { UseProxy = false, MaxConnectionsPerServer = concurrency + 8 };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };

            var sw = Stopwatch.StartNew();
            var perReplay = new ConcurrentBag<CertificationSummary>();
            var cacheSeenSha = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var cacheSeenVerifiedId = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var cachePublishLock = new object();
            if (saveCache && !string.IsNullOrWhiteSpace(cacheDir) && Directory.Exists(cacheDir))
            {
                // Pre-register identity of all healthy existing cache files outside the active --corpus/--limit
                // subset so --save-cache never publishes a duplicate copy of an already-cached replay on disk.
                PreregisterExternalCacheIdentity(cacheDir, replayIds, decoder, cacheSeenSha, cacheSeenVerifiedId);
            }
            var recoveryQueue = new ConcurrentBag<(string Id, string SavePath, CertificationSummary CorruptLocal)>();
            int processed = 0, cacheHits = 0, streamed = 0;

            Task<(byte[] Bytes, CertificationSummary Eval, bool ReceivedCorruptPayload)> DownloadAndEvalAsync(string id, CancellationToken ct)
            {
                string[] urls = cdnBase.Contains("sereplaycn", StringComparison.OrdinalIgnoreCase)
                    ? new[] { cdnBase + id, "https://sereplayjp.feimogames.com/prod/" + id }
                    : new[] { cdnBase + id };
                return DownloadAndEvaluateFromUrlsAsync(
                    client,
                    urls,
                    fetched => SafeEvaluateReplay(id, fetched, decoder, catalog),
                    () => Interlocked.Increment(ref streamed),
                    ct);
            }

            void ReportProgress()
            {
                int done = Interlocked.Increment(ref processed);
                if (done % 250 == 0 || done == replayIds.Count)
                    Console.WriteLine($"  progress: {done:N0}/{replayIds.Count:N0} ({sw.Elapsed.TotalSeconds:F1}s | cached={cacheHits:N0}, streamed={streamed:N0})");
            }

            void PublishCacheIfUnique(string savePath, byte[] bytes, CertificationSummary local)
            {
                TryPublishCacheIfUnique(savePath, cacheDir, saveCache, bytes, local, cacheSeenSha, cacheSeenVerifiedId, cachePublishLock);
            }

            // Phase 1: Read and register ALL existing local cached files first (no recovery writes yet),
            // so healthy local caches always register their SHA-256 / VerifiedReplayId BEFORE any corrupt
            // cache recovery or new download can publish a duplicate file to disk.
            async ValueTask ReadLocalCachePhaseAsync(string id, CancellationToken ct)
            {
                string cachedPath = ResolveCachedReplayPath(cacheDir, id);
                string savePath = ResolveSaveCachePath(cacheDir, id, cachedPath);
                byte[] bytes = null;
                bool hadCorruptLocalFile = false;
                if (cachedPath != null)
                {
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(cachedPath, ct);
                        if (bytes.Length == 0)
                        {
                            bytes = null;
                            hadCorruptLocalFile = true;
                        }
                        else
                        {
                            Interlocked.Increment(ref cacheHits);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"WARN: Failed to read cache file {cachedPath}: {ex.Message}");
                        hadCorruptLocalFile = true;
                    }
                }

                if (bytes == null)
                {
                    var corruptLocal = hadCorruptLocalFile
                        ? new CertificationSummary { ReplaysAttempted = 1, RequestedReplayId = id, TruncatedOrCorrupt = 1 }
                        : null;
                    recoveryQueue.Add((id, savePath, corruptLocal));
                    return;
                }

                var local = SafeEvaluateReplay(id, bytes, decoder, catalog);
                if (NeedsCacheRecovery(local))
                {
                    recoveryQueue.Add((id, savePath, local));
                    return;
                }

                if (IsCacheableAndClean(local))
                {
                    if (!string.IsNullOrEmpty(local.ContentSha256)) cacheSeenSha.TryAdd(local.ContentSha256, 0);
                    if (!string.IsNullOrEmpty(local.VerifiedReplayId)) cacheSeenVerifiedId.TryAdd(local.VerifiedReplayId, 0);
                }

                perReplay.Add(local);
                ReportProgress();
            }

            // Phase 2: Download uncached IDs and attempt ONE automatic CDN recovery for corrupt cached files,
            // with full semantic evaluation inside the CDN fallback loop and post-registration cache dedup.
            async ValueTask DownloadOrRecoverPhaseAsync((string Id, string SavePath, CertificationSummary CorruptLocal) item, CancellationToken ct)
            {
                var (bytes, eval, receivedCorruptRemote) = await DownloadAndEvalAsync(item.Id, ct);
                CertificationSummary finalLocal;
                if (bytes != null && eval != null && ShouldAdoptRecoveryResult(eval))
                {
                    finalLocal = eval;
                    PublishCacheIfUnique(item.SavePath, bytes, finalLocal);
                }
                else if (item.CorruptLocal != null)
                {
                    finalLocal = item.CorruptLocal;
                }
                else if (eval != null)
                {
                    finalLocal = eval;
                }
                else
                {
                    finalLocal = receivedCorruptRemote
                        ? new CertificationSummary { ReplaysAttempted = 1, RequestedReplayId = item.Id, TruncatedOrCorrupt = 1 }
                        : new CertificationSummary { ReplaysAttempted = 1, RequestedReplayId = item.Id, DownloadFailed = 1 };
                }

                perReplay.Add(finalLocal);
                ReportProgress();
            }

            var (cachedIds, uncachedIds) = PartitionByCachePresence(replayIds, cacheDir);
            if (cachedIds.Count > 0)
            {
                await Parallel.ForEachAsync(cachedIds, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, ReadLocalCachePhaseAsync);
            }
            foreach (string id in uncachedIds)
            {
                recoveryQueue.Add((id, ResolveSaveCachePath(cacheDir, id, null), null));
            }
            if (!recoveryQueue.IsEmpty)
            {
                await Parallel.ForEachAsync(recoveryQueue, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, DownloadOrRecoverPhaseAsync);
            }

            sw.Stop();
            var total = DeduplicateAndMerge(perReplay);

            bool passed = ReportWriter.PrintAndExport(total, catalog, sw.Elapsed.TotalSeconds, thresholds, jsonOut, mdOut);
            return passed ? 0 : 2;
        }

        public static bool HasCacheInputPathConflict(
            IEnumerable<string> replayIds,
            string cacheDir,
            string corpusFile,
            IReadOnlyList<string> mapFiles,
            out string error)
        {
            return HasCachePathConflict(replayIds, cacheDir, corpusFile, mapFiles, null, null, out error);
        }

        public static bool HasCachePathConflict(
            IEnumerable<string> replayIds,
            string cacheDir,
            string corpusFile,
            IReadOnlyList<string> mapFiles,
            string jsonOut,
            string mdOut,
            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(cacheDir) || replayIds == null) return false;

            var cmp = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            var inputCanon = new HashSet<string>(cmp);
            if (!string.IsNullOrWhiteSpace(corpusFile))
            {
                string canon = ReportWriter.ResolveCanonicalPath(corpusFile);
                if (!string.IsNullOrEmpty(canon)) inputCanon.Add(canon);
            }
            if (mapFiles != null)
            {
                for (int i = 0; i < mapFiles.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(mapFiles[i])) continue;
                    string canon = ReportWriter.ResolveCanonicalPath(mapFiles[i]);
                    if (!string.IsNullOrEmpty(canon)) inputCanon.Add(canon);
                }
            }

            var outputCanon = new HashSet<string>(cmp);
            if (!string.IsNullOrWhiteSpace(jsonOut))
            {
                string canon = ReportWriter.ResolveCanonicalPath(jsonOut);
                if (!string.IsNullOrEmpty(canon)) outputCanon.Add(canon);
            }
            if (!string.IsNullOrWhiteSpace(mdOut))
            {
                string canon = ReportWriter.ResolveCanonicalPath(mdOut);
                if (!string.IsNullOrEmpty(canon)) outputCanon.Add(canon);
            }

            if (inputCanon.Count == 0 && outputCanon.Count == 0) return false;

            var allIds = new HashSet<string>(replayIds, StringComparer.Ordinal);
            if (outputCanon.Count > 0 && Directory.Exists(cacheDir))
            {
                foreach (string extId in DiscoverCachedReplayIds(cacheDir))
                {
                    allIds.Add(extId);
                }
            }
            var activeSet = new HashSet<string>(replayIds, StringComparer.Ordinal);

            foreach (string id in allIds)
            {
                string rawFlat = Path.Combine(cacheDir, id);
                string rawNested = Path.Combine(cacheDir, id, id);
                string existing = ResolveCachedReplayPath(cacheDir, id);
                string savePath = ResolveSaveCachePath(cacheDir, id, existing);
                string[] candidates = { rawFlat, rawNested, existing, savePath };
                for (int c = 0; c < candidates.Length; c++)
                {
                    if (string.IsNullOrEmpty(candidates[c])) continue;
                    string canon = ReportWriter.ResolveCanonicalPath(candidates[c]);
                    if (string.IsNullOrEmpty(canon)) continue;
                    if (activeSet.Contains(id) && inputCanon.Contains(canon))
                    {
                        error = $"ERROR: Replay cache path for ID \"{id}\" conflicts with an input file (--corpus / --maps).";
                        return true;
                    }
                    if (outputCanon.Contains(canon))
                    {
                        error = $"ERROR: Report output (--json-out / --md-out) conflicts with replay cache path for ID \"{id}\".";
                        return true;
                    }
                }
            }
            return false;
        }

        public static void PreregisterExternalCacheIdentity(
            string cacheDir,
            IEnumerable<string> activeReplayIds,
            IProtocolDecoder decoder,
            ConcurrentDictionary<string, byte> cacheSeenSha,
            ConcurrentDictionary<string, byte> cacheSeenVerifiedId)
        {
            if (string.IsNullOrWhiteSpace(cacheDir) || !Directory.Exists(cacheDir)) return;
            var activeSet = activeReplayIds != null
                ? new HashSet<string>(activeReplayIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var existingIds = DiscoverCachedReplayIds(cacheDir);
            for (int i = 0; i < existingIds.Count; i++)
            {
                string extId = existingIds[i];
                if (activeSet.Contains(extId)) continue;
                string extPath = ResolveCachedReplayPath(cacheDir, extId);
                if (extPath == null) continue;
                try
                {
                    byte[] bytes = File.ReadAllBytes(extPath);
                    if (bytes == null || bytes.Length == 0) continue;
                    if (!ReplayEvaluator.HasValidFrameStream(bytes, decoder, extId, out string extVerifiedId))
                        continue;
                    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    cacheSeenSha.TryAdd(sha, 0);
                    if (!string.IsNullOrEmpty(extVerifiedId))
                    {
                        cacheSeenVerifiedId.TryAdd(extVerifiedId, 0);
                    }
                }
                catch { }
            }
        }

        public static bool NeedsCacheRecovery(CertificationSummary s)
        {
            return s != null && (s.TruncatedOrCorrupt > 0 || s.ParseErrors > 0 || s.IllegalMoveEdges > 0);
        }

        public static bool ShouldAdoptRecoveryResult(CertificationSummary s)
        {
            return s != null && s.TruncatedOrCorrupt == 0 && s.ParseErrors == 0 && s.IllegalMoveEdges == 0 &&
                   (s.ReplaysVerified == 1 || s.SkippedNonPve == 1 || s.MissingMap == 1);
        }

        public static bool IsCacheableAndClean(CertificationSummary s)
        {
            return s != null && (s.ReplaysVerified == 1 || s.SkippedNonPve == 1) &&
                   s.TruncatedOrCorrupt == 0 && s.ParseErrors == 0 && s.IllegalMoveEdges == 0;
        }

        public static (List<string> CachedIds, List<string> UncachedIds) PartitionByCachePresence(IReadOnlyList<string> replayIds, string cacheDir)
        {
            var cached = new List<string>();
            var uncached = new List<string>();
            for (int i = 0; i < replayIds.Count; i++)
            {
                if (ResolveCachedReplayPath(cacheDir, replayIds[i]) != null) cached.Add(replayIds[i]);
                else uncached.Add(replayIds[i]);
            }
            return (cached, uncached);
        }

        public static async Task<(byte[] Bytes, CertificationSummary Eval, bool ReceivedCorruptPayload)> DownloadAndEvaluateFromUrlsAsync(
            HttpClient client,
            IReadOnlyList<string> urls,
            Func<byte[], CertificationSummary> evaluatePayload,
            Action onStreamedSuccess,
            CancellationToken ct)
        {
            bool receivedCorrupt = false;
            CertificationSummary lastCorruptEval = null;
            byte[] bestMissingMapBytes = null;
            CertificationSummary bestMissingMapEval = null;

            for (int u = 0; u < urls.Count; u++)
            {
                string url = urls[u];
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using var resp = await client.GetAsync(url, ct);
                        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            break;
                        }
                        resp.EnsureSuccessStatusCode();
                        byte[] fetched = await resp.Content.ReadAsByteArrayAsync(ct);
                        var eval = evaluatePayload(fetched);
                        if (IsCacheableAndClean(eval))
                        {
                            onStreamedSuccess?.Invoke();
                            return (fetched, eval, false);
                        }
                        if (ShouldAdoptRecoveryResult(eval))
                        {
                            // Clean MissingMap diagnostic: retain as fallback diagnostic, but continue
                            // trying remaining backup CDNs in case another mirror serves a certifiable layout.
                            if (bestMissingMapEval == null)
                            {
                                bestMissingMapBytes = fetched;
                                bestMissingMapEval = eval;
                            }
                            break;
                        }
                        receivedCorrupt = true;
                        lastCorruptEval = eval;
                        if (attempt < 1) await Task.Delay(150 * (attempt + 1), ct);
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
            }

            if (bestMissingMapEval != null)
            {
                onStreamedSuccess?.Invoke();
                return (bestMissingMapBytes, bestMissingMapEval, false);
            }

            return (null, lastCorruptEval, receivedCorrupt);
        }

        public static async Task<(byte[] Bytes, bool ReceivedCorruptPayload)> DownloadReplayFromUrlsAsync(
            HttpClient client,
            IReadOnlyList<string> urls,
            Func<byte[], bool> isValidPayload,
            Action onStreamedSuccess,
            CancellationToken ct)
        {
            bool receivedCorrupt = false;
            for (int u = 0; u < urls.Count; u++)
            {
                string url = urls[u];
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using var resp = await client.GetAsync(url, ct);
                        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            break;
                        }
                        resp.EnsureSuccessStatusCode();
                        byte[] fetched = await resp.Content.ReadAsByteArrayAsync(ct);
                        if (isValidPayload(fetched))
                        {
                            onStreamedSuccess?.Invoke();
                            return (fetched, false);
                        }
                        receivedCorrupt = true;
                        if (attempt < 1) await Task.Delay(150 * (attempt + 1), ct);
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
            }
            return (null, receivedCorrupt);
        }

        public static CertificationSummary DeduplicateAndMerge(IEnumerable<CertificationSummary> items)
        {
            var total = new CertificationSummary();
            if (items == null) return total;

            var seenSha = new HashSet<string>(StringComparer.Ordinal);
            var seenVerifiedId = new HashSet<string>(StringComparer.Ordinal);

            var ordered = items
                .Select((item, idx) => (item, idx))
                .OrderBy(t => t.item?.RequestedReplayId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(t => t.idx)
                .ToList();

            // Pass 1: Process all verified PVE & SkippedNonPve items first to establish canonical
            // SHA-256 and 1016.ReplayId identity sets independent of requested replay ID sort order.
            var remaining = new List<CertificationSummary>();
            foreach (var (item, _) in ordered)
            {
                if (item == null) continue;
                if (item.ReplaysVerified > 0 || item.SkippedNonPve > 0)
                {
                    bool dupSha = !string.IsNullOrEmpty(item.ContentSha256) && !seenSha.Add(item.ContentSha256);
                    bool dupId = !string.IsNullOrEmpty(item.VerifiedReplayId) && !seenVerifiedId.Add(item.VerifiedReplayId);
                    if (dupSha || dupId)
                    {
                        total.MergeFrom(new CertificationSummary
                        {
                            ReplaysAttempted = item.ReplaysAttempted > 0 ? item.ReplaysAttempted : 1,
                            TruncatedOrCorrupt = 1,
                            DuplicateReplays = 1
                        });
                        continue;
                    }
                    total.MergeFrom(item);
                }
                else
                {
                    remaining.Add(item);
                }
            }

            // Pass 2: Merge remaining items (corrupt, download failed, missing map, parse error).
            // If a corrupt/ID-mismatch item carries the exact SHA-256 or verified ReplayId of an already
            // verified/non-PVE replay, preserve its TruncatedOrCorrupt defect AND record DuplicateReplays=1
            // so the duplicate request cannot dilute DownloadFailRatio's unique denominator.
            for (int i = 0; i < remaining.Count; i++)
            {
                var item = remaining[i];
                bool matchesVerifiedIdentity =
                    item.TruncatedOrCorrupt > 0 &&
                    ((!string.IsNullOrEmpty(item.ContentSha256) && seenSha.Contains(item.ContentSha256)) ||
                     (!string.IsNullOrEmpty(item.VerifiedReplayId) && seenVerifiedId.Contains(item.VerifiedReplayId)));

                if (matchesVerifiedIdentity && item.DuplicateReplays == 0)
                {
                    total.MergeFrom(new CertificationSummary
                    {
                        ReplaysAttempted = item.ReplaysAttempted > 0 ? item.ReplaysAttempted : 1,
                        TruncatedOrCorrupt = item.TruncatedOrCorrupt,
                        DuplicateReplays = 1,
                        MissingPredictions = item.MissingPredictions,
                        OutOfSupportPredictions = item.OutOfSupportPredictions,
                        IllegalMoveEdges = item.IllegalMoveEdges
                    });
                }
                else
                {
                    total.MergeFrom(item);
                }
            }

            return total;
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

        public static bool IsCacheTargetInsideCacheRoot(string targetPath, string cacheRootDir)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(cacheRootDir)) return false;
            try
            {
                string fullTarget = Path.GetFullPath(targetPath);
                string parentDir = Path.GetDirectoryName(fullTarget);
                if (string.IsNullOrEmpty(parentDir)) return false;
                return ReportWriter.IsPathInsideDirectory(parentDir, cacheRootDir) &&
                       ReportWriter.IsPathInsideDirectory(fullTarget, cacheRootDir);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryPublishCacheIfUnique(
            string savePath,
            string cacheRootDir,
            bool saveCache,
            byte[] bytes,
            CertificationSummary local,
            ConcurrentDictionary<string, byte> cacheSeenSha,
            ConcurrentDictionary<string, byte> cacheSeenVerifiedId,
            object publishLock)
        {
            if (!IsCacheableAndClean(local)) return false;
            lock (publishLock)
            {
                bool alreadySeen =
                    (!string.IsNullOrEmpty(local.ContentSha256) && cacheSeenSha.ContainsKey(local.ContentSha256)) ||
                    (!string.IsNullOrEmpty(local.VerifiedReplayId) && cacheSeenVerifiedId.ContainsKey(local.VerifiedReplayId));
                if (alreadySeen) return false;

                if (saveCache && savePath != null)
                {
                    // Commit dedup identity ONLY after atomic write succeeds inside cacheRootDir;
                    // if publishing fails (e.g. target blocked by directory or symlink escaping cacheRootDir),
                    // release identity so another valid target with the same replay can still publish.
                    if (!TryWriteCacheAtomic(savePath, bytes, cacheRootDir))
                        return false;
                }

                if (!string.IsNullOrEmpty(local.ContentSha256)) cacheSeenSha.TryAdd(local.ContentSha256, 0);
                if (!string.IsNullOrEmpty(local.VerifiedReplayId)) cacheSeenVerifiedId.TryAdd(local.VerifiedReplayId, 0);
                return true;
            }
        }

        public static string ResolveCachedReplayPath(string cacheDir, string replayId)
        {
            if (string.IsNullOrWhiteSpace(cacheDir) || string.IsNullOrWhiteSpace(replayId)) return null;
            string flat = Path.Combine(cacheDir, replayId);
            if (File.Exists(flat) && IsCacheTargetInsideCacheRoot(flat, cacheDir)) return flat;
            string nested = Path.Combine(cacheDir, replayId, replayId);
            if (File.Exists(nested) && IsCacheTargetInsideCacheRoot(nested, cacheDir)) return nested;
            return null;
        }

        public static string ResolveSaveCachePath(string cacheDir, string replayId, string existingCachedPath)
        {
            if (string.IsNullOrWhiteSpace(cacheDir) || string.IsNullOrWhiteSpace(replayId)) return null;
            string candidate;
            if (!string.IsNullOrWhiteSpace(existingCachedPath))
            {
                candidate = existingCachedPath;
            }
            else
            {
                string flat = Path.Combine(cacheDir, replayId);
                candidate = Directory.Exists(flat) ? Path.Combine(flat, replayId) : flat;
            }
            return IsCacheTargetInsideCacheRoot(candidate, cacheDir) ? candidate : null;
        }

        public static CertificationSummary SafeEvaluateReplay(string replayId, byte[] bytes, IProtocolDecoder decoder, BoardCatalog catalog)
        {
            try { return ReplayEvaluator.EvaluateBytes(replayId, bytes, decoder, catalog); }
            catch { return new CertificationSummary { ReplaysAttempted = 1, ParseErrors = 1 }; }
        }

        public static bool TryWriteCacheAtomic(string savePath, byte[] bytes, string cacheRootDir = null)
        {
            if (string.IsNullOrWhiteSpace(savePath) || bytes == null) return false;
            if (!string.IsNullOrWhiteSpace(cacheRootDir) && !IsCacheTargetInsideCacheRoot(savePath, cacheRootDir))
            {
                Console.Error.WriteLine($"WARN: Refusing to write cache file outside cache root: {savePath}");
                return false;
            }
            string tmpPath = null;
            try
            {
                string parentDir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
                tmpPath = savePath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(tmpPath, bytes);
                File.Move(tmpPath, savePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARN: Failed to write cache file {savePath}: {ex.Message}");
                try { if (tmpPath != null && File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                return false;
            }
        }
    }
}
