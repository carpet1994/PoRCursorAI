// =============================================================================
//  La Via della Redenzione — Systems/BattleSystem.cs
//  PROMPT 11 — Logica core del combattimento a turni (CTB semplificato / FFX-like).
//
//  Dipendenze: Core (Character, Enemy, CardModel, CardEffect, GameEnums).
//  Nessuna dipendenza da MAUI: la UI ascolta eventi e legge stato esposto.
// =============================================================================

using LaViaDellaRedenzione.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LaViaDellaRedenzione.Systems
{
    // =========================================================================
    //  RUNTIME — istanza nemico in battaglia
    // =========================================================================

    /// <summary>
    /// Nemico vivo in campo: HP corrente, stati, riferimento al template dati.
    /// </summary>
    public sealed class EnemyInstance
    {
        public Enemy Template { get; }
        public int CurrentHP { get; set; }
        public List<StatusEffect> ActiveStatusEffects { get; } = new();

        public EnemyInstance(Enemy template)
        {
            Template = template;
            CurrentHP = template.MaxHP;
        }

        public bool IsAlive => CurrentHP > 0;

        public float GetResistance(ElementType e) => Template.GetResistance(e);

        public int EffectiveSPD
        {
            get
            {
                float spd = Template.SPD;
                if (HasStatus(StatusEffectType.Rallentato))
                    spd *= 0.7f;
                if (HasStatus(StatusEffectType.Velocizzato))
                    spd *= 1.25f;
                return Math.Max(1, (int)spd);
            }
        }

        public bool HasStatus(StatusEffectType t)
            => ActiveStatusEffects.Any(s => s.Type == t && s.TurnsLeft > 0);
    }

    // =========================================================================
    //  BATTLE TARGET — bersaglio unificato (alleato o nemico)
    // =========================================================================

    /// <summary>
    /// Wrapper per risolvere effetti su personaggi o nemici.
    /// </summary>
    public sealed class BattleTarget
    {
        public Character? Ally { get; }
        public EnemyInstance? Foe { get; }

        public bool IsAlly => Ally != null;
        public bool IsEnemy => Foe != null;

        public static BattleTarget FromAlly(Character c) => new(c, null);
        public static BattleTarget FromEnemy(EnemyInstance e) => new(null, e);

        BattleTarget(Character? ally, EnemyInstance? foe)
        {
            Ally = ally;
            Foe  = foe;
        }

        public string DisplayName
            => Ally?.DisplayName ?? Foe?.Template.Name ?? "?";

        public bool IsAlive
            => IsAlly ? Ally!.CurrentHP > 0 : Foe!.IsAlive;

        public int CurrentHP
        {
            get => IsAlly ? Ally!.CurrentHP : Foe!.CurrentHP;
            set
            {
                if (IsAlly) Ally!.CurrentHP = Math.Max(0, value);
                else Foe!.CurrentHP = Math.Max(0, value);
            }
        }

        public int MaxHP
            => IsAlly ? Ally!.MaxHP : Foe!.Template.MaxHP;

        public float GetResistance(ElementType e)
            => IsAlly ? Ally!.ElementalResistance.GetValueOrDefault(e, 1f) : Foe!.GetResistance(e);
    }

    // =========================================================================
    //  EVENT ARGS
    // =========================================================================

    public sealed class BattleEndedEventArgs : EventArgs
    {
        public BattleState Outcome      { get; init; }
        public int         ExpGained    { get; init; }
        public int         GoldGained   { get; init; }
        public bool        Fled         { get; init; }
    }

    public sealed class DamageResolvedEventArgs : EventArgs
    {
        public BattleTarget Target      { get; init; } = null!;
        public int          Amount      { get; init; }
        public bool         WasCritical { get; init; }
        public ElementType  Element     { get; init; }
        public bool         WasHealing  { get; init; }
    }

    public sealed class TurnChangedEventArgs : EventArgs
    {
        public BattleTarget? CurrentActor { get; init; }
        public int           RoundIndex   { get; init; }
    }

    // =========================================================================
    //  BATTLE SYSTEM
    // =========================================================================

    /// <summary>
    /// Combattimento a turni: ordine per SPD, timeline, carte, difesa, fuga, stati.
    /// </summary>
    public sealed class BattleSystem
    {
        public const int TimelinePreviewCount = 7;

        readonly Random _rng = new();

        readonly List<Character>    _party   = new();
        readonly List<EnemyInstance> _enemies = new();

        /// <summary>Ordine di tutti i partecipanti vivi ordinati per SPD (decrescente), ricalcolato a inizio round.</summary>
        readonly List<BattleTarget> _turnQueue = new();

        int  _roundIndex;
        int  _turnIndexInRound;
        bool _battleOver;

        /// <summary>Difesa attiva fino all'inizio del prossimo turno del personaggio.</summary>
        readonly HashSet<string> _defending = new(StringComparer.OrdinalIgnoreCase);

        public BattleState State { get; private set; } = BattleState.PlayerTurn;

        /// <summary>Bersaglio attualmente in esecuzione (null se tra turni o fine).</summary>
        public BattleTarget? ActiveActor { get; private set; }

        public IReadOnlyList<Character>    Party   => _party;
        public IReadOnlyList<EnemyInstance> Enemies => _enemies;

        public event EventHandler<BattleState>?           StateChanged;
        public event EventHandler<TurnChangedEventArgs>? TurnChanged;
        public event EventHandler<DamageResolvedEventArgs>? DamageResolved;
        public event EventHandler<string>?               BattleLog;
        public event EventHandler<BattleEndedEventArgs>? BattleEnded;

        // ---------------------------------------------------------------------
        //  Inizializzazione
        // ---------------------------------------------------------------------

        public void StartBattle(IEnumerable<Character> party, IEnumerable<Enemy> enemyTemplates)
        {
            _party.Clear();
            _enemies.Clear();
            _turnQueue.Clear();
            _defending.Clear();
            _battleOver = false;
            _roundIndex = 0;
            _turnIndexInRound = 0;

            foreach (var c in party)
                _party.Add(c);

            foreach (var e in enemyTemplates)
                _enemies.Add(new EnemyInstance(e));

            RebuildTurnQueue();
            State = BattleState.PlayerTurn;
            RaiseState();

            BeginNextActorTurn();
        }

        void RebuildTurnQueue()
        {
            _turnQueue.Clear();
            var actors = new List<BattleTarget>();

            foreach (var c in _party.Where(x => x.CurrentHP > 0))
                actors.Add(BattleTarget.FromAlly(c));
            foreach (var e in _enemies.Where(x => x.IsAlive))
                actors.Add(BattleTarget.FromEnemy(e));

            foreach (var a in actors.OrderByDescending(EffectiveSpeed))
                _turnQueue.Add(a);
        }

        int EffectiveSpeed(BattleTarget t)
        {
            if (t.IsAlly)
            {
                var c = t.Ally!;
                float s = c.SPD;
                if (c.ActiveStatusEffects.Any(x => x.Type == StatusEffectType.Rallentato && x.TurnsLeft > 0))
                    s *= 0.7f;
                if (c.ActiveStatusEffects.Any(x => x.Type == StatusEffectType.Velocizzato && x.TurnsLeft > 0))
                    s *= 1.25f;
                return Math.Max(1, (int)s);
            }

            return t.Foe!.EffectiveSPD;
        }

        // ---------------------------------------------------------------------
        //  Timeline — prossimi N turni (simulazione round ripetuti)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Restituisce i prossimi <paramref name="count"/> turni previsti (max 7),
        /// ricalcolando l'ordine come cicli di SPD decrescente.
        /// </summary>
        public IReadOnlyList<BattleTarget> GetTimelinePreview(int count = TimelinePreviewCount)
        {
            var list = new List<BattleTarget>();
            if (_turnQueue.Count == 0 || _battleOver)
                return list;

            int n = Math.Min(count, TimelinePreviewCount);
            int idx = _turnIndexInRound;
            for (int i = 0; i < n * 2 && list.Count < n; i++)
            {
                if (_turnQueue.Count == 0) break;
                if (idx >= _turnQueue.Count)
                    idx = 0;
                var t = _turnQueue[idx++];
                if (t.IsAlive)
                    list.Add(t);
            }

            return list;
        }

        // ---------------------------------------------------------------------
        //  Turno
        // ---------------------------------------------------------------------

        void BeginNextActorTurn()
        {
            if (CheckBattleEnd())
                return;

            if (_turnQueue.Count == 0)
                RebuildTurnQueue();

            if (_turnIndexInRound >= _turnQueue.Count)
            {
                _roundIndex++;
                _turnIndexInRound = 0;
                OnRoundStart();
                RebuildTurnQueue();
            }

            while (_turnIndexInRound < _turnQueue.Count)
            {
                var actor = _turnQueue[_turnIndexInRound];
                if (!actor.IsAlive)
                {
                    _turnIndexInRound++;
                    continue;
                }

                if (ShouldSkipTurn(actor))
                {
                    Log($"{actor.DisplayName} è stordito e salta il turno.");
                    ConsumeStordito(actor);
                    AdvanceTurnIndex();
                    continue;
                }

                if (actor.IsAlly)
                    _defending.Remove(actor.Ally!.CharacterId);

                ActiveActor = actor;
                TurnChanged?.Invoke(this, new TurnChangedEventArgs
                {
                    CurrentActor = actor,
                    RoundIndex   = _roundIndex
                });

                if (actor.IsEnemy)
                {
                    State = BattleState.EnemyTurn;
                    RaiseState();
                    ExecuteEnemyTurn(actor.Foe!);
                }
                else
                {
                    State = BattleState.PlayerTurn;
                    RaiseState();
                }

                return;
            }

            BeginNextActorTurn();
        }

        bool ShouldSkipTurn(BattleTarget actor)
        {
            if (actor.IsAlly)
                return actor.Ally!.ActiveStatusEffects.Any(s => s.Type == StatusEffectType.Stordito && s.TurnsLeft > 0);
            return actor.Foe!.ActiveStatusEffects.Any(s => s.Type == StatusEffectType.Stordito && s.TurnsLeft > 0);
        }

        static void ConsumeStordito(BattleTarget actor)
        {
            var list = actor.IsAlly ? actor.Ally!.ActiveStatusEffects : actor.Foe!.ActiveStatusEffects;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Type != StatusEffectType.Stordito) continue;
                list.RemoveAt(i);
                return;
            }
        }

        void AdvanceTurnIndex()
        {
            _turnIndexInRound++;
            if (_turnIndexInRound >= _turnQueue.Count)
            {
                _roundIndex++;
                _turnIndexInRound = 0;
                OnRoundStart();
                RebuildTurnQueue();
            }
        }

        void OnRoundStart()
        {
            TickStatusesOnRoundStart();
        }

        void TickStatusesOnRoundStart()
        {
            foreach (var c in _party)
                TickStatuses(c.ActiveStatusEffects, c);
            foreach (var e in _enemies)
                TickStatuses(e.ActiveStatusEffects, null, e);
        }

        void TickStatuses(List<StatusEffect> list, Character? ch, EnemyInstance? en = null)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var s = list[i];
                if (s.TurnsLeft > 0)
                {
                    s.TurnsLeft--;
                    if (ch != null)
                        s.OnTurnStart?.Invoke(ch);
                }
                if (s.TurnsLeft <= 0)
                    list.RemoveAt(i);
            }
        }

        /// <summary>
        /// Usa una carta intera (tutti gli effetti in sequenza).
        /// </summary>
        public bool TryPlayCard(Character user, CardModel card, IReadOnlyList<BattleTarget> explicitTargets)
        {
            if (_battleOver || ActiveActor?.Ally != user || State != BattleState.PlayerTurn)
                return false;

            if (user.CurrentSP < card.SpCost)
            {
                Log("SP insufficienti.");
                return false;
            }

            user.CurrentSP -= card.SpCost;
            State = BattleState.AnimatingAction;

            foreach (var fx in card.Effects)
            {
                var targets = ResolveTargets(user, fx, explicitTargets, card);
                foreach (var t in targets)
                    ResolveEffect(user, fx, card, t);
            }

            RaiseState();
            return true;
        }

        /// <summary>
        /// Risolve un singolo effetto (come richiesto dal prompt per API granulari).
        /// </summary>
        public void UseCardEffect(Character user, CardEffect effect, CardModel sourceCard, IReadOnlyList<BattleTarget> targets)
        {
            foreach (var t in targets)
                ResolveEffect(user, effect, sourceCard, t);
        }

        List<BattleTarget> ResolveTargets(Character user, CardEffect fx, IReadOnlyList<BattleTarget> explicitTargets, CardModel card)
        {
            var list = new List<BattleTarget>();

            switch (fx.Target)
            {
                case TargetType.Self:
                    list.Add(BattleTarget.FromAlly(user));
                    break;
                case TargetType.SingleEnemy:
                    if (explicitTargets.Count > 0 && explicitTargets[0].IsEnemy)
                        list.Add(explicitTargets[0]);
                    else
                    {
                        var first = _enemies.FirstOrDefault(e => e.IsAlive);
                        if (first != null) list.Add(BattleTarget.FromEnemy(first));
                    }
                    break;
                case TargetType.AllEnemies:
                    foreach (var e in _enemies.Where(x => x.IsAlive))
                        list.Add(BattleTarget.FromEnemy(e));
                    break;
                case TargetType.SingleAlly:
                    if (explicitTargets.Count > 0 && explicitTargets[0].IsAlly)
                        list.Add(explicitTargets[0]);
                    else
                        list.Add(BattleTarget.FromAlly(user));
                    break;
                case TargetType.AllAllies:
                    foreach (var c in _party.Where(x => x.CurrentHP > 0))
                        list.Add(BattleTarget.FromAlly(c));
                    break;
                case TargetType.Random:
                    {
                        int hits = Math.Max(1, fx.HitCount);
                        var alive = _enemies.Where(e => e.IsAlive).ToList();
                        for (int h = 0; h < hits && alive.Count > 0; h++)
                        {
                            var pick = alive[_rng.Next(alive.Count)];
                            list.Add(BattleTarget.FromEnemy(pick));
                        }
                    }
                    break;
            }

            return list;
        }

        void ResolveEffect(Character user, CardEffect fx, CardModel card, BattleTarget target)
        {
            var ctx = user.ToStatContext();
            if (user.IsKael)
            {
                ctx.ATK *= user.GetMoraleAtkMultiplier();
            }

            float value = fx.EvaluateValue(ctx);
            var element = card.ElementType;

            switch (fx.EffectType)
            {
                case EffectType.Danno:
                    for (int h = 0; h < Math.Max(1, fx.HitCount); h++)
                        ApplyDamage(user, target, value, element, physical: IsPhysicalDominant(fx, card));
                    break;
                case EffectType.Cura:
                    ApplyHeal(target, (int)value, element);
                    break;
                case EffectType.Buff:
                case EffectType.Debuff:
                case EffectType.Stato:
                    if (fx.StatusEffect.HasValue && _rng.NextDouble() <= fx.StatusChance)
                        ApplyStatus(target, fx.StatusEffect.Value, fx.StatusDuration, fx.StatusIntensity, card.Id);
                    break;
                case EffectType.Scudo:
                case EffectType.DrawCard:
                case EffectType.Evoca:
                    Log($"Effetto {fx.EffectType} (placeholder) da {card.Name}");
                    break;
            }

            if (CheckBattleEnd())
                return;

            TryTriggerReactions(user, target, fx);
        }

        static bool IsPhysicalDominant(CardEffect fx, CardModel card)
        {
            if (fx.ScalingFormula.Contains("MAG", StringComparison.OrdinalIgnoreCase))
                return false;
            if (fx.ScalingFormula.Contains("ATK", StringComparison.OrdinalIgnoreCase))
                return true;
            return card.ElementType is ElementType.Neutro or ElementType.Terra;
        }

        void ApplyDamage(Character attacker, BattleTarget target, float raw, ElementType element, bool physical)
        {
            if (!target.IsAlive) return;

            int atkStat = physical ? attacker.ATK : attacker.MAG;
            float scaled = raw;
            if (scaled <= 0)
                scaled = atkStat * 1.1f;

            float defHalf = physical
                ? GetDef(target) * 0.5f
                : GetRes(target) * 0.5f;

            if (target.IsAlly && _defending.Contains(target.Ally!.CharacterId))
                defHalf *= 2f;

            float baseDmg = Math.Max(1, scaled - defHalf);
            float elem = target.GetResistance(element);
            if (elem <= 0) elem = 0.01f;

            float statusMul = GetStatusDamageMultiplier(target);
            float final = baseDmg * elem * statusMul;

            float critChance = Math.Min(0.4f, attacker.LUK / 200f);
            bool crit = _rng.NextDouble() < critChance;
            if (crit) final *= 1.5f;

            final *= 1f + (float)(_rng.NextDouble() * 0.1 - 0.05);

            int amount = Math.Max(1, (int)final);
            target.CurrentHP -= amount;
            if (!target.IsAlive)
                RebuildQueueAfterDeath();

            DamageResolved?.Invoke(this, new DamageResolvedEventArgs
            {
                Target      = target,
                Amount      = amount,
                WasCritical = crit,
                Element     = element,
                WasHealing  = false
            });

            Log($"{attacker.DisplayName} → {target.DisplayName}: {amount}{(crit ? " CRIT!" : "")}");
        }

        static float GetStatusDamageMultiplier(BattleTarget t)
        {
            if (t.IsAlly)
            {
                if (t.Ally!.ActiveStatusEffects.Any(s => s.Type == StatusEffectType.Potenziato && s.TurnsLeft > 0))
                    return 1.1f;
                if (t.Ally.ActiveStatusEffects.Any(s => s.Type == StatusEffectType.Depresso && s.TurnsLeft > 0))
                    return 0.9f;
            }
            return 1f;
        }

        int GetDef(BattleTarget t)
            => t.IsAlly ? t.Ally!.DEF : t.Foe!.Template.DEF;

        int GetRes(BattleTarget t)
            => t.IsAlly ? t.Ally!.RES : t.Foe!.Template.RES;

        void ApplyHeal(BattleTarget target, int amount, ElementType _)
        {
            if (!target.IsAlive) return;
            int max = target.MaxHP;
            int nh = Math.Min(max, target.CurrentHP + Math.Max(1, amount));
            int healed = nh - target.CurrentHP;
            target.CurrentHP = nh;

            DamageResolved?.Invoke(this, new DamageResolvedEventArgs
            {
                Target      = target,
                Amount      = healed,
                WasCritical = false,
                Element     = ElementType.Neutro,
                WasHealing  = true
            });
            Log($"Cura su {target.DisplayName}: +{healed}");
        }

        void ApplyStatus(BattleTarget target, StatusEffectType type, int duration, float intensity, string sourceId)
        {
            var se = new StatusEffect
            {
                Type          = type,
                TurnsLeft     = Math.Max(1, duration),
                Intensity     = intensity,
                SourceCardId  = sourceId
            };
            if (target.IsAlly)
                target.Ally!.ActiveStatusEffects.Add(se);
            else
                target.Foe!.ActiveStatusEffects.Add(se);
            Log($"{target.DisplayName}: stato {type}");
        }

        void TryTriggerReactions(Character attacker, BattleTarget victim, CardEffect fx)
        {
            // Hook per carte Reazione (es. Voce di Edric): estensione futura con CardDatabase
        }

        // ---------------------------------------------------------------------
        //  Difesa / oggetti / fuga
        // ---------------------------------------------------------------------

        public void Defend(Character user)
        {
            if (ActiveActor?.Ally != user || State != BattleState.PlayerTurn)
                return;

            _defending.Add(user.CharacterId);
            user.CurrentSP = Math.Min(user.MaxSP, user.CurrentSP + 1);
            Log($"{user.DisplayName} si difende.");
            State = BattleState.AnimatingAction;
            RaiseState();
        }

        public bool TryUseItem(string itemId, Character user, BattleTarget? target)
        {
            Log($"Oggetto {itemId} (inventario non collegato — placeholder).");
            State = BattleState.AnimatingAction;
            RaiseState();
            return true;
        }

        public bool TryFlee(Character initiator)
        {
            if (State != BattleState.PlayerTurn || ActiveActor?.Ally != initiator)
                return false;

            bool ok = _rng.NextDouble() < 0.5;
            if (initiator.IsKael)
                initiator.ApplyMoraleDelta(-10);

            if (ok)
            {
                State = BattleState.Fleeing;
                RaiseState();
                EndBattle(BattleState.Fleeing, fled: true);
                return true;
            }

            Log("Fuga fallita.");
            State = BattleState.AnimatingAction;
            RaiseState();
            return false;
        }

        // ---------------------------------------------------------------------
        //  Nemico — AI semplice
        // ---------------------------------------------------------------------

        void ExecuteEnemyTurn(EnemyInstance foe)
        {
            var viable = foe.Template.Actions
                .Where(a => EvaluateEnemyCondition(foe, a.Condition))
                .ToList();

            if (viable.Count == 0)
                viable = foe.Template.Actions;

            var pick = WeightedPick(viable, a => a.Weight);
            Log($"{foe.Template.Name} usa {pick.DisplayName}.");

            bool magical = pick.EffectTags.Any(t => t.Contains("magic", StringComparison.OrdinalIgnoreCase));
            var target = BattleTarget.FromAlly(_party.FirstOrDefault(c => c.CurrentHP > 0) ?? _party[0]);

            float raw = magical ? foe.Template.MAG * 1.2f : foe.Template.ATK * 1.1f;
            ApplyDamageFromEnemy(foe, target, raw, magical);

            State = BattleState.AnimatingAction;
            RaiseState();

            AdvanceTurnIndex();
            BeginNextActorTurn();
        }

        bool EvaluateEnemyCondition(EnemyInstance foe, string condition)
        {
            string c = condition.Trim().ToLowerInvariant();
            if (c is "always" or "") return true;
            float hpPct = foe.Template.MaxHP <= 0 ? 0 : (float)foe.CurrentHP / foe.Template.MaxHP;

            if (c.StartsWith("hp_percent <", StringComparison.Ordinal))
            {
                if (float.TryParse(c.Replace("hp_percent <", "").Trim(), out var thr))
                    return hpPct < thr / 100f || hpPct < thr;
            }

            if (c.Contains("< 50")) return hpPct < 0.5f;
            if (c.Contains("< 60")) return hpPct < 0.6f;
            if (c.Contains("< 70")) return hpPct < 0.7f;
            if (c.Contains("< 55")) return hpPct < 0.55f;
            if (c.Contains("< 45")) return hpPct < 0.45f;
            if (c.Contains("< 40")) return hpPct < 0.4f;
            if (c.Contains("< 75")) return hpPct < 0.75f;

            return true;
        }

        T WeightedPick<T>(IReadOnlyList<T> items, Func<T, float> weight)
        {
            float sum = items.Sum(weight);
            float r = (float)_rng.NextDouble() * sum;
            foreach (var x in items)
            {
                r -= weight(x);
                if (r <= 0) return x;
            }
            return items[^1];
        }

        void ApplyDamageFromEnemy(EnemyInstance foe, BattleTarget target, float raw, bool magical)
        {
            float def = magical ? GetRes(target) * 0.5f : GetDef(target) * 0.5f;
            if (target.IsAlly && _defending.Contains(target.Ally!.CharacterId))
                def *= 2f;

            float dmg = Math.Max(1, raw - def);
            dmg *= 1f + (float)(_rng.NextDouble() * 0.1 - 0.05);
            int amt = Math.Max(1, (int)dmg);
            target.CurrentHP -= amt;
            if (!target.IsAlive)
                RebuildQueueAfterDeath();

            DamageResolved?.Invoke(this, new DamageResolvedEventArgs
            {
                Target      = target,
                Amount      = amt,
                WasCritical = false,
                Element     = ElementType.Neutro,
                WasHealing  = false
            });
            Log($"{foe.Template.Name} colpisce {target.DisplayName} per {amt}.");
        }

        // ---------------------------------------------------------------------
        //  Fine battaglia
        // ---------------------------------------------------------------------

        bool CheckBattleEnd()
        {
            if (!_enemies.Any(e => e.IsAlive))
            {
                EndBattle(BattleState.Victory, fled: false);
                return true;
            }

            if (!_party.Any(c => c.CurrentHP > 0))
            {
                EndBattle(BattleState.Defeat, fled: false);
                return true;
            }

            return false;
        }

        void EndBattle(BattleState outcome, bool fled)
        {
            _battleOver = true;
            State = outcome;
            ActiveActor = null;

            int exp = 0, gold = 0;
            if (outcome == BattleState.Victory)
            {
                exp  = _enemies.Sum(e => e.Template.ExpReward);
                gold = _enemies.Sum(e => e.Template.GoldReward);
            }

            RaiseState();
            BattleEnded?.Invoke(this, new BattleEndedEventArgs
            {
                Outcome   = outcome,
                ExpGained = exp,
                GoldGained = gold,
                Fled      = fled
            });
        }

        void Log(string msg)
        {
            BattleLog?.Invoke(this, msg);
        }

        void RaiseState() => StateChanged?.Invoke(this, State);

        /// <summary>
        /// Dopo animazione turno giocatore: avanza al prossimo attore.
        /// </summary>
        public void EndPlayerAnimationPhase()
        {
            if (_battleOver) return;

            if (State == BattleState.AnimatingAction && ActiveActor?.IsAlly == true)
            {
                AdvanceTurnIndex();
                BeginNextActorTurn();
            }
        }

        void RebuildQueueAfterDeath()
        {
            if (_battleOver) return;
            int before = _turnIndexInRound;
            RebuildTurnQueue();
            if (_turnQueue.Count == 0) return;
            if (before >= _turnQueue.Count)
                _turnIndexInRound = 0;
        }
    }
}
