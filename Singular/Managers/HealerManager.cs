﻿
using System.Collections.Generic;
using System.Linq;

using Singular.Settings;
using Singular.Helpers;

using Styx;
using Styx.Common.Helpers;
using Styx.CommonBot;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using Styx.TreeSharp;
using System;
using System.Drawing;
using CommonBehaviors.Actions;

using Action = Styx.TreeSharp.Action;

namespace Singular.Managers
{
    /*
     * Targeting works like so, in order of being called
     * 
     * GetInitialObjectList - Return a list of initial objects for the targeting to use.
     * RemoveTargetsFilter - Remove anything that doesn't belong in the list.
     * IncludeTargetsFilter - If you want to include units regardless of the remove filter
     * WeighTargetsFilter - Weigh each target in the list.     
     *
     */

    internal class HealerManager : HealTargeting
    {
        private static LocalPlayer Me { get { return StyxWoW.Me; } }

        private static readonly WaitTimer _tankReset = WaitTimer.ThirtySeconds;

        // private static ulong _tankGuid;

        static HealerManager()
        {
            // Make sure we have a singleton instance!
            HealTargeting.Instance = Instance = new HealerManager();
        }

        public new static HealerManager Instance { get; private set; }

        // following property is set by BT implementations for spec + context
        // .. and controls whether we should include Healing support
        public static bool NeedHealTargeting { get; set; }

        protected override List<WoWObject> GetInitialObjectList()
        {
            List<WoWObject> heallist;
            // Targeting requires a list of WoWObjects - so it's not bound to any specific type of object. Just casting it down to WoWObject will work fine.
            // return ObjectManager.ObjectList.Where(o => o is WoWPlayer).ToList();
            if (Me.GroupInfo.IsInRaid)
                heallist = ObjectManager.ObjectList
                    .Where(o => ((o is WoWPlayer && o.ToPlayer().IsInMyRaid)))
                    .ToList();
            else if (Me.GroupInfo.IsInParty)
                heallist = ObjectManager.ObjectList
                    .Where(o => ((o is WoWPlayer && o.ToPlayer().IsInMyRaid) || (SingularSettings.Instance.IncludeCompanionssAsHealTargets && o is WoWUnit && o.ToUnit().SummonedByUnitGuid == Me.Guid && !o.ToUnit().IsPet)))
                    .ToList();
            else
                heallist = ObjectManager.ObjectList
                    .Where(o => (SingularSettings.Instance.IncludeCompanionssAsHealTargets && o is WoWUnit && o.ToUnit().SummonedByUnitGuid == Me.Guid && !o.ToUnit().IsPet))
                    .ToList();

            return heallist;
        }

        private static ulong lastCompanion = 0;
        private static ulong lastFocus = 0;

        protected override void DefaultIncludeTargetsFilter(List<WoWObject> incomingUnits, HashSet<WoWObject> outgoingUnits)
        {
            bool foundMe = false;
            bool isHorde = StyxWoW.Me.IsHorde;
            ulong focusGuid = Me.FocusedUnitGuid;
            bool foundFocus = false;

            foreach (WoWObject incomingUnit in incomingUnits)
            {
                try
                {
                    if (incomingUnit.IsMe)
                        foundMe = true;
                    else if (incomingUnit.Guid == focusGuid)
                        foundFocus = true;
                    else if (SingularSettings.Debug && incomingUnit.Guid != lastCompanion && SingularSettings.Instance.IncludeCompanionssAsHealTargets && incomingUnit is WoWUnit && incomingUnit.ToUnit().SummonedByUnitGuid == Me.Guid)
                    {
                        // temporary code only used to verify a companion found!
                        lastCompanion = incomingUnit.Guid;
                        Logger.WriteDebug( Color.White, "HealTargets: including found companion {0}#{1}", incomingUnit.Name, incomingUnit.Entry);
                    }

                    outgoingUnits.Add(incomingUnit);

                    var player = incomingUnit as WoWPlayer;
                    if (SingularSettings.Instance.IncludePetsAsHealTargets && player != null && player.GotAlivePet)
                        outgoingUnits.Add(player.Pet);
                }
                catch (System.AccessViolationException)
                {
                }
                catch (Styx.InvalidObjectPointerException)
                {
                }
            }

            if (!foundMe)
            {
                outgoingUnits.Add(Me);
                if (SingularSettings.Instance.IncludePetsAsHealTargets && Me.GotAlivePet)
                    outgoingUnits.Add(Me.Pet);
            }

            /*
            if (StyxWoW.Me.GotTarget && StyxWoW.Me.CurrentTarget.IsFriendly && !StyxWoW.Me.CurrentTarget.IsPlayer)
                outgoingUnits.Add(StyxWoW.Me.CurrentTarget);
            */
            try
            {
                if (Me.FocusedUnit != null && Me.FocusedUnit.IsFriendly )
                {
                    if (!foundFocus)
                    {
                        outgoingUnits.Add(StyxWoW.Me.FocusedUnit);
                        if (Me.FocusedUnit.GotAlivePet)
                            outgoingUnits.Add(Me.FocusedUnit.Pet);
                    }

                    if (SingularSettings.Debug && Me.FocusedUnit.Guid != lastFocus)
                    {
                        lastFocus = Me.FocusedUnit.Guid;
                        Logger.WriteDebug(Color.White, "HealTargets: including focused unit {0}#{1}", Me.FocusedUnit.Name, Me.FocusedUnit.Entry);
                    }
                }
            }
            catch
            {
            }
        }

