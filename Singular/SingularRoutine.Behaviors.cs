﻿//#define SHOW_BEHAVIOR_LOAD_DESCRIPTION
//#define BOTS_NOT_CALLING_PULLBUFFS
//#define TESTING_WHILE_IN_VEHICLE_COMPLETED

using Singular.Dynamics;
using Singular.Helpers;
using Singular.Managers;
using Singular.Settings;
using Styx;
using Styx.TreeSharp;
using Singular.ClassSpecific;
using System.Drawing;
using CommonBehaviors.Actions;
using Styx.Common;
using System;

using Action = Styx.TreeSharp.Action;
using Styx.CommonBot;
using Styx.WoWInternals.WoWObjects;
using Styx.Helpers;
using Styx.WoWInternals;
using Styx.Common.Helpers;
using System.Collections.Generic;

namespace Singular
{
    partial class SingularRoutine
    {
        private Composite _combatBehavior;
        private Composite _combatBuffsBehavior;
        private Composite _healBehavior;
        private Composite _preCombatBuffsBehavior;
        private Composite _pullBehavior;
        private Composite _pullBuffsBehavior;
        private Composite _restBehavior;
        private Composite _lostControlBehavior;
        public override Composite CombatBehavior { get { return _combatBehavior; } }
        public override Composite CombatBuffBehavior { get { return _combatBuffsBehavior; } }
        public override Composite HealBehavior { get { return _healBehavior; } }
        public override Composite PreCombatBuffBehavior { get { return _preCombatBuffsBehavior; } }
        public override Composite PullBehavior { get { return _pullBehavior; } }
        public override Composite PullBuffBehavior { get { return _pullBuffsBehavior; } }
        public override Composite RestBehavior { get { return _restBehavior; } }

        private static ulong _guidLastTarget = 0;
        private static WaitTimer _timerLastTarget = new WaitTimer(TimeSpan.FromSeconds(5));

        public bool RebuildBehaviors(bool silent = false)
        {
            Logger.PrintStackTrace("RebuildBehaviors called.");

            InitBehaviors();

            // save single consistent copy for building behaves since CurrentWoWContext is dynamically evaluated
            WoWContext context = CurrentWoWContext;

            // DO NOT UPDATE: This will cause a recursive event
            // Update the current context. Handled in SingularRoutine.Context.cs
            //UpdateContext();

            // special behavior - reset KitingBehavior hook prior to calling class specific createion
            TreeHooks.Instance.ReplaceHook(HookName("KitingBehavior"), new ActionAlwaysFail());

            // If these fail, then the bot will be stopped. We want to make sure combat/pull ARE implemented for each class.
            if (!EnsureComposite(true, context, BehaviorType.Combat))
            {
                return false;
            }

            if (!EnsureComposite(true, context, BehaviorType.Pull))
            {
                return false;
            }

            // If there's no class-specific resting, just use the default, which just eats/drinks when low.
            EnsureComposite(false, context, BehaviorType.Rest);
            if (TreeHooks.Instance.Hooks[HookName(BehaviorType.Rest)] == null)
                TreeHooks.Instance.ReplaceHook(HookName(BehaviorType.Rest), Helpers.Rest.CreateDefaultRestBehaviour());


            // These are optional. If they're not implemented, we shouldn't stop because of it.
            EnsureComposite(false, context, BehaviorType.CombatBuffs);
            EnsureComposite(false, context, BehaviorType.Heal);
            EnsureComposite(false, context, BehaviorType.PullBuffs);
            EnsureComposite(false, context, BehaviorType.PreCombatBuffs);

            EnsureComposite(false, context, BehaviorType.LossOfControl);

#if SHOW_BEHAVIOR_LOAD_DESCRIPTION
            // display concise single line describing what behaviors we are loading
            if (!silent)
            {
                string sMsg = "";
                if (_healBehavior != null)
                    sMsg += (!string.IsNullOrEmpty(sMsg) ? "," : "") + " Heal";
                if (_pullBuffsBehavior != null)
                    sMsg += (!string.IsNullOrEmpty(sMsg) ? "," : "") + " PullBuffs";
                if (_pullBehavior != null)
                    sMsg += (!string.IsNullOrEmpty(sMsg) ? "," : "") + " Pull";
                if (_preCombatBuffsBehavior != null)
                    sMsg += (!string.IsNullOrEmpty(sMsg) ? "," : "") + " PreCombatBuffs";
                if (_combatBuffsBehavior != null)
                    sMsg += (!string.IsNullOrEmpty(sMsg) ? "," : "") + " CombatBuffs";
                if (_combatBehavior != null)
                    sMsg += (!string.IsNullOrEmpty(sMsg) ? "," : "") + " Combat";
                if (_restBehavior != null)
                    sMsg += (!string.IsNullOrEmpty(sMsg) ? "," : "") + " Rest";

                Logger.Write(Color.LightGreen, "Loaded{0} behaviors for {1}: {2}", Me.Specialization.ToString().CamelToSpaced(), context.ToString(), sMsg);
            }
#endif
            return true;
        }

