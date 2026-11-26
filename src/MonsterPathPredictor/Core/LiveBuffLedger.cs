using System;
using System.Collections.Generic;

namespace MonsterPathPredictor.Core
{
    public readonly struct LiveBuffEntry
    {
        public long UniqueId { get; }
        public int BuffId { get; }
        public int Progress { get; }
        public int SourceSkillId { get; }

        public LiveBuffEntry(long uniqueId, int buffId, int progress, int sourceSkillId)
        {
            UniqueId = uniqueId;
            BuffId = buffId;
            Progress = progress;
            SourceSkillId = sourceSkillId;
        }
    }

    public readonly struct LiveBuffSummary
    {
        public bool HasConfirmedData { get; }
        public bool HasProblemStudent { get; }
        public bool HasMandarinDuck { get; }
        public bool IsTrueCulprit { get; }
        public bool HasUnchallengeableStealth { get; }
        public bool HasWardenReveal { get; }
        public int CrimeBuffProgress { get; }
        public bool HasTaunt { get; }
        public int TauntSourceSkillId { get; }

        public LiveBuffSummary(
            bool hasConfirmedData,
            bool hasProblemStudent,
            bool hasMandarinDuck,
            bool isTrueCulprit,
            bool hasUnchallengeableStealth,
            bool hasWardenReveal,
            int crimeBuffProgress,
            bool hasTaunt,
            int tauntSourceSkillId)
        {
            HasConfirmedData = hasConfirmedData;
            HasProblemStudent = hasProblemStudent;
            HasMandarinDuck = hasMandarinDuck;
            IsTrueCulprit = isTrueCulprit;
            HasUnchallengeableStealth = hasUnchallengeableStealth;
            HasWardenReveal = hasWardenReveal;
            CrimeBuffProgress = crimeBuffProgress;
            HasTaunt = hasTaunt;
            TauntSourceSkillId = tauntSourceSkillId;
        }
    }

    /// <summary>
    /// Mirrors <c>Core.Unit.BuffContainer._buffDict</c> keyed strictly by <c>UniqueId</c> so
    /// duplicate <c>BuffId</c> instances and op <c>0</c> full replacements behave identically to
    /// <c>AstralParty.Runtime.dll</c> across both live runtime events and <c>ReplayCertifier</c>.
    /// </summary>
    public sealed class LiveBuffMap
    {
        private readonly Dictionary<long, LiveBuffEntry> _byUniqueId = new Dictionary<long, LiveBuffEntry>();
        private long _syntheticKeyCounter = -1;
        private bool _hasConfirmedState;
        private bool _wardenRevealedLatched;

        public bool HasConfirmedState => _hasConfirmedState;
        public bool WardenRevealedLatched => _wardenRevealedLatched;
        public int Count => _byUniqueId.Count;

        public void ClearAll()
        {
            _byUniqueId.Clear();
            _hasConfirmedState = false;
            _wardenRevealedLatched = false;
        }

        public void ApplyFullReplace(IReadOnlyList<LiveBuffEntry> entries)
        {
            _byUniqueId.Clear();
            _hasConfirmedState = true;
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                PutEntry(entries[i]);
            }
        }