        protected override void DefaultRemoveTargetsFilter(List<WoWObject> units)
        {
            bool isHorde = StyxWoW.Me.IsHorde;
            int maxHealRangeSqr;

            if (MovementManager.IsMovementDisabled)
                maxHealRangeSqr = 40 * 40;
            else
                maxHealRangeSqr = SingularSettings.Instance.MaxHealTargetRange * SingularSettings.Instance.MaxHealTargetRange;

            WoWPoint myLoc = Me.Location;

            for (int i = units.Count - 1; i >= 0; i--)
            {
                WoWUnit unit = units[i].ToUnit();
                try
                {
                    if (unit == null || !unit.IsValid || unit.IsDead || unit.HealthPercent <= 0 || !unit.IsFriendly)
                    {
                        units.RemoveAt(i);
                        continue;
                    }

                    WoWPlayer p = unit as WoWPlayer;
                    if (p == null && unit.IsPet)
                    {
                        var ownedByRoot = unit.OwnedByRoot;
                        if (ownedByRoot != null && ownedByRoot.IsPlayer)
                            p = unit.OwnedByRoot.ToPlayer();
                    }

                    if (p != null)
                    {
                        // Make sure we ignore dead/ghost players. If we need res logic, they need to be in the class-specific area.
                        if (p.IsGhost)
                        {
                            units.RemoveAt(i);
                            continue;
                        }

                        // They're not in our party/raid. So ignore them. We can't heal them anyway.
                        /*
                        if (!p.IsInMyPartyOrRaid)
                        {
                            units.RemoveAt(i);
                            continue;
                        }
                        */
                        /*
                                            if (!p.Combat && p.HealthPercent >= SingularSettings.Instance.IgnoreHealTargetsAboveHealth)
                                            {
                                                units.RemoveAt(i);
                                                continue;
                                            }
                         */
                    }

                    // If we have movement turned off, ignore people who aren't in range.
                    // Almost all healing is 40 yards, so we'll use that. If in Battlegrounds use a slightly larger value to expane our 
                    // healing range, but not too large that we are running all over the bg zone 
                    // note: reordered following tests so only one floating point distance comparison done due to evalution of DisableAllMovement
                    if (unit.Location.DistanceSqr(myLoc) > maxHealRangeSqr)
                    {
                        units.RemoveAt(i);
                        continue;
                    }
                }
                catch (System.AccessViolationException)
                {
                    units.RemoveAt(i);
                    continue;
                }
                catch (Styx.InvalidObjectPointerException)
                {
                    units.RemoveAt(i);
                    continue;
                }
            }
        }

