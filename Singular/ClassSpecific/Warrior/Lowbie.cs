﻿using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;

using Styx;
using Styx.Combat.CombatRoutine;

using Styx.TreeSharp;

namespace Singular.ClassSpecific.Warrior
{
    public class Lowbie
    {
        [Spec((WoWSpec)0)]
        [Behavior(BehaviorType.Combat)]
        [Class(WoWClass.Warrior)]
        [Priority(500)]
        [Context(WoWContext.Normal)]
        public static Composite CreateLowbieWarriorCombat()
        {
            return new PrioritySelector(
                // Ensure Target
                Safers.EnsureTarget(),
                // LOS Check
                Movement.CreateMoveToLosBehavior(),
                // face target
                Movement.CreateFaceTargetBehavior(),
                // Auto Attack
                Helpers.Common.CreateAutoAttack(false),
                Helpers.Common.CreateInterruptSpellCast(ret => StyxWoW.Me.CurrentTarget),
                // Heal
                Spell.Cast("Victory Rush"),
                // AOE
                new Decorator(
                    ret => Clusters.GetClusterCount(StyxWoW.Me, Unit.NearbyUnfriendlyUnits, ClusterType.Radius, 6f) >= 2,
                    new PrioritySelector(
                        Spell.Cast("Victory Rush"),
                        Spell.Cast("Thunder Clap"),
                        Spell.Cast("Heroic Strike"))),
                // DPS
                Spell.Cast("Heroic Strike"),
                Spell.Cast("Thunder Clap", ret => StyxWoW.Me.RagePercent > 50),
                //move to melee
                Movement.CreateMoveToTargetBehavior(true, 5f)
                );
        }

        [Spec((WoWSpec)0)]
        [Behavior(BehaviorType.Pull)]
        [Class(WoWClass.Warrior)]
        [Priority(500)]
        [Context(WoWContext.Normal)]
        public static Composite CreateLowbieWarriorPull()
        {
            return new PrioritySelector(
                // Ensure Target
                Safers.EnsureTarget(),
                // LOS
                Movement.CreateMoveToLosBehavior(),
                // face target
                Movement.CreateFaceTargetBehavior(),
                // Auto Attack
                Helpers.Common.CreateAutoAttack(false),
                // charge
                Spell.Cast("Charge", ret => StyxWoW.Me.CurrentTarget.Distance > 10 && StyxWoW.Me.CurrentTarget.Distance < 25),
                Spell.Cast("Throw", ret => StyxWoW.Me.CurrentTarget.IsFlying && Item.RangedIsType(WoWItemWeaponClass.Thrown)), Spell.Cast(
                    "Shoot",
                    ret =>
                    StyxWoW.Me.CurrentTarget.IsFlying && (Item.RangedIsType(WoWItemWeaponClass.Bow) || Item.RangedIsType(WoWItemWeaponClass.Gun))),
                // move to melee
                Movement.CreateMoveToTargetBehavior(true, 5f)
                );
        }
    }
}
