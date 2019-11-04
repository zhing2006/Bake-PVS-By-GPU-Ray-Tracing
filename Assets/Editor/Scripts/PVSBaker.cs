using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEditor;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// the PVS baker
/// </summary>
public static class PVSBaker
{
  /// <summary>
  /// bake current scene.
  /// </summary>
  [MenuItem("PVS/Bake")]
  private static void Bake()
  {
    var startTime = Time.realtimeSinceStartup;

    // get or create the PVSManager.
    var pvsManager = GetPVSManager();
    if (pvsManager.cellSize.sqrMagnitude < 0.01f)
    {
      Debug.LogError($"The \"cellSize\" on the \"{pvsManager.name}\" is too small! You can't set it smaller than 0.01.");
      return;
    }

    InitPVSManager(pvsManager);

    try
    {
      for (var index = 0; index < pvsManager.renderers.Count; ++index)
      {
        if (false == BakeVisibleFlagForRenderer(pvsManager, index, (float) index / pvsManager.renderers.Count))
        {
          Debug.LogError("Cancelled by user.");
          break;
        }
      }
    }
    finally
    {
      EditorUtility.ClearProgressBar();
      pvsManager.ReleaseBakeResource();
    }

    Debug.Log($"Used Time: {Time.realtimeSinceStartup - startTime}s");
  }

  /// <summary>
  /// bake visible flag for one renderer.
  /// </summary>
  /// <param name="pvsManager">the PVS manager.</param>
  /// <param name="index">the renderer index.</param>
  /// <param name="progress">the baking progress.</param>
  public static bool BakeVisibleFlagForRenderer(PVSManager pvsManager, int index, float progress)
  {
//    if (EditorUtility.DisplayCancelableProgressBar("Backing", $"Renderer: {index}/{pvsManager.renderers.Count}", progress))
//    {
//      return false;
//    }

    var renderer = pvsManager.renderers[index];
    var targetBounds = renderer.bounds;
    targetBounds.Expand(1e-5f);
    var targetPlanes = PVSManager.GetPlanesFromBounds(ref targetBounds);
    for (var cellIndexX = 0; cellIndexX < pvsManager.cachedCellCountX; ++cellIndexX)
    {
      if (EditorUtility.DisplayCancelableProgressBar("Backing", $"Renderer: {index}/{pvsManager.renderers.Count} Cell: {cellIndexX}/{pvsManager.cachedCellCountX}", progress))
      {
        return false;
      }

      for (var cellIndexY = 0; cellIndexY < pvsManager.cachedCellCountY; ++cellIndexY)
      {
//        if (EditorUtility.DisplayCancelableProgressBar("Backing", $"Renderer: {index}/{pvsManager.renderers.Count} Cell: {cellIndexX}/{pvsManager.cachedCellCountX}, {cellIndexY}/{pvsManager.cachedCellCountY}", progress))
//        {
//          return false;
//        }

        for (var cellIndexZ = 0; cellIndexZ < pvsManager.cachedCellCountZ; ++cellIndexZ)
        {
//          if (EditorUtility.DisplayCancelableProgressBar("Backing", $"Renderer: {index}/{pvsManager.renderers.Count} Cell: {cellIndexX}/{pvsManager.cachedCellCountX}, {cellIndexY}/{pvsManager.cachedCellCountY}, {cellIndexZ}/{pvsManager.cachedCellCountZ}", progress))
//          {
//            return false;
//          }

          var viewBounds = new Bounds(
            pvsManager.worldBounds.min + new Vector3(
              cellIndexX * pvsManager.cellSize.x + 0.5f * pvsManager.cellSize.x,
              cellIndexY * pvsManager.cellSize.y + 0.5f * pvsManager.cellSize.y,
              cellIndexZ * pvsManager.cellSize.z + 0.5f * pvsManager.cellSize.z),
            pvsManager.cellSize);

          // check visible.
          var isVisible = false;
          if (viewBounds.Intersects(targetBounds))
          {
            isVisible = true;
          }
          else
          {
            var viewPlanes = PVSManager.GetPlanesFromBounds(ref viewBounds);

            // find view bounds and target bounds side face side planes.
            foreach (var viewPlane in viewPlanes)
            {
              foreach (var targetPlane in targetPlanes)
              {
                if (!PVSManager.IsPlaneFacePlane(viewPlane, targetPlane))
                  continue;

                isVisible = pvsManager.RayTracingPlane2PlaneGPU(viewPlane, targetPlane);
                if (isVisible)
                  break;
              }

              if (isVisible)
                break;
            }
          }

          pvsManager.SetVisibleFlag(index, cellIndexX, cellIndexY, cellIndexZ, isVisible);
        }
      }
    }

    return true;
  }

