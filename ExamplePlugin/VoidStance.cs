// File: Plugins/VoidStance/VoidStance.cs
#define VS_SHORT_ANIM

using BepInEx;
using BepInEx.Logging;
using R2API;
using R2API.ContentManagement;
using R2API.Utils;
using RoR2;
using RoR2.Skills;
using EntityStates;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking; // UNet HLAPI
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using IOPath = System.IO.Path;

[assembly: NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod)]

namespace VoidStanceMod
{
    [BepInPlugin(PluginGUID, PluginName, PluginVer)]
    [BepInDependency(LanguageAPI.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.MyName.VoidStance";
        public const string PluginName = "VoidStance";
        public const string PluginVer = "1.0.0";

        internal static ManualLogSource Log;
        internal static SkillDef StanceSkillDef;

        private string _pluginDir;
        private Sprite _modIcon;
        private Sprite _specialIcon;

        private void Awake()
        {
            Log = Logger;

            _pluginDir = IOPath.GetDirectoryName(Info.Location);
            _modIcon = LoadSprite("icon.png");
            _specialIcon = LoadSprite("icon_special.png");

            ContentAddition.AddEntityState<VoidStanceToggle>(out _);

            StanceSkillDef = ScriptableObject.CreateInstance<SkillDef>();
            StanceSkillDef.skillName = "VOIDSTANCE_TOGGLE";
            StanceSkillDef.skillNameToken = "VOIDSTANCE_TOGGLE_NAME";
            StanceSkillDef.skillDescriptionToken = "VOIDSTANCE_TOGGLE_DESC";
            StanceSkillDef.icon = _specialIcon != null ? _specialIcon : _modIcon;
            StanceSkillDef.activationState = new SerializableEntityStateType(typeof(VoidStanceToggle));
            StanceSkillDef.activationStateMachineName = "Weapon";
            StanceSkillDef.baseMaxStock = 1;
            StanceSkillDef.rechargeStock = 1;
            StanceSkillDef.baseRechargeInterval = 2f;
            StanceSkillDef.beginSkillCooldownOnSkillEnd = true;
            StanceSkillDef.canceledFromSprinting = false;
            StanceSkillDef.cancelSprintingOnActivation = false;
            StanceSkillDef.fullRestockOnAssign = true;
            StanceSkillDef.isCombatSkill = true;
            StanceSkillDef.interruptPriority = InterruptPriority.Any;
            StanceSkillDef.mustKeyPress = true;
            StanceSkillDef.requiredStock = 1;
            StanceSkillDef.resetCooldownTimerOnUse = false;
            ContentAddition.AddSkillDef(StanceSkillDef);

            LanguageAPI.Add("VOIDSTANCE_TOGGLE_NAME", "Void Stance");
            LanguageAPI.Add("VOIDSTANCE_TOGGLE_DESC", "Toggle between Controlled and Corrupted at will. Prevents passive corruption drift and auto-transforms.");

            RoR2Application.onLoad += InjectIntoVoidFiend;

            CharacterBody.onBodyStartGlobal += TryAttachControllers;
            On.RoR2.CharacterBody.Start += HookBodyStartAttach;

            Log.LogInfo("[VoidStance] Loaded 1.3.3");
        }

        private Sprite LoadSprite(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(_pluginDir)) return null;
                var path = IOPath.Combine(_pluginDir, fileName);
                if (!File.Exists(path)) return null;
                var data = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(data)) return null;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception e)
            {
                Log?.LogWarning("[VoidStance] LoadSprite failed for '" + fileName + "': " + e.Message);
                return null;
            }
        }

        private void InjectIntoVoidFiend()
        {
            try
            {
                var vfBody = Addressables.LoadAssetAsync<GameObject>("RoR2/DLC1/VoidSurvivor/VoidSurvivorBody.prefab").WaitForCompletion();
                if (!vfBody)
                {
                    Log.LogError("[VoidStance] Could not load VoidSurvivorBody.");
                    return;
                }

                var locator = vfBody.GetComponent<SkillLocator>();
                if (!locator || !locator.special || !locator.special.skillFamily)
                {
                    Log.LogError("[VoidStance] Void Fiend special family not found.");
                    return;
                }

                var fam = locator.special.skillFamily;
                if (!fam.variants.Any(v => v.skillDef == StanceSkillDef))
                {
                    Array.Resize(ref fam.variants, fam.variants.Length + 1);
                    fam.variants[^1] = new SkillFamily.Variant
                    {
                        skillDef = StanceSkillDef,
                        unlockableDef = null,
                        viewableNode = new ViewablesCatalog.Node(StanceSkillDef.skillNameToken, false, null)
                    };
                }

                Log.LogInfo("[VoidStance] Added 'Void Stance' to Void Fiend Special.");
            }
            catch (Exception e)
            {
                Log.LogError("[VoidStance] Injection failed: " + e);
            }
        }

        private void HookBodyStartAttach(On.RoR2.CharacterBody.orig_Start orig, CharacterBody self)
        {
            orig(self);
            TryAttachControllers(self);
        }

        private void TryAttachControllers(CharacterBody body)
        {
            if (!body) return;

            bool looksLikeVF =
                (body.gameObject && body.gameObject.name.IndexOf("VoidSurvivor", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(body.baseNameToken) && body.baseNameToken.IndexOf("VOID_SURVIVOR", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (BodyCatalog.GetBodyName(body.bodyIndex)?.IndexOf("VoidSurvivor", StringComparison.OrdinalIgnoreCase) >= 0);

            if (!looksLikeVF) return;

            if (!body.GetComponent<VoidStanceController>())
                body.gameObject.AddComponent<VoidStanceController>();

            if (!body.GetComponent<VoidStanceNet>())
                body.gameObject.AddComponent<VoidStanceNet>();
        }

        // === Special state ===
        public class VoidStanceToggle : BaseSkillState
        {
            public override void OnEnter()
            {
                base.OnEnter();
#if VS_SHORT_ANIM
                characterBody?.SetAimTimer(0.2f);
                PlayCrossfade("Gesture, Additive", "BufferEmpty", "BufferEmpty.playbackRate", 0.1f, 0.05f);
#else
                characterBody?.SetAimTimer(0.6f);
                PlayCrossfade("Gesture, Additive", "PrepBarrage", "PrepBarrage.playbackRate", 0.25f, 0.05f);
#endif
                if (!characterBody) return;

                var ctrl = characterBody.GetComponent<VoidStanceController>();
                if (!ctrl) ctrl = characterBody.gameObject.AddComponent<VoidStanceController>();

                if (!NetworkServer.active)
                {
                    var net = characterBody.GetComponent<VoidStanceNet>();
                    if (net != null) net.RequestToggle();
                    else Plugin.Log?.LogWarning("[VoidStance] Net component missing on client.");
                }
                else
                {
                    ServerToggleNow(ctrl);
                }
            }

            private void ServerToggleNow(VoidStanceController ctrl)
            {
                ctrl.ResolveAndBindController();
                if (ctrl.LockedIsHigh()) ctrl.GoControlledNow();
                else ctrl.GoCorruptedNow();
            }

            public override void FixedUpdate()
            {
                base.FixedUpdate();
                if (fixedAge >= 0.2f) outer.SetNextStateToMain();
            }

            public override InterruptPriority GetMinimumInterruptPriority() => InterruptPriority.Skill;
        }
    }

    // ==== Network bridge ====
    public class VoidStanceNet : NetworkBehaviour
    {
        CharacterBody body;
        VoidStanceController ctrl;

        void Awake()
        {
            body = GetComponent<CharacterBody>();
            ctrl = GetComponent<VoidStanceController>();
        }

        public void RequestToggle()
        {
            if (!isServer) CmdToggle();
            else ServerToggle();
        }

        [Command] private void CmdToggle() => ServerToggle();

        private void ServerToggle()
        {
            if (!ctrl) ctrl = GetComponent<VoidStanceController>();
            if (!ctrl) return;

            ctrl.ResolveAndBindController();

            if (ctrl.LockedIsHigh()) ctrl.GoControlledNow();
            else ctrl.GoCorruptedNow();

            TargetOnToggled(connectionToClient, ctrl.LockedIsHigh());
        }

        [TargetRpc] private void TargetOnToggled(NetworkConnection target, bool isCorrupted) { }
    }

    // ==== Main controller ====
    public class VoidStanceController : MonoBehaviour
    {
        CharacterBody body;
        GenericSkill special;

        Component vfController;
        Transform boundTransform;

        FieldInfo fldCorruption;
        PropertyInfo propCorruption;
        FieldInfo fldIsCorrupted;
        FieldInfo fldCorruptionActive;
        // thresholds kept, but we DO NOT touch them
        FieldInfo fldMinThreshold;
        FieldInfo fldMaxThreshold;

        FieldInfo[] corruptionRelatedFloats; // ONLY drift/rate-like floats

        MethodInfo mEnterCorruption;
        MethodInfo mExitCorruption;
        MethodInfo mSetCorruptionActiveBool;
        MethodInfo mTryTransformToCorrupted;
        MethodInfo mTryTransformToBase;
        MethodInfo mRequestTransformationBool;

        float lockedCorruption = float.NaN;
        bool corruptionIsPercent = true;

        // Persistent selection policy
        bool userChoseStance;           // true iff player selected our special in loadout
        bool overrideApplied;           // whether we've applied the contextual override

        float rebindTick;
        bool dumped;

        void Start()
        {
            body = GetComponent<CharacterBody>();
            special = body?.skillLocator?.special;

            // Initialize based on actual selection (no forcing)
            userChoseStance = HasVoidStanceSelected();

            ResolveAndBindController();
            SnapLockValue();
        }

        void OnDestroy()
        {
            // cleanup any persistent override
            TryUnsetOverride();
        }

        bool HasVoidStanceSelected()
        {
            if (!special || Plugin.StanceSkillDef == null) return false;
            return special.skillDef == Plugin.StanceSkillDef;
        }

        bool FamilyContainsOurDef()
        {
            if (!special || special.skillFamily == null || Plugin.StanceSkillDef == null) return false;
            var fam = special.skillFamily;
            for (int i = 0; i < fam.variants.Length; i++)
                if (fam.variants[i].skillDef == Plugin.StanceSkillDef)
                    return true;
            return false;
        }

        void TryApplyOverride()
        {
            if (overrideApplied) return;
            if (!special || Plugin.StanceSkillDef == null) return;
            if (!FamilyContainsOurDef()) return;
            special.SetSkillOverride(body, Plugin.StanceSkillDef, GenericSkill.SkillOverridePriority.Contextual);
            overrideApplied = true;
        }

        void TryUnsetOverride()
        {
            if (!overrideApplied) return;
            if (!special || Plugin.StanceSkillDef == null) { overrideApplied = false; return; }
            special.UnsetSkillOverride(body, Plugin.StanceSkillDef, GenericSkill.SkillOverridePriority.Contextual);
            overrideApplied = false;
        }

        public void ForceDump() => DumpAllComponents();

        void FixedUpdate()
        {
            // Rebind in case components were rebuilt
            rebindTick -= Time.fixedDeltaTime;
            if (rebindTick <= 0f)
            {
                rebindTick = 0.1f;
                if (!vfController) ResolveAndBindController();
            }

            // Track user's real selection once; we do not force-selection if they didn't pick us.
            // If they picked us, keep a contextual override active persistently so rebuilds cannot snap to default.
            if (HasVoidStanceSelected()) userChoseStance = true;

            if (userChoseStance)
                TryApplyOverride();
            else
                TryUnsetOverride();

            if (!NetworkServer.active) return;
            if (!vfController) return;
            if (!userChoseStance) return; // only operate if player actually chose Void Stance

            StrongLockCorruption();
        }

        void Update()
        {
            if (!NetworkServer.active) return;
            if (!vfController) return;
            if (!userChoseStance) return;

            StrongLockCorruption();
        }

        // === Public helpers ===
        public bool LockedIsHigh()
        {
            if (float.IsNaN(lockedCorruption)) lockedCorruption = ReadCorruption();
            var v = float.IsNaN(lockedCorruption) ? 0f : lockedCorruption;
            return v > (corruptionIsPercent ? 50f : 0.5f);
        }

        public void GoCorruptedNow()
        {
            ResolveAndBindController();

            // Maintain selection if the user had chosen us
            if (HasVoidStanceSelected()) userChoseStance = true;
            if (userChoseStance) TryApplyOverride();

            // prefer flags via methods; keep active logic off to avoid drift
            SafeInvoke(mSetCorruptionActiveBool, vfController, new object[] { false });
            SafeInvoke(mEnterCorruption, vfController, null);
            SafeInvoke(mTryTransformToCorrupted, vfController, null);
            SafeInvoke(mRequestTransformationBool, vfController, new object[] { true });

            SafeWriteBool(fldIsCorrupted, vfController, true);
            SafeWriteBool(fldCorruptionActive, vfController, false);

            lockedCorruption = corruptionIsPercent ? 100f : 1f;
            WriteCorruption(lockedCorruption);

            Plugin.Log?.LogInfo("[VoidStance] -> Corrupted");
        }

        public void GoControlledNow()
        {
            ResolveAndBindController();

            if (HasVoidStanceSelected()) userChoseStance = true;
            if (userChoseStance) TryApplyOverride();

            SafeInvoke(mSetCorruptionActiveBool, vfController, new object[] { false });
            SafeInvoke(mExitCorruption, vfController, null);
            SafeInvoke(mTryTransformToBase, vfController, null);
            SafeInvoke(mRequestTransformationBool, vfController, new object[] { false });

            SafeWriteBool(fldIsCorrupted, vfController, false);
            SafeWriteBool(fldCorruptionActive, vfController, false);

            lockedCorruption = 0f;
            WriteCorruption(lockedCorruption);

            Plugin.Log?.LogInfo("[VoidStance] -> Controlled");
        }

        // === Binding ===
        public void ResolveAndBindController()
        {
            vfController = null;
            boundTransform = null;

            fldCorruption = null; propCorruption = null;
            fldIsCorrupted = null; fldCorruptionActive = null;
            fldMinThreshold = null; fldMaxThreshold = null;
            corruptionRelatedFloats = null;

            mEnterCorruption = null; mExitCorruption = null;
            mSetCorruptionActiveBool = null;
            mTryTransformToCorrupted = null;
            mTryTransformToBase = null;
            mRequestTransformationBool = null;

            if (!body) return;

            var roots = new List<Transform> { body.transform };
            var ml = body.modelLocator;
            if (ml && ml.modelTransform) roots.Add(ml.modelTransform);
            if (ml && ml.modelBaseTransform) roots.Add(ml.modelBaseTransform);
            if (body.masterObject) roots.Add(body.masterObject.transform);

            foreach (var r in roots.Where(x => x))
                foreach (var c in r.GetComponentsInChildren<Component>(true))
                    if (c && c.GetType().Name.Equals("VoidSurvivorController", StringComparison.OrdinalIgnoreCase))
                        if (BindFromComponent(c)) return;

            foreach (var r in roots.Where(x => x))
                foreach (var c in r.GetComponentsInChildren<Component>(true))
                {
                    if (!c) continue;
                    var t = c.GetType();
                    if (HasAnyMethod(t, new[] { "EnterCorruption","EnterCorrupted","StartCorruption",
                                            "ExitCorruption","ExitCorrupted","StopCorruption",
                                            "SetCorruptionActive","TryTransformToCorrupted","TryTransformToBase","RequestTransformation"}))
                        if (BindFromComponent(c)) return;
                }

            foreach (var r in roots.Where(x => x))
                foreach (var c in r.GetComponentsInChildren<Component>(true))
                    if (c && HasPlausibleCorruptionValue(c, out _, out _))
                        if (BindFromComponent(c)) return;

            if (!dumped) { dumped = true; DumpAllComponents(); }
        }

        bool BindFromComponent(Component c)
        {
            vfController = c;
            boundTransform = c.transform;
            var type = c.GetType();

            fldCorruption = FirstField(type, typeof(float), new[] { "corruption", "corruptionFrac", "corruptionFraction", "corruptionPercent", "corruptionValue" })
                            ?? GuessCorruptionFloatField(type, c);
            if (fldCorruption == null)
                propCorruption = FirstProperty(type, typeof(float), new[] { "Corruption", "CorruptionFraction", "CorruptionPercent", "Value" });

            fldIsCorrupted = FirstField(type, typeof(bool), new[] { "isCorrupted", "isCorruption", "corrupted", "isInCorruptedMode" });
            fldCorruptionActive = FirstField(type, typeof(bool), new[] { "corruptionActive", "isCorruptionActive", "isCorruptionOn" });
            fldMinThreshold = FirstField(type, typeof(float), new[] { "minCorruptionToTransform", "minCorruptionToActivate", "minCorruption" });
            fldMaxThreshold = FirstField(type, typeof(float), new[] { "maxCorruptionToRevert", "maxCorruptionToDeactivate", "maxCorruption" });

            // collect corruption-related floats EXCLUDING the primary value and EXCLUDING thresholds/limits
            var allFloats = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                               .Where(f => f.FieldType == typeof(float))
                               .ToArray();

            bool ShouldZero(FieldInfo f)
            {
                var n = f.Name.ToLowerInvariant();
                if (fldCorruption != null && f == fldCorruption) return false; // never touch the primary value

                // exclude threshold/limit/bounds and target values
                if (n.Contains("min") || n.Contains("max") || n.Contains("threshold") || n.Contains("limit") || n.Contains("bound"))
                    return false;
                if (n.Contains("toactivate") || n.Contains("totransform")) return false;

                // exclude obvious “state” caches
                if (n.EndsWith("percent") || n.EndsWith("fraction") || n.EndsWith("value")) return false;

                // only zero drivers of change over time
                return n.Contains("rate") || n.Contains("drift") || n.Contains("delta") || n.Contains("speed")
                    || n.Contains("gain") || n.Contains("loss") || n.Contains("regen") || n.Contains("persecond");
            }

            var list = new List<FieldInfo>();
            foreach (var f in allFloats)
            {
                var n = f.Name.ToLowerInvariant();
                if (!n.Contains("corrupt")) continue;
                if (ShouldZero(f)) list.Add(f);
            }
            corruptionRelatedFloats = list.ToArray();

            mEnterCorruption = FirstMethod(type, new[] { "EnterCorruption", "EnterCorrupted", "StartCorruption" }, Type.EmptyTypes);
            mExitCorruption = FirstMethod(type, new[] { "ExitCorruption", "ExitCorrupted", "StopCorruption" }, Type.EmptyTypes);
            mSetCorruptionActiveBool = FirstMethod(type, new[] { "SetCorruptionActive", "SetCorrupted" }, new[] { typeof(bool) });
            mTryTransformToCorrupted = FirstMethod(type, new[] { "TryTransformToCorrupted", "TryEnterCorruption" }, Type.EmptyTypes);
            mTryTransformToBase = FirstMethod(type, new[] { "TryTransformToBase", "TryExitCorruption" }, Type.EmptyTypes);
            mRequestTransformationBool = FirstMethod(type, new[] { "RequestTransformation", "RequestCorruption" }, new[] { typeof(bool) });

            var v = ReadCorruption();
            if (!float.IsNaN(v))
            {
                // treat 0..1 as fraction, >1..<=100 as percent
                corruptionIsPercent = (v > 1.0001f && v <= 100f);
            }
            else
            {
                corruptionIsPercent = false; // default to fraction
            }

            Plugin.Log?.LogInfo($"[VoidStance] Bound: {type.FullName} @ {PathOf(boundTransform)} | value={(fldCorruption?.Name ?? propCorruption?.Name ?? "<?>")} | floatsToZero={corruptionRelatedFloats?.Length ?? 0}");
            return true;
        }

        // === Lock logic (no threshold edits) ===
        void StrongLockCorruption()
        {
            if (float.IsNaN(lockedCorruption))
                lockedCorruption = ReadCorruption();
            if (float.IsNaN(lockedCorruption))
                lockedCorruption = 0f;

            // clamp to valid domain
            if (corruptionIsPercent)
                lockedCorruption = Mathf.Clamp(lockedCorruption, 0f, 100f);
            else
                lockedCorruption = Mathf.Clamp01(lockedCorruption);

            // write only the corruption scalar; let native code manage flags/UI
            WriteCorruption(lockedCorruption);

            // keep drift logic off, but do not toggle isCorrupted every tick
            SafeInvoke(mSetCorruptionActiveBool, vfController, new object[] { false });

            // zero only driver floats (rates/drift), not thresholds
            if (corruptionRelatedFloats != null)
                for (int i = 0; i < corruptionRelatedFloats.Length; i++)
                    SafeWriteFloat(corruptionRelatedFloats[i], vfController, 0f);
        }

        // === Diagnostics dump ===
        void DumpAllComponents()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== VoidStance Component Dump ===");
                var ml = body?.modelLocator;
                var roots = new List<Transform>();
                if (body) roots.Add(body.transform);
                if (ml && ml.modelTransform) roots.Add(ml.modelTransform);
                if (ml && ml.modelBaseTransform) roots.Add(ml.modelBaseTransform);
                if (body && body.masterObject) roots.Add(body.masterObject.transform);

                foreach (var r in roots.Where(x => x))
                {
                    sb.AppendLine($"-- ROOT: {PathOf(r)}");
                    foreach (var c in r.GetComponentsInChildren<Component>(true))
                    {
                        if (!c) continue;
                        var t = c.GetType();
                        sb.AppendLine($"  {PathOf(c.transform)}  ::  {t.FullName}");
                        if (HasAnyMethod(t, new[] { "EnterCorruption", "ExitCorruption", "SetCorruptionActive", "TryTransformToCorrupted", "TryTransformToBase", "RequestTransformation" }))
                            sb.AppendLine("    [*] has corruption methods");
                        if (HasPlausibleCorruptionValue(c, out var f, out var p))
                            sb.AppendLine($"    [*] has corruption value via {(f != null ? ("field:" + f.Name) : ("prop:" + p.Name))}");
                    }
                }

                var dir = IOPath.Combine(Paths.BepInExRootPath, "LogOutput");
                Directory.CreateDirectory(dir);
                var filePath = IOPath.Combine(dir, "VoidStance.dump.txt");
                File.WriteAllText(filePath, sb.ToString());
                Plugin.Log?.LogWarning($"[VoidStance] Dump written: {filePath}");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[VoidStance] Dump failed: " + e.Message);
            }
        }

        // === Reflection helpers ===
        static bool HasAnyMethod(Type t, IEnumerable<string> names)
        {
            foreach (var n in names)
                if (t.GetMethod(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null)
                    return true;
            return false;
        }

        static bool HasPlausibleCorruptionValue(Component c, out FieldInfo fOut, out PropertyInfo pOut)
        {
            fOut = null; pOut = null;
            var t = c.GetType();
            var f = FirstField(t, typeof(float), new[] { "corruption", "corruptionFraction", "corruptionFrac", "corruptionPercent", "corruptionValue" })
                    ?? GuessCorruptionFloatField(t, c);
            PropertyInfo p = null;
            if (f == null)
                p = FirstProperty(t, typeof(float), new[] { "Corruption", "CorruptionFraction", "CorruptionPercent", "Value" });

            float val = float.NaN;
            try
            {
                if (f != null) val = (float)f.GetValue(c);
                else if (p != null) val = (float)p.GetValue(c, null);
            }
            catch { }

            if (!float.IsNaN(val) && ((val >= 0f && val <= 1f) || (val >= 0f && val <= 100f)))
            {
                fOut = f; pOut = p; return true;
            }
            return false;
        }

        static FieldInfo FirstField(Type t, Type ft, IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == ft) return f;
            }
            return null;
        }

        static PropertyInfo FirstProperty(Type t, Type pt, IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.PropertyType == pt && p.CanRead && p.CanWrite) return p;
            }
            return null;
        }

        static MethodInfo FirstMethod(Type t, IEnumerable<string> names, Type[] sig)
        {
            foreach (var n in names)
            {
                var m = t.GetMethod(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, sig, null);
                if (m != null) return m;
            }
            return null;
        }

        static MethodInfo FirstMethod(Type t, IEnumerable<string> names, Type emptySig)
            => FirstMethod(t, names, Type.EmptyTypes);

        static FieldInfo GuessCorruptionFloatField(Type t, object inst)
        {
            try
            {
                var all = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                           .Where(f => f.FieldType == typeof(float)).ToArray();

                var withKeyword = all.Where(f => f.Name.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                var domain = (withKeyword.Length > 0) ? withKeyword : all;

                foreach (var f in domain)
                {
                    try
                    {
                        var v = (float)f.GetValue(inst);
                        if ((v >= 0f && v <= 1f) || (v >= 0f && v <= 100f)) return f;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        float ReadCorruption()
        {
            try
            {
                if (fldCorruption != null) return (float)fldCorruption.GetValue(vfController);
                if (propCorruption != null) return (float)propCorruption.GetValue(vfController, null);
            }
            catch { }
            return float.NaN;
        }

        void WriteCorruption(float value)
        {
            try
            {
                if (fldCorruption != null) fldCorruption.SetValue(vfController, value);
                else if (propCorruption != null) propCorruption.SetValue(vfController, value, null);
            }
            catch { }
        }

        void SafeWriteFloat(FieldInfo f, object o, float v)
        {
            if (f == null) return;
            try { f.SetValue(o, v); } catch { }
        }

        void SafeWriteBool(FieldInfo f, object o, bool v)
        {
            if (f == null) return;
            try { f.SetValue(o, v); } catch { }
        }

        bool? ReadBool(FieldInfo f, object o)
        {
            if (f == null) return null;
            try { return (bool)f.GetValue(o); } catch { return null; }
        }

        bool SafeInvoke(MethodInfo m, object o, object[] args)
        {
            if (m == null) return false;
            try { m.Invoke(o, args); return true; } catch { return false; }
        }

        static string PathOf(Transform t)
        {
            if (!t) return "<null>";
            var stack = new List<string>();
            while (t) { stack.Add(t.name); t = t.parent; }
            stack.Reverse();
            return string.Join("/", stack);
        }

        void SnapLockValue()
        {
            // Prefer authoritative flag if present; fall back to numeric value
            bool? isCorr = ReadBool(fldIsCorrupted, vfController);
            if (isCorr.HasValue)
            {
                lockedCorruption = isCorr.Value
                    ? (corruptionIsPercent ? 100f : 1f)
                    : 0f;
                WriteCorruption(lockedCorruption);
                return;
            }

            var v = ReadCorruption();
            if (float.IsNaN(v))
                v = 0f;

            if (v < 0f) v = 0f;
            if (v > 100f) v = 100f;

            if (v <= 1.0001f) corruptionIsPercent = false;
            else if (v <= 100f) corruptionIsPercent = true;

            lockedCorruption = v;
        }
    }
}