        protected override void DefaultTargetWeight(List<TargetPriority> units)
        {
            var tanks = GetMainTankGuids();
            var inBg = Battlegrounds.IsInsideBattleground;
            var amHolyPally = StyxWoW.Me.Specialization == WoWSpec.PaladinHoly;
            var myLoc = Me.Location;

            foreach (TargetPriority prio in units)
            {
                WoWUnit u = prio.Object.ToUnit();
                if (u == null || !u.IsValid)
                {
                    prio.Score = -9999f;
                    continue;
                }

                // The more health they have, the lower the score.
                // This should give -500 for units at 100%
                // And -50 for units at 10%
                try
                {
                    prio.Score = u.IsAlive ? 500f : -500f;
                    prio.Score -= u.HealthPercent * 5;

                    // If they're out of range, give them a bit lower score.
                    if (u.Location.DistanceSqr(myLoc) > 40 * 40)
                    {
                        prio.Score -= 50f;
                    }

                    // If they're out of LOS, again, lower score!
                    if (!u.InLineOfSpellSight)
                    {
                        prio.Score -= 100f;
                    }

                    // Give tanks more weight. If the tank dies, we all die. KEEP HIM UP.
                    if (tanks.Contains(u.Guid) && u.HealthPercent != 100 &&
                        // Ignore giving more weight to the tank if we have Beacon of Light on it.
                        (!amHolyPally || !u.Auras.Any(a => a.Key == "Beacon of Light" && a.Value.CreatorGuid == StyxWoW.Me.Guid)))
                    {
                        prio.Score += 100f;
                    }

                    // Give flag carriers more weight in battlegrounds. We need to keep them alive!
                    if (inBg && u.IsPlayer && u.Auras.Keys.Any(a => a.ToLowerInvariant().Contains("flag")))
                    {
                        prio.Score += 100f;
                    }
                }
                catch (System.AccessViolationException)
                {
                    prio.Score = -9999f;
                    continue;
                }
                catch (Styx.InvalidObjectPointerException)
                {
                    prio.Score = -9999f;
                    continue;
                }
            }
        }

        public override void Pulse()
        {
            if (NeedHealTargeting)
                base.Pulse();
        }

        public static HashSet<ulong> GetMainTankGuids()
        {
            var infos = StyxWoW.Me.GroupInfo.RaidMembers;

            return new HashSet<ulong>(
                from pi in infos
                where (pi.Role & WoWPartyMember.GroupRole.Tank) != 0
                select pi.Guid);
        }

        /// <summary>
        /// finds the lowest health target in HealerManager.  HealerManager updates the list over multiple pulses, resulting in 
        /// the .FirstUnit entry often being at higher health than later entries.  This method dynamically searches the current
        /// list and returns the lowest at this moment.
        /// </summary>
        /// <returns></returns>
        public static WoWUnit FindLowestHealthTarget()
        {
#if LOWEST_IS_FIRSTUNIT
            return HealerManager.Instance.FirstUnit;
#else
            double minHealth = 999;
            WoWUnit minUnit = null;

            // iterate the list so we make a single pass through it
            foreach (WoWUnit unit in HealerManager.Instance.TargetList)
            {
                try
                {
                    if (unit.HealthPercent < minHealth)
                    {
                        minHealth = unit.HealthPercent;
                        minUnit = unit;
                    }
                }
                catch
                {
                    // simply eat the exception here
                }
            }

            return minUnit;
#endif
        }

        /// <summary>
        /// check if Healer should be permitted to do straight DPS abilities (with purpose to damage and not indirect heal, buff, mana return, etc.)
        /// </summary>
        /// <returns></returns>
        public static bool AllowHealerDPS()
        {
            WoWContext ctx = SingularRoutine.CurrentWoWContext;
            if (ctx == WoWContext.Normal)
                return true;

            if (Me.GroupInfo.IsInRaid)
                return false;

            double rangeCheck = SingularSettings.Instance.MaxHealTargetRange * SingularSettings.Instance.MaxHealTargetRange;
            if (!SingularSettings.Instance.HealerCombatAllow && Unit.GroupMembers.Any(m => m.IsAlive && !m.IsMe && m.Distance2DSqr < rangeCheck))
                return false;

            if (Me.ManaPercent < SingularSettings.Instance.HealerCombatMinMana)
                return false;

            if (HealerManager.Instance.FirstUnit != null && HealerManager.Instance.FirstUnit.HealthPercent < SingularSettings.Instance.HealerCombatMinHealth)
                return false;

            return true;
        }

