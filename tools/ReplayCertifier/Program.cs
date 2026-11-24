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
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            string protocolDir = GetArg(args, "--protocol-dir");
            var mapFiles = GetAllArgs(args, "--maps");
            string corpusFile = GetArg(args, "--corpus");
            string cacheDir = GetArg(args, "--cache-dir");
            string cdnBase = GetArg(args, "--cdn") ?? "https://sereplaycn.feimogames.com/prod/";
            string jsonOut = GetArg(args, "--json-out");
            string mdOut = GetArg(args, "--md-out");
            int concurrency = int.TryParse(GetArg(args, "--concurrency"), out int c) ? Math.Clamp(c, 1, 64) : 24;
            int limit = int.TryParse(GetArg(args, "--limit"), out int lim) ? lim : 0;
            bool saveCache = args.Contains("--save-cache");
            double minDetAcc = double.TryParse(GetArg(args, "--min-det-accuracy"), out double mda) ? mda : 0.998;
            double minThiefAcc = double.TryParse(GetArg(args, "--min-thief-accuracy"), out double mta) ? mta : 0.995;
            double maxAbsZ = double.TryParse(GetArg(args, "--max-abs-z"), out double mz) ? mz : 3.0;

            if (string.IsNullOrWhiteSpace(protocolDir) || mapFiles.Count == 0 || (string.IsNullOrWhiteSpace(corpusFile) && string.IsNullOrWhiteSpace(cacheDir)))
            {
                Console.WriteLine("Usage: ReplayCertifier --protocol-dir <dir> --maps <maps.json> [--maps <extra.json>] [--corpus <ids.txt>] [--cache-dir <dir>] [--save-cache] [--concurrency 24] [--limit N] [--json-out report.json] [--md-out report.md]");
                return 1;
            }

            var decoder = new ProtocolDecoder(protocolDir);
            var catalog = new BoardCatalog();
            foreach (var mf in mapFiles) catalog.LoadJsonFile(mf);
            if (catalog.MapCount == 0)
            {
                Console.Error.WriteLine("ERROR: No maps loaded from --maps arguments: " + string.Join(", ", mapFiles));
                return 1;
            }

            var replayIds = new List<string>();
            if (!string.IsNullOrWhiteSpace(corpusFile) && File.Exists(corpusFile))
            {
                replayIds.AddRange(File.ReadAllLines(corpusFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length >= 10 && l.All(char.IsDigit)));
            }
            else if (!string.IsNullOrWhiteSpace(cacheDir) && Directory.Exists(cacheDir))
            {
                replayIds.AddRange(Directory.GetFiles(cacheDir).Select(Path.GetFileName));
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
                string cachedPath = !string.IsNullOrWhiteSpace(cacheDir) ? Path.Combine(cacheDir, id) : null;
                if (cachedPath != null && File.Exists(cachedPath))
                {
                    var fi = new FileInfo(cachedPath);
                    if (fi.Length > 1000)
                    {
                        bytes = await File.ReadAllBytesAsync(cachedPath, ct);
                        Interlocked.Increment(ref cacheHits);
                    }
                }

                if (bytes == null)
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            bytes = await client.GetByteArrayAsync(cdnBase + id, ct);
                            if (bytes.Length > 1000)
                            {
                                Interlocked.Increment(ref streamed);
                                if (saveCache && cachedPath != null)
                                    await File.WriteAllBytesAsync(cachedPath, bytes, ct);
                                break;
                            }
                            bytes = null;
                        }
                        catch
                        {
                            bytes = null;
                            if (attempt < 2) await Task.Delay(250 * (attempt + 1), ct);
                        }
                    }
                }

                CertificationSummary local;
                if (bytes == null)
                {
                    local = new CertificationSummary { ReplaysAttempted = 1, DownloadFailed = 1 };
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

            bool passed = ReportWriter.PrintAndExport(total, catalog, sw.Elapsed.TotalSeconds, minDetAcc, minThiefAcc, maxAbsZ, jsonOut, mdOut);
            return passed ? 0 : 2;
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static List<string> GetAllArgs(string[] args, string name)
        {
            var list = new List<string>();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) list.Add(args[i + 1]);
            return list;
        }
    }
}
