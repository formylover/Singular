﻿using System.Linq;
using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Singular.Settings;
using Styx;

using Styx.CommonBot;
using Styx.TreeSharp;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using Rest = Singular.Helpers.Rest;
using System.Drawing;

namespace Singular.ClassSpecific.Paladin
{
    public class Retribution
    {

        #region Properties & Fields

        private static LocalPlayer Me { get { return StyxWoW.Me; } }
        private static PaladinSettings PaladinSettings { get { return SingularSettings.Instance.Paladin; } }

        private const int RET_T13_ITEM_SET_ID = 1064;

        private static int NumTier13Pieces
        {
            get
            {
                return StyxWoW.Me.CarriedItems.Count(i => i.ItemInfo.ItemSetId == RET_T13_ITEM_SET_ID);
            }
        }

        private static bool Has2PieceTier13Bonus { get { return NumTier13Pieces >= 2; } }

        private static int _mobCount;

        #endregion

        #region Heal
        [Behavior(BehaviorType.Heal, WoWClass.Paladin, WoWSpec.PaladinRetribution)]
        public static Composite CreatePaladinRetributionHeal()
        {
            return new PrioritySelector(
                Spell.WaitForCastOrChannel(),
                new Decorator(
                    ret => !Spell.IsGlobalCooldown(),
                    new PrioritySelector(                       
                        Spell.Heal("Lay on Hands",
                            mov => false,
                            on => Me,
                            req => Me.GetPredictedHealthPercent(true) <= PaladinSettings.LayOnHandsHealth,
                            cancel => false,
                            true),
                        Spell.Heal("Word of Glory",
                            mov => false,
                            on => Me,
                            req => Me.GetPredictedHealthPercent(true) <= PaladinSettings.WordOfGloryHealth && Me.CurrentHolyPower >= 3,
                            cancel => Me.HealthPercent > PaladinSettings.WordOfGloryHealth,
                            true),
                        Spell.Heal("Flash of Light",
                            mov => false,
                            on => Me,
                            req => Me.GetPredictedHealthPercent(true) <= PaladinSettings.RetributionHealHealth,
                            cancel => Me.HealthPercent > PaladinSettings.RetributionHealHealth,
                            true)
                        )
                    )
                );
        }

        [Behavior(BehaviorType.Rest, WoWClass.Paladin, WoWSpec.PaladinRetribution)]
        public static Composite CreatePaladinRetributionRest()
        {
            return new PrioritySelector( // use ooc heals if we have mana to
                Spell.WaitForCastOrChannel(false),
                new Decorator(
                    ret => !Spell.IsGlobalCooldown(),
                    new PrioritySelector(
                        Spell.Heal("Flash of Light",
                            mov => true,
                            on => Me,
                            req => Me.GetPredictedHealthPercent(true) <= 85 && !Me.HasAura("Drink") && !Me.HasAura("Food"),
                            cancel => Me.HealthPercent > 90,
                            true),
                        // Since we used predicted health above, add a test before calling default rest since it does not
                        new Decorator(
                            ret => Me.GetPredictedHealthPercent(true) < SingularSettings.Instance.MinHealth || Me.ManaPercent < SingularSettings.Instance.MinMana,
                            Rest.CreateDefaultRestBehaviour()
                            ),
                        // Can we res people?
                        Spell.Resurrect("Redemption")
                        )
                    )
                );
        }
        #endregion

        #region Normal Rotation

