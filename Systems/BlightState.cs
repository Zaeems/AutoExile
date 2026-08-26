using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks blight encounter state using entity events + per-tick dynamic updates.
    /// Entity lifecycle (add/remove) is handled by OnEntityAdded/OnEntityRemoved.
    /// Per-tick updates handle dynamic data (monster positions, pump StateMachine).
    ///
    /// All cached positions are in GRID coordinates (entity.GridPosNum).
    /// Convert to world via * Pathfinding.GridToWorld for NavigateTo/WorldToScreen.
    /// </summary>
    public class BlightState
    {
        // Pump tracking (cached as grid position + ID, not entity ref)
        public Vector2? PumpPosition { get; private set; }
        public long PumpEntityId { get; private set; }
        public bool IsPumpInRange { get; private set; }

        /// <summary>
        /// The actual point monsters converge on — the base of the blight organism,
        /// computed as the average lane endpoint closest to the pump. This is NOT the
        /// clickable pump entity (which is offset from the organism). Falls back to
        /// PumpPosition if lanes haven't been computed yet.
        /// </summary>
        public Vector2? DefensePosition => LaneTracker.HubPosition ?? PumpPosition;

        // Encounter state (derived from pump StateMachine)
        public bool IsEncounterActive { get; set; }
        public bool IsEncounterDone { get; private set; }
        public bool IsTimerDone { get; set; }
        public bool EncounterSucceeded { get; private set; }
        public DateTime? TimerDoneAt { get; set; }
        public DateTime? EncounterStartedAt { get; private set; }

        // Fast-forward
        public bool HasClickedFastForward { get; set; }

        // Countdown UI text
        public string CountdownText { get; private set; } = "";

        // Chest positions (grid coords, cleaned up via events)
        public HashSet<Vector2> ChestPositions { get; } = new();

        // --- Cached entity data (survives going off-screen) ---

        public Dictionary<long, CachedTower> CachedTowers { get; } = new();
        public HashSet<long> FullyUpgradedTowerIds { get; } = new();
        public Dictionary<long, CachedFoundation> CachedFoundations { get; } = new();
        public Dictionary<long, CachedMonster> CachedMonsters { get; } = new();

        // Legacy accessors (updated via events)
        public HashSet<long> KnownTowerEntityIds { get; } = new();

        // Lane tracker
        public BlightLaneTracker LaneTracker { get; private set; } = new();
        public string LaneDebug { get; private set; } = "";

        // Danger awareness
        public bool PumpUnderAttack { get; private set; }
        public int AliveMonsterCount { get; private set; }

        // Blight currency (read from UI each tick)
        public int Currency { get; private set; }

        // Portal tracking — cache position when first seen in map for exit navigation
        public Vector2? PortalPosition { get; set; }

        // Map completion tracking
        public bool MapComplete { get; set; }
        public int DeathCount { get; set; }

        // Debug diagnostics
        public string FoundationDebug { get; private set; } = "";

        // Timestamps for tower actions (used to trigger lane rescan)
        public DateTime LastTowerBuildAt { get; set; } = DateTime.MinValue;
        public DateTime LastTowerUpgradeAt { get; set; } = DateTime.MinValue;

        // Distance thresholds (grid units)
        private const float PumpRejectDistance = 92f;   // ~1000 world
        private static float RenderRange => Pathfinding.NetworkBubbleRadius;
        private const float PumpDangerRadius = 28f;     // ~300 world

        // Internal state
        private bool _wasEncounterActive;
        private bool _wasTimerRunning;
        private int _timerCheckTicks;
        private DateTime _lastThreatUpdateAt = DateTime.MinValue;
        private DateTime _lastCoverageUpdateAt = DateTime.MinValue;
        private DateTime _prevTowerBuildAt = DateTime.MinValue;
        private DateTime _prevTowerUpgradeAt = DateTime.MinValue;
        private int _pumpNonTargetableTicks;
        private const int PumpNonTargetableThreshold = 30; // ~0.5s sustained before trusting

        // Chest ID→position mapping (needed for removal by event)
        private readonly Dictionary<long, Vector2> _chestEntityPositions = new();

        public void Reset()
        {
            PumpPosition = null;
            PumpEntityId = 0;
            IsPumpInRange = false;
            IsEncounterActive = false;
            IsEncounterDone = false;
            IsTimerDone = false;
            EncounterSucceeded = false;
            TimerDoneAt = null;
            EncounterStartedAt = null;
            HasClickedFastForward = false;
            CountdownText = "";
            ChestPositions.Clear();
            CachedTowers.Clear();
            CachedFoundations.Clear();
            CachedMonsters.Clear();
            KnownTowerEntityIds.Clear();
            FullyUpgradedTowerIds.Clear();
            _chestEntityPositions.Clear();
            LaneTracker = new BlightLaneTracker();
            LaneDebug = "";
            PumpUnderAttack = false;
            AliveMonsterCount = 0;
            LastTowerBuildAt = DateTime.MinValue;
            LastTowerUpgradeAt = DateTime.MinValue;
            _wasEncounterActive = false;
            _wasTimerRunning = false;
            _timerCheckTicks = 0;
            _lastThreatUpdateAt = DateTime.MinValue;
            _lastCoverageUpdateAt = DateTime.MinValue;
            _prevTowerBuildAt = DateTime.MinValue;
            _prevTowerUpgradeAt = DateTime.MinValue;
            _pumpNonTargetableTicks = 0;
            PortalPosition = null;
            MapComplete = false;
            DeathCount = 0;
        }

        public void OnEntityAdded(Entity entity)
        {
            if (entity.Path == null) return;

            // Pump
            if (entity.Type == EntityType.IngameIcon && entity.Path.EndsWith("/BlightPump"))
            {
                var pos = entity.GridPosNum;
                if (PumpPosition.HasValue && Vector2.Distance(pos, PumpPosition.Value) > PumpRejectDistance)
                    return;
                PumpPosition = pos;
                PumpEntityId = entity.Id;
                IsPumpInRange = true;
                return;
            }

            // Tower
            if (entity.Path.Contains("BlightTower") && !entity.Path.Contains("TargetMarker"))
            {
                var btId = BlightLaneTracker.GetBlightTowerId(entity);

                if (!CachedTowers.TryGetValue(entity.Id, out var ct))
                {
                    ct = new CachedTower { EntityId = entity.Id };
                    CachedTowers[entity.Id] = ct;
                }

                ct.Position = entity.GridPosNum;
                ct.BlightTowerId = btId;
                ct.TowerType = btId != null ? BlightLaneTracker.GetTypeFromBlightTowerId(btId) : null;
                ct.Tier = btId != null ? BlightLaneTracker.GetTierFromBlightTowerId(btId) : 1;
                ct.LastSeen = DateTime.Now;
                ct.IsVisible = true;
                KnownTowerEntityIds.Add(entity.Id);

                if (entity.TryGetComponent<BlightTower>(out var bt) && bt.Info != null && bt.Info.Radius > 0)
                    ct.Radius = bt.Info.Radius;

                return;
            }

            // Foundation
            if (entity.Path.Contains("BlightFoundation"))
            {
                if (!CachedFoundations.TryGetValue(entity.Id, out var cf))
                {
                    cf = new CachedFoundation { EntityId = entity.Id };
                    CachedFoundations[entity.Id] = cf;
                }

                cf.Position = entity.GridPosNum;
                cf.LastSeen = DateTime.Now;
                cf.IsVisible = true;
                if (!cf.IsBuilt)
                    cf.IsBuilt = false;
                return;
            }

            // Monster
            if (entity.Type == EntityType.Monster && entity.IsHostile)
            {
                if (!CachedMonsters.TryGetValue(entity.Id, out var cm))
                {
                    cm = new CachedMonster { EntityId = entity.Id };
                    CachedMonsters[entity.Id] = cm;
                }

                cm.Position = entity.GridPosNum;
                cm.Rarity = entity.Rarity;
                cm.AssumedAlive = entity.IsAlive && entity.IsTargetable;
                cm.LastSeen = DateTime.Now;
                cm.IsVisible = true;
                return;
            }

            // Portal
            if (entity.Type == EntityType.TownPortal)
            {
                PortalPosition = entity.GridPosNum;
                return;
            }

            // Chest
            if (entity.Type == EntityType.Chest)
            {
                var pos = entity.GridPosNum;
                if (!entity.IsOpened)
                {
                    ChestPositions.Add(pos);
                    _chestEntityPositions[entity.Id] = pos;
                }
                else
                {
                    ChestPositions.Remove(pos);
                    _chestEntityPositions.Remove(entity.Id);
                }
                return;
            }
        }

        public void OnEntityRemoved(Entity entity, Vector2 playerPos)
        {
            var id = entity.Id;

            if (id == PumpEntityId)
            {
                IsPumpInRange = false;
                return;
            }

            if (CachedFoundations.TryGetValue(id, out var cf))
            {
                cf.IsVisible = false;
                if (Vector2.Distance(playerPos, cf.Position) < RenderRange)
                    cf.IsBuilt = true;
                UpdateFoundationDebugText();
                return;
            }

            if (CachedTowers.TryGetValue(id, out var ct))
            {
                ct.IsVisible = false;
                KnownTowerEntityIds.Remove(id);
                if (Vector2.Distance(playerPos, ct.Position) < RenderRange)
                {
                    CachedTowers.Remove(id);
                    FullyUpgradedTowerIds.Remove(id);
                }
                return;
            }

            if (CachedMonsters.TryGetValue(id, out var cm))
            {
                cm.IsVisible = false;
                if (Vector2.Distance(playerPos, cm.Position) < RenderRange)
                    cm.AssumedAlive = false;
                return;
            }

            if (_chestEntityPositions.TryGetValue(id, out var chestPos))
            {
                if (Vector2.Distance(playerPos, chestPos) < RenderRange)
                {
                    ChestPositions.Remove(chestPos);
                    _chestEntityPositions.Remove(id);
                }
                return;
            }
        }

        public void InitializeFromCurrentEntities(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                OnEntityAdded(entity);
            UpdateFoundationDebugText();
        }

        public void ScanForPump(GameController gc)
        {
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.IngameIcon || entity.Path == null || !entity.Path.EndsWith("/BlightPump"))
                    continue;

                var pos = entity.GridPosNum;
                if (PumpPosition.HasValue && Vector2.Distance(pos, PumpPosition.Value) > PumpRejectDistance)
                    continue;

                PumpPosition = pos;
                PumpEntityId = entity.Id;
                IsPumpInRange = true;

                if (entity.TryGetComponent<StateMachine>(out var states))
                {
                    var activated = GetStateValue(states, "activated");
                    if (activated > 0)
                        IsEncounterActive = true;

                    var encounterDone = GetStateValue(states, "encounter_done");
                    var success = GetStateValue(states, "success");
                    var fail = GetStateValue(states, "fail");
                    if (encounterDone > 0 || success > 0 || fail > 0)
                    {
                        IsEncounterDone = true;
                        IsTimerDone = true;
                        EncounterSucceeded = success > 0;
                        TimerDoneAt ??= DateTime.Now;
                    }
                }

                if (!IsEncounterActive)
                {
                    if (!entity.IsTargetable)
                    {
                        _pumpNonTargetableTicks++;
                        if (_pumpNonTargetableTicks >= PumpNonTargetableThreshold)
                            IsEncounterActive = true;
                    }
                    else
                    {
                        _pumpNonTargetableTicks = 0;
                    }
                }
                break;
            }
        }

        public void Tick(GameController gc)
        {
            UpdateDynamicEntityData(gc);
            TrackLanes(gc);
            TrackCountdown(gc);
            TrackEncounterCompletion(gc);
            TrackDanger(gc);
            TrackCurrency(gc);
            UpdateFoundationDebugText();
        }

        private void UpdateDynamicEntityData(GameController gc)
        {
            bool prevEncounterActive = IsEncounterActive;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (CachedMonsters.TryGetValue(entity.Id, out var cm))
                {
                    cm.Position = entity.GridPosNum;
                    cm.Rarity = entity.Rarity;
                    cm.AssumedAlive = entity.IsAlive && entity.IsTargetable;
                    cm.LastSeen = DateTime.Now;
                    continue;
                }

                if (CachedTowers.TryGetValue(entity.Id, out var ct))
                {
                    var btId = BlightLaneTracker.GetBlightTowerId(entity);
                    ct.BlightTowerId = btId;
                    ct.TowerType = btId != null ? BlightLaneTracker.GetTypeFromBlightTowerId(btId) : null;
                    ct.Tier = btId != null ? BlightLaneTracker.GetTierFromBlightTowerId(btId) : 1;
                    ct.LastSeen = DateTime.Now;

                    if (entity.TryGetComponent<BlightTower>(out var bt) && bt.Info != null && bt.Info.Radius > 0)
                        ct.Radius = bt.Info.Radius;

                    continue;
                }

                if (entity.Id == PumpEntityId)
                {
                    var pos = entity.GridPosNum;
                    if (!PumpPosition.HasValue || Vector2.Distance(pos, PumpPosition.Value) < PumpRejectDistance)
                        PumpPosition = pos;

                    IsPumpInRange = true;
                    if (entity.TryGetComponent<StateMachine>(out var states))
                    {
                        var activated = GetStateValue(states, "activated");
                        if (activated > 0)
                            IsEncounterActive = true;
                    }
                    if (!IsEncounterActive)
                    {
                        if (!entity.IsTargetable)
                        {
                            _pumpNonTargetableTicks++;
                            if (_pumpNonTargetableTicks >= PumpNonTargetableThreshold)
                                IsEncounterActive = true;
                        }
                        else
                        {
                            _pumpNonTargetableTicks = 0;
                        }
                    }
                    continue;
                }
            }

            var staleChestIds = new List<long>();
            foreach (var (id, pos) in _chestEntityPositions)
            {
                var chestEntity = gc.EntityListWrapper.OnlyValidEntities.FirstOrDefault(e => e.Id == id);
                if (chestEntity != null && chestEntity.IsOpened)
                {
                    ChestPositions.Remove(pos);
                    staleChestIds.Add(id);
                }
            }
            foreach (var id in staleChestIds)
                _chestEntityPositions.Remove(id);

            if (IsEncounterActive && !prevEncounterActive)
                EncounterStartedAt = DateTime.Now;
            _wasEncounterActive = IsEncounterActive;
        }

        private void TrackLanes(GameController gc)
        {
            LaneTracker.PumpPosition = PumpPosition;
            LaneTracker.Tick(gc);

            if (IsEncounterActive && LaneTracker.HasLaneData)
            {
                var now = DateTime.Now;
                bool needsDanger = false;

                if ((now - _lastThreatUpdateAt).TotalMilliseconds >= 250)
                {
                    LaneTracker.UpdateThreat(gc);
                    _lastThreatUpdateAt = now;
                    needsDanger = true;
                }

                bool towerStateChanged = LastTowerBuildAt != _prevTowerBuildAt || LastTowerUpgradeAt != _prevTowerUpgradeAt;
                if (towerStateChanged || (now - _lastCoverageUpdateAt).TotalMilliseconds >= 2000)
                {
                    LaneTracker.UpdateCoverage(CachedTowers.Values);
                    _lastCoverageUpdateAt = now;
                    needsDanger = true;
                }

                if (needsDanger)
                    LaneTracker.UpdateDanger();
            }

            LaneDebug = LaneTracker.GetDebugText();
        }

        private void TrackCountdown(GameController gc)
        {
            try
            {
                var countdownElement = gc.IngameState.IngameUi.Parent
                    .GetChildFromIndices(1, 25, 4, 0, 0, 0, 0);
                CountdownText = countdownElement?.Text ?? "";
            }
            catch
            {
                CountdownText = "";
            }

            // Fallback search if primary UI index was empty
            if (string.IsNullOrEmpty(CountdownText))
            {
                try
                {
                    var ui = gc.IngameState.IngameUi;
                    var parent = ui.LeagueMechanicButtons?.Parent;
                    if (parent != null)
                    {
                        foreach (var child in parent.Children)
                        {
                            if (child?.IsVisible == true && !string.IsNullOrEmpty(child.Text) && child.Text.Contains(':'))
                            {
                                var text = child.Text.Trim();
                                if (text.Length <= 5 && char.IsDigit(text[0]))
                                {
                                    CountdownText = text;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // Detect timer done — countdown reaching zero
            if (!IsTimerDone && IsEncounterActive)
            {
                bool timerRunning = !string.IsNullOrEmpty(CountdownText) &&
                    CountdownText.Trim() != "0:00" && CountdownText.Trim() != "00:00";

                if (timerRunning)
                {
                    _wasTimerRunning = true;
                }

                if (_wasTimerRunning && !timerRunning)
                {
                    IsTimerDone = true;
                    TimerDoneAt ??= DateTime.Now;
                }
                else if (!_wasTimerRunning && !timerRunning)
                {
                    var encounterAge = EncounterStartedAt.HasValue
                        ? (DateTime.Now - EncounterStartedAt.Value).TotalSeconds : 0;

                    // On re-entry after death (DeathCount > 0), if 5s passed and no timer is running,
                    // the timer finished while dead in hideout.
                    // On fresh encounters (DeathCount == 0), wait 360s (6mins) before assuming timer done.
                    double requiredAge = DeathCount > 0 ? 5.0 : 360.0;

                    if (encounterAge > requiredAge)
                    {
                        _timerCheckTicks++;
                        if (_timerCheckTicks > 30)
                        {
                            IsTimerDone = true;
                            TimerDoneAt ??= DateTime.Now;
                        }
                    }
                }
            }
        }

        private void TrackEncounterCompletion(GameController gc)
        {
            if (IsEncounterDone) return;
            if (PumpEntityId == 0) return;

            Entity? pump = null;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == PumpEntityId)
                {
                    pump = entity;
                    break;
                }
            }

            if (pump == null) return;

            if (pump.TryGetComponent<StateMachine>(out var states))
            {
                var encounterDone = GetStateValue(states, "encounter_done");
                var success = GetStateValue(states, "success");
                var fail = GetStateValue(states, "fail");

                if (encounterDone > 0 || success > 0 || fail > 0)
                {
                    IsEncounterDone = true;
                    IsTimerDone = true;
                    EncounterSucceeded = success > 0;
                    TimerDoneAt ??= DateTime.Now;
                }
            }
        }

        private void TrackDanger(GameController gc)
        {
            PumpUnderAttack = false;
            AliveMonsterCount = 0;

            if (!DefensePosition.HasValue) return;
            var defensePos = DefensePosition.Value;

            foreach (var cm in CachedMonsters.Values)
            {
                if (!cm.AssumedAlive) continue;
                AliveMonsterCount++;

                if (!PumpUnderAttack && Vector2.Distance(cm.Position, defensePos) < PumpDangerRadius)
                    PumpUnderAttack = true;
            }
        }

        private void TrackCurrency(GameController gc)
        {
            if (IsEncounterDone) return;

            var ui = gc.IngameState.IngameUi;
            if (ui == null) return;

            try
            {
                var c11 = ui.GetChildAtIndex(11);
                if (c11?.IsVisible == true && c11.ChildCount > 0)
                {
                    var c0 = c11.GetChildAtIndex(0);
                    if (c0 != null && c0.ChildCount > 3)
                    {
                        var hud = c0.GetChildAtIndex(3);
                        if (hud?.IsVisible == true && hud.ChildCount > 2)
                        {
                            var textEl = hud.GetChildFromIndices(2, 0, 1);
                            if (textEl?.IsVisible == true && TryParseCurrency(textEl.Text, out var val))
                            {
                                Currency = val;
                                return;
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                for (int i = 0; i < ui.ChildCount && i < 40; i++)
                {
                    var top = ui.GetChildAtIndex(i);
                    if (top == null || !top.IsVisible || top.ChildCount < 1) continue;

                    var inner = top.GetChildAtIndex(0);
                    if (inner == null || inner.ChildCount <= 3) continue;

                    var hud = inner.GetChildAtIndex(3);
                    if (hud == null || !hud.IsVisible || hud.ChildCount < 3) continue;

                    var durLabel = hud.GetChildFromIndices(1, 0, 0);
                    if (durLabel?.Text == null || !durLabel.Text.Contains("Pump")) continue;

                    var textEl = hud.GetChildFromIndices(2, 0, 1);
                    if (textEl != null && TryParseCurrency(textEl.Text, out var val))
                    {
                        Currency = val;
                        return;
                    }
                }
            }
            catch { }
        }

        private static bool TryParseCurrency(string? text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            return int.TryParse(text.Replace(",", ""), out value) && value >= 0;
        }

        private void UpdateFoundationDebugText()
        {
            int visible = 0, built = 0;
            foreach (var cf in CachedFoundations.Values)
            {
                if (cf.IsVisible) visible++;
                if (cf.IsBuilt) built++;
            }
            FoundationDebug = $"Foundations: {visible} visible, {built} built, {CachedFoundations.Count} cached, Towers: {CachedTowers.Count}";
        }

        private static long GetStateValue(StateMachine states, string name)
        {
            if (states?.States == null) return 0;
            foreach (var s in states.States)
            {
                if (s.Name == name)
                    return s.Value;
            }
            return 0;
        }

        public static Vector2 ToWorld(Vector2 gridPos) => gridPos * Pathfinding.GridToWorld;
        public static Vector3 ToWorld3(Vector2 gridPos, float z = 0) => new(gridPos.X * Pathfinding.GridToWorld, gridPos.Y * Pathfinding.GridToWorld, z);
    }

    public class CachedTower
    {
        public long EntityId;
        public Vector2 Position;
        public string? BlightTowerId;
        public string? TowerType;
        public int Tier;
        public float Radius;
        public DateTime LastSeen;
        public bool IsVisible;
    }

    public class CachedFoundation
    {
        public long EntityId;
        public Vector2 Position;
        public bool IsBuilt;
        public DateTime LastSeen;
        public bool IsVisible;
    }

    public class CachedMonster
    {
        public long EntityId;
        public Vector2 Position;
        public MonsterRarity Rarity;
        public bool AssumedAlive;
        public DateTime LastSeen;
        public bool IsVisible;
    }
}