        /// <summary>
        /// check whether a healer DPS cast in progress should be cancelled
        /// </summary>
        /// <returns></returns>
        public static bool CancelHealerDPS()
        {
            // allow combat setting is false, so cast originated by some other logic, so allow it to finish
            if (!SingularSettings.Instance.HealerCombatAllow)
                return false;

            // always let DPS casts while solo complete
            WoWContext ctx = SingularRoutine.CurrentWoWContext;
            if (ctx == WoWContext.Normal)
                return false;

            // allow casts that are close to finishing to finish regardless
            if (Spell.IsCastingOrChannelling() && Me.CurrentCastTimeLeft.TotalMilliseconds < 333)
            {
                Logger.WriteDebug("CancelHealerDPS: suppressing /cancel since less than 333 ms remaining");
                return false;
            }

            // use a window less than actual to avoid cast/cancel/cast/cancel due to mana hovering at setting level
            if (Me.ManaPercent < (SingularSettings.Instance.HealerCombatMinMana - 3))
            {
                Logger.WriteDebug("CancelHealerDPS: /cancel because Mana={0:F1}% fell below Min={1}%", Me.ManaPercent, SingularSettings.Instance.HealerCombatMinMana);
                return true;
            }

            // check if group health has dropped below setting
            if (HealerManager.Instance.FirstUnit != null && HealerManager.Instance.FirstUnit.HealthPercent < SingularSettings.Instance.HealerCombatMinHealth)
            {
                Logger.WriteDebug("CancelHealerDPS: /cancel because {0} @ {1:F1}% health fell below Min={2}%", HealerManager.Instance.FirstUnit.SafeName(), HealerManager.Instance.FirstUnit.HealthPercent, SingularSettings.Instance.HealerCombatMinHealth);
                return true;
            }

            return false;
        }

        public static WoWUnit GetBestCoverageTarget(string spell, int health, int range, int radius, int minCount, SimpleBooleanDelegate requirements = null, IEnumerable<WoWUnit> mainTarget = null)
        {
            if (!Me.IsInGroup() || !Me.Combat)
                return null;

            if (!Spell.CanCastHack(spell, Me, skipWowCheck: true))
            {
                if (!SingularSettings.DebugSpellCanCast)
                    Logger.WriteDebug("GetBestCoverageTarget: CanCastHack says NO to [{0}]", spell);
                return null;
            }

            if (requirements == null)
                requirements = req => true;

            // build temp list of targets that could use heal and are in range + radius
            List<WoWUnit> coveredTargets = HealerManager.Instance.TargetList
                .Where(u => u.IsAlive && u.SpellDistance() < (range + radius) && u.HealthPercent < health && requirements(u))
                .ToList();


            // create a iEnumerable of the possible heal targets wtihin range
            IEnumerable<WoWUnit> listOf;
            if (range == 0)
                listOf = new List<WoWUnit>() { Me };
            else if (mainTarget == null)
                listOf = HealerManager.Instance.TargetList.Where(p => p.IsAlive && p.SpellDistance() <= range);
            else
                listOf = mainTarget;

            // now search list finding target with greatest number of heal targets in radius
            var t = listOf
                .Select(p => new
                {
                    Player = p,
                    Count = coveredTargets
                        .Where(pp => pp.IsAlive && pp.SpellDistance(p) < radius)
                        .Count()
                })
                .OrderByDescending(v => v.Count)
                .DefaultIfEmpty(null)
                .FirstOrDefault();

            if (t != null)
            {
                if (t.Count >= minCount)
                {
                    Logger.WriteDebug("GetBestCoverageTarget('{0}'): found {1} with {2} nearby under {3}%", spell, t.Player.SafeName(), t.Count, health);
                    return t.Player;
                }

                if (SingularSettings.DebugSpellCanCast)
                {
                    Logger.WriteDebug("GetBestCoverageTarget('{0}'): not enough found - {1} with {2} nearby under {3}%", spell, t.Player.SafeName(), t.Count, health);
                }
            }

            return null;
        }

        /// <summary>
        /// find best Tank target that is missing Heal Over Time passed
        /// </summary>
        /// <param name="hotName">spell name of HoT</param>
        /// <returns>reference to target that needs the HoT</returns>
        public static WoWUnit GetBestTankTargetForHOT(string hotName, float health = 100f)
        {
            WoWUnit hotTarget = null;
            hotTarget = Group.Tanks.Where(u => u.IsAlive && u.Combat && u.HealthPercent < health && u.DistanceSqr < 40 * 40 && !u.HasMyAura(hotName) && u.InLineOfSpellSight).OrderBy(u => u.HealthPercent).FirstOrDefault();
            if (hotTarget != null)
                Logger.WriteDebug("GetBestTankTargetForHOT('{0}'): found tank {1} @ {2:F1}%, hasmyaura={3} with {4} ms left", hotName, hotTarget.SafeName(), hotTarget.HealthPercent, hotTarget.HasMyAura(hotName), (int)hotTarget.GetAuraTimeLeft("Riptide").TotalMilliseconds);
            return hotTarget;
        }