        /// <summary>
        /// initialize all base behaviors.  replaceable portion which will vary by context is represented by a single
        /// HookExecutor that gets assigned elsewhere (typically EnsureComposite())
        /// </summary>
        private void InitBehaviors()
        {
            // be sure to turn off -- routines needing it will enable when rebuilt
            HealerManager.NeedHealTargeting = false;

            // we only do this one time
            if (_restBehavior != null)
                return;

            // note regarding behavior intros....
            // WAIT: Rest and PreCombatBuffs should wait on gcd/cast in progress (return RunStatus.Success)
            // SKIP: PullBuffs, CombatBuffs, and Heal should fall through if gcd/cast in progress (wrap in decorator)
            // HANDLE: Pull and Combat should wait or skip as needed in class specific manner required

            // loss of control behavior must be defined prior to any embedded references by other behaviors
            _lostControlBehavior = new Decorator(
                ret => Me.Fleeing || Me.Stunned,
                new PrioritySelector(
                    new Action( r => { 
                        if ( !StyxWoW.IsInGame )
                        {
                            Logger.WriteDebug(Color.White, "Not in game...");
                            return RunStatus.Success;
                        }
                        
                        return RunStatus.Failure; 
                        }),
                    new ThrottlePasses(1, 1, new Decorator(ret => Me.Fleeing, new Action(r => { Logger.Write(Color.White, "FLEEING! (loss of control)"); return RunStatus.Failure; }))),
                    new ThrottlePasses(1, 1, new Decorator(ret => Me.Stunned, new Action(r => { Logger.Write(Color.White, "STUNNED! (loss of control)"); return RunStatus.Failure; }))),
                    new ThrottlePasses(1, 1, new Decorator(ret => Me.Silenced, new Action(r => { Logger.Write(Color.White, "SILENCED! (loss of control)"); return RunStatus.Failure; }))),
                    new Throttle(1,
                        new PrioritySelector(
                            new HookExecutor(HookName(BehaviorType.LossOfControl)),
                            new Decorator(
                                ret => SingularSettings.Instance.UseRacials,
                                new PrioritySelector(
                                    Spell.Cast("Will of the Forsaken", on => Me, ret => Me.Race == WoWRace.Undead && Me.Fleeing),
                                    Spell.Cast("Every Man for Himself", on => Me, ret => Me.Race == WoWRace.Human && (Me.Stunned || Me.Fleeing))
                                    )
                                ),

                            Item.UseEquippedTrinket(TrinketUsage.CrowdControlled),
                            Item.UseEquippedTrinket(TrinketUsage.CrowdControlledSilenced)
                            )
                        ),
                    new ActionAlwaysSucceed()
                    )
                );

            _restBehavior = new LockSelector(
                new CallWatch( "Rest",
                    new Decorator(
                        ret => !Me.IsFlying && AllowBehaviorUsage() && !SingularSettings.Instance.DisableNonCombatBehaviors,
                        new PrioritySelector(
                            // new Action(r => { _guidLastTarget = 0; return RunStatus.Failure; }),
                            Spell.WaitForGcdOrCastOrChannel(),
                            _lostControlBehavior,
                            new HookExecutor(HookName(BehaviorType.Rest))
                            )
                        )
                    )
                );

            _preCombatBuffsBehavior = new LockSelector(
                new CallWatch( "PreCombat",
                    new Decorator(  // suppress non-combat buffing if standing around waiting on DungeonBuddy or BGBuddy queues
                        ret => !Me.Mounted
                            && !SingularSettings.Instance.DisableNonCombatBehaviors
                            && AllowNonCombatBuffing(),
                        new PrioritySelector(
                            Spell.WaitForGcdOrCastOrChannel(),
                            Item.CreateUseAlchemyBuffsBehavior(),
                    // Generic.CreateFlasksBehaviour(),
                            new HookExecutor(HookName(BehaviorType.PreCombatBuffs))
                            )
                        )
                    )
                );

            _pullBuffsBehavior = new LockSelector(
                new CallWatch("PullBuffs",
                    new Decorator(
                        ret => AllowBehaviorUsage() && !Spell.IsGlobalCooldown() && !Spell.IsCastingOrChannelling(),
                        new HookExecutor(HookName(BehaviorType.PullBuffs))
                        )
                    )
                );

            _combatBuffsBehavior = new LockSelector(
                new CallWatch("CombatBuffs",
                    new Decorator(
                        ret => AllowBehaviorUsage() && !Spell.IsGlobalCooldown() && !Spell.IsCastingOrChannelling(),
                        new PrioritySelector(
                            new Decorator(ret => !HotkeyDirector.IsCombatEnabled, new ActionAlwaysSucceed()),
                            Generic.CreateUseTrinketsBehaviour(),
                            Generic.CreatePotionAndHealthstoneBehavior(),
                            Generic.CreateRacialBehaviour(),
                            new HookExecutor(HookName(BehaviorType.CombatBuffs))
                            )
                        )
                    )
                );

            _healBehavior = new LockSelector(
                new CallWatch("Heal",
                    _lostControlBehavior,
                    new Decorator(
                        ret => Kite.IsKitingActive(),
                        new HookExecutor(HookName("KitingBehavior"))
                        ),
                    new Decorator(
                        ret => AllowBehaviorUsage() && !Spell.IsGlobalCooldown() && !Spell.IsCastingOrChannelling(),
                        new HookExecutor(HookName(BehaviorType.Heal))
                        )
                    )
                );

            _pullBehavior = new LockSelector(
                new CallWatch("Pull",
                    new Decorator(
                        ret => AllowBehaviorUsage(), // && (!Me.GotTarget || !Blacklist.Contains(Me.CurrentTargetGuid, BlacklistFlags.Combat)),
                        new PrioritySelector(
                            new Decorator(
                                ret => !HotkeyDirector.IsCombatEnabled,
                                new ActionAlwaysSucceed()
                                ),
    #if BOTS_NOT_CALLING_PULLBUFFS
                            _pullBuffsBehavior,
    #endif
                            CreateLogTargetChanges("<<< PULL >>>"),
                            new HookExecutor(HookName(BehaviorType.Pull))
                            )
                        )
                    )
                );

            _combatBehavior = new LockSelector(
                new CallWatch("Combat",
                    new Decorator(
                        ret => AllowBehaviorUsage(), // && (!Me.GotTarget || !Blacklist.Contains(Me.CurrentTargetGuid, BlacklistFlags.Combat)),
                        new PrioritySelector(
                            new Decorator(
                                ret => !HotkeyDirector.IsCombatEnabled,
                                new ActionAlwaysSucceed()
                                ),
                            CreateLogTargetChanges("<<< ADD >>>"),
                            new HookExecutor(HookName(BehaviorType.Combat))
                            )
                        )
                    )
                );
        }

