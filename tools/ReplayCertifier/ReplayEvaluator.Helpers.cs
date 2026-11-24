using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.ReplayCertifier
{
    internal static partial class ReplayEvaluator
    {
        private static List<(int CmdId, byte[] Payload)> ReadFrames(byte[] bytes)
        {
            var list = new List<(int, byte[])>();
            int i = 0;
            while (i + 6 <= bytes.Length)
            {
                int cmd = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i, 2));
                int len = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i + 2, 4));
                if (len < 0 || i + 6 + len > bytes.Length) break;
                list.Add((cmd, len > 0 ? bytes.AsSpan(i + 6, len).ToArray() : Array.Empty<byte>()));
                i += 6 + len;
            }
            return list;
        }

        private static int[] ToIntArray(IEnumerable items)
        {
            if (items == null) return Array.Empty<int>();
            var list = new List<int>();
            foreach (var x in items) list.Add(Convert.ToInt32(x));
            return list.ToArray();
        }

        private static UnitSim BuildUnit(dynamic unitObj, bool isMonster)
        {
            dynamic h = unitObj.Hero;
            int slot = (int)unitObj.Slot;
            if (!isMonster)
            {
                try { int cs = (int)unitObj.ChangeSlot; if (cs >= 0 && cs < 4) slot = cs; } catch { }
            }
            var u = new UnitSim
            {
                Id = (long)unitObj.Id,
                Slot = slot,
                HeroId = (int)h.HeroId,
                IsMonster = isMonster,
                MonsterType = isMonster ? Convert.ToInt32(h.MonsterType) : 0,
                NodeId = (int)h.NodeId,
                BackNodeId = (int)h.BackNodeId,
                FrontNodeIds = ToIntArray((IEnumerable)h.FrontNodeIds),
                Hp = (int)h.Hp,
                Gold = (int)h.Gold,
                NotSelect = (bool)h.NotSelect
            };
            if (h.Buffs != null)
            {
                foreach (dynamic kv in (IEnumerable)h.Buffs)
                    if (kv?.Value != null)
                    {
                        int bid = (int)kv.Value.BuffId;
                        u.BuffIds.Add(bid);
                        if (bid == 10011 && kv.Value.Source != null) u.TauntSkillId = (int)kv.Value.Source.Id;
                    }
            }
            return u;
        }

        private static bool ApplyAttr(dynamic attr, Dictionary<long, UnitSim> players, Dictionary<long, UnitSim> monsters, bool fromSkillMove, long curTurnActor)
        {
            bool thiefStole = false;
            if (attr?.EffectDatas == null) return false;
            long attrOwner = (long)attr.PlayerId;
            bool actorIsThief = curTurnActor != 0 && attrOwner == curTurnActor &&
                monsters.TryGetValue(curTurnActor, out var curM) && curM.MonsterType == 1;

            foreach (dynamic eff in (IEnumerable)attr.EffectDatas)
            {
                if (eff == null) continue;
                long pid = (long)eff.PlayerId;
                if (!players.TryGetValue(pid, out var target) && !monsters.TryGetValue(pid, out target)) continue;

                if (eff.Hp != null)
                {
                    int currHp = (int)eff.Hp.CurrHp;
                    int maxHp = (int)eff.Hp.MaxHp;
                    int oriHp = (int)eff.Hp.OriHp;
                    int changeHp = (int)eff.Hp.ChangeHp;
                    if (maxHp > 0 || oriHp > 0 || changeHp != 0 || currHp > 0)
                        target.Hp = Math.Max(0, currHp);
                }
                if (eff.Gold != null)
                {
                    int chg = (int)eff.Gold.ChangeGold;
                    target.Gold = (int)eff.Gold.CurrGold;
                    if (actorIsThief && !target.IsMonster && chg < 0) thiefStole = true;
                }
                if (eff.NotSelect != null) target.NotSelect = (bool)eff.NotSelect.NotSelect;
                if (eff.Disappear != null && target.IsMonster) target.Hp = 0;

                if (eff.Buff != null)
                {
                    int op = Convert.ToInt32(eff.Buff.Op);
                    if (eff.Buff.Buff != null)
                    {
                        int bid = (int)eff.Buff.Buff.BuffId;
                        if (op == 2) { target.BuffIds.Remove(bid); if (bid == 10011) target.TauntSkillId = 0; }
                        else if (op == 1 || op == 3)
                        {
                            target.BuffIds.Add(bid);
                            if (bid == 10011 && eff.Buff.Buff.Source != null) target.TauntSkillId = (int)eff.Buff.Buff.Source.Id;
                        }
                    }
                    else if (eff.Buff.Buffs != null)
                    {
                        foreach (dynamic kv in (IEnumerable)eff.Buff.Buffs)
                        {
                            if (kv?.Value == null) continue;
                            int bid = (int)kv.Value.BuffId;
                            if (op == 2) { target.BuffIds.Remove(bid); if (bid == 10011) target.TauntSkillId = 0; }
                            else if (op == 1 || op == 3)
                            {
                                target.BuffIds.Add(bid);
                                if (bid == 10011 && kv.Value.Source != null) target.TauntSkillId = (int)kv.Value.Source.Id;
                            }
                        }
                    }
                }

                if (eff.Place != null && eff.Place.Place != null)
                {
                    dynamic pl = eff.Place.Place;
                    int newNode = (int)pl.NodeId;
                    int newBack = (int)pl.BackNodeId;
                    int[] newFront = ToIntArray((IEnumerable)pl.FrontNodeIds);
                    if (newNode >= 0) target.NodeId = newNode;
                    target.BackNodeId = newBack;
                    if (newFront.Length > 0) target.FrontNodeIds = newFront;
                    if (target.IsMonster && fromSkillMove) target.DisplacedBySkill = true;
                }
            }
            return thiefStole;
        }
    }
}