  /// <summary>
  /// initialize PVS manager.
  /// </summary>
  /// <param name="pvsManager">the PVS manager.</param>
  public static void InitPVSManager(PVSManager pvsManager)
  {
    pvsManager.debugPlanes = new List<PVSManager.Plane>();
    pvsManager.debugLines = new List<PVSManager.Line>();

    var currentScene = SceneManager.GetActiveScene();
    var allRootGos = new List<GameObject>();
    currentScene.GetRootGameObjects(allRootGos);
    var worldBounds = new Bounds(Vector3.zero, Vector3.zero);
    var renderers = new List<Renderer>();
    var colliders = new List<Renderer>();
    foreach (var go in allRootGos)
    {
      WalkThroughTransformTree(go.transform, (tr) =>
      {
        var renderer = tr.GetComponent<Renderer>();
        if (!renderer || !tr.gameObject.isStatic) return;

        worldBounds.Encapsulate(renderer.bounds);
        if (renderer.gameObject.tag == "PVS")
          renderers.Add(renderer);
        colliders.Add(renderer);
      });
    }

    var counts = new Vector3(math.ceil(worldBounds.size.x / pvsManager.cellSize.x), math.ceil(worldBounds.size.y / pvsManager.cellSize.y), math.ceil(worldBounds.size.z / pvsManager.cellSize.z));
    worldBounds.size = new Vector3(counts.x * pvsManager.cellSize.x, counts.y * pvsManager.cellSize.y, counts.z * pvsManager.cellSize.z);
    pvsManager.worldBounds = worldBounds;
    pvsManager.renderers = renderers;
    pvsManager.colliders = colliders;
    pvsManager.AllocVisibleFlags();
    Debug.Log($"{sizeof(int) * pvsManager.visibleFlags.Length} bytes were allocated for visible flags.");

    pvsManager.accelerationStructure = new RayTracingAccelerationStructure();
    var subMeshFlagArray = new bool[32];
    var subMeshCutoffArray = new bool[32];
    for (var i = 0; i < 32; ++i)
    {
      subMeshFlagArray[i] = true;
      subMeshCutoffArray[i] = false;
    }
    foreach (var renderer in colliders)
      pvsManager.accelerationStructure.AddInstance(renderer, subMeshFlagArray, subMeshCutoffArray);
    pvsManager.accelerationStructure.Build();
  }

  /// <summary>
  /// get or create PVS manager from current scene.
  /// </summary>
  /// <returns>the PVS manager.</returns>
  private static PVSManager GetPVSManager()
  {
    var pvsManager = GameObject.FindObjectOfType<PVSManager>();
    if (pvsManager) return pvsManager;

    var go = new GameObject("PVSManager", typeof(PVSManager));
    pvsManager = go.GetComponent<PVSManager>();
    return pvsManager;
  }

  /// <summary>
  /// walk through the transform tree.
  /// </summary>
  /// <param name="parentTr">the parent transform.</param>
  /// <param name="action">the action.</param>
  private static void WalkThroughTransformTree(Transform parentTr, System.Action<Transform> action)
  {
    if (!parentTr.gameObject.activeInHierarchy)
      return;

    foreach (Transform childTr in parentTr)
    {
      WalkThroughTransformTree(childTr, action);
    }

    action(parentTr);
  }
}
