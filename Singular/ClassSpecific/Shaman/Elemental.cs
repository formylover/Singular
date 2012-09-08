﻿using System.Linq;
using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Singular.Settings;

using Styx;
using Styx.Combat.CombatRoutine;
using Styx.CommonBot;
using Styx.Helpers;

using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

using Styx.TreeSharp;
using Rest = Singular.Helpers.Rest;

namespace Singular.ClassSpecific.Shaman
{
    public class Elemental
    {
        #region Common

        [Behavior(BehaviorType.PreCombatBuffs, WoWClass.Shaman, WoWSpec.ShamanElemental)]
        public static Composite CreateShamanElementalPreCombatBuffs()
        {
            return new PrioritySelector(
                new Decorator(
                    ret => StyxWoW.Me.Inventory.Equipped.MainHand.TemporaryEnchantment.Id != 283 && StyxWoW.Me.Inventory.Equipped.MainHand.ItemInfo.WeaponClass != WoWItemWeaponClass.FishingPole && SpellManager.HasSpell("Flametongue Weapon") &&
                            SpellManager.CanCast("Flametongue Weapon", null, false, false),
                    new Sequence(
                        new Action(ret => Lua.DoString("CancelItemTempEnchantment(1)")),
                        new Action(ret => Logger.Write("Imbuing main hand weapon with Flametongue")),
                        new Action(ret => SpellManager.Cast("Flametongue Weapon", null))
                        )),

                Spell.BuffSelf("Lightning Shield"),
                new Decorator(ret => Totems.NeedToRecallTotems,
                    new Action(ret => Totems.RecallTotems()))
                );
        }

        [Behavior(BehaviorType.Rest, WoWClass.Shaman, WoWSpec.ShamanElemental)]
        public static Composite CreateShamanElementalRest()
        {
            return
                new PrioritySelector(
                    new Decorator(
                        ret => !StyxWoW.Me.HasAura("Drink") && !StyxWoW.Me.HasAura("Food"),
                        CreateShamanElementalHeal()),
                    Rest.CreateDefaultRestBehaviour(),
                    Spell.Resurrect("Ancestral Spirit")
                    );
        }

        [Behavior(BehaviorType.Heal, WoWClass.Shaman, WoWSpec.ShamanElemental, WoWContext.Normal | WoWContext.Instances)]
        public static Composite CreateShamanElementalHeal()
        {
            return
                new Decorator(
                    ret => SingularSettings.Instance.Shaman.EnhancementHeal,
                    new PrioritySelector(
                        // Heal the party in dungeons if the healer is dead
                        new Decorator(
                            ret => StyxWoW.Me.CurrentMap.IsDungeon && !StyxWoW.Me.IsInRaid &&
                                   Group.Healers.Count(h => h.IsAlive) == 0,
                            Restoration.CreateRestoShamanHealingOnlyBehavior()),

                        // This will work for both solo play and battlegrounds
                        new Decorator(
                            ret => Group.Healers.Any() && Group.Healers.Count(h => h.IsAlive) == 0 && !StyxWoW.Me.IsInRaid ,
                            new PrioritySelector(
                                Spell.Heal("Healing Surge",
                                    ret => StyxWoW.Me,
                                    ret => StyxWoW.Me.HealthPercent <= 60)))
                        ));
        }

        #endregion

        #region Normal Rotation

        [Behavior(BehaviorType.Pull, WoWClass.Shaman, WoWSpec.ShamanElemental, WoWContext.Normal)]
        public static Composite CreateShamanElementalNormalPull()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.WaitForCast(true),
                Spell.BuffSelf("Lightning Shield"),

                new Decorator(
                    ret => StyxWoW.Me.CurrentTarget.DistanceSqr < 40 * 40,
                    Totems.CreateSetTotems()),

                Spell.Cast("Lightning Bolt"),

                Spell.Cast("Earth Shock",
                    ret => StyxWoW.Me.HasAura("Lightning Shield", 5)),