        internal static string HookName(string name)
        {
            return "Singular." + name;
        }

        internal static string HookName(BehaviorType typ)
        {
            return "Singular." + typ.ToString();
        }

        private static bool AllowBehaviorUsage()
        {
#if TESTING_WHILE_IN_VEHICLE_COMPLETED
            return (!IsQuestBotActive || !Me.InVehicle) && (!Me.IsOnTransport || Me.Transport.Entry == 56171);
#else
            // The boss 'Elegon' sits on a transport, this is just one of several examples why bot needs to fight back when on a transport while in an dungeon.
            // return (IsDungeonBuddyActive || !Me.IsOnTransport || Me.Transport.Entry == 56171 || Me.IsInInstance);
            
            return !IsQuestBotActive || (!Me.InVehicle && !PetBattleInProgress());
            // return true; 
#endif
        }

        private static bool AllowNonCombatBuffing()
        {
            if (IsBgBotActive && !Battlegrounds.IsInsideBattleground)
                return false;

            if (IsDungeonBuddyActive && !Me.IsInInstance)
                return false;

            if (!AllowBehaviorUsage())
                return false;

            return true;
        }

        private static bool _lastPetBattleCheck = false;
        private static WaitTimer _timerLastPetBattleCheck = new WaitTimer(TimeSpan.FromSeconds(1));