        [Behavior(BehaviorType.Pull | BehaviorType.Combat, WoWClass.Paladin, WoWSpec.PaladinRetribution, WoWContext.Normal | WoWContext.Battlegrounds )]
        public static Composite CreatePaladinRetributionNormalPullAndCombat()
        {
            return new PrioritySelector(

                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),

                Spell.WaitForCastOrChannel(),

                new Decorator( 
                    ret => !Spell.IsGlobalCooldown() && Me.GotTarget,
                    new PrioritySelector(
                        // aoe count
                        ActionAoeCount(),

                        CreateRetDiagnosticOutputBehavior(),

                        Helpers.Common.CreateAutoAttack(true),
                        Helpers.Common.CreateInterruptSpellCast(ret => Me.CurrentTarget),

                        // Defensive
                        Spell.BuffSelf("Hand of Freedom",
                            ret => Me.HasAuraWithMechanic(WoWSpellMechanic.Dazed,
                                                                  WoWSpellMechanic.Disoriented,
                                                                  WoWSpellMechanic.Frozen,
                                                                  WoWSpellMechanic.Incapacitated,
                                                                  WoWSpellMechanic.Rooted,
                                                                  WoWSpellMechanic.Slowed,
                                                                  WoWSpellMechanic.Snared)),

                        Spell.BuffSelf("Divine Shield", ret => Me.HealthPercent <= 20 && !Me.HasAura("Forbearance") && (!Me.HasAura("Horde Flag") || !Me.HasAura("Alliance Flag"))),
                        Spell.BuffSelf("Divine Protection", ret => Me.HealthPercent <= PaladinSettings.DivineProtectionHealthProt),

                        Common.CreatePaladinSealBehavior(),

                        //7	Blow buffs seperatly.  No reason for stacking while grinding.
                        Spell.Cast("Guardian of Ancient Kings", ret => PaladinSettings.RetGoatK && _mobCount >= 4),
                        Spell.Cast("Holy Avenger", ret => PaladinSettings.RetGoatK && _mobCount < 4),
                        Spell.BuffSelf("Avenging Wrath", ret => _mobCount >= 4 || (!Me.HasAura("Holy Avenger") && Spell.GetSpellCooldown("Holy Avenger").TotalSeconds > 10)),

                        Spell.Cast("Execution Sentence", ret => Me.CurrentTarget.TimeToDeath() > 15),
                        Spell.Cast("Holy Prism", on => Group.Tanks.FirstOrDefault(t => t.IsAlive && t.Distance < 40)),

                        new Decorator(
                            ret => _mobCount >= 2 && Spell.UseAOE,
                            new PrioritySelector(
                                Spell.CastOnGround("Light's Hammer", loc => Me.CurrentTarget.Location, ret => 2 <= Clusters.GetClusterCount(Me.CurrentTarget, Unit.NearbyUnfriendlyUnits, ClusterType.Radius, 10f)),

                                // EJ: Inq > 5HP TV > ES > HoW > Exo > CS > Judge > 3-4HP TV (> SS)
                                Spell.BuffSelf("Inquisition", ret => Me.CurrentHolyPower > 0 && Me.GetAuraTimeLeft("Inquisition", true).TotalSeconds < 4),
                                Spell.Cast( ret => SpellManager.HasSpell("Divine Storm") ? "Divine Storm" : "Templar's Verdict", ret => Me.CurrentHolyPower == 5),
                                Spell.Cast("Execution Sentence" ),
                                Spell.Cast("Hammer of Wrath"),
                                Spell.Cast("Exorcism"),
                                Spell.Cast(ret => SpellManager.HasSpell("Hammer of the Righteous") ? "Hammer of the Righteous" : "Crusader Strike"),
                                Spell.Cast("Judgment"),
                                Spell.Cast("Templar's Verdict", ret => Me.CurrentHolyPower >= 3),
                                Spell.BuffSelf("Sacred Shield"),
                                Movement.CreateMoveToMeleeBehavior(true)
                                )
                            ),

                        // EJ: Inq > 5HP TV > ES > HoW > Exo > CS > Judge > 3-4HP TV (> SS)
                        Spell.BuffSelf("Inquisition", ret => Me.CurrentHolyPower > 0 && Me.GetAuraTimeLeft("Inquisition", true).TotalSeconds < 4),
                        Spell.Cast( "Templar's Verdict", ret => Me.CurrentHolyPower == 5),
                        Spell.Cast("Execution Sentence" ),
                        Spell.Cast("Hammer of Wrath"),
                        Spell.Cast("Exorcism"),
                        Spell.Cast("Crusader Strike"),
                        Spell.Cast("Judgment"),
                        Spell.Cast("Templar's Verdict", ret => Me.CurrentHolyPower >= 3),
                        Spell.BuffSelf("Sacred Shield")
                        )
                    ),

                // Move to melee is LAST. Period.
                Movement.CreateMoveToMeleeBehavior(true)
                );
        }

