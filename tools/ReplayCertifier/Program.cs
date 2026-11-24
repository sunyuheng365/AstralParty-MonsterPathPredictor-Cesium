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
            if (args.Contains("--self-test"))
            {
                return RunSelfTests();
            }

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
            var thresholds = new GateThresholds
            {
                MinDetAccuracy = double.TryParse(GetArg(args, "--min-det-accuracy"), out double mda) ? mda : 0.998,
                MinThiefAccuracy = double.TryParse(GetArg(args, "--min-thief-accuracy"), out double mta) ? mta : 0.995,
                MaxAbsZ = double.TryParse(GetArg(args, "--max-abs-z"), out double mz) ? mz : 3.0,
                MinAggroSamples = long.TryParse(GetArg(args, "--min-aggro-samples"), out long mas) ? mas : 100,
                MinThiefSamples = long.TryParse(GetArg(args, "--min-thief-samples"), out long mts) ? mts : 10,
                MinCalibratedBins = int.TryParse(GetArg(args, "--min-calib-bins"), out int mcb) ? mcb : 1,
                MaxDownloadFailRatio = double.TryParse(GetArg(args, "--max-download-fail-rate"), out double mdf) ? mdf : 0.02
            };

            if (string.IsNullOrWhiteSpace(protocolDir) || mapFiles.Count == 0 || (string.IsNullOrWhiteSpace(corpusFile) && string.IsNullOrWhiteSpace(cacheDir)))
            {
                Console.WriteLine("Usage: ReplayCertifier --protocol-dir <dir> --maps <maps.json> [--maps <extra.json>] [--corpus <ids.txt>] [--cache-dir <dir>] [--save-cache] [--concurrency 24] [--limit N] [--json-out report.json] [--md-out report.md] | --self-test");
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
                    string[] cdns = cdnBase.Contains("sereplaycn", StringComparison.OrdinalIgnoreCase)
                        ? new[] { cdnBase, "https://sereplayjp.feimogames.com/prod/" }
                        : new[] { cdnBase };

                    foreach (string baseUrl in cdns)
                    {
                        for (int attempt = 0; attempt < 2; attempt++)
                        {
                            try
                            {
                                bytes = await client.GetByteArrayAsync(baseUrl + id, ct);
                                if (bytes.Length > 1000)
                                {
                                    Interlocked.Increment(ref streamed);
                                    if (saveCache && cachedPath != null)
                                        await File.WriteAllBytesAsync(cachedPath, bytes, ct);
                                    break;
                                }
                                bytes = null;
                            }
                            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                bytes = null;
                                break;
                            }
                            catch
                            {
                                bytes = null;
                                if (attempt < 1) await Task.Delay(250 * (attempt + 1), ct);
                            }
                        }
                        if (bytes != null) break;
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

            bool passed = ReportWriter.PrintAndExport(total, catalog, sw.Elapsed.TotalSeconds, thresholds, jsonOut, mdOut);
            return passed ? 0 : 2;
        }

        /// <summary>
        /// Built-in CI self-tests verifying certification gate soundness without requiring game assemblies:
        /// 1. Empty-sample / partial corpus (1/1 aggro, 0/0 thief, 0 calib bins) MUST fail.
        /// 2. Missing prediction on a legal fork MUST fail coverage gate.
        /// 3. 50/50 directional Red bias MUST be detected (Obs=100%, z >> 3.0 -> FAIL).
        /// 4. FrontAuditStats MUST serialize non-empty fields into JSON.
        /// </summary>
        private static int RunSelfTests()
        {
            var t = new GateThresholds();

            // Test 1: 1/1 aggro, 0/0 thief, 0 bins -> MUST FAIL with InsufficientSample
            var emptyLike = new CertificationSummary { ReplaysAttempted = 1, ReplaysVerified = 1 };
            emptyLike.Buckets["MidWalkFork"].Total = 1;
            emptyLike.Buckets["MidWalkFork"].DetTotal = 1;
            emptyLike.Buckets["MidWalkFork"].DetHits = 1;
            var e1 = ReportWriter.EvaluateGates(emptyLike, t);
            if (e1.Passed || e1.AggroGate != GateStatus.InsufficientSample ||
                e1.ThiefGate != GateStatus.InsufficientSample || e1.CalibrationGate != GateStatus.InsufficientSample)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 1): empty-sample summary falsely passed.");
                return 1;
            }

            // Test 2: MissingPrediction > 0 -> CoverageGate MUST FAIL
            var missingPred = new CertificationSummary
            {
                ReplaysAttempted = 10,
                ReplaysVerified = 10,
                MissingPredictions = 1
            };
            missingPred.Buckets["MidWalkFork"].DetTotal = 200;
            missingPred.Buckets["MidWalkFork"].DetHits = 200;
            missingPred.Buckets["ThiefFork"].DetTotal = 20;
            missingPred.Buckets["ThiefFork"].DetHits = 20;
            missingPred.ProbBins["50%"] = new ProbBin { Opp = 40, SumPred = 20.0, Chosen = 20 };
            var e2 = ReportWriter.EvaluateGates(missingPred, t);
            if (e2.Passed || e2.CoverageGate != GateStatus.Fail)
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 2): MissingPredictions did not fail coverage gate.");
                return 1;
            }

            // Test 3: 50/50 directional bias (always picks Red=22 instead of fair 50/50 between 22 and 55) MUST FAIL
            var board = new MonsterPathPredictor.Core.BoardGraph(new[]
            {
                new MonsterPathPredictor.Core.LandNodeSnapshot(23, new[] { 22, 55, 24 }),
                new MonsterPathPredictor.Core.LandNodeSnapshot(22, new[] { 23 }),
                new MonsterPathPredictor.Core.LandNodeSnapshot(55, new[] { 23 }),
                new MonsterPathPredictor.Core.LandNodeSnapshot(24, new[] { 23 })
            });
            var exits = new List<MonsterPathPredictor.Core.ForkExitPrediction>
            {
                new() { NextLandId = 22, ConditionalProbability = 0.5 },
                new() { NextLandId = 55, ConditionalProbability = 0.5 }
            };
            var biased = new CertificationSummary { ReplaysAttempted = 10, ReplaysVerified = 10 };
            biased.Buckets["MidWalkFork"].DetTotal = 200;
            biased.Buckets["MidWalkFork"].DetHits = 200;
            biased.Buckets["ThiefFork"].DetTotal = 20;
            biased.Buckets["ThiefFork"].DetHits = 20;
            for (int i = 0; i < 40; i++)
            {
                ReplayEvaluator.RecordMultiBranchCalibration(board, 23, exits, actualNext: 22, biased.ProbBins);
            }
            var e3 = ReportWriter.EvaluateGates(biased, t);
            if (e3.Passed || e3.CalibrationGate != GateStatus.Fail || biased.ProbBins["50%"].Opp != 40 || biased.ProbBins["50%"].Chosen != 40)
            {
                Console.Error.WriteLine($"SELF-TEST FAILED (Test 3): 50/50 Red bias went undetected (opp={biased.ProbBins["50%"].Opp}, chosen={biased.ProbBins["50%"].Chosen}, gate={e3.CalibrationGate}).");
                return 1;
            }

            // Test 4: FrontAuditStats JSON serialization must include non-empty properties (not {})
            var fa = new FrontAuditStats { SpawnF1 = 5, SpawnF1Actual = 5, StopMulti = 3, StopMultiRedBlue = 3 };
            string json = System.Text.Json.JsonSerializer.Serialize(fa);
            if (!json.Contains("\"SpawnF1\":5", StringComparison.Ordinal) || !json.Contains("\"StopMultiRedBlue\":3", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("SELF-TEST FAILED (Test 4): FrontAuditStats serialized empty: " + json);
                return 1;
            }

            // Test 5: BoardCatalog out-of-bounds mapIndex MUST return false (no silent fallback to layout 0)
            var catalog = new BoardCatalog();
            string tmpMap = Path.Combine(Path.GetTempPath(), "certifier-selftest-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmpMap, "{\"id\":82016,\"name\":\"Test\",\"nodes\":[{\"id\":1,\"neighbors\":[2]},{\"id\":2,\"neighbors\":[1]}]}");
                catalog.LoadJsonFile(tmpMap);
                if (!catalog.TryGetGraph(82016, 0, out _) || catalog.TryGetGraph(82016, 1, out _) || catalog.TryGetGraph(82016, -1, out _))
                {
                    Console.Error.WriteLine("SELF-TEST FAILED (Test 5): BoardCatalog out-of-bounds mapIndex fell back to layout 0.");
                    return 1;
                }
            }
            finally
            {
                try { if (File.Exists(tmpMap)) File.Delete(tmpMap); } catch { }
            }

            Console.WriteLine("ReplayCertifier self-tests PASSED (5/5).");
            return 0;
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
