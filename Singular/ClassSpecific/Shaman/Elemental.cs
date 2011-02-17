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

using Styx.Combat.CombatRoutine;

using TreeSharp;

namespace Singular
{
    partial class SingularRoutine
    {
        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Context(WoWContext.All)]
        [Behavior(BehaviorType.Combat)]
        [Behavior(BehaviorType.Pull)]
        public Composite CreateElementalShamanCombat()
        {
            return new PrioritySelector(
                CreateEnsureTarget(),
                CreateRangeAndFace(39, ret => Me.CurrentTarget),
                CreateWaitForCast(),
                CreateSpellCast("Elemental Mastery"),
                CreateSpellBuff("Flame Shock"),
                CreateSpellCast("Unleash Elements"),
                CreateSpellCast("Lava Burst"),
                CreateSpellCast("Earth Shock", ret => HasAuraStacks("Lightning Shield", 6)),
                CreateSpellCast("Lightning Bolt")
                );
        }

        [Class(WoWClass.Shaman)]
        [Spec(TalentSpec.ElementalShaman)]
        [Context(WoWContext.All)]
        [Behavior(BehaviorType.PreCombatBuffs)]
        public Composite CreateElementalShamanBuffs()
        {
            return new PrioritySelector(
                CreateSpellBuffOnSelf("Lightning Shield"),
                CreateSpellCast("Flametongue Weapon", ret => Me.Inventory.Equipped.MainHand.TemporaryEnchantment.Name != "Flametongue Weapon")
                );
        }
    }
}