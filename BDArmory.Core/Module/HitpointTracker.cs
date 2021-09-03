﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BDArmory.Core.Extension;
using BDArmory.Core.Utils;
using KSP.Localization;
using UnityEngine;

namespace BDArmory.Core.Module
{
    public class HitpointTracker : PartModule, IPartMassModifier, IPartCostModifier
    {
        #region KSP Fields
        public float GetModuleMass(float baseMass, ModifierStagingSituation situation) => armorMass + HullmassAdjust;

        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;
        public float GetModuleCost(float baseCost, ModifierStagingSituation situation) => armorCost;
        public ModifierChangeWhen GetModuleCostChangeWhen() => ModifierChangeWhen.FIXED;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#LOC_BDArmory_Hitpoints"),//Hitpoints
        UI_ProgressBar(affectSymCounterparts = UI_Scene.None, controlEnabled = false, scene = UI_Scene.All, maxValue = 100000, minValue = 0, requireFullControl = false)]
        public float Hitpoints;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#LOC_BDArmory_ArmorThickness"),//Armor Thickness
        UI_FloatRange(minValue = 0f, maxValue = 500f, stepIncrement = 5f, scene = UI_Scene.All)]
        public float Armor = 10f;

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "#LOC_BDArmory_Armor_ArmorType"),//Armor Types
        UI_FloatRange(minValue = 1, maxValue = 999, stepIncrement = 1, scene = UI_Scene.All)]
        public float ArmorTypeNum = 1; //replace with prev/next buttons? //or a popup GUI box with a list of selectable types...

        //Add a part material type setting, so parts can be selected to be made out of wood/aluminium/steel to adjust base partmass/HP?
        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "#LOC_BDArmory_Armor_HullType"),//hull material Types
        UI_FloatRange(minValue = 1, maxValue = 3, stepIncrement = 1, scene = UI_Scene.Editor)]
        public float HullTypeNum = 2;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#LOC_BDArmory_Armor_HullMat")]//Status
        public string guiHullTypeString = "#LOC_BDArmory_Aluminium";

        public float HullmassAdjust = 0f;

        private bool IgnoreForArmorSetup = false;

        private bool isAI = false;

        private bool isProcWing = false;
        private bool massWasChanged = false;
        private float OldArmorType = 1;

        [KSPField(advancedTweakable = true, guiActive = false, guiActiveEditor = true, guiName = "#LOC_BDArmory_ArmorMass")]//armor mass
        public float armorMass = 0f;

        [KSPField(advancedTweakable = true, guiActive = false, guiActiveEditor = true, guiName = "#LOC_BDArmory_ArmorCost")]//armor cost
        public float armorCost = 0f;

        [KSPField(isPersistant = true)]
        public string SelectedArmorType = "None"; //presumably Aubranium can use this to filter allowed/banned types

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#LOC_BDArmory_ArmorCurrent")]//Status
        public string guiArmorTypeString = "def";

        private ArmorInfo armorInfo;

        private bool armorReset = false;

        [KSPField(isPersistant = true)]
        public float maxHitPoints = -1f;

        [KSPField(isPersistant = true)]
        public float ArmorThickness = 0f;

        [KSPField(isPersistant = true)]
        public bool ArmorSet;

        [KSPField(isPersistant = true)]
        public string ExplodeMode = "Never";

        [KSPField(isPersistant = true)]
        public bool FireFX = true;

        [KSPField(isPersistant = true)]
        public float FireFXLifeTimeInSeconds = 5f;

        //Armor Vars
        [KSPField(isPersistant = true)]
        public float Density;
        [KSPField(isPersistant = true)]
        public float Diffusivity;
        [KSPField(isPersistant = true)]
        public float Ductility;
        [KSPField(isPersistant = true)]
        public float Hardness;
        [KSPField(isPersistant = true)]
        public float Strength;
        [KSPField(isPersistant = true)]
        public float SafeUseTemp;
        [KSPField(isPersistant = true)]
        public float Cost;

        private bool startsArmored = false;

        //Part vars
        private float partMass = 0f;
        public Vector3 partSize;
        [KSPField(isPersistant = true)]
        public float maxSupportedArmor = -1; //upper cap on armor per part, overridable in MM/.cfg
        [KSPField(isPersistant = true)]
        public float armorVolume = -1;
        private float sizeAdjust;
        [KSPField(isPersistant = true)]
        public bool IsTrangularPanel = false;
        AttachNode bottom;
        AttachNode top;

        public List<Shader> defaultShader;
        public List<Color> defaultColor;
        public bool RegisterProcWingShader = false;

        #endregion KSP Fields

        #region Heart Bleed
        private double nextHeartBleedTime = 0;
        #endregion Heart Bleed

        private readonly float hitpointMultiplier = BDArmorySettings.HITPOINT_MULTIPLIER;

        private float previousHitpoints = -1;
        private bool _updateHitpoints = false;
        private bool _forceUpdateHitpointsUI = false;
        private const int HpRounding = 100;

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);

            if (!HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight) return;

            if (part.partInfo == null)
            {
                // Loading of the prefab from the part config
                _updateHitpoints = true;
            }
            else
            {
                // Loading of the part from a saved craft
                if (HighLogic.LoadedSceneIsEditor)
                {
                    _updateHitpoints = true;
                    ArmorSet = false;
                }
                else // Loading of the part from a craft in flight mode
                {
                    if (BDArmorySettings.RESET_HP && part.vessel != null) // Reset Max HP
                    {
                        var maxHPString = ConfigNodeUtils.FindPartModuleConfigNodeValue(part.partInfo.partConfig, "HitpointTracker", "maxHitPoints");
                        if (!string.IsNullOrEmpty(maxHPString)) // Use the default value from the MM patch.
                        {
                            try
                            {
                                maxHitPoints = float.Parse(maxHPString);
                                if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log("[BDArmory.HitPointTracker]: setting maxHitPoints of " + part + " on " + part.vessel.vesselName + " to " + maxHitPoints);
                                _updateHitpoints = true;
                            }
                            catch (Exception e)
                            {
                                Debug.LogError("[BDArmory.HitPointTracker]: Failed to parse maxHitPoints configNode: " + e.Message);
                            }
                        }
                        else // Use the stock default value.
                            maxHitPoints = -1f;
                    }
                    else // Don't.
                    {
                        enabled = false;
                    }
                }
            }
        }

        public void SetupPrefab()
        {
            if (part != null)
            {
                var maxHitPoints_ = CalculateTotalHitpoints();

                if (!_forceUpdateHitpointsUI && previousHitpoints == maxHitPoints_) return;

                //Add Hitpoints
                UI_ProgressBar damageFieldFlight = (UI_ProgressBar)Fields["Hitpoints"].uiControlFlight;
                damageFieldFlight.maxValue = maxHitPoints_;
                damageFieldFlight.minValue = 0f;

                UI_ProgressBar damageFieldEditor = (UI_ProgressBar)Fields["Hitpoints"].uiControlEditor;
                damageFieldEditor.maxValue = maxHitPoints_;
                damageFieldEditor.minValue = 0f;

                Hitpoints = maxHitPoints_;

                if (!ArmorSet) overrideArmorSetFromConfig();

                previousHitpoints = maxHitPoints_;
                part.RefreshAssociatedWindows();
            }
            else
            {
                if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log("[BDArmory.HitpointTracker]: OnStart part is null");
            }
        }

        public override void OnStart(StartState state)
        {
            isEnabled = true;
            //if (part != null) _updateHitpoints = true; 
            partMass = 0; //null these before part.mass is taken for HP calcs to ensure proper part mass recorded as original value
            HullmassAdjust = 0;
            if (part.name.Contains("B9.Aero.Wing.Procedural"))
            {
                isProcWing = true;
            }
            if (HighLogic.LoadedSceneIsFlight)
            {
                UI_FloatRange armorField = (UI_FloatRange)Fields["Armor"].uiControlFlight;
                //Once started the max value of the field should be the initial one
                armorField.maxValue = Armor;
                part.RefreshAssociatedWindows();
            }
            if (HighLogic.LoadedSceneIsEditor)
            {
                int typecount = 0;
                for (int i = 0; i < ArmorInfo.armorNames.Count; i++)
                {
                    typecount++;
                }
                if (part.name == "bdPilotAI" || part.name == "bdShipAI" || part.name == "bdPilotAI" || part.name == "missileController" || part.name == "bdammGuidanceModule")
                {
                    isAI = true;
                    Fields["ArmorTypeNum"].guiActiveEditor = false;
                    Fields["guiArmorTypeString"].guiActiveEditor = false;
                    Fields["guiArmorTypeString"].guiActiveEditor = false;
                    Fields["armorCost"].guiActiveEditor = false;
                    Fields["armorMass"].guiActiveEditor = false;
                }
                if (part.IsMissile())
                {
                    Fields["ArmorTypeNum"].guiActiveEditor = false;
                    Fields["guiArmorTypeString"].guiActiveEditor = false;
                    Fields["armorCost"].guiActiveEditor = false;
                    Fields["armorMass"].guiActiveEditor = false;
                }
                UI_FloatRange ATrangeEditor = (UI_FloatRange)Fields["ArmorTypeNum"].uiControlEditor;
                ATrangeEditor.onFieldChanged = ArmorSetup;
                ATrangeEditor.maxValue = (float)typecount;
                if (isAI || part.IsMissile())
                {
                    Fields["ArmorTypeNum"].guiActiveEditor = false;
                    ATrangeEditor.maxValue = 1;
                }
                UI_FloatRange HTrangeEditor = (UI_FloatRange)Fields["HullTypeNum"].uiControlEditor;
                HTrangeEditor.onFieldChanged = HullSetup;
                //if part is an engine/fueltank don't allow wood construction/mass reduction
                if (part.IsMissile() || part.IsWeapon() || isAI)
                {
                    HullTypeNum = 2;
                    HTrangeEditor.minValue = 2;
                    HTrangeEditor.maxValue = 2;
                    Fields["HullTypeNum"].guiActiveEditor = false;
                    Fields["guiHullTypeString"].guiActiveEditor = false;
                    IgnoreForArmorSetup = true;
                }
                if (ArmorThickness > 10) //Set to 10, Cerulean's HP MM patches all have armorThickness 10 fields
                {
                    startsArmored = true;
                    Armor = ArmorThickness;
                    ArmorTypeNum = 2;
                }
            }
            GameEvents.onEditorShipModified.Add(ShipModified);
            GameEvents.onPartDie.Add(OnPartDie);
            bottom = part.FindAttachNode("bottom");
            top = part.FindAttachNode("top");
            int topSize = 0;
            int bottomSize = 0;
            try
            {
                if (top != null)
                {
                    topSize = top.size;
                }
                if (bottom != null)
                {
                    bottomSize = bottom.size;
                }
            }
            catch
            {
                Debug.Log("[BDArmoryCore.HitpointTracker]: no node size detected");
            }
            //if attachnode top != bottom, then cone. is nodesize Attachnode.radius or Attachnode.size?
            //getSize returns size of a rectangular prism; most parts are circular, some are conical; use sizeAdjust to compensate
            if (bottom != null && top != null) //cylinder
            {
                sizeAdjust = 0.783f;
            }
            else if ((bottom == null && top != null) || (bottom != null && top == null) || (topSize > bottomSize || bottomSize > topSize)) //cone
            {
                sizeAdjust = 0.422f;
            }
            else //no bottom or top nodes, assume srf attached part; these are usually panels of some sort. Will need to determine method of ID'ing triangular panels/wings
            {                                                                                               //Wings at least could use WingLiftArea as a workaround for approx. surface area...
                sizeAdjust = 0.5f; //armor on one side, otherwise will have armor thickness on both sides of the panel, nonsensical + doiuble weight
            }
            armorMass = 0;
            partMass = part.mass;
            partSize = CalcPartBounds(this.part, this.transform).size;
            if (armorVolume < 0) //make this persistant to get around diffeences in part bounds between SPH/Flight. 
            {
                armorVolume =  // thickness * armor mass; moving it to Start since it only needs to be calc'd once
                    ((((partSize.x * partSize.y) * 2) + ((partSize.x * partSize.z) * 2) + ((partSize.y * partSize.z) * 2)) * sizeAdjust);  //mass * surface area approximation of a cylinder, where H/W are unknown
                if (HighLogic.LoadedSceneIsFlight) //Value correction for loading legacy craft via VesselMover spawner/tournament autospawn that haven't got a armorvolume value in their .craft file.
                {
                    armorVolume *= 0.63f; //part bounds dimensions when calced in Flight are consistantly 1.6-1.7x larger than correct SPH dimensions. Won't be exact, but good enough for legacy craft support
                }
                if (BDArmorySettings.DRAW_ARMOR_LABELS) Debug.Log("[ARMOR]: part size is (X: " + partSize.x + ";, Y: " + partSize.y + "; Z: " + partSize.z);
                if (BDArmorySettings.DRAW_ARMOR_LABELS) Debug.Log("[ARMOR]: size adjust mult: " + sizeAdjust + "; part srf area: " + ((((partSize.x * partSize.y) * 2) + ((partSize.x * partSize.z) * 2) + ((partSize.y * partSize.z) * 2)) * sizeAdjust));
            }
            if (IsTrangularPanel)
            {
                armorVolume /= 2;
            }
            if (!isProcWing)
            {
                SetupPrefab();
            }
            ArmorSetup(null, null);
            HullSetup(null, null); //reaquire hull mass adjust for mass calcs
            if (HighLogic.LoadedSceneIsEditor)
            {
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship); //will error if called in flightscene
            }
            if (!isProcWing)
            {
                var r = part.GetComponentsInChildren<Renderer>();
                {
                    for (int i = 0; i < r.Length; i++)
                    {
                        defaultShader.Add(r[i].material.shader);
                        if (BDArmorySettings.DRAW_ARMOR_LABELS) Debug.Log("[ARMOR] part shader is " + r[i].material.shader.name);
                        if (r[i].material.HasProperty("_Color"))
                        {
                            defaultColor.Add(r[i].material.color);
                        }
                    }
                }
            }
            if (HighLogic.LoadedSceneIsFlight)
            {
                if (BDArmorySettings.DRAW_ARMOR_LABELS) Debug.Log("[ARMOR] part mass is: " + (part.mass - armorMass) + "; Armor mass is: " + armorMass + "; hull mass adjust: " + HullmassAdjust + "; total: " + part.mass);
            }
        }

        private void OnDestroy()
        {
            GameEvents.onEditorShipModified.Remove(ShipModified);
            GameEvents.onPartDie.Remove(OnPartDie);
        }

        void OnPartDie() { OnPartDie(part); }
        void OnPartDie(Part p)
        {
            if (p == part)
            {
                Destroy(this); // Force this module to be removed from the gameObject as something is holding onto part references and causing a memory leak.
            }
        }
        public void ShipModified(ShipConstruct data)
        {
            _updateHitpoints = true;

            if (isProcWing)
            {
                if (!massWasChanged)
                {
                    HullSetup(null, null);
                }
            }
            massWasChanged = false;
        }

        public override void OnUpdate()
        {
            RefreshHitPoints();
            if (BDArmorySettings.HEART_BLEED_ENABLED && ShouldHeartBleed())
            {
                HeartBleed();
            }
        }

        void Update()
        {
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (ArmorTypeNum != OldArmorType)
                {
                    OldArmorType = ArmorTypeNum;
                    ArmorSetup(null, null);
                }
                RefreshHitPoints();
            }
            if (HighLogic.LoadedSceneIsFlight)
            {
                if (part.skinTemperature > SafeUseTemp * 1.5f)
                {
                    ReduceArmor((armorVolume * ((float)part.skinTemperature / SafeUseTemp)) * TimeWarp.fixedDeltaTime); //armor's melting off ship
                }
            }
        }

        private void RefreshHitPoints()
        {
            if (_updateHitpoints)
            {
                _updateHitpoints = false;
                _forceUpdateHitpointsUI = false;
                SetupPrefab();
            }
            if (massWasChanged)
            {
                if (HighLogic.LoadedSceneIsFlight)
                {
                    GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
                }
            }
        }

        private bool ShouldHeartBleed()
        {
            // wait until "now" exceeds the "next tick" value
            double dTime = Planetarium.GetUniversalTime();
            if (dTime < nextHeartBleedTime)
            {
                //Debug.Log(string.Format("[HitpointTracker] TimeSkip ShouldHeartBleed for {0} on {1}", part.name, part.vessel.vesselName));
                return false;
            }

            // assign next tick time
            double interval = BDArmorySettings.HEART_BLEED_INTERVAL;
            nextHeartBleedTime = dTime + interval;

            return true;
        }

        private void HeartBleed()
        {
            float rate = BDArmorySettings.HEART_BLEED_RATE;
            float deduction = Hitpoints * rate;
            if (Hitpoints - deduction < BDArmorySettings.HEART_BLEED_THRESHOLD)
            {
                // can't die from heart bleed
                return;
            }
            // deduct hp base on the rate
            //Debug.Log(string.Format("[HitpointTracker] Heart bleed {0} on {1} by {2:#.##} ({3:#.##}%)", part.name, part.vessel.vesselName, deduction, rate*100.0));
            AddDamage(deduction);
        }

        #region Hitpoints Functions

        public float CalculateTotalHitpoints()
        {
            float hitpoints;

            if (!part.IsMissile())
            {
                var averageSize = part.GetAverageBoundSize();
                var sphereRadius = averageSize * 0.5f;
                var sphereSurface = 4 * Mathf.PI * sphereRadius * sphereRadius;
                var structuralVolume = sphereSurface * 0.1f;
                //var structuralVolume = ((partSize.x * partSize.y * partSize.z) * sizeAdjust);

                var density = ((part.mass - armorMass) * 1000f) / structuralVolume;
                density = Mathf.Clamp(density, 1000, 10000);
                //if (BDArmorySettings.DRAW_DEBUG_LABELS) 
                //Debug.Log("[BDArmory.HitpointTracker]: Hitpoint Calc" + part.name + " | structuralVolume : " + structuralVolume);
                // if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log("[BDArmory.HitpointTracker]: Hitpoint Calc" + part.name + " | Density : " + density);

                var structuralMass = density * structuralVolume; //this just means hp = mass
                //biger things need more hp; but things that are denser, should also have more hp, so it's a bit mroe complicated than have hp = volume * hp mult
                //hp = (volume * Hp mult) * density mod?
                //lets take some examples; 3 identical size parts, mk1 cockpit(930kg), mk1 stuct tube (100kg), mk1 LF tank (250kg)
                //if, say, a Hp mod of 300, so 2.55m3 * 300 = 765 -> 800hp
                //cockpit has a density of ~364, fueltank of 98, struct tube of 39
                //density can't be linear scalar. Cuberoot? would need to reduce hp mult.
                //2.55 * 100* 364^1/3 = 1785, 2.55 * 100 * 98^1/3 = 1157, 2.55 * 100 * 39^1/3 = 854

                // if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log("[BDArmory.HitpointTracker]: " + part.name + " structural Volume: " + structuralVolume + "; density: " + density);
                //3. final calculations
                hitpoints = structuralMass * hitpointMultiplier * 0.333f;
                //hitpoints = (structuralVolume * Mathf.Pow(density, .333f) * Mathf.Clamp(80 - (structuralVolume / 2), 80 / 4, 80)) * hitpointMultiplier * 0.333f; //volume * cuberoot of density * HP mult scaled by size
                if (hitpoints > 10 * partMass * 1000f * hitpointMultiplier * 0.333f || hitpoints < 0.1f * partMass * 1000f * hitpointMultiplier * 0.333f)
                {
                    if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log($"[BDArmory.HitpointTracker]: Clamping hitpoints for part {part.name}");
                    hitpoints = hitpointMultiplier * partMass * 333f;
                }
                // SuicidalInsanity B9 patch //should this come before the hp clamping?
                if (isProcWing)
                {
					if (part.Modules.Contains("FARWingAerodynamicModel") || part.Modules.Contains("FARControllableSurface"))
					{
						//procwing hp already modified by mass, because it is mass
						//so using base part mass is it can be properly modified my material HP mod below
						hitpoints = ((part.mass - armorMass - HullmassAdjust) * 1000f) * 3.5f * hitpointMultiplier * 0.333f; //To account for FAR's Strength-mass Scalar.     
					}
					else
					{
						hitpoints = ((part.mass - armorMass - HullmassAdjust) * 1000f) * 7f * hitpointMultiplier * 0.333f; // since wings are basically a 2d object, lets have mass be our scalar - afterall, 2x the mass will ~= 2x the surfce area
                    } //breaks when pWings are made stupidly thick/large  //should really figure out a fix for that someday

                }       
                if (HullTypeNum == 1)
                {
                    hitpoints /= 4;
                }
                if (HullTypeNum == 3)
                {
                    hitpoints *= 1.75f;
                }
                hitpoints = Mathf.Round(hitpoints / HpRounding) * HpRounding;
                if (hitpoints <= 0) hitpoints = HpRounding;
            }
            else
            {
                hitpoints = 5;
                Armor = 2;
            }

            //override based on part configuration for custom parts
            if (maxHitPoints > 0)
            {
                if (HullTypeNum == 1)
                {
                    hitpoints = maxHitPoints / 4;
                }
                else if (HullTypeNum == 3)
                {
                    hitpoints = maxHitPoints * 1.75f;
                }
                else
                {
                    hitpoints = maxHitPoints;
                }
            }

            if (hitpoints <= 0) hitpoints = HpRounding;
            return hitpoints;
        }

        public void DestroyPart()
        {
            if ((part.mass - armorMass) <= 2f) part.explosionPotential *= 0.85f;

            PartExploderSystem.AddPartToExplode(part);
        }

        public float GetMaxArmor()
        {
            UI_FloatRange armorField = (UI_FloatRange)Fields["Armor"].uiControlEditor;
            return armorField.maxValue;
        }

        public float GetMaxHitpoints()
        {
            UI_ProgressBar hitpointField = (UI_ProgressBar)Fields["Hitpoints"].uiControlEditor;
            return hitpointField.maxValue;
        }

        public bool GetFireFX()
        {
            return FireFX;
        }

        public void SetDamage(float partdamage)
        {
            Hitpoints -= partdamage;

            if (Hitpoints <= 0)
            {
                DestroyPart();
            }
        }

        public void AddDamage(float partdamage)
        {
            if (isAI) return;

            partdamage = Mathf.Max(partdamage, 0f) * -1;
            Hitpoints += partdamage;
            if (Hitpoints <= 0)
            {
                DestroyPart();
            }
        }
        public void AddDamageToKerbal(KerbalEVA kerbal, float damage)
        {
            damage = Mathf.Max(damage, 0f) * -1;
            Hitpoints += damage;

            if (Hitpoints <= 0)
            {
                // oh the humanity!
                PartExploderSystem.AddPartToExplode(kerbal.part);
            }
        }
        #endregion Hitpoints Functions

        #region Armour
        public void ReduceArmor(float massToReduce) //incoming massToreduce should be cm3
        {
            if (BDArmorySettings.DRAW_ARMOR_LABELS)
            {
                Debug.Log("[HPTracker] armor mass: " + armorMass + "; mass to reduce: " + (massToReduce * (Density / 1000000000))); //g/m3
            }
            float reduceMass = (massToReduce * (Density / 1000000000)); //g/cm3 conversion to yield tons
            Armor -= (reduceMass / armorMass) * Armor; //now properly reduces armor thickness
            if (Armor < 0)
            {
                Armor = 0;
            }
            armorMass -= reduceMass; //tons
            if (armorMass < 0)
            {
                armorMass = 0;
            }
        }

        public void overrideArmorSetFromConfig()
        {
            ArmorSet = true;
            if (ArmorThickness > 10) //primarily panels, but any thing that starts with more than default armor
            {
                startsArmored = true;
                Armor = ArmorThickness;
                UI_FloatRange armortypes = (UI_FloatRange)Fields["ArmorTypeNum"].uiControlEditor;
                armortypes.minValue = 2f; //prevent panels from being switched to "None" armor type
                ArmorTypeNum = 2;
            }
            if (maxSupportedArmor < 0) //hasn't been set in cfg
            {
                if (part.IsAero())
                {
                    maxSupportedArmor = 20;
                }
                else
                {
                    maxSupportedArmor = ((partSize.x / 20) * 1000); //~62mm for Size1, 125mm for S2, 185mm for S3
                    maxSupportedArmor /= 5;
                    maxSupportedArmor = Mathf.Round(maxSupportedArmor);
                    maxSupportedArmor *= 5;
                }
                if (ArmorThickness > 10 && ArmorThickness > maxSupportedArmor)//part has custom armor value, use that
                {
                    maxSupportedArmor = ArmorThickness;
                }
            }
            if (BDArmorySettings.DRAW_ARMOR_LABELS)
            {
                Debug.Log("[ARMOR] max supported armor for " + part.name + " is " + maxSupportedArmor);
            }
            //if maxSupportedArmor > 0 && < armorThickness, that's entirely the fault of the MM patcher
            UI_FloatRange armorFieldFlight = (UI_FloatRange)Fields["Armor"].uiControlFlight;
            armorFieldFlight.minValue = 0f;
            armorFieldFlight.maxValue = maxSupportedArmor;
            UI_FloatRange armorFieldEditor = (UI_FloatRange)Fields["Armor"].uiControlEditor;
            armorFieldEditor.maxValue = maxSupportedArmor;
            armorFieldEditor.minValue = 0f;
            armorFieldEditor.onFieldChanged = ArmorSetup;
            part.RefreshAssociatedWindows();
        }
        public void ArmorSetup(BaseField field, object obj)
        {
            if ((ArmorTypeNum - 1) > ArmorInfo.armorNames.Count) //in case of trying to load a craft using a mod armor type that isn't installed and having a armorTypeNum larger than the index size
            {
                if (startsArmored)
                {
                    ArmorTypeNum = 2; //part starts with armor
                }
                else
                {
                    ArmorTypeNum = 1; //reset to 'None'
                }
            }
            if (isAI || part.IsMissile())
            {
                ArmorTypeNum = 1; //reset to 'None'
            }
            armorInfo = ArmorInfo.armors[ArmorInfo.armorNames[(int)ArmorTypeNum - 1]]; //what does this return if armorname cannot be found (mod armor removed/not present in install?)
            //if (SelectedArmorType != ArmorInfo.armorNames[(int)ArmorTypeNum - 1]) //armor selection overridden by Editor widget
            //{
            //	armorInfo = ArmorInfo.armors[SelectedArmorType];
            //    ArmorTypeNum = ArmorInfo.armors.FindIndex(t => t.name == SelectedArmorType); //adjust part's current armor setting to match
            //}
            guiArmorTypeString = armorInfo.name;
            SelectedArmorType = armorInfo.name;
            Density = armorInfo.Density;
            Diffusivity = armorInfo.Diffusivity;
            Ductility = armorInfo.Ductility;
            Hardness = armorInfo.Hardness;
            Strength = armorInfo.Strength;
            SafeUseTemp = armorInfo.SafeUseTemp;
            if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log("[BDArmory.HitpointTracker]: armorType = " + ArmorTypeNum);
            SetArmor();
            armorMass = 0;
            armorCost = 0;
            if (ArmorTypeNum > 1) //don't apply cost/mass to None armor type
            {
                armorMass = (Armor / 1000) * armorVolume * Density / 1000; //armor mass in tons
                armorCost = (Armor / 1000) * armorVolume * armorInfo.Cost; //armor cost, tons
            }
            //part.RefreshAssociatedWindows(); //having this fire every time a change happens prevents sliders from being used. Add delay timer?
        }

        public void SetArmor()
        {
            if (isAI) return; //replace with newer implementation
            if (part.IsMissile()) return;
            if (ArmorTypeNum > 1)
            {
                UI_FloatRange armorFieldFlight = (UI_FloatRange)Fields["Armor"].uiControlFlight;
                if (armorFieldFlight.maxValue != maxSupportedArmor)
                {
                    armorReset = false;
                    armorFieldFlight.minValue = 0f;
                    armorFieldFlight.maxValue = maxSupportedArmor;
                }
                UI_FloatRange armorFieldEditor = (UI_FloatRange)Fields["Armor"].uiControlEditor;
                if (armorFieldEditor.maxValue != maxSupportedArmor)
                {
                    armorReset = false;
                    armorFieldEditor.maxValue = maxSupportedArmor;
                    armorFieldEditor.minValue = 0f;
                }
                armorFieldEditor.onFieldChanged = ArmorSetup;
                if (!armorReset)
                {
                    part.RefreshAssociatedWindows();
                }
                armorReset = true;
            }
            else
            {
                Armor = 10;
                UI_FloatRange armorFieldEditor = (UI_FloatRange)Fields["Armor"].uiControlEditor;
                armorFieldEditor.maxValue = 10; //max none armor to 10 (simulate part skin of alimunium)
                armorFieldEditor.minValue = 0;
                UI_FloatRange armorFieldFlight = (UI_FloatRange)Fields["Armor"].uiControlFlight;
                armorFieldFlight.minValue = 0f;
                armorFieldFlight.maxValue = 10;
                part.RefreshAssociatedWindows();
                //GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            }
        }
        private static Bounds CalcPartBounds(Part p, Transform t)
        {
            Bounds result = new Bounds(t.position, Vector3.zero);
            Bounds[] bounds = p.GetRendererBounds(); //slower than getColliderBounds, but it only runs once, and doesn't have to deal with culling isTrgger colliders (airlocks, ladders, etc)
                                                     //Err... not so sure about that, me. This is yielding different resutls in SPH/flight. SPH is proper dimensions, flight is giving bigger x/y/z
                                                     // a mk1 cockpit (x: 1.25, y: 1.6, z: 1.9, area 11 in SPh becomes x: 2.5, y: 1.25, z: 2.5, area 19
            {
                if (!p.Modules.Contains("LaunchClamp"))
                {
                    for (int i = 0; i < bounds.Length; i++)
                    {
                        result.Encapsulate(bounds[i]);
                    }
                }
            }
            return result;
        }

        public void HullSetup(BaseField field, object obj) //no longer needed for realtime HP calcs, but does need to be updated occasionally to give correct vessel mass
        {
            if (IgnoreForArmorSetup) return;
            if (part.isEngine() && HullTypeNum < 2) //can armor engines, but not make them out of wood.
            {
                HullTypeNum = 2;
            }
            HullmassAdjust = 0;
            if (isProcWing)
            {
                StartCoroutine(WaitForProcWings());
            }
            else
            {
                SetHullMass();
            }
        }
        IEnumerator WaitForProcWings()
        {
            yield return null;
            partMass = ((part.mass - armorMass) - HullmassAdjust);
            SetHullMass();
        }
        void SetHullMass()
        {
            if (HullTypeNum == 1)
            {
                HullmassAdjust = ((partMass) / 3) - (partMass);
                guiHullTypeString = Localizer.Format("#LOC_BDArmory_Wood");
                part.maxTemp = 770;
            }
            else if (HullTypeNum == 2)
            {
                guiHullTypeString = Localizer.Format("#LOC_BDArmory_Aluminium");
                part.maxTemp = 1000;
            }
            else //hulltype 3
            {
                HullmassAdjust = (partMass);
                guiHullTypeString = Localizer.Format("#LOC_BDArmory_Steel");
                part.maxTemp = 2000;                
            }
            massWasChanged = true;
        }
        #endregion Armour
    }
}