        /// <summary>
        /// selects the Tank we should stay near.  Priority is RaFHelper.Leader, then First Role.Tank either
        /// in combat or 
        /// </summary>
        public static WoWUnit TankToStayNear
        {
            get
            {
                if (!SingularSettings.Instance.StayNearTank)
                    return null;

                if (RaFHelper.Leader != null && RaFHelper.Leader.IsValid && RaFHelper.Leader.IsAlive && (RaFHelper.Leader.Combat || RaFHelper.Leader.Distance < SingularSettings.Instance.MaxHealTargetRange))
                    return RaFHelper.Leader;

                return Group.Tanks.Where(t => t.IsAlive && (t.Combat || t.Distance < SingularSettings.Instance.MaxHealTargetRange)).OrderBy(t => t.Distance).FirstOrDefault();
            }
        }

        private static int moveNearTank { get; set; }
        private static int stopNearTank { get; set; }

        public static Composite CreateStayNearTankBehavior()
        {
            if (!SingularSettings.Instance.StayNearTank)
                return new ActionAlwaysFail();

            if (SingularRoutine.CurrentWoWContext != WoWContext.Instances)
                return new ActionAlwaysFail();

            moveNearTank = Math.Max(5, SingularSettings.Instance.StayNearTankRange);
            stopNearTank = Math.Max( moveNearTank / 2, moveNearTank - 5);

            return new PrioritySelector(
                ctx => HealerManager.TankToStayNear,
                // no healing needed, then move within heal range of tank
                new Decorator(
                    ret => ((WoWUnit)ret) != null,
                    new Sequence(
                        new PrioritySelector(
                            Movement.CreateMoveToLosBehavior(unit => ((WoWUnit)unit)),
                            Movement.CreateMoveToUnitBehavior(unit => ((WoWUnit)unit), moveNearTank, stopNearTank),
                            Movement.CreateEnsureMovementStoppedBehavior(stopNearTank, unit => (WoWUnit)unit, "in heal range of tank")
                            ),
                        new ActionAlwaysFail()
                        )
                    )
                );
        }

        public static Composite CreateAttackEnsureTarget()
        {
            if (SingularSettings.DisableAllTargeting || SingularRoutine.CurrentWoWContext != WoWContext.Instances)
                return new ActionAlwaysFail();

            return new PrioritySelector(
                new Decorator(
                    req => Me.GotTarget && !Me.CurrentTarget.IsPlayer,
                    new PrioritySelector(
                        ctx => Unit.HighestHealthMobAttackingTank(),
                        new Decorator(
                            req => req != null && Me.CurrentTargetGuid != ((WoWUnit)req).Guid && (Me.CurrentTarget.HealthPercent + 10) < ((WoWUnit)req).HealthPercent,
                            new Sequence(
                                new Action(on =>
                                {
                                    Logger.Write(Color.LightCoral, "switch to highest health mob {0} @ {1:F1}%", ((WoWUnit)on).SafeName(), ((WoWUnit)on).HealthPercent);
                                    ((WoWUnit)on).Target();
                                }),
                                new Wait(1, req => Me.CurrentTargetGuid == ((WoWUnit)req).Guid, new ActionAlwaysFail())
                                )
                            )
                        )
                    ),
                new Decorator(
                    req => !Me.GotTarget,
                    new Sequence(
                        ctx => Unit.HighestHealthMobAttackingTank(),
                        new Action(on =>
                        {
                            Logger.Write(Color.LightCoral, "target highest health mob {0} @ {1:F1}%", ((WoWUnit)on).SafeName(), ((WoWUnit)on).HealthPercent);
                            ((WoWUnit)on).Target();
                        }),
                        new Wait(1, req => Me.CurrentTargetGuid == ((WoWUnit)req).Guid, new ActionAlwaysFail())
                        )
                    )
                );
        }


        #region Off-heal Checks and Control

