﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

//code ENTIRELY SKIDDED from BIOLOGIAL TESTICALS THE FIFTH

namespace phosphorite.Patches;

[HarmonyPatch(typeof(GameLightingManager))]
public class GameLightingManagerPatches
{
    public static int MaxLightCount = 512;

    private static Task sortingTask;
    private static bool sortComplete = false;
    private static object sortLock = new();
    public static List<GameLight> sortedLights = new();

    private static float LightViewRadius = 25f;

    [HarmonyPatch(nameof(GameLightingManager.SortLights)), HarmonyPrefix]
    public static bool SortLights(GameLightingManager __instance)
    {
        if (__instance.gameLights.Count <= MaxLightCount || __instance.mainCameraTransform == null)
            return false;

        lock (sortLock)
        {
            if (sortingTask == null || sortingTask.IsCompleted)
            {
                Vector3 cameraPosition = __instance.mainCameraTransform.position;
                float maxDistSqr = LightViewRadius * LightViewRadius;

                sortingTask = Task.Run(() =>
                {
                    List<GameLight> visibleLights = new();

                    foreach (var light in __instance.gameLights)
                    {
                        if (light?.light == null) continue;

                        float distSqr = (cameraPosition - light.light.transform.position).sqrMagnitude;
                        if (distSqr <= maxDistSqr)
                            visibleLights.Add(light);
                    }

                    visibleLights.Sort((a, b) =>
                    {
                        float distA = (cameraPosition - a.light.transform.position).sqrMagnitude;
                        float distB = (cameraPosition - b.light.transform.position).sqrMagnitude;
                        return distA.CompareTo(distB);
                    });

                    lock (sortLock)
                    {
                        sortedLights = visibleLights;
                        sortComplete = true;
                    }
                });
            }

            if (sortComplete)
            {
                __instance.gameLights = new List<GameLight>(sortedLights);
                sortComplete = false;
            }
        }

        return false;
    }
    
    [HarmonyPatch(nameof(GameLightingManager.CompareDistFromCamera)), HarmonyPrefix]
    public static bool CompareDistFromCamera(GameLightingManager __instance, GameLight a, GameLight b, ref int __result)
    {
        if (a?.light == null)
        {
            __result = b?.light == null ? 0 : 1;
            return false;
        }

        if (b?.light == null)
        {
            __result = -1;
            return false;
        }

        float distA = (__instance.cameraPosForSort - a.light.transform.position).sqrMagnitude;
        float distB = (__instance.cameraPosForSort - b.light.transform.position).sqrMagnitude;

        __result = distA.CompareTo(distB);
        return false;
    }
}