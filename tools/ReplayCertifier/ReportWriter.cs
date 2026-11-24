using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MonsterPathPredictor.ReplayCertifier
{
    public enum GateStatus
    {
        Pass,
        Fail,
        InsufficientSample
    }

    public sealed class GateThresholds
    {
        public double MinDetAccuracy { get; set; } = 0.998;
        public double MinThiefAccuracy { get; set; } = 0.995;
        public double MaxAbsZ { get; set; } = 3.0;
        public long MinAggroSamples { get; set; } = 100;
        public long MinThiefSamples { get; set; } = 10;
        public int MinCalibratedBins { get; set; } = 1;
        public double MaxDownloadFailRatio { get; set; } = 0.02;
    }

    public sealed class GateEvaluation
    {
        public GateStatus CoverageGate { get; set; }
        public GateStatus AggroGate { get; set; }
        public GateStatus ThiefGate { get; set; }
        public GateStatus CalibrationGate { get; set; }
        public bool Passed =>
            CoverageGate == GateStatus.Pass &&
            AggroGate == GateStatus.Pass &&
            ThiefGate == GateStatus.Pass &&
            CalibrationGate == GateStatus.Pass;

        public long AggroDetTotal { get; set; }
        public long AggroDetHits { get; set; }
        public long AggroProbTotal { get; set; }
        public double? AggroAccuracy { get; set; }
        public double? WilsonLow { get; set; }
        public double? WilsonHigh { get; set; }
        public long ThiefDetTotal { get; set; }
        public long ThiefDetHits { get; set; }
        public long ThiefProbTotal { get; set; }
        public double? ThiefAccuracy { get; set; }
        public bool BucketCountsConsistent { get; set; }
        public int EvaluatedBins { get; set; }
        public double WorstAbsZ { get; set; }
        public double DownloadFailRatio { get; set; }
        public List<object> CalibrationRows { get; } = new();
    }

    public static class ReportWriter
    {
        public static GateEvaluation EvaluateGates(CertificationSummary s, GateThresholds t)
        {
            string[] aggroKeys = { "SpawnFork", "ForkStart", "PostCombatFork", "MidWalkFork", "PostNormalCombatMidFork" };
            long aggroDetTotal = aggroKeys.Sum(k => s.Buckets[k].DetTotal);
            long aggroDetHits = aggroKeys.Sum(k => s.Buckets[k].DetHits);
            long aggroProbTotal = aggroKeys.Sum(k => s.Buckets[k].ProbTotal);
            double? aggroAcc = aggroDetTotal > 0 ? (double)aggroDetHits / aggroDetTotal : (double?)null;
            double? wLow = null, wHigh = null;
            if (aggroDetTotal > 0)
            {
                var w = CertificationSummary.Wilson95(aggroDetHits, aggroDetTotal);
                wLow = w.Lower;
                wHigh = w.Upper;
            }

            var thiefB = s.Buckets["ThiefFork"];
            double? thiefAcc = thiefB.DetTotal > 0 ? (double)thiefB.DetHits / thiefB.DetTotal : (double?)null;

            bool bucketCountsConsistent = s.Buckets.Values.All(b => b.Total == b.DetTotal + b.ProbTotal);
            double dlFailRatio = s.ReplaysAttempted > 0 ? (double)s.DownloadFailed / s.ReplaysAttempted : 1.0;

            bool thresholdsValid =
                double.IsFinite(t.MinDetAccuracy) && t.MinDetAccuracy >= 0.0 && t.MinDetAccuracy <= 1.0 &&
                double.IsFinite(t.MinThiefAccuracy) && t.MinThiefAccuracy >= 0.0 && t.MinThiefAccuracy <= 1.0 &&
                double.IsFinite(t.MaxDownloadFailRatio) && t.MaxDownloadFailRatio >= 0.0 && t.MaxDownloadFailRatio <= 1.0 &&
                double.IsFinite(t.MaxAbsZ) && t.MaxAbsZ >= 0.0 &&
                t.MinAggroSamples >= 0 &&
                t.MinThiefSamples >= 0 &&
                t.MinCalibratedBins >= 0;

            GateStatus coverageGate;
            if (!thresholdsValid)
                coverageGate = GateStatus.Fail;
            else if (s.ReplaysAttempted <= 0 || s.ReplaysVerified <= 0)
                coverageGate = GateStatus.InsufficientSample;
            else if (s.MissingMap > 0 || s.ParseErrors > 0 || s.TruncatedOrCorrupt > 0 ||
                     s.MissingPredictions > 0 || s.OutOfSupportPredictions > 0 || s.IllegalMoveEdges > 0 ||
                     !bucketCountsConsistent || dlFailRatio > t.MaxDownloadFailRatio)
                coverageGate = GateStatus.Fail;
            else
                coverageGate = GateStatus.Pass;

            GateStatus aggroGate = !thresholdsValid
                ? GateStatus.Fail
                : (aggroDetTotal < t.MinAggroSamples
                    ? GateStatus.InsufficientSample
                    : (aggroAcc.GetValueOrDefault() >= t.MinDetAccuracy ? GateStatus.Pass : GateStatus.Fail));

            GateStatus thiefGate = !thresholdsValid
                ? GateStatus.Fail
                : (thiefB.DetTotal < t.MinThiefSamples
                    ? GateStatus.InsufficientSample
                    : (thiefAcc.GetValueOrDefault() >= t.MinThiefAccuracy ? GateStatus.Pass : GateStatus.Fail));

            int evaluatedBins = 0;
            double worstZ = 0.0;
            bool calibOk = true;
            var eval = new GateEvaluation
            {
                CoverageGate = coverageGate,
                AggroGate = aggroGate,
                ThiefGate = thiefGate,
                AggroDetTotal = aggroDetTotal,
                AggroDetHits = aggroDetHits,
                AggroProbTotal = aggroProbTotal,
                AggroAccuracy = aggroAcc,
                WilsonLow = wLow,
                WilsonHigh = wHigh,
                ThiefDetTotal = thiefB.DetTotal,
                ThiefDetHits = thiefB.DetHits,
                ThiefProbTotal = thiefB.ProbTotal,
                ThiefAccuracy = thiefAcc,
                BucketCountsConsistent = bucketCountsConsistent,
                DownloadFailRatio = dlFailRatio
            };

            foreach (var kv in s.ProbBins.OrderByDescending(k => k.Value.Opp))
            {
                var pb = kv.Value;
                if (pb.Opp < 20) continue;
                double pred = pb.SumPred / pb.Opp;
                double obs = (double)pb.Chosen / pb.Opp;
                double se = Math.Sqrt(pred * (1.0 - pred) / pb.Opp);
                double z = se > 0 ? (obs - pred) / se : 0.0;
                if (pb.Opp >= 30)
                {
                    evaluatedBins++;
                    if (Math.Abs(z) > worstZ) worstZ = Math.Abs(z);
                    if (!double.IsFinite(z) || !double.IsFinite(t.MaxAbsZ) || Math.Abs(z) > t.MaxAbsZ) calibOk = false;
                }
                eval.CalibrationRows.Add(new { bin = kv.Key, n = pb.Opp, predicted = pred, observed = obs, diff = obs - pred, z, shapes = pb.Shapes });
            }

            eval.EvaluatedBins = evaluatedBins;
            eval.WorstAbsZ = worstZ;
            eval.CalibrationGate = !thresholdsValid
                ? GateStatus.Fail
                : (evaluatedBins < t.MinCalibratedBins
                    ? GateStatus.InsufficientSample
                    : (calibOk ? GateStatus.Pass : GateStatus.Fail));

            return eval;
        }

        public static string FormatAccuracy(long hits, long total, int decimals = 4)
        {
            if (total <= 0) return "N/A";
            return (100.0 * hits / total).ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatGate(GateStatus st) => st switch
        {
            GateStatus.Pass => "PASS",
            GateStatus.InsufficientSample => "INSUFFICIENT_SAMPLE",
            _ => "FAIL"
        };

        public static bool AreSameOutputPath(string pathA, string pathB)
        {
            if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB)) return false;
            try
            {
                string normA = Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normB = Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var cmp = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return string.Equals(normA, normB, cmp);
            }
            catch
            {
                return false;
            }
        }

        public static bool PrintAndExport(
            CertificationSummary s,
            BoardCatalog catalog,
            double elapsedSec,
            GateThresholds t,
            string jsonOut,
            string mdOut)
        {
            if (AreSameOutputPath(jsonOut, mdOut))
            {
                throw new ArgumentException("JSON and Markdown output paths must not point to the same file.");
            }
            var eval = EvaluateGates(s, t);
            bool passed = eval.Passed;
            long aggroDetHits = eval.AggroDetHits;
            long aggroDetTotal = eval.AggroDetTotal;
            long aggroProbTotal = eval.AggroProbTotal;
            double? aggroAcc = eval.AggroAccuracy;
            double? wLow = eval.WilsonLow;
            double? wHigh = eval.WilsonHigh;
            double? thiefAcc = eval.ThiefAccuracy;
            double worstZ = eval.WorstAbsZ;
            var thiefB = s.Buckets["ThiefFork"];
            string wilsonStr = (wLow.HasValue && wHigh.HasValue)
                ? $"[{wLow.Value * 100:F4}%, {wHigh.Value * 100:F4}%]"
                : "[N/A]";

            Console.WriteLine();
            Console.WriteLine("=========================================================================");
            Console.WriteLine($"REPLAY CERTIFICATION {(passed ? "PASSED" : "FAILED")} | {s.ReplaysVerified:N0} PVE replays in {elapsedSec:F1}s");
            Console.WriteLine($"  Attempted={s.ReplaysAttempted:N0}, Verified={s.ReplaysVerified:N0}, NonPVE={s.SkippedNonPve}, MissingMap={s.MissingMap}, DownloadFail={s.DownloadFailed}, Corrupt={s.TruncatedOrCorrupt}, Err={s.ParseErrors}, MissingPred={s.MissingPredictions}, OutOfSupport={s.OutOfSupportPredictions}, IllegalEdge={s.IllegalMoveEdges}");
            Console.WriteLine("=========================================================================");

            foreach (var b in s.Buckets.Values)
            {
                string accStr = FormatAccuracy(b.DetHits, b.DetTotal, 3);
                Console.WriteLine($"  {b.Name,-44} Total={b.Total,7:N0} | Det={b.DetHits,7:N0}/{b.DetTotal,-7:N0} ({accStr,8}) | Prob={b.ProbTotal,5:N0}");
            }
            Console.WriteLine("-------------------------------------------------------------------------");
            Console.WriteLine($"  [GATE 0] Coverage & Integrity: verified={s.ReplaysVerified:N0}/{s.ReplaysAttempted:N0} (dlFail={eval.DownloadFailRatio * 100:F2}% <= {t.MaxDownloadFailRatio * 100:F1}%, corrupt={s.TruncatedOrCorrupt}, missingPred={s.MissingPredictions}, outOfSupport={s.OutOfSupportPredictions}, illegalEdge={s.IllegalMoveEdges}, bucketSumOk={eval.BucketCountsConsistent}) => {FormatGate(eval.CoverageGate)}");
            Console.WriteLine($"  [GATE 1] Aggressive 100%-Det : {aggroDetHits:N0}/{aggroDetTotal:N0} = {FormatAccuracy(aggroDetHits, aggroDetTotal)} (95% Wilson {wilsonStr}, req >= {t.MinDetAccuracy * 100:F2}%, n >= {t.MinAggroSamples}) => {FormatGate(eval.AggroGate)}");
            Console.WriteLine($"  [GATE 2] Thief 100%-Det      : {thiefB.DetHits:N0}/{thiefB.DetTotal:N0} = {FormatAccuracy(thiefB.DetHits, thiefB.DetTotal)} (multi-branch={thiefB.ProbTotal:N0}, req >= {t.MinThiefAccuracy * 100:F2}%, n >= {t.MinThiefSamples}) => {FormatGate(eval.ThiefGate)}");
            Console.WriteLine($"  [GATE 3] Prob Calibration    : {aggroProbTotal:N0} multi-branch forks, {eval.EvaluatedBins} bins (n>=30), max |z|={worstZ:F2} (req <= {t.MaxAbsZ:F1}) => {FormatGate(eval.CalibrationGate)}");
            Console.WriteLine("-------------------------------------------------------------------------");

            Console.WriteLine("\n=== PER-MAP BREAKDOWN ===");
            foreach (var kv in s.ByMap.OrderBy(k => k.Key))
            {
                var ms = kv.Value;
                string accStr = FormatAccuracy(ms.DetHits, ms.DetTotal, 3);
                Console.WriteLine($"  {kv.Key} ({catalog.GetMapName(kv.Key),-8}): replays={ms.Replays,4:N0} | Det={ms.DetHits,7:N0}/{ms.DetTotal,-7:N0} ({accStr,8}) | Prob={ms.ProbTotal,5:N0} | Thief={ms.ThiefDetHits}/{ms.ThiefDetTotal} (+{ms.ThiefProbTotal} prob)");
            }

            Console.WriteLine("\n=== PROBABILISTIC BRANCH CALIBRATION (n >= 20) ===");
            foreach (var kv in s.ProbBins.OrderByDescending(k => k.Value.Opp))
            {
                var pb = kv.Value;
                if (pb.Opp < 20) continue;
                double p = pb.SumPred / pb.Opp, obs = (double)pb.Chosen / pb.Opp;
                double z = Math.Sqrt(p * (1 - p) / pb.Opp) is double se && se > 0 ? (obs - p) / se : 0;
                Console.WriteLine($"  Bin {kv.Key,-8}: n={pb.Opp,6:N0} | Pred={p * 100,6:F2}% | Obs={obs * 100,6:F2}% | Diff={(obs - p) * 100:+0.00;-0.00;0.00}% | z={z:+0.00;-0.00;0.00}");
            }

            var fa = s.FrontAudit;
            Console.WriteLine("\n=== FORK-START CURRENT-FACING AUDIT ===");
            Console.WriteLine($"  Spawn     Front==1: {fa.SpawnF1Actual:N0}/{fa.SpawnF1:N0} followed Front[0] (vs {fa.SpawnF1RedBlue:N0} RedBlue)");
            Console.WriteLine($"  Stopped   Front==1: {fa.StopF1Actual:N0}/{fa.StopF1:N0} followed Front[0] (vs {fa.StopF1RedBlue:N0} RedBlue)");
            Console.WriteLine($"  Stopped   Front>=2: {fa.StopMultiRedBlue:N0}/{fa.StopMulti:N0} followed RedBlue within FrontNodeIds");
            Console.WriteLine($"  PostCmbt  Front>=2: {fa.PostCombatMultiRedBlue:N0}/{fa.PostCombatMulti:N0} followed RedBlue within FrontNodeIds");

            Console.WriteLine("\n=== SAMPLE MISMATCHES ===");
            foreach (var b in s.Buckets.Values)
            {
                if (b.Mismatches.Count == 0) continue;
                long detMismatch = b.DetTotal - b.DetHits;
                long totalMismatch = detMismatch + b.OutOfSupport;
                Console.WriteLine($"--- {b.Name} (mismatches={totalMismatch}, detMismatch={detMismatch}, outOfSupport={b.OutOfSupport}, showing up to {b.Mismatches.Count}) ---");
                foreach (var m in b.Mismatches.Take(12)) Console.WriteLine("  " + m);
            }

            if (!string.IsNullOrWhiteSpace(jsonOut))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
                var payload = new
                {
                    generatedAtUtc = DateTime.UtcNow.ToString("O"),
                    passed,
                    elapsedSeconds = Math.Round(elapsedSec, 2),
                    replays = new { s.ReplaysAttempted, s.ReplaysVerified, s.SkippedNonPve, s.MissingMap, s.DownloadFailed, s.TruncatedOrCorrupt, s.ParseErrors, s.MissingPredictions, s.OutOfSupportPredictions, s.IllegalMoveEdges },
                    gates = new
                    {
                        coverageGate = FormatGate(eval.CoverageGate),
                        aggroGate = FormatGate(eval.AggroGate),
                        thiefGate = FormatGate(eval.ThiefGate),
                        calibGate = FormatGate(eval.CalibrationGate),
                        minDetAcc = t.MinDetAccuracy,
                        minThiefAcc = t.MinThiefAccuracy,
                        maxAbsZ = t.MaxAbsZ,
                        worstAbsZ = worstZ,
                        evaluatedBins = eval.EvaluatedBins
                    },
                    aggressiveDeterministic = new { hits = aggroDetHits, total = aggroDetTotal, accuracy = aggroAcc, wilson95Low = wLow, wilson95High = wHigh },
                    thiefDeterministic = new { hits = thiefB.DetHits, total = thiefB.DetTotal, probTotal = thiefB.ProbTotal, accuracy = thiefAcc },
                    buckets = s.Buckets.Values.Select(b => new
                    {
                        b.Key,
                        b.Name,
                        b.Total,
                        b.DetHits,
                        b.DetTotal,
                        b.ProbTotal,
                        detMismatchCount = b.DetTotal - b.DetHits,
                        outOfSupportCount = b.OutOfSupport,
                        mismatchCount = (b.DetTotal - b.DetHits) + b.OutOfSupport,
                        samples = b.Mismatches.Take(15)
                    }),
                    byMap = s.ByMap.OrderBy(k => k.Key).Select(k => new { mapId = k.Key, mapName = catalog.GetMapName(k.Key), k.Value.Replays, k.Value.DetHits, k.Value.DetTotal, k.Value.ProbTotal, k.Value.ThiefDetHits, k.Value.ThiefDetTotal, k.Value.ThiefProbTotal }),
                    calibration = eval.CalibrationRows,
                    frontAudit = fa
                };
                File.WriteAllText(jsonOut, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                Console.WriteLine($"\nWrote JSON report: {jsonOut}");
            }

            if (!string.IsNullOrWhiteSpace(mdOut))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mdOut))!);
                var sb = new StringBuilder();
                sb.AppendLine($"# Replay Certification Report ({(passed ? "PASSED" : "FAILED")})");
                sb.AppendLine();
                sb.AppendLine($"- **Verified PVE Replays**: {s.ReplaysVerified:N0} ({s.ByMap.Count} maps, {elapsedSec:F1}s)");
                sb.AppendLine($"- **Aggressive 100%-Deterministic Accuracy**: **{aggroDetHits:N0} / {aggroDetTotal:N0} = {FormatAccuracy(aggroDetHits, aggroDetTotal)}** (95% Wilson `{wilsonStr}`)");
                sb.AppendLine($"- **Thief 100%-Deterministic Accuracy**: **{thiefB.DetHits:N0} / {thiefB.DetTotal:N0} = {FormatAccuracy(thiefB.DetHits, thiefB.DetTotal)}** (multi-branch: {thiefB.ProbTotal:N0})");
                sb.AppendLine($"- **Probabilistic Multi-Branch Calibration**: {aggroProbTotal:N0} forks, {eval.EvaluatedBins} bins, max `|z| = {worstZ:F2}`");
                sb.AppendLine();
                sb.AppendLine("| Bucket | Total | Det Hits / Total | Accuracy | Multi-Branch |");
                sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
                foreach (var b in s.Buckets.Values)
                {
                    sb.AppendLine($"| {b.Name} | {b.Total:N0} | {b.DetHits:N0} / {b.DetTotal:N0} | {FormatAccuracy(b.DetHits, b.DetTotal)} | {b.ProbTotal:N0} |");
                }
                sb.AppendLine();
                sb.AppendLine("| Map ID | Map Name | Replays | Det Hits / Total | Accuracy | Multi-Branch |");
                sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
                foreach (var kv in s.ByMap.OrderBy(k => k.Key))
                {
                    var ms = kv.Value;
                    sb.AppendLine($"| {kv.Key} | {catalog.GetMapName(kv.Key)} | {ms.Replays:N0} | {ms.DetHits:N0} / {ms.DetTotal:N0} | {FormatAccuracy(ms.DetHits, ms.DetTotal)} | {ms.ProbTotal:N0} |");
                }
                File.WriteAllText(mdOut, sb.ToString(), Encoding.UTF8);
                Console.WriteLine($"Wrote Markdown report: {mdOut}");
            }

            return passed;
        }
    }
}
