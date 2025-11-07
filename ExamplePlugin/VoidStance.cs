// VoidFiendToggle.cs
// BepInEx 5, R2API ContentManagement + LanguageAPI required

using BepInEx;
using BepInEx.Configuration;
using EntityStates;
using R2API;
using R2API.Utils;
using RoR2;
using RoR2.Skills;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

[assembly: NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod)]

namespace CloseAirSupport.VoidFiendToggle
{
    [BepInDependency(R2API.ContentManagement.R2APIContentManager.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(LanguageAPI.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin("cameron.cas.voidfiend.toggle", "Void Fiend Stance Toggle", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        // Config
        private static ConfigEntry<bool> cfgFreezeMeter;
        private static ConfigEntry<KeyCode> cfgTestHotkey; // optional manual test

        // Content
        internal static SkillDef ToggleSkillDef;
        internal static BuffDef ToggleOverrideBuff; // hidden marker buff so our hooks know when to freeze corruption

        private static BodyIndex voidFiendBodyIndex = BodyIndex.None;
        private static GameObject voidFiendBodyPrefab;

        // Reflection cache for VoidSurvivorController
        private static Type tVSC;
        private static FieldInfo fCorruption;
        private static FieldInfo fIsCorrupted;
        private static MethodInfo mSetCorruptedServer; // optional direct call if present
        private static MethodInfo mAddCorruption;
        private static MethodInfo mSetCorruptionServer; // if present in your version

        public void Awake()
        {
            cfgFreezeMeter = Config.Bind("General", "FreezeMeterInToggleMode", true, "If true, corruption is clamped to 0 or 100 while using the Stance Toggle skill.");
            cfgTestHotkey = Config.Bind("Debug", "ManualToggleKey", KeyCode.None, "Optional hotkey to toggle when playing as Void Fiend (client-side test).");

            // Language
            LanguageAPI.Add("VF_TOGGLE_NAME", "Stance Toggle");
            LanguageAPI.Add("VF_TOGGLE_DESC", "Instantly switch between <style=cIsHealth>Controlled</style> and <style=cIsHealth>Corrupted</style> forms. " +
                                               (cfgFreezeMeter.Value ? "Meter is overridden while active." : "Meter continues to change normally."));

            // Create buff (hidden, non-stack)
            ToggleOverrideBuff = ScriptableObject.CreateInstance<BuffDef>();
            ToggleOverrideBuff.name = "CAS_VoidFiend_ToggleOverrideBuff";
            ToggleOverrideBuff.buffColor = Color.clear;
            ToggleOverrideBuff.isDebuff = false;
            ToggleOverrideBuff.canStack = false;
            ToggleOverrideBuff.eliteDef = null;
            ToggleOverrideBuff.iconSprite = null; // hidden
            ContentAddition.AddBuffDef(ToggleOverrideBuff);

            // Create entity state + skilldef
            ContentAddition.AddEntityState<ToggleStanceState>(out _);

            ToggleSkillDef = ScriptableObject.CreateInstance<SkillDef>();
            ToggleSkillDef.skillName = "VF_TOGGLE";
            ToggleSkillDef.skillNameToken = "VF_TOGGLE_NAME";
            ToggleSkillDef.skillDescriptionToken = "VF_TOGGLE_DESC";
            ToggleSkillDef.activationState = new EntityStates.SerializableEntityStateType(typeof(ToggleStanceState));
            ToggleSkillDef.activationStateMachineName = "Weapon";
            ToggleSkillDef.baseMaxStock = 1;
            ToggleSkillDef.rechargeStock = 1;
            ToggleSkillDef.baseRechargeInterval = 0.1f;
            ToggleSkillDef.beginSkillCooldownOnSkillEnd = false;
            ToggleSkillDef.canceledFromSprinting = false;
            ToggleSkillDef.cancelSprintingOnActivation = false;
            ToggleSkillDef.isCombatSkill = false;
            ToggleSkillDef.mustKeyPress = true;
            ToggleSkillDef.interruptPriority = EntityStates.InterruptPriority.Any;
            ToggleSkillDef.icon = null;
            ContentAddition.AddSkillDef(ToggleSkillDef);

            // Hook catalog init so the body exists
            RoR2Application.onLoad += TryInjectSkill;

            // Cache reflection on first run
            On.RoR2.CharacterBody.Start += (orig, self) =>
            {
                orig(self);
                if (tVSC == null) CacheVoidSurvivorReflection();
            };

            // Freeze corruption if our hidden buff is present
            On.RoR2.CharacterBody.FixedUpdate += BodyFixedUpdate_ClampCorruption;

            // Optional keyboard quick toggle for testing
            On.RoR2.CharacterBody.Update += CharacterBody_UpdateDebugHotkey;
        }

        private void TryInjectSkill()
        {
            voidFiendBodyPrefab = BodyCatalog.FindBodyPrefab("VoidSurvivorBody");
            if (!voidFiendBodyPrefab)
            {
                Logger.LogWarning("VoidSurvivorBody not found; cannot inject Stance Toggle.");
                return;
            }
            voidFiendBodyIndex = BodyCatalog.FindBodyIndex("VoidSurvivorBody");

            var sl = voidFiendBodyPrefab.GetComponent<SkillLocator>();
            if (!sl || !sl.special || !sl.special.skillFamily)
            {
                Logger.LogWarning("VoidSurvivorBody has no valid Special skill family.");
                return;
            }

            var fam = sl.special.skillFamily;
            if (fam.variants.Any(v => v.skillDef == ToggleSkillDef)) return;

            Array.Resize(ref fam.variants, fam.variants.Length + 1);
            fam.variants[fam.variants.Length - 1] = new SkillFamily.Variant
            {
                skillDef = ToggleSkillDef,
                viewableNode = new ViewablesCatalog.Node(ToggleSkillDef.skillNameToken, false, null)
            };
        }

        private void CacheVoidSurvivorReflection()
        {
            tVSC ??= typeof(RoR2.CharacterBody).Assembly.GetType("RoR2.VoidSurvivorController");
            if (tVSC == null) { Logger.LogWarning("Could not find RoR2.VoidSurvivorController type."); return; }

            fCorruption ??= tVSC.GetField("corruption", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            fIsCorrupted ??= tVSC.GetField("isCorrupted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            mSetCorruptedServer ??= tVSC.GetMethod("ServerSetCorrupted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mSetCorruptionServer ??= tVSC.GetMethod("ServerSetCorruption", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mAddCorruption ??= tVSC.GetMethod("AddCorruption", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static void CharacterBody_UpdateDebugHotkey(On.RoR2.CharacterBody.orig_Update orig, CharacterBody self)
        {
            orig(self);
            try
            {
                if (!self || !self.hasAuthority || cfgTestHotkey.Value == KeyCode.None) return;
                if (self.bodyIndex != voidFiendBodyIndex) return;

                if (Input.GetKeyDown(cfgTestHotkey.Value))
                {
                    var vsc = self.GetComponent(tVSC);
                    if (vsc != null) ToggleNow(self, vsc as Component);
                }
            }
            catch { }
        }

        private static void BodyFixedUpdate_ClampCorruption(On.RoR2.CharacterBody.orig_FixedUpdate orig, CharacterBody self)
        {
            orig(self);

            if (!cfgFreezeMeter.Value) return;
            if (!self) return;
            if (self.bodyIndex != voidFiendBodyIndex) return;
            if (!self.HasBuff(ToggleOverrideBuff)) return;

            var vsc = self.GetComponent(tVSC);
            if (vsc == null) return;

            try
            {
                bool isCorr = fIsCorrupted != null && (bool)fIsCorrupted.GetValue(vsc);
                if (fCorruption != null)
                {
                    fCorruption.SetValue(vsc, isCorr ? 100f : 0f);
                }
                // If the game exposes a server setter, keep it in sync
                if (NetworkServer.active && mSetCorruptionServer != null)
                {
                    mSetCorruptionServer.Invoke(vsc, new object[] { isCorr ? 100f : 0f });
                }
            }
            catch { }
        }

        internal static void ToggleNow(CharacterBody body, Component vsc)
        {
            if (!body || vsc == null) return;

            try
            {
                bool isCorr = fIsCorrupted != null && (bool)fIsCorrupted.GetValue(vsc);
                bool next = !isCorr;

                if (NetworkServer.active)
                {
                    // Prefer a dedicated server call if the game exposes it
                    if (mSetCorruptedServer != null)
                    {
                        mSetCorruptedServer.Invoke(vsc, new object[] { next });
                    }
                    else
                    {
                        // Fallback: drive corruption to boundary and rely on built-in switch
                        if (fCorruption != null)
                            fCorruption.SetValue(vsc, next ? 100f : 0f);
                        if (mSetCorruptionServer != null)
                            mSetCorruptionServer.Invoke(vsc, new object[] { next ? 100f : 0f });
                    }
                }

                // Add/remove hidden buff used to freeze meter
                if (cfgFreezeMeter.Value)
                {
                    if (next) body.AddBuff(ToggleOverrideBuff);
                    else body.RemoveBuff(ToggleOverrideBuff);
                }
            }
            catch { }
        }
    }

    // === Entity State that performs the toggle ===
    public class ToggleStanceState : EntityStates.BaseSkillState
    {
        public override void OnEnter()
        {
            base.OnEnter();

            if (!characterBody) { outer.SetNextStateToMain(); return; }

            // Only server actually flips; clients just play VFX if you want
            if (NetworkServer.active)
            {
                var vsc = characterBody.GetComponent(Plugin.tVSC);
                if (vsc != null) Plugin.ToggleNow(characterBody, vsc as Component);
            }

            // Small blink of utility VFX/sound could be added here if you’d like

            outer.SetNextStateToMain();
        }

        public override InterruptPriority GetMinimumInterruptPriority() => InterruptPriority.Any;
    }
}