        public void ApplyOperation(int op, IReadOnlyList<LiveBuffEntry> entries)
        {
            if (op == 0)
            {
                ApplyFullReplace(entries);
                return;
            }
            if (entries == null) return;
            _hasConfirmedState = true;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (op == 2)
                {
                    RemoveEntry(e);
                }
                else if (op == 1 || op == 3)
                {
                    PutEntry(e);
                }
            }
        }

        private void PutEntry(LiveBuffEntry e)
        {
            if (e.BuffId <= 0) return;
            long key = e.UniqueId != 0 ? e.UniqueId : (_syntheticKeyCounter--);
            _byUniqueId[key] = new LiveBuffEntry(key, e.BuffId, e.Progress, e.SourceSkillId);
            if (e.BuffId == 10671303 || (e.BuffId == 10671301 && e.Progress > 0))
            {
                _wardenRevealedLatched = true;
            }
        }

        private void RemoveEntry(LiveBuffEntry e)
        {
            if (e.UniqueId != 0)
            {
                _byUniqueId.Remove(e.UniqueId);
                return;
            }
            // Fallback when UniqueId is unavailable: remove a single matching BuffId instance.
            long foundKey = 0;
            bool found = false;
            foreach (var kv in _byUniqueId)
            {
                if (kv.Value.BuffId == e.BuffId)
                {
                    foundKey = kv.Key;
                    found = true;
                    break;
                }
            }
            if (found) _byUniqueId.Remove(foundKey);
        }

        public bool ContainsBuff(int buffId)
        {
            foreach (var kv in _byUniqueId)
            {
                if (kv.Value.BuffId == buffId) return true;
            }
            return false;
        }

        public bool HasAnyExcludedBuff(HashSet<int> excludedIds)
        {
            if (excludedIds == null || excludedIds.Count == 0) return false;
            foreach (var kv in _byUniqueId)
            {
                if (excludedIds.Contains(kv.Value.BuffId)) return true;
            }
            return false;
        }

        public LiveBuffSummary BuildSummary()
        {
            if (!_hasConfirmedState)
            {
                return new LiveBuffSummary(false, false, false, false, false, _wardenRevealedLatched, 0, false, 0);
            }
            bool ps = false;
            bool md = false;
            bool tc = false;
            bool stealth = false;
            bool wardenRev = _wardenRevealedLatched;
            int crimeProg = 0;
            bool taunt = false;
            int tauntSkill = 0;

            foreach (var kv in _byUniqueId)
            {
                var b = kv.Value;
                switch (b.BuffId)
                {
                    case 2101001: ps = true; break;
                    case 10470101: md = true; break;
                    case 10671302: tc = true; break;
                    case 1270202: stealth = true; break;
                    case 10671303: wardenRev = true; break;
                    case 10671301:
                        if (b.Progress > 0) wardenRev = true;
                        break;
                    case 10013:
                        if (b.Progress > crimeProg) crimeProg = b.Progress;
                        break;
                    case 10011:
                        taunt = true;
                        if (b.SourceSkillId > 0) tauntSkill = b.SourceSkillId;
                        break;
                }
            }
            if (wardenRev) _wardenRevealedLatched = true;
            return new LiveBuffSummary(true, ps, md, tc, stealth, wardenRev, crimeProg, taunt, tauntSkill);
        }
    }

    /// <summary>
    /// Queues tile-triggered <c>1096 HeroSkillMoveEffectS2C</c> payloads keyed by <c>(moverId, triggerLandId)</c>
    /// and consumes each payload exactly once when the mover arrives at <c>triggerLandId</c>.
    /// </summary>
    public sealed class SkillMoveEffectLedger<TPayload>
    {
        private readonly Dictionary<(long MoverId, int TriggerLandId), Queue<TPayload>> _pending =
            new Dictionary<(long, int), Queue<TPayload>>();

        public void Clear() => _pending.Clear();

        public void ClearForMover(long moverId)
        {
            if (moverId == 0 || _pending.Count == 0) return;
            List<(long, int)> toRemove = null;
            foreach (var k in _pending.Keys)
            {
                if (k.MoverId == moverId)
                {
                    if (toRemove == null) toRemove = new List<(long, int)>();
                    toRemove.Add(k);
                }
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++) _pending.Remove(toRemove[i]);
            }
        }

        public void Enqueue(long moverId, int triggerLandId, TPayload payload)
        {
            if (moverId == 0 || triggerLandId < 0) return;
            var key = (moverId, triggerLandId);
            if (!_pending.TryGetValue(key, out var q))
            {
                q = new Queue<TPayload>(2);
                _pending[key] = q;
            }
            if (q.Count >= 16) q.Dequeue();
            q.Enqueue(payload);
        }

        public bool TryConsumeOnArrival(long moverId, int arrivedLandId, out List<TPayload> payloads)
        {
            payloads = null;
            if (moverId == 0 || arrivedLandId < 0) return false;
            var key = (moverId, arrivedLandId);
            if (!_pending.TryGetValue(key, out var q) || q.Count == 0) return false;
            payloads = new List<TPayload>(q.Count);
            while (q.Count > 0) payloads.Add(q.Dequeue());
            _pending.Remove(key);
            return true;
        }
    }
}
