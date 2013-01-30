﻿using System.Drawing;
using System.Linq;
using CommonBehaviors.Actions;
using Singular.Settings;

using Styx;
using Styx.CommonBot;
using Styx.CommonBot.POI;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using Styx.TreeSharp;
using Singular.Managers;
using Action = Styx.TreeSharp.Action;

namespace Singular.Helpers
{
    internal static class Safers
    {
        private static Color targetColor = Color.LightCoral;

        /// <summary>
        ///  This behavior SHOULD be called at top of the combat behavior. This behavior won't let the rest of the combat behavior to be called
        /// if you don't have a target. Also it will find a proper target, if the current target is dead or you don't have a target and still in combat.
        /// Tank targeting is also dealed in this behavior.
        /// </summary>
        /// <returns></returns>
        public static Composite EnsureTarget()
        {

            return
                new Decorator(
                    ret => !SingularSettings.Instance.DisableAllTargeting,
                    new PrioritySelector(
                        new Decorator(
                            // DisableTankTargeting is a user-setting. NeedTankTargeting is an internal one. Make sure both are turned on.
                            ret => !SingularSettings.Instance.DisableTankTargetSwitching && Group.MeIsTank &&
                                   TankManager.TargetingTimer.IsFinished && StyxWoW.Me.Combat && TankManager.Instance.FirstUnit != null &&
                                   (StyxWoW.Me.CurrentTarget == null || StyxWoW.Me.CurrentTarget != TankManager.Instance.FirstUnit),
                            new Sequence(
                                // pending spells like mage blizard cause targeting to fail.
                                new DecoratorContinue(ctx => StyxWoW.Me.CurrentPendingCursorSpell != null,
                                    new Action(ctx => Lua.DoString("SpellStopTargeting()"))
                                    ),
                                new Action(
                                    ret =>
                                    {
                                        Logger.WriteDebug("Targeting first unit of TankTargeting");
                                        TankManager.Instance.FirstUnit.Target();
                                    }),
                                Helpers.Common.CreateWaitForLagDuration(),
                                new Action(ret => TankManager.TargetingTimer.Reset())
                                )
                            ),

                        new PrioritySelector(
                            ctx => 
                            {
                                // We are making sure we have the proper target in all cases here.

                                // No target switching for tanks. They check for their own stuff above.
                                if (Group.MeIsTank && !SingularSettings.Instance.DisableTankTargetSwitching)
                                    return null;

                                // Go below if current target is null or dead. We have other checks to deal with that
                                if (StyxWoW.Me.CurrentTarget == null || StyxWoW.Me.CurrentTarget.IsDead)
                                    return null;

                                // If the current target is in combat or has aggro towards us, it should be a valid target.
                                if (StyxWoW.Me.CurrentTarget.Combat || StyxWoW.Me.CurrentTarget.Aggro)
                                    return null;

                                // Check botpoi first and make sure our target is set to POI's object.
                                if (BotPoi.Current.Type == PoiType.Kill )
                                {
                                    var obj = BotPoi.Current.AsObject;

                                    if (obj != null)
                                    {
                                        if (StyxWoW.Me.CurrentTarget != obj && (obj as WoWUnit).IsAlive)
                                        {
                                            Logger.Write(targetColor, "Current target is not BotPOI.  Switching to " + obj.SafeName() + "!");
                                            return obj;
                                        }
                                    }
                                }

                                // Make sure we have the proper target from Targeting. 
                                // The Botbase should give us the best target in targeting.
                                var firstUnit = Targeting.Instance.FirstUnit;

                                if (firstUnit != null)
                                {
                                    if (StyxWoW.Me.CurrentTarget != firstUnit)
                                    {
                                        Logger.Write(targetColor, "Current target is not Bots first choice.  Switching to " + firstUnit.SafeName() + "!");
                                        return firstUnit;
                                    }
                                }

                                return null;
                            },
                            new Decorator(
                                ret => ret != null,
                                new Sequence(
                                    new Action(ret => ((WoWUnit)ret).Target()),
                                    new WaitContinue(
                                        2,
                                        ret => StyxWoW.Me.CurrentTarget != null &&
                                                StyxWoW.Me.CurrentTarget == (WoWUnit)ret,
                                        new ActionAlwaysSucceed())
                                    )
                                )
                            ),
                        new Decorator(
                            ret => StyxWoW.Me.CurrentTarget == null || StyxWoW.Me.CurrentTarget.IsDead,
                            new PrioritySelector(
                                ctx =>
                                {
                                    // If we have a RaF leader, then use its target.
                                    var rafLeader = RaFHelper.Leader;
                                    if (rafLeader != null && rafLeader.IsValid && !rafLeader.IsMe && rafLeader.Combat &&
                                        rafLeader.CurrentTarget != null && rafLeader.CurrentTarget.IsAlive && !Blacklist.Contains(rafLeader.CurrentTarget))
                                    {
                                        Logger.Write(targetColor, "Current target invalid. Switching to Tanks target " + rafLeader.CurrentTarget.SafeName() + "!");
                                        return rafLeader.CurrentTarget;
                                    }

                                    // Check bot poi.
                                    if (BotPoi.Current.Type == PoiType.Kill)
                                    {
                                        var unit = BotPoi.Current.AsObject as WoWUnit;

                                        if (unit != null && unit.IsAlive && !unit.IsMe && !Blacklist.Contains(unit))
                                        {
                                            Logger.Write(targetColor, "Current target invalid. Switching to POI " + unit.SafeName() + "!");
                                            return unit;
                                        }
                                    }

                                    // Does the target list have anything in it? And is the unit in combat?
                                    // Make sure we only check target combat, if we're NOT in a BG. (Inside BGs, all targets are valid!!)
                                    var firstUnit = Targeting.Instance.FirstUnit;
                                    if (firstUnit != null && firstUnit.IsAlive && !firstUnit.IsMe && firstUnit.Combat &&
                                        !Blacklist.Contains(firstUnit))
                                    {
                                        Logger.Write(targetColor, "Current target invalid. Switching to Bot First Unit " + firstUnit.SafeName() + "!");
                                        return firstUnit;
                                    }

                                    // Cache this query, since we'll be using it for 2 checks. No need to re-query it.
                                    var agroMob =
                                        ObjectManager.GetObjectsOfType<WoWUnit>(false, false)
                                            .Where(p => !Blacklist.Contains(p) && p.IsHostile && !p.IsOnTransport && !p.IsDead
                                                    && !p.Mounted && p.DistanceSqr <= 70 * 70 && p.IsPlayer && p.Combat && (p.IsTargetingMeOrPet || p.IsTargetingMyRaidMember))
                                            .OrderBy(u => u.DistanceSqr)
                                            .FirstOrDefault();

                                    if (agroMob != null)
                                    {
                                        if (!agroMob.IsPet || agroMob.SummonedByUnit == null)
                                        {
                                            Logger.Write(targetColor, "Current target invalid. Switching to player attacking us " + agroMob.SafeName() + "!");
                                        }
                                        else
                                        {
                                            Logger.Write(targetColor, "Current target invalid. Enemy player pet {0} attacking us, switching to player {1}!", agroMob.SafeName(), agroMob.SummonedByUnit.SafeName());
                                            agroMob = agroMob.SummonedByUnit;
                                        }

                                        return agroMob;
                                    }

                                    // Cache this query, since we'll be using it for 2 checks. No need to re-query it.
                                    agroMob =
                                        ObjectManager.GetObjectsOfType<WoWUnit>(false, false)
                                            .Where(p => !Blacklist.Contains(p) && p.IsHostile && !p.IsOnTransport && !p.IsDead
                                                    && !p.Mounted && p.DistanceSqr <= 70 * 70 && (p.Aggro || p.PetAggro))
                                            .OrderBy(u => u.DistanceSqr)
                                            .FirstOrDefault();

                                    if (agroMob != null)
                                    {
                                        // Return the closest one to us
                                        Logger.Write(targetColor, "Current target invalid. Switching to aggroed mob " + agroMob.SafeName() + "!");
                                        return agroMob;
                                    }

                                    // And there's nothing left, so just return null, kthx.
                                    return null;
                                },
                                // Make sure the target is VALID. If not, then ignore this next part. (Resolves some silly issues!)
                                new Decorator(
                                    ret => ret != null,
                                    new Sequence(
                                        // pending spells like mage blizard cause targeting to fail.
                                        new DecoratorContinue(ctx => StyxWoW.Me.CurrentPendingCursorSpell != null,
                                            new Action(ctx => Lua.DoString("SpellStopTargeting()"))),
                                        new Action(ret => ((WoWUnit)ret).Target()),
                                        new WaitContinue(
                                            2,
                                            ret => StyxWoW.Me.CurrentTarget != null &&
                                                   StyxWoW.Me.CurrentTarget == (WoWUnit)ret,
                                            new ActionAlwaysSucceed())
                                        )
                                    ),
                                new ActionAlwaysSucceed()
                                )
                            )
                        )
                    );
        }
    }
}
