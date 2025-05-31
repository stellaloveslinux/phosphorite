using System;
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
    public static GameLightingManager.LightData[] lightData;
    public static List<GameLight> gameLights;
    public static List<GameLight> sortedLights = new();

    public static bool customVertexLightingEnabled;
    public static bool desaturateAndTintEnabled;
    public static Transform mainCameraTransform;
    public static GraphicsBuffer lightDataBuffer;
    public static Vector3 cameraPosForSort;
    public static bool skipNextSlice;
    public static bool immediateSort;

    private static Task sortingTask;
    private static bool sortComplete = false;
    private static object sortLock = new();

    private static float LightViewRadius = 25f;

    [HarmonyPatch(nameof(GameLightingManager.InitData)), HarmonyPrefix]
    public static bool InitData(GameLightingManager __instance)
    {
        gameLights = new List<GameLight>();
        sortedLights = new List<GameLight>();
        lightDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxLightCount,
            UnsafeUtility.SizeOf<GameLightingManager.LightData>());
        lightData = new GameLightingManager.LightData[MaxLightCount];
        __instance.ClearGameLights();
        __instance.SetDesaturateAndTintEnabled(false, Color.black);
        __instance.SetAmbientLightDynamic(Color.black);
        __instance.SetCustomDynamicLightingEnabled(false);

        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.OnDestroy)), HarmonyPrefix]
    public static bool OnDestroy(GameLightingManager __instance)
    {
        __instance.ClearGameLights();
        __instance.SetDesaturateAndTintEnabled(false, Color.black);
        __instance.SetAmbientLightDynamic(Color.black);
        __instance.SetCustomDynamicLightingEnabled(false);
        lightDataBuffer?.Release();
        lightDataBuffer = null;
        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.SetCustomDynamicLightingEnabled)), HarmonyPrefix]
    public static bool SetCustomDynamicLightingEnabled(GameLightingManager __instance, bool enable)
    {
        if (enable)
            Shader.EnableKeyword("_ZONE_DYNAMIC_LIGHTS__CUSTOMVERTEX");
        else
            Shader.DisableKeyword("_ZONE_DYNAMIC_LIGHTS__CUSTOMVERTEX");

        customVertexLightingEnabled = enable;
        return false;
    }


    [HarmonyPatch(nameof(GameLightingManager.SliceUpdate)), HarmonyPrefix]
    public static bool SliceUpdate(GameLightingManager __instance)
    {
        if (mainCameraTransform == null && Camera.main != null)
            mainCameraTransform = Camera.main.transform;

        if (mainCameraTransform == null) return false;


        immediateSort = false;
        __instance.SortLights();

        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.SetAmbientLightDynamic)), HarmonyPrefix]
    public static bool SetAmbientLightDynamic(GameLightingManager __instance, Color color)
    {
        Shader.SetGlobalColor("_GT_GameLight_Ambient_Color", color);
        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.SetDesaturateAndTintEnabled)), HarmonyPrefix]
    public static bool SetDesaturateAndTintEnabled(GameLightingManager __instance, bool enable, Color tint)
    {
        Shader.SetGlobalColor("_GT_DesaturateAndTint_TintColor", tint);
        Shader.SetGlobalFloat("_GT_DesaturateAndTint_TintAmount", enable ? 1 : 0);
        desaturateAndTintEnabled = enable;
        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.OnEnable)), HarmonyPrefix]
    public static bool OnEnable(GameLightingManager __instance)
    {
        GorillaSlicerSimpleManager.RegisterSliceable(__instance, GorillaSlicerSimpleManager.UpdateStep.Update);
        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.OnDisable)), HarmonyPrefix]
    public static bool OnDisable(GameLightingManager __instance)
    {
        GorillaSlicerSimpleManager.UnregisterSliceable(__instance, GorillaSlicerSimpleManager.UpdateStep.Update);
        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.SortLights)), HarmonyPrefix]
    public static bool SortLights(GameLightingManager __instance)
    {
        if (gameLights.Count <= MaxLightCount || mainCameraTransform == null)
            return false;

        lock (sortLock)
        {
            if (sortingTask == null || sortingTask.IsCompleted)
            {
                Vector3 cameraPosition = mainCameraTransform.position;
                float maxDistSqr = LightViewRadius * LightViewRadius;

                sortingTask = Task.Run(() =>
                {
                    List<GameLight> visibleLights = new();

                    foreach (var light in gameLights)
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
                gameLights = new List<GameLight>(sortedLights);
                sortComplete = false;
            }
        }

        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.Update)), HarmonyPrefix]
    public static bool Update(GameLightingManager __instance)
    {
        RefreshLightData(__instance);
        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.RefreshLightData)), HarmonyPrefix]
    public static bool RefreshLightData(GameLightingManager __instance)
    {
        if (lightData == null || !customVertexLightingEnabled)
            return false;

        if (immediateSort)
        {
            immediateSort = false;
            skipNextSlice = true;
            __instance.SortLights();
        }

        int lightCount = gameLights.Count;
        int count = Mathf.Min(MaxLightCount, lightCount);

        for (int i = 0; i < MaxLightCount; i++)
        {
            if (i < count)
                __instance.GetFromLight(i, i);
            else
                __instance.ResetLight(i);
        }

        lightDataBuffer.SetData(lightData);
        Shader.SetGlobalBuffer("_GT_GameLight_Lights", lightDataBuffer);

        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.AddGameLight)), HarmonyPrefix]
    public static bool AddGameLight(GameLightingManager __instance, GameLight light, ref int __result)
    {
        if (light == null || !light.gameObject.activeInHierarchy || light.light == null || !light.light.enabled || gameLights.Contains(light))
        {
            __result = -1;
            return false;
        }

        gameLights.Add(light);
        immediateSort = true;
        __result = gameLights.Count - 1;
        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.RemoveGameLight)), HarmonyPrefix]
    public static void RemoveGameLight(GameLightingManager __instance, GameLight light)
    {
        gameLights.Remove(light);
    }

    [HarmonyPatch(nameof(GameLightingManager.ClearGameLights)), HarmonyPrefix]
    public static bool ClearGameLights(GameLightingManager __instance)
    {
        if (gameLights != null)
            gameLights.Clear();
        else
            return false;

        for (int lightIndex = 0; lightIndex < lightData.Length; ++lightIndex)
            __instance.ResetLight(lightIndex);

        lightDataBuffer.SetData(lightData);
        Shader.SetGlobalBuffer("_GT_GameLight_Lights", lightDataBuffer);

        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.GetFromLight)), HarmonyPrefix]
    public static bool GetFromLight(GameLightingManager __instance, int lightIndex, int gameLightIndex)
    {
        if (lightIndex < 0 || lightIndex >= lightData.Length)
            return false;

        GameLight gameLight = null;
        if (gameLightIndex >= 0 && gameLightIndex < gameLights.Count)
            gameLight = gameLights[gameLightIndex];

        if (gameLight == null || gameLight.light == null)
            return false;

        Vector4 position = (Vector4)gameLight.light.transform.position with { w = 1f };
        Color color = gameLight.light.color * gameLight.light.intensity * (gameLight.negativeLight ? -1f : 1f);
        Vector3 forward = gameLight.light.transform.forward;

        lightData[lightIndex] = new GameLightingManager.LightData()
        {
            lightPos = position,
            lightColor = color,
            lightDirection = forward
        };

        lightData[lightIndex].lightColor *= 2;

        return false;
    }

    [HarmonyPatch(nameof(GameLightingManager.ResetLight)), HarmonyPrefix]
    public static bool ResetLight(GameLightingManager __instance, int lightIndex)
    {
        lightData[lightIndex].lightPos = Vector4.zero;
        lightData[lightIndex].lightColor = Vector4.zero;
        lightData[lightIndex].lightDirection = Vector4.zero;
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

        float distA = (cameraPosForSort - a.light.transform.position).sqrMagnitude;
        float distB = (cameraPosForSort - b.light.transform.position).sqrMagnitude;

        __result = distA.CompareTo(distB);
        return false;
    }
}