                Spell.Cast("Unleash Weapon",
                    ret => StyxWoW.Me.Inventory.Equipped.MainHand != null
                        && StyxWoW.Me.Inventory.Equipped.MainHand.TemporaryEnchantment.Id == 5),

                Movement.CreateMoveToTargetBehavior(true, 35f)
                );
        }

        [Behavior(BehaviorType.Combat, WoWClass.Shaman, WoWSpec.ShamanElemental, WoWContext.Normal)]
        public static Composite CreateShamanElementalNormalCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.WaitForCast(true),
                Totems.CreateSetTotems(),
                Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),
                Spell.BuffSelf("Lightning Shield"),

                Spell.BuffSelf("Elemental Mastery",
                    ret => Unit.NearbyUnitsInCombatWithMe.Any(u => u.Elite || u.IsPlayer) &&
                        !StyxWoW.Me.HasAnyAura("Bloodlust", "Heroism", "Time Warp", "Ancient Hysteria")),

                Spell.BuffSelf("Blood Fury",
                    ret => SingularSettings.Instance.UseRacials &&
                        StyxWoW.Me.Race == WoWRace.Orc &&
                        !StyxWoW.Me.HasAnyAura("Elemental Mastery", "Bloodlust", "Heroism", "Time Warp", "Ancient Hysteria")),
                Spell.BuffSelf("Berserking",
                    ret => SingularSettings.Instance.UseRacials &&
                        StyxWoW.Me.Race == WoWRace.Troll &&
                        !StyxWoW.Me.HasAnyAura("Elemental Mastery", "Bloodlust", "Heroism", "Time Warp", "Ancient Hysteria")),

                Spell.BuffSelf("Spiritwalker's Grace", ret => StyxWoW.Me.IsMoving),

                Spell.BuffSelf("Thunderstorm", ret => Unit.NearbyUnfriendlyUnits.Count( u => u.Distance < 10f ) >= 3),

                new Decorator(
                    ret => Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 3,
                    new PrioritySelector(
                        new Action( act => { Logger.WriteDebug("performing aoe behavior"); return RunStatus.Failure; }),

                        Spell.BuffSelf("Astral Shift", ret => StyxWoW.Me.HealthPercent < 40 || Unit.NearbyUnitsInCombatWithMe.Count() >= 5),

                        Spell.BuffSelf("Astral Shift", ret => StyxWoW.Me.HealthPercent < 40 || Unit.NearbyUnitsInCombatWithMe.Count() >= 5),

                        Spell.BuffSelf( StyxWoW.Me.IsHorde ? "Bloodlust" : "Heroism", 
                            ret => Unit.NearbyUnitsInCombatWithMe.Count() >= 5 ||
                                Unit.NearbyUnitsInCombatWithMe.Any( u => u.Elite || u.IsPlayer )),

                        Spell.BuffSelf("Magma Totem",
                            ret => Unit.NearbyUnfriendlyUnits.Count(u => u.DistanceSqr < 8 * 8 && u.IsTargetingMeOrPet) >= 3 &&
                                   !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental || t.WoWTotem == WoWTotem.Magma)),

                        Spell.BuffSelf("Elemental Mastery", ret =>
                            !StyxWoW.Me.HasAnyAura("Bloodlust", "Heroism", "Time Warp", "Ancient Hysteria")),

                        Spell.CastOnGround("Earthquake", ret => StyxWoW.Me.CurrentTarget.Location, req => 
                            (StyxWoW.Me.ManaPercent > 60 || StyxWoW.Me.HasAura( "Clearcasting")) &&
                            Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 6),

                        Spell.Cast("Chain Lightning", ret => Clusters.GetBestUnitForCluster(Unit.UnfriendlyUnitsNearTarget(15f), ClusterType.Chained, 12))
                        )),

                // Totem stuff
                // Pop the ele on bosses
                Spell.BuffSelf("Fire Elemental Totem", 
                            ret => (Unit.NearbyUnitsInCombatWithMe.Count() >= 5 || Unit.NearbyUnitsInCombatWithMe.Any( u => u.Elite || u.IsPlayer )) &&
                                !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),
                Spell.BuffSelf("Searing Totem",
                    ret => StyxWoW.Me.CurrentTarget.Distance < Totems.GetTotemRange(WoWTotem.Searing) - 2f &&
                           !StyxWoW.Me.Totems.Any(
                                t => t.Unit != null && t.WoWTotem == WoWTotem.Searing &&
                                     t.Unit.Location.Distance(StyxWoW.Me.CurrentTarget.Location) < Totems.GetTotemRange(WoWTotem.Searing)) &&
                           !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),

                Spell.Buff("Flame Shock", true),

                Spell.Cast("Lava Burst"),
                Spell.Cast("Earth Shock",
                    ret => StyxWoW.Me.HasAura("Lightning Shield", 5) &&
                           StyxWoW.Me.CurrentTarget.GetAuraTimeLeft("Flame Shock", true).TotalSeconds > 3),

                Spell.Cast("Unleash Elements", ret => 
                    StyxWoW.Me.IsMoving &&
                    !StyxWoW.Me.HasAura( "Spiritwalker's Grace") &&
                    StyxWoW.Me.Inventory.Equipped.MainHand != null &&
                    StyxWoW.Me.Inventory.Equipped.MainHand.TemporaryEnchantment.Id == 5),

                Spell.Cast("Chain Lightning", ret => Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2),
                Spell.Cast("Lightning Bolt"),
                Movement.CreateMoveToTargetBehavior(true, 35f)
                );
        }

        #endregion

        #region Battleground Rotation

        [Behavior(BehaviorType.Pull | BehaviorType.Combat, WoWClass.Shaman, WoWSpec.ShamanElemental, WoWContext.Battlegrounds)]
        public static Composite CreateShamanElementalPvPPullAndCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.WaitForCast(true),
                Totems.CreateSetTotems(),
                Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                Spell.BuffSelf("Lightning Shield"),

                Spell.BuffSelf("Spiritwalker's Grace", ret => StyxWoW.Me.IsMoving && StyxWoW.Me.Combat),

                Spell.BuffSelf("Elemental Mastery",
                    ret => StyxWoW.Me.IsMoving && StyxWoW.Me.Combat &&
                           (!SpellManager.HasSpell("Spiritwalker's Grace") ||
                           SpellManager.Spells["Spiritwalker's Grace"].Cooldown && !StyxWoW.Me.HasAura("Spiritwalker's Grace"))),
                Spell.BuffSelf("Thunderstorm", ret => StyxWoW.Me.IsStunned()),
                new Decorator(
                    ret => Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 3,
                    new PrioritySelector(
                        // Pop the ele on bosses
                        Spell.BuffSelf("Fire Elemental Totem", ret => !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),
                        Spell.CastOnGround("Earthquake", ret => StyxWoW.Me.CurrentTarget.Location),
                        Spell.Cast("Chain Lightning", ret => Clusters.GetBestUnitForCluster(Unit.UnfriendlyUnitsNearTarget(15f), ClusterType.Chained, 12))
                        )),

                // Totem stuff
                Spell.BuffSelf("Searing Totem",
                    ret => StyxWoW.Me.CurrentTarget.Distance < Totems.GetTotemRange(WoWTotem.Searing) - 2f &&
                           !StyxWoW.Me.Totems.Any(
                                t => t.Unit != null && t.WoWTotem == WoWTotem.Searing &&
                                     t.Unit.Location.Distance(StyxWoW.Me.CurrentTarget.Location) < Totems.GetTotemRange(WoWTotem.Searing)) &&
                           StyxWoW.Me.Totems.All(t => t.WoWTotem != WoWTotem.FireElemental)),

                Spell.Buff("Flame Shock", true),
                Spell.Cast("Lava Burst"),
                Spell.Cast("Earth Shock",
                    ret => StyxWoW.Me.HasAura("Lightning Shield", 5) &&
                           StyxWoW.Me.CurrentTarget.GetAuraTimeLeft("Flame Shock", true).TotalSeconds > 3),
                Spell.Cast("Unleash Elements",
                    ret => (StyxWoW.Me.Inventory.Equipped.MainHand.TemporaryEnchantment.Id == 8024) && StyxWoW.Me.IsMoving && !StyxWoW.Me.HasAura("Spiritwalker's Grace")),
                Spell.Cast("Chain Lightning", ret => Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2),
                Spell.Cast("Lightning Bolt"),
                Movement.CreateMoveToTargetBehavior(true, 35f)
                );
        }

        #endregion

        #region Instance Rotation

        [Behavior(BehaviorType.Pull | BehaviorType.Combat, WoWClass.Shaman, WoWSpec.ShamanElemental, WoWContext.Instances)]
        public static Composite CreateShamanElementalInstancePullAndCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),
                Spell.WaitForCast(true),
                Totems.CreateSetTotems(),
                Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),

                Spell.BuffSelf("Lightning Shield"),
                Spell.BuffSelf("Spiritwalker's Grace", ret => StyxWoW.Me.IsMoving && StyxWoW.Me.Combat),
