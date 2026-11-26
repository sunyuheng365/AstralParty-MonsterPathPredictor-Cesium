using System;
using Core.Net;
using Cysharp.Threading.Tasks;
using party.protocol;
using Tools;

namespace MonsterPathPredictor.Runtime
{
    /// <summary>
    /// Safely hooks unconflicted <c>NetManager.RPC</c> server-callback delegates (never touching RPCs
    /// already managed by <c>CesiumLoader.SDK.Gameplay.GameEvents.HookSlot</c>) so live protocol packets
    /// (<c>1026</c>, <c>1015</c>, <c>1117</c>, <c>5022</c>, <c>5214</c>, <c>1086</c>, <c>1018</c>,
    /// <c>1096</c>, <c>RefreshRoomStateS2C</c>, <c>WatchRefreshRoomStateS2C</c>) are observed synchronously
    /// on arrival before presentation <c>UniTask</c> awaits.
    /// </summary>
    internal sealed class RpcProtocolHookBridge
    {
        public event Action<ActionStartNotifyS2C> ActionStarted;
        public event Action<RoundStartS2C> RoundStarted;
        public event Action<GameRoundChangeS2C> GameRoundChanged;
        public event Action<ThrowDiceS2C> DiceThrown;
        public event Action<MonsterPursuitS2C> MonsterPursuit;
        public event Action<MapIndexChangeS2C> MapIndexChanged;
        public event Action<MonsterRefreshS2C> MonsterRefreshed;
        public event Action<HeroSkillMoveEffectS2C> SkillMoveEffectsQueued;
        public event Action FullRoomSnapshotReceived;

        private sealed class Slot<TDelegate> where TDelegate : Delegate
        {
            public object Owner;
            public TDelegate Original;
            public TDelegate Wrapped;

            public void Ensure(object owner, TDelegate current, Func<TDelegate, TDelegate> wrap, Action<TDelegate> assign)
            {
                if (owner == null || current == null) return;
                if (ReferenceEquals(Owner, owner) && ReferenceEquals(current, Wrapped)) return;
                Owner = owner;
                Original = current;
                Wrapped = wrap(current);
                assign(Wrapped);
            }

            public void Restore(object owner, TDelegate current, Action<TDelegate> assign)
            {
                if (owner != null && ReferenceEquals(Owner, owner) && ReferenceEquals(current, Wrapped))
                {
                    assign(Original);
                }
                Owner = null;
                Original = null;
                Wrapped = null;
            }
        }

        private readonly Slot<ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate> _actionStart = new Slot<ActionStartNotifyS2CRPC.OnActionStartNotifyS2CServerDelegate>();
        private readonly Slot<RoundStartS2CRPC.OnRoundStartS2CServerDelegate> _roundStart = new Slot<RoundStartS2CRPC.OnRoundStartS2CServerDelegate>();
        private readonly Slot<GameRoundChangeS2CRPC.OnGameRoundChangeS2CServerDelegate> _gameRoundChange = new Slot<GameRoundChangeS2CRPC.OnGameRoundChangeS2CServerDelegate>();
        private readonly Slot<ThrowDiceS2CRPC.OnThrowDiceS2CServerDelegate> _throwDice = new Slot<ThrowDiceS2CRPC.OnThrowDiceS2CServerDelegate>();
        private readonly Slot<MonsterPursuitS2CRPC.OnMonsterPursuitS2CServerDelegate> _monsterPursuit = new Slot<MonsterPursuitS2CRPC.OnMonsterPursuitS2CServerDelegate>();
        private readonly Slot<MapIndexChangeS2CRPC.OnMapIndexChangeS2CServerDelegate> _mapIndexChange = new Slot<MapIndexChangeS2CRPC.OnMapIndexChangeS2CServerDelegate>();
        private readonly Slot<MonsterRefreshS2CRPC.OnMonsterRefreshS2CServerDelegate> _monsterRefresh = new Slot<MonsterRefreshS2CRPC.OnMonsterRefreshS2CServerDelegate>();
        private readonly Slot<HeroSkillMoveEffectS2CRPC.OnHeroSkillMoveEffectS2CServerDelegate> _skillMoveEffect = new Slot<HeroSkillMoveEffectS2CRPC.OnHeroSkillMoveEffectS2CServerDelegate>();
        private readonly Slot<RefreshRoomStateS2CRPC.OnRefreshRoomStateS2CServerDelegate> _refreshRoom = new Slot<RefreshRoomStateS2CRPC.OnRefreshRoomStateS2CServerDelegate>();
        private readonly Slot<WatchRefreshRoomStateS2CRPC.OnWatchRefreshRoomStateS2CServerDelegate> _watchRefreshRoom = new Slot<WatchRefreshRoomStateS2CRPC.OnWatchRefreshRoomStateS2CServerDelegate>();