        private static Action ActionAoeCount()
        {
            return new Action(ret =>
            {
                _mobCount = Unit.NearbyUnfriendlyUnits.Count(u => u.Distance < (u.MeleeDistance() + 3));
                return RunStatus.Failure;
            });
        }

        #endregion


        #region Instance Rotation

        [Behavior(BehaviorType.Heal | BehaviorType.Pull | BehaviorType.Combat, WoWClass.Paladin, WoWSpec.PaladinRetribution, WoWContext.Instances)]
        public static Composite CreatePaladinRetributionInstancePullAndCombat()
        {
            return new PrioritySelector(
                Safers.EnsureTarget(),
                Movement.CreateMoveToLosBehavior(),
                Movement.CreateFaceTargetBehavior(),

                Spell.WaitForCastOrChannel(),

                new Decorator( 
                    ret => !Spell.IsGlobalCooldown() && Me.GotTarget,
                    new PrioritySelector(
                        // aoe count
                        new Action(ret =>
                        {
                            _mobCount = Unit.NearbyUnfriendlyUnits.Count(u => u.Distance < (u.MeleeDistance() + 3));
                            return RunStatus.Failure;
                        }),

                        CreateRetDiagnosticOutputBehavior(),

                        Helpers.Common.CreateAutoAttack(true),
                        Helpers.Common.CreateInterruptSpellCast(ret => Me.CurrentTarget),

                        // Defensive
                        Spell.BuffSelf("Hand of Freedom",
                                       ret =>
                                       !Me.Auras.Values.Any(
                                           a => a.Name.Contains("Hand of") && a.CreatorGuid == Me.Guid) &&
                                       Me.HasAuraWithMechanic(WoWSpellMechanic.Dazed,
                                                                      WoWSpellMechanic.Disoriented,
                                                                      WoWSpellMechanic.Frozen,
                                                                      WoWSpellMechanic.Incapacitated,
                                                                      WoWSpellMechanic.Rooted,
                                                                      WoWSpellMechanic.Slowed,
                                                                      WoWSpellMechanic.Snared)),

                        Spell.BuffSelf("Divine Shield", 
                            ret => Me.HealthPercent <= 20  && !Me.HasAura("Forbearance")),
                        Spell.BuffSelf("Divine Protection",
                            ret => Me.HealthPercent <= PaladinSettings.DivineProtectionHealthProt),

                        Common.CreatePaladinSealBehavior(),

                        new Decorator(
                            ret => PartyBuff.WeHaveBloodlust,
                            new Action(abc =>
                            {
                                WoWItem item = Item.FindFirstUsableItemBySpell("Golem's Strength", "Potion of Mogu Power");
                                if (item != null) 
                                    item.Use();
                            })),

                        new Decorator(
                            ret => Me.CurrentTarget.IsWithinMeleeRange,
                            new PrioritySelector(
                                Spell.Cast("Guardian of Ancient Kings",
                                    ret => PaladinSettings.RetGoatK
                                        && Me.CurrentTarget.IsBoss()
                                        && Me.ActiveAuras.ContainsKey("Inquisition")),
                                Spell.BuffSelf("Avenging Wrath", 
                                    ret => Me.ActiveAuras.ContainsKey("Inquisition")
                                        && (!SpellManager.HasSpell("Guardian of Ancient Kings") || !PaladinSettings.RetGoatK || Common.HasTalent(PaladinTalents.SanctifiedWrath) || Spell.GetSpellCooldown("Guardian of Ancient Kings").TotalSeconds <= 290)),
                                Spell.Cast("Holy Avenger", ret => PaladinSettings.RetGoatK && Me.HasAura("Avenging Wrath"))
                                )
                            ),

                        // react to Divine Purpose proc
                        new Decorator(
                            ret => Me.GetAuraTimeLeft("Divine Purpose", true).TotalSeconds > 0,
                            new PrioritySelector(
                                Spell.BuffSelf("Inquisition", ret => Me.GetAuraTimeLeft("Inquisition", true).TotalSeconds <= 2),
                                Spell.Cast("Templar's Verdict")
                                )
                            ),

                        Spell.Cast( "Execution Sentence", ret => Me.CurrentTarget.TimeToDeath() > 12 ),
                        Spell.Cast( "Holy Prism", on => Group.Tanks.FirstOrDefault( t => t.IsAlive && t.Distance < 40)),

                        //Use Synapse Springs Engineering thingy if inquisition is up

                        new Decorator(
                            ret => _mobCount >= 2 && Spell.UseAOE,
                            new PrioritySelector(
                                Spell.CastOnGround("Light's Hammer", loc => Me.CurrentTarget.Location, ret => 2 <= Clusters.GetClusterCount(Me.CurrentTarget, Unit.NearbyUnfriendlyUnits, ClusterType.Radius, 10f)),

                                // EJ Multi Rotation: Inq > 5HP TV > ES > HoW > Exo > CS > Judge > 3-4HP TV (> SS)
                                Spell.BuffSelf("Inquisition", ret => Me.CurrentHolyPower > 0 && Me.GetAuraTimeLeft("Inquisition", true).TotalSeconds < 4),
                                Spell.Cast(ret => SpellManager.HasSpell("Divine Storm") ? "Divine Storm" : "Templar's Verdict", ret => Me.CurrentHolyPower == 5),
                                Spell.Cast("Execution Sentence"),
                                Spell.Cast("Hammer of Wrath"),
                                Spell.Cast("Exorcism"),
                                Spell.Cast(ret => SpellManager.HasSpell("Hammer of the Righteous") ? "Hammer of the Righteous" : "Crusader Strike"),
                                Spell.Cast("Judgment"),
                                Spell.Cast("Templar's Verdict", ret => Me.CurrentHolyPower >= 3),
                                Spell.BuffSelf("Sacred Shield"),
                                Movement.CreateMoveToMeleeBehavior(true)
                                )
                            ),

                        // Single Target Priority - Laria's mix of EJ/Icy Veins/...
                        Spell.BuffSelf("Inquisition", ret => Me.CurrentHolyPower >= 3 && Me.GetAuraTimeLeft("Inquisition", true).TotalSeconds <= 2),
                        Spell.Cast("Execution Sentence", ret => Me.HasAura("Inquisition")),
                        Spell.Cast("Templar's Verdict", ret => Me.CurrentHolyPower == 5),
                        Spell.Cast("Hammer of Wrath"),
                        //Wait if HammerOfWrath CD is between 0 and 0.2 sec
                        Spell.Cast("Exorcism"),
                        Spell.Cast("Judgement", ret => Me.CurrentTarget.HealthPercent <= 20 || Me.HasAura("Avenging Wrath")),
                        Spell.Cast("Crusader Strike"),
                        Spell.Cast("Judgment"),
                        Spell.Cast("Templar's Verdict", ret => Me.CurrentHolyPower >= 3)
                        )
                    ),


                    // Move to melee is LAST. Period.
                    Movement.CreateMoveToMeleeBehavior(true)
                );
        }

        #endregion


        private static Composite CreateRetDiagnosticOutputBehavior()
        {
            if (!SingularSettings.Instance.EnableDebugLogging)
                return new Action( ret => { return RunStatus.Failure; } );

            return new Throttle(1,
                new Action(ret =>
                {
                    string sMsg;
                    sMsg = string.Format(".... h={0:F1}%, m={1:F1}%, moving={2}, mobs={3}",
                        Me.HealthPercent,
                        Me.ManaPercent,
                        Me.IsMoving,
                        _mobCount
                        );

                    WoWUnit target = Me.CurrentTarget;
                    if (target != null)
                    {
                        sMsg += string.Format(
                            ", {0}, {1:F1}%, {2:F1} yds, loss={3}",
                            target.SafeName(),
                            target.HealthPercent,
                            target.Distance,
                            target.InLineOfSpellSight
                            );
                    }

                    Logger.WriteDebug(Color.LightYellow, sMsg);
                    return RunStatus.Failure;
                })
                );
        }

    }
}