#if DISCRETE_MASTERY_USAGE
                Spell.BuffSelf("Elemental Mastery",
                    ret => StyxWoW.Me.HasAnyAura("Bloodlust", "Heroism", "Time Warp", "Ancient Hysteria")),
                Spell.BuffSelf("Elemental Mastery", 
                    ret => StyxWoW.Me.IsMoving && StyxWoW.Me.Combat &&
                           (!SpellManager.HasSpell("Spiritwalker's Grace") || 
                           SpellManager.Spells["Spiritwalker's Grace"].Cooldown && !StyxWoW.Me.HasAura("Spiritwalker's Grace"))),
#else
                Spell.BuffSelf("Elemental Mastery", ret => StyxWoW.Me.Combat),
#endif
                new Decorator(
                    ret => Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 3,
                    new PrioritySelector(
                        new Action(act => { Logger.WriteDebug("performing aoe behavior"); return RunStatus.Failure; }),
                        Spell.CastOnGround("Earthquake", ret => StyxWoW.Me.CurrentTarget.Location),
                        Spell.Cast("Chain Lightning", ret => Clusters.GetBestUnitForCluster(Unit.UnfriendlyUnitsNearTarget(15f), ClusterType.Chained, 12))
                        )),
                
                // Totem stuff
                // Pop the ele on bosses
                Spell.BuffSelf("Fire Elemental Totem", ret => StyxWoW.Me.CurrentTarget.IsBoss() && !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),
                Spell.BuffSelf("Searing Totem",
                    ret => StyxWoW.Me.CurrentTarget.Distance < Totems.GetTotemRange(WoWTotem.Searing) - 2f &&
                           !StyxWoW.Me.Totems.Any(
                                t => t.Unit != null && t.WoWTotem == WoWTotem.Searing &&
                                     t.Unit.Location.Distance(StyxWoW.Me.CurrentTarget.Location) < Totems.GetTotemRange(WoWTotem.Searing)) &&
                           !StyxWoW.Me.Totems.Any(t => t.WoWTotem == WoWTotem.FireElemental)),

                Spell.Buff("Flame Shock", true),
                Spell.Cast("Lava Burst"),
                Spell.Cast("Earth Shock", 
                    ret => StyxWoW.Me.HasAura("Lightning Shield", 5) &&
                           StyxWoW.Me.CurrentTarget.GetAuraTimeLeft("Flame Shock", true).TotalSeconds > 3),
                Spell.Cast("Unleash Elements",
                    ret => (StyxWoW.Me.Inventory.Equipped.MainHand.TemporaryEnchantment.Id == 8024) && StyxWoW.Me.IsMoving && !StyxWoW.Me.HasAura("Spiritwalker's Grace")),
                Spell.Cast("Chain Lightning", ret => Unit.UnfriendlyUnitsNearTarget(10f).Count() >= 2),
                Spell.Cast("Lightning Bolt"),
                Movement.CreateMoveToTargetBehavior(true, 35f)
                );
        }

        #endregion
    }
}