        private static RPCMsgManager TryGetRpc()
        {
            try
            {
                if (!MonoSingletonProvider<NetManager>.hasInstance) return null;
                var nm = MonoSingletonProvider<NetManager>.inst;
                return nm != null ? nm.RPC : null;
            }
            catch { return null; }
        }

        public void EnsureHooked()
        {
            var rpc = TryGetRpc();
            if (rpc == null) return;

            try
            {
                if (rpc.ActionStartNotifyS2C != null)
                    _actionStart.Ensure(rpc.ActionStartNotifyS2C, rpc.ActionStartNotifyS2C.OnActionStartNotifyS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(ActionStarted, m); return orig(m, err, d); },
                        w => rpc.ActionStartNotifyS2C.OnActionStartNotifyS2CServerCallBackAsync = w);

                if (rpc.RoundStartS2C != null)
                    _roundStart.Ensure(rpc.RoundStartS2C, rpc.RoundStartS2C.OnRoundStartS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(RoundStarted, m); return orig(m, err, d); },
                        w => rpc.RoundStartS2C.OnRoundStartS2CServerCallBackAsync = w);

                if (rpc.GameRoundChangeS2C != null)
                    _gameRoundChange.Ensure(rpc.GameRoundChangeS2C, rpc.GameRoundChangeS2C.OnGameRoundChangeS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(GameRoundChanged, m); return orig(m, err, d); },
                        w => rpc.GameRoundChangeS2C.OnGameRoundChangeS2CServerCallBackAsync = w);

