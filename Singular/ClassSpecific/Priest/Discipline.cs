﻿#region Revision Info

// This file is part of Singular - A community driven Honorbuddy CC
// $Author$
// $Date$
// $HeadURL$
// $LastChangedBy$
// $LastChangedDate$
// $LastChangedRevision$
// $Revision$

#endregion

using System.Collections.Generic;
using System.Linq;

using Singular.Settings;

using Styx.Combat.CombatRoutine;
using Styx.Logic;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

using TreeSharp;

namespace Singular
{
    partial class SingularRoutine
    {
        public List<WoWPlayer> ResurrectablePlayers { get { return ObjectManager.GetObjectsOfType<WoWPlayer>().Where(p => !p.IsMe && p.Dead).ToList(); } }

        [Class(WoWClass.Priest)]
        [Spec(TalentSpec.DisciplineHealingPriest)]
        [Behavior(BehaviorType.Rest)]
        [Context(WoWContext.All)]
        public Composite CreatDiscHealRest()
        {
            return new PrioritySelector(
                // Rest up damnit! Do this first, so we make sure we're fully rested.
                CreateDefaultRestComposite(SingularSettings.Instance.DefaultRestHealth, SingularSettings.Instance.DefaultRestMana),
                // Make sure we're healing OOC too!
                CreateDiscHealOnlyBehavior(),
                // Can we res people?
                new Decorator(
                    ret => ResurrectablePlayers.Count != 0,
                    CreateSpellCast("Resurrection", ret => true, ret => ResurrectablePlayers.FirstOrDefault()))
                );
        }

        private Composite CreateDiscHealOnlyBehavior()
        {
            // Atonement - Tab 1  index 10 - 1/2 pts
            const int Penance = 30,
                      FlashHeal = 40,
                      GreaterHeal = 50,
                      Heal = 70,
                      Renew = 80,
                      PainSuppression = 30,
                      BindingHealMe = 70,
                      BindingHealThem = 70;

            const int PrayerOfHealing = 50,
                      // Number of players to use POH for
                      PrayerOfHealingCount = 3;
            return new
                Decorator(
                ret => HealTargeting.Instance.FirstUnit != null,
                new PrioritySelector(
                    ctx => HealTargeting.Instance.FirstUnit,
                    CreateWaitForCast(),
                    // Ensure we're in range of the unit to heal, and it's in LOS.
                    CreateRangeAndFace(35f, ret => (WoWUnit)ret),
                    //CreateSpellBuff("Renew", ret => HealTargeting.Instance.TargetList.FirstOrDefault(u => !u.HasAura("Renew") && u.HealthPercent < 90) != null, ret => HealTargeting.Instance.TargetList.FirstOrDefault(u => !u.HasAura("Renew") && u.HealthPercent < 90)),
                    CreateSpellBuff("Power Word: Shield", ret => !((WoWUnit)ret).HasAura("Weakened Soul"), ret => (WoWUnit)ret),
                    new Decorator(
                        ret =>
                        NearbyFriendlyPlayers.Count(p => !p.Dead && p.HealthPercent < PrayerOfHealing) > PrayerOfHealingCount &&
                        (SpellManager.CanCast("Prayer of Healing") || SpellManager.CanCast("Divine Hymn")),
                        new Sequence(
                            CreateSpellCast("Archangel"),
                            // This will skip over DH if we can't cast it.
                            // If we can, the sequence fails, since PoH can't be cast (as we're still casting at this point)
                            new DecoratorContinue(
                                ret => SpellManager.CanCast("Divine Hymn"),
                                CreateSpellCast("Divine Hymn")),
                            CreateSpellCast("Prayer of Healing"))),
                    CreateSpellBuff("Pain Supression", ret => ((WoWUnit)ret).HealthPercent < PainSuppression, ret => (WoWUnit)ret),
                    CreateSpellBuff("Penance", ret => ((WoWUnit)ret).HealthPercent < Penance, ret => (WoWUnit)ret),
                    CreateSpellCast("Flash Heal", ret => ((WoWUnit)ret).HealthPercent < FlashHeal, ret => (WoWUnit)ret),
                    CreateSpellCast(
                        "Binding Heal", ret => ((WoWUnit)ret).HealthPercent < BindingHealThem && Me.HealthPercent < BindingHealMe,
                        ret => (WoWUnit)ret),
                    CreateSpellCast("Greater Heal", ret => ((WoWUnit)ret).HealthPercent < GreaterHeal, ret => (WoWUnit)ret),
                    CreateSpellCast("Heal", ret => ((WoWUnit)ret).HealthPercent < Heal, ret => (WoWUnit)ret),
                    CreateSpellBuff("Renew", ret => ((WoWUnit)ret).HealthPercent < Renew, ret => (WoWUnit)ret),
                    CreateSpellBuff("Prayer of Mending", ret => ((WoWUnit)ret).HealthPercent < 90, ret => (WoWUnit)ret)

                    // Divine Hymn
                    // Desperate Prayer
                    // Prayer of Mending
                    // Prayer of Healing
                    // Power Word: Barrier
                    // TODO: Add smite healing. Only if Atonement is talented. (Its useless otherwise)
                    ));
        }

        // This behavior is used in combat/heal AND pull. Just so we're always healing our party.
        // Note: This will probably break shit if we're solo, but oh well!
        [Class(WoWClass.Priest)]
        [Spec(TalentSpec.DisciplineHealingPriest)]
        [Behavior(BehaviorType.Combat)]
        [Behavior(BehaviorType.Heal)]
        [Behavior(BehaviorType.Pull)]
        [Context(WoWContext.All)]
        public Composite CreateDiscHealComposite()
        {
            return new PrioritySelector(
                // Firstly, deal with healing people!
                CreateDiscHealOnlyBehavior(),
                // If we have nothing to heal, and we're in combat (or the leader is)... kill something!
                new Decorator(
                    ret => HealTargeting.Instance.FirstUnit == null && (Me.Combat || (RaFHelper.Leader != null && RaFHelper.Leader.Combat)),
                    new PrioritySelector(
                        CreateEnsureTarget(),
                        CreateRangeAndFace(39f, ret => Me.CurrentTarget),
                        CreateSpellBuff("Power Word: Shadow"),
                        CreateSpellCast("Smite")
                        )),
                // No combat... check for resurrectable people!
                new Decorator(
                    ret => !Me.Combat,
                    new PrioritySelector(
                        // Find and res a friendly player damnit!
                        new Decorator(
                            ret => ResurrectablePlayers.Count != 0,
                            CreateSpellCast("Resurrection", ret => true, ret => ResurrectablePlayers.FirstOrDefault()))))
                );
        }
    }
}