        private static bool PetBattleInProgress()
        {
            if (!_timerLastPetBattleCheck.IsFinished)
                return _lastPetBattleCheck;

            _timerLastPetBattleCheck.Reset();
            List<string> results = Lua.GetReturnValues("return C_PetBattles.IsTrapAvailable()");
            try
            {
                _lastPetBattleCheck = (results[1] != "0");
            }
            catch
            {
                _lastPetBattleCheck = false;
            }
            return false;
        }

        /// <summary>
        /// Ensures we have a composite for the given BehaviorType.  
        /// </summary>
        /// <param name="error">true: report error if composite not found, false: allow null composite</param>
        /// <param name="type">BehaviorType that should be loaded</param>
        /// <returns>true: composite loaded and saved to hook, false: failure</returns>
        private bool EnsureComposite(bool error, WoWContext context, BehaviorType type)
        {
            int count = 0;
            Composite composite;

            Logger.WriteDebug("Creating " + type + " behavior.");

            composite = CompositeBuilder.GetComposite(Class, TalentManager.CurrentSpec, type, context, out count);

            // handle those composites we need to default if not found
            if (composite == null)
            {
                if (type == BehaviorType.Rest)
                    composite = Helpers.Rest.CreateDefaultRestBehaviour();
            }

            TreeHooks.Instance.ReplaceHook(HookName(type), composite);

            if ((composite == null || count <= 0) && error)
            {
                StopBot(string.Format("Singular does not support {0} for this {1} {2} in {3} context!", type, StyxWoW.Me.Class, TalentManager.CurrentSpec, context));
                return false;
            }

            return composite != null;
        }

        private static Composite CreateLogTargetChanges(string sType)
        {
            return new Action(r =>
                {
                    // there are moments where CurrentTargetGuid != 0 but CurrentTarget == null. following
                    // .. tries to handle by only checking CurrentTarget reference and treating null as guid = 0
                    if ((SingularSettings.Debug && Me.CurrentTargetGuid != _guidLastTarget))
                    {
                        if (Me.CurrentTarget == null)
                        {
                            if (_guidLastTarget != 0)
                            {
                                Logger.WriteDebug(sType + " CurrentTarget now: (null)");
                                _guidLastTarget = 0;
                            }
                        }
                        else
                        {
                            _guidLastTarget = Me.CurrentTargetGuid;

                            string info = "";
                            WoWUnit target = Me.CurrentTarget;

                            if (Styx.CommonBot.POI.BotPoi.Current.Guid == Me.CurrentTargetGuid)
                                info += string.Format(", IsBotPoi={0}", Styx.CommonBot.POI.BotPoi.Current.Type);

                            if (Styx.CommonBot.Targeting.Instance.TargetList.Contains(Me.CurrentTarget))
                                info += string.Format(", TargetIndex={0}", Styx.CommonBot.Targeting.Instance.TargetList.IndexOf(Me.CurrentTarget) + 1);

                            Logger.WriteDebug(sType + " CurrentTarget now: {0} h={1:F1}%, maxh={2}, d={3:F1} yds, box={4:F1}, player={5}, hostile={6}, faction={7}, loss={8}, facing={9}" + info,
                                target.SafeName(),
                                target.HealthPercent,
                                target.MaxHealth,
                                target.Distance,
                                target.CombatReach,
                                target.IsPlayer.ToYN(),
                                target.IsHostile.ToYN(),
                                target.FactionId ,
                                target.InLineOfSpellSight.ToYN(),
                                Me.IsSafelyFacing(target).ToYN()
                                );
                        }

                        _timerLastTarget.Reset();
                    }

                    return RunStatus.Failure;
                });

        }

        private static int _prevPullDistance = -1;

        private static void MonitorQuestingPullDistance()
        {
            if (SingularRoutine.IsQuestBotActive)
            {
                if ( SingularSettings.Instance.PullDistanceOverride == CharacterSettings.Instance.PullDistance)
                {
                    int newPullDistance = 0;
                    switch (Me.Class)
                    {
                        case WoWClass.DeathKnight:
                        case WoWClass.Monk:
                        case WoWClass.Paladin:
                        case WoWClass.Rogue:
                        case WoWClass.Warrior:
                            break;

                        default:
                            if (Me.Specialization == WoWSpec.None || Me.Specialization == WoWSpec.DruidFeral || Me.Specialization == WoWSpec.DruidGuardian || Me.Specialization == WoWSpec.ShamanEnhancement)
                                break;

                            newPullDistance = 40;
                            break;
                    }

                    if (newPullDistance != 0)
                    {
                        Logger.Write(Color.White, "Quest Profile set Pull Distance to {0}, forcing to {1} for next Pull", CharacterSettings.Instance.PullDistance, newPullDistance);
                        CharacterSettings.Instance.PullDistance = newPullDistance;
                        _prevPullDistance = newPullDistance;
                    }
                }

                if (_prevPullDistance != CharacterSettings.Instance.PullDistance)
                {
                    _prevPullDistance = CharacterSettings.Instance.PullDistance;
                    if ( _prevPullDistance != -1 )
                        Logger.WriteDebug( Color.White, "warning: Pull Distance set to {0} yds by Questing Profile", _prevPullDistance );
                }
            }
        }

        #region Nested type: LockSelector

        /// <summary>
        /// This behavior wraps the child behaviors in a 'FrameLock' which can provide a big performance improvement 
        /// if the child behaviors makes multiple api calls that internally run off a frame in WoW in one CC pulse.
        /// </summary>
        private class LockSelector : PrioritySelector
        {
            public LockSelector(params Composite[] children)
                : base(children)
            {
            }

            public override RunStatus Tick(object context)
            {
                using (StyxWoW.Memory.AcquireFrame())
                {
                    return base.Tick(context);
                }
            }
        }

        #endregion
    }


    public class CallWatch : PrioritySelector
    {
        public static DateTime LastCall { get; set; }
        public static ulong CountCall { get; set; }
        public static double WarnTime { get; set; }

        public string Name { get; set; }

        private static bool _init = false;

        private static void Initialize()
        {
            if (_init)
                return;

            _init = true;
            SingularRoutine.OnBotEvent += (src, arg) =>
            {
                // reset time on Start
                if (arg.Event == SingularBotEvent.BotStart)
                    LastCall = DateTime.Now;
                else if (arg.Event == SingularBotEvent.BotStop)
                {
                    if (SingularSettings.Debug)
                    {
                        DateTime rightNow = DateTime.Now;
                        if ((rightNow - LastCall).TotalSeconds > WarnTime && LastCall != DateTime.MinValue)
                        {
                            if ( SingularSettings.Debug)
                                Logger.WriteDebug(Color.HotPink, "warning: {0:F1} seconds since BotBase last called Singular (now in OnBotStop)", (rightNow - LastCall).TotalSeconds);
                            else
                                Logger.WriteFile("warning: {0:F1} seconds since BotBase last called Singular (now in OnBotStop)", (rightNow - LastCall).TotalSeconds);
                        }
                    }
                }
            };
        }

        public CallWatch(string name, params Composite[] children)
            : base(children)
        {
            Initialize();

            if (WarnTime == 0)
                WarnTime = 5;

            Name = name;
            LastCall = DateTime.MinValue;
        }

        protected override IEnumerable<RunStatus> Execute(object context)
        {
            CountCall++;

            if (SingularSettings.Debug)
            {
                DateTime rightNow = DateTime.Now;
                if ((rightNow - LastCall).TotalSeconds > WarnTime && LastCall != DateTime.MinValue )
                    Logger.WriteDebug(Color.HotPink, "warning: {0:F1} seconds since BotBase last called Singular (now in {1})", (rightNow - LastCall).TotalSeconds, Name);
            }

            IEnumerable<RunStatus> ret = base.Execute(context);

            LastCall = DateTime.Now;
            return ret;
        }
    }
}