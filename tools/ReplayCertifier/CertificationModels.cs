using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.ReplayCertifier
{
    public sealed class BucketStats
    {
        public string Key;
        public string Name;
        public long Total;
        public long DetTotal;
        public long DetHits;
        public long ProbTotal;
        public List<string> Mismatches = new();

        public void MergeFrom(BucketStats other, int maxSamples = 25)
        {
            Total += other.Total;
            DetTotal += other.DetTotal;
            DetHits += other.DetHits;
            ProbTotal += other.ProbTotal;
            foreach (var m in other.Mismatches)
            {
                if (Mismatches.Count >= maxSamples) break;
                Mismatches.Add(m);
            }
        }
    }

    public sealed class MapStats
    {
        public int Replays;
        public long DetTotal;
        public long DetHits;
        public long ProbTotal;
        public long ThiefDetTotal;
        public long ThiefDetHits;
        public long ThiefProbTotal;

        public void MergeFrom(MapStats o)
        {
            Replays += o.Replays;
            DetTotal += o.DetTotal;
            DetHits += o.DetHits;
            ProbTotal += o.ProbTotal;
            ThiefDetTotal += o.ThiefDetTotal;
            ThiefDetHits += o.ThiefDetHits;
            ThiefProbTotal += o.ThiefProbTotal;
        }
    }

    public sealed class ProbBin
    {
        public long Opp;
        public double SumPred;
        public long Chosen;
        public Dictionary<string, long> Shapes = new(StringComparer.Ordinal);

        public void AddShape(string shape)
        {
            if (string.IsNullOrEmpty(shape)) return;
            Shapes.TryGetValue(shape, out long cur);
            Shapes[shape] = cur + 1;
        }

        public void MergeFrom(ProbBin o)
        {
            Opp += o.Opp;
            SumPred += o.SumPred;
            Chosen += o.Chosen;
            foreach (var kv in o.Shapes)
            {
                Shapes.TryGetValue(kv.Key, out long cur);
                Shapes[kv.Key] = cur + kv.Value;
            }
        }
    }

    public sealed class FrontAuditStats
    {
        public long SpawnF1 { get; set; }
        public long SpawnF1Actual { get; set; }
        public long SpawnF1RedBlue { get; set; }
        public long StopF1 { get; set; }
        public long StopF1Actual { get; set; }
        public long StopF1RedBlue { get; set; }
        public long StopMulti { get; set; }
        public long StopMultiRedBlue { get; set; }
        public long PostCombatMulti { get; set; }
        public long PostCombatMultiRedBlue { get; set; }

        public void MergeFrom(FrontAuditStats o)
        {
            SpawnF1 += o.SpawnF1; SpawnF1Actual += o.SpawnF1Actual; SpawnF1RedBlue += o.SpawnF1RedBlue;
            StopF1 += o.StopF1; StopF1Actual += o.StopF1Actual; StopF1RedBlue += o.StopF1RedBlue;
            StopMulti += o.StopMulti; StopMultiRedBlue += o.StopMultiRedBlue;
            PostCombatMulti += o.PostCombatMulti; PostCombatMultiRedBlue += o.PostCombatMultiRedBlue;
        }
    }

    public sealed class CertificationSummary
    {
        public int ReplaysAttempted;
        public int ReplaysVerified;
        public int SkippedNonPve;
        public int MissingMap;
        public int DownloadFailed;
        public int ParseErrors;
        public int TruncatedOrCorrupt;
        public long MissingPredictions;
        public long OutOfSupportPredictions;
        public long IllegalMoveEdges;

        public Dictionary<string, BucketStats> Buckets = new()
        {
            ["SpawnFork"] = new BucketStats { Key = "SpawnFork", Name = "出生岔路 (SpawnFork)" },
            ["ForkStart"] = new BucketStats { Key = "ForkStart", Name = "停靠岔路起步 (ForkStart)" },
            ["PostCombatFork"] = new BucketStats { Key = "PostCombatFork", Name = "岔路战后续走 (PostCombatFork)" },
            ["MidWalkFork"] = new BucketStats { Key = "MidWalkFork", Name = "半路遇岔 (MidWalkFork)" },
            ["PostNormalCombatMidFork"] = new BucketStats { Key = "PostNormalCombatMidFork", Name = "普通格战后再遇岔 (PostNormalCombatMidFork)" },
            ["ThiefFork"] = new BucketStats { Key = "ThiefFork", Name = "小偷岔路含偷窃 (ThiefFork)" }
        };
        public Dictionary<int, MapStats> ByMap = new();
        public Dictionary<string, ProbBin> ProbBins = new();
        public FrontAuditStats FrontAudit = new();

        public void MergeFrom(CertificationSummary other)
        {
            ReplaysAttempted += other.ReplaysAttempted;
            ReplaysVerified += other.ReplaysVerified;
            SkippedNonPve += other.SkippedNonPve;
            MissingMap += other.MissingMap;
            DownloadFailed += other.DownloadFailed;
            ParseErrors += other.ParseErrors;
            TruncatedOrCorrupt += other.TruncatedOrCorrupt;
            MissingPredictions += other.MissingPredictions;
            OutOfSupportPredictions += other.OutOfSupportPredictions;
            IllegalMoveEdges += other.IllegalMoveEdges;
            FrontAudit.MergeFrom(other.FrontAudit);

            foreach (var kv in other.Buckets) Buckets[kv.Key].MergeFrom(kv.Value);
            foreach (var kv in other.ByMap)
            {
                if (!ByMap.TryGetValue(kv.Key, out var ms)) ByMap[kv.Key] = ms = new MapStats();
                ms.MergeFrom(kv.Value);
            }
            foreach (var kv in other.ProbBins)
            {
                if (!ProbBins.TryGetValue(kv.Key, out var pb)) ProbBins[kv.Key] = pb = new ProbBin();
                pb.MergeFrom(kv.Value);
            }
        }

        public static (double Lower, double Upper) Wilson95(long hits, long total)
        {
            if (total <= 0) return (0.0, 1.0);
            const double z = 1.959963984540054;
            double p = (double)hits / total;
            double z2 = z * z;
            double denom = 1.0 + z2 / total;
            double center = (p + z2 / (2.0 * total)) / denom;
            double half = (z * Math.Sqrt((p * (1.0 - p) + z2 / (4.0 * total)) / total)) / denom;
            return (Math.Max(0.0, center - half), Math.Min(1.0, center + half));
        }
    }
}