                if (rpc.ThrowDiceS2C != null)
                    _throwDice.Ensure(rpc.ThrowDiceS2C, rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(DiceThrown, m); return orig(m, err, d); },
                        w => rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync = w);

                if (rpc.MonsterPursuitS2C != null)
                    _monsterPursuit.Ensure(rpc.MonsterPursuitS2C, rpc.MonsterPursuitS2C.OnMonsterPursuitS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(MonsterPursuit, m); return orig(m, err, d); },
                        w => rpc.MonsterPursuitS2C.OnMonsterPursuitS2CServerCallBackAsync = w);

                if (rpc.MapIndexChangeS2C != null)
                    _mapIndexChange.Ensure(rpc.MapIndexChangeS2C, rpc.MapIndexChangeS2C.OnMapIndexChangeS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(MapIndexChanged, m); return orig(m, err, d); },
                        w => rpc.MapIndexChangeS2C.OnMapIndexChangeS2CServerCallBackAsync = w);

                if (rpc.MonsterRefreshS2C != null)
                    _monsterRefresh.Ensure(rpc.MonsterRefreshS2C, rpc.MonsterRefreshS2C.OnMonsterRefreshS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(MonsterRefreshed, m); return orig(m, err, d); },
                        w => rpc.MonsterRefreshS2C.OnMonsterRefreshS2CServerCallBackAsync = w);

                if (rpc.HeroSkillMoveEffectS2C != null)
                    _skillMoveEffect.Ensure(rpc.HeroSkillMoveEffectS2C, rpc.HeroSkillMoveEffectS2C.OnHeroSkillMoveEffectS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0 && m != null) SafeInvoke(SkillMoveEffectsQueued, m); return orig(m, err, d); },
                        w => rpc.HeroSkillMoveEffectS2C.OnHeroSkillMoveEffectS2CServerCallBackAsync = w);

                if (rpc.RefreshRoomStateS2C != null)
                    _refreshRoom.Ensure(rpc.RefreshRoomStateS2C, rpc.RefreshRoomStateS2C.OnRefreshRoomStateS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0) SafeInvoke(FullRoomSnapshotReceived); return orig(m, err, d); },
                        w => rpc.RefreshRoomStateS2C.OnRefreshRoomStateS2CServerCallBackAsync = w);

                if (rpc.WatchRefreshRoomStateS2C != null)
                    _watchRefreshRoom.Ensure(rpc.WatchRefreshRoomStateS2C, rpc.WatchRefreshRoomStateS2C.OnWatchRefreshRoomStateS2CServerCallBackAsync,
                        orig => (m, err, d) => { if (err == 0) SafeInvoke(FullRoomSnapshotReceived); return orig(m, err, d); },
                        w => rpc.WatchRefreshRoomStateS2C.OnWatchRefreshRoomStateS2CServerCallBackAsync = w);
            }
            catch { }
        }

        public void Unhook()
        {
            var rpc = TryGetRpc();
            try
            {
                if (rpc != null)
                {
                    if (rpc.ActionStartNotifyS2C != null)
                        _actionStart.Restore(rpc.ActionStartNotifyS2C, rpc.ActionStartNotifyS2C.OnActionStartNotifyS2CServerCallBackAsync, w => rpc.ActionStartNotifyS2C.OnActionStartNotifyS2CServerCallBackAsync = w);
                    if (rpc.RoundStartS2C != null)
                        _roundStart.Restore(rpc.RoundStartS2C, rpc.RoundStartS2C.OnRoundStartS2CServerCallBackAsync, w => rpc.RoundStartS2C.OnRoundStartS2CServerCallBackAsync = w);
                    if (rpc.GameRoundChangeS2C != null)
                        _gameRoundChange.Restore(rpc.GameRoundChangeS2C, rpc.GameRoundChangeS2C.OnGameRoundChangeS2CServerCallBackAsync, w => rpc.GameRoundChangeS2C.OnGameRoundChangeS2CServerCallBackAsync = w);
                    if (rpc.ThrowDiceS2C != null)
                        _throwDice.Restore(rpc.ThrowDiceS2C, rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync, w => rpc.ThrowDiceS2C.OnThrowDiceS2CServerCallBackAsync = w);
                    if (rpc.MonsterPursuitS2C != null)
                        _monsterPursuit.Restore(rpc.MonsterPursuitS2C, rpc.MonsterPursuitS2C.OnMonsterPursuitS2CServerCallBackAsync, w => rpc.MonsterPursuitS2C.OnMonsterPursuitS2CServerCallBackAsync = w);
                    if (rpc.MapIndexChangeS2C != null)
                        _mapIndexChange.Restore(rpc.MapIndexChangeS2C, rpc.MapIndexChangeS2C.OnMapIndexChangeS2CServerCallBackAsync, w => rpc.MapIndexChangeS2C.OnMapIndexChangeS2CServerCallBackAsync = w);
                    if (rpc.MonsterRefreshS2C != null)
                        _monsterRefresh.Restore(rpc.MonsterRefreshS2C, rpc.MonsterRefreshS2C.OnMonsterRefreshS2CServerCallBackAsync, w => rpc.MonsterRefreshS2C.OnMonsterRefreshS2CServerCallBackAsync = w);
                    if (rpc.HeroSkillMoveEffectS2C != null)
                        _skillMoveEffect.Restore(rpc.HeroSkillMoveEffectS2C, rpc.HeroSkillMoveEffectS2C.OnHeroSkillMoveEffectS2CServerCallBackAsync, w => rpc.HeroSkillMoveEffectS2C.OnHeroSkillMoveEffectS2CServerCallBackAsync = w);
                    if (rpc.RefreshRoomStateS2C != null)
                        _refreshRoom.Restore(rpc.RefreshRoomStateS2C, rpc.RefreshRoomStateS2C.OnRefreshRoomStateS2CServerCallBackAsync, w => rpc.RefreshRoomStateS2C.OnRefreshRoomStateS2CServerCallBackAsync = w);
                    if (rpc.WatchRefreshRoomStateS2C != null)
                        _watchRefreshRoom.Restore(rpc.WatchRefreshRoomStateS2C, rpc.WatchRefreshRoomStateS2C.OnWatchRefreshRoomStateS2CServerCallBackAsync, w => rpc.WatchRefreshRoomStateS2C.OnWatchRefreshRoomStateS2CServerCallBackAsync = w);
                }
            }
            catch { }
        }

        private static void SafeInvoke<T>(Action<T> handler, T arg)
        {
            try { handler?.Invoke(arg); } catch { }
        }

        private static void SafeInvoke(Action handler)
        {
            try { handler?.Invoke(); } catch { }
        }
    }
}