        private static bool EnableOffHeal
        {
            get
            {
                if (!SingularSettings.Instance.DpsOffHealAllowed)
                    return false;

                if (Me.GroupInfo.IsInRaid)
                    return false;

                WoWUnit first = HealerManager.Instance.FirstUnit;
                if (first != null)
                {
                    double health = first.GetPredictedHealthPercent(includeMyHeals: true);
                    if (health < SingularSettings.Instance.DpsOffHealBeginPct)
                    {
                        Logger.WriteDiagnostic("EnableOffHeal: entering off-heal mode since {0} @ {1:F1}%", first.SafeName(), health);
                        return true;
                    }
                }
                
                return false;
            }
        }

        private static bool DisableOffHeal
        {
            get
            {
                if (!SingularSettings.Instance.DpsOffHealAllowed)
                    return true;

                if (Me.GroupInfo.IsInRaid)
                    return true;

                WoWUnit healer = null;
                if (SingularRoutine.CurrentWoWContext != WoWContext.Normal)
                {
                    healer = Group.Healers.FirstOrDefault(h => h.IsAlive && h.Distance < SingularSettings.Instance.MaxHealTargetRange);
                    if (healer == null)
                        return false;
                }

                WoWUnit lowest = FindLowestHealthTarget();
                if (lowest != null && lowest.HealthPercent <= SingularSettings.Instance.DpsOffHealEndPct)
                    return false;

                if (SingularRoutine.CurrentWoWContext == WoWContext.Normal)
                    Logger.WriteDiagnostic("DisableOffHeal: leaving off-heal mode since lowest target is {0} @ {1:F1}% and solo", lowest.SafeName(), lowest.HealthPercent);
                else 
                    Logger.WriteDiagnostic("DisableOffHeal: leaving off-heal mode since lowest target is {0} @ {1:F1}% and {2} is {3:F1} yds away", lowest.SafeName(), lowest.HealthPercent, healer.SafeName(), healer.Distance);
                return true;
            }
        }

        private static bool _actingHealer = false;

        public static bool ActingAsOffHealer
        {
            get
            {
                if (!_actingHealer && EnableOffHeal)
                {
                    _actingHealer = true;
                    Logger.WriteDiagnostic("ActingAsOffHealer: offheal enabled");
                }
                else if (_actingHealer && DisableOffHeal)
                {
                    _actingHealer = false;
                    Logger.WriteDiagnostic("ActingAsOffHealer: offheal disabled");
                }
                return _actingHealer;
            }
        }

        #endregion  
    }

    class PrioritizedBehaviorList
    {
        class PrioritizedBehavior
        {
            public int Priority { get; set; }
            public string Name { get; set; }
            public Composite behavior { get; set; }

            public PrioritizedBehavior(int p, string s, Composite bt)
            {
                Priority = p;
                Name = s;
                behavior = bt;
            }
        }

        List<PrioritizedBehavior> blist = new List<PrioritizedBehavior>();

        public void AddBehavior(int pri, string behavName, string spellName, Composite bt)
        {
            if (pri == 0)
                Logger.WriteDebug("Skipping Behavior [{0}] configured for Priority {1}", behavName, pri);
            else if (!String.IsNullOrEmpty(spellName) && !SpellManager.HasSpell(spellName))
                Logger.WriteDebug("Skipping Behavior [{0}] since spell '{1}' is not known by this character", behavName, spellName);
            else
                blist.Add(new PrioritizedBehavior(pri, behavName, bt));
        }

        public void OrderBehaviors()
        {
            blist = blist.OrderByDescending(b => b.Priority).ToList();
        }

        public Composite GenerateBehaviorTree()
        {
            if (!SingularSettings.Debug)
                return new PrioritySelector(blist.Select(b => b.behavior).ToArray());

            PrioritySelector pri = new PrioritySelector();
            foreach (PrioritizedBehavior pb in blist)
            {
                pri.AddChild(new CallTrace(pb.Name, pb.behavior));
            }

            return pri;
        }

        public void ListBehaviors()
        {
            if (Dynamics.CompositeBuilder.SilentBehaviorCreation)
                return;

            foreach (PrioritizedBehavior hs in blist)
            {
                Logger.WriteDebug(Color.GreenYellow, "   Priority {0} for Behavior [{1}]", hs.Priority.ToString().AlignRight(4), hs.Name);
            }
        }
    }

}