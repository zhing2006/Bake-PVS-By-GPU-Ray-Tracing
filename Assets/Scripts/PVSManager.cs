using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// the PVS manager.
/// </summary>
public class PVSManager : MonoBehaviour
{
  /// <summary>
  /// the plane.
  /// </summary>
  [System.Serializable]
  public class Plane
  {
    public Vector3 leftBottomCorner;
    public Vector2 size;
    public Vector3 rightDir;
    public Vector3 upDir;

    public Vector3 rightBottomCorner => leftBottomCorner + size.x * rightDir;
    public Vector3 rightTopCorner => leftBottomCorner + size.x * rightDir + size.y * upDir;
    public Vector3 leftTopCorner => leftBottomCorner + size.y * upDir;
    public Vector3 center => 0.5f * (leftBottomCorner + rightTopCorner);
    public Vector3 normal => Vector3.Cross(upDir, rightDir);
  }

  /// <summary>
  /// the PVS lines.
  /// </summary>
  [System.Serializable]
  public class Line
  {
    public Vector3 pt1;
    public Vector3 pt2;

    public Line(Vector3 p1, Vector3 p2)
    {
      pt1 = p1;
      pt2 = p2;
    }
  }

  /// <summary>
  /// the ray trace shader used for ray cast.
  /// </summary>
  public RayTracingShader bakeRayTraceShader;

  /// <summary>
  /// the compute shader used for count result.
  /// </summary>
  public ComputeShader countPixelComputeShader;

  /// <summary>
  /// the acceleration structure.
  /// </summary>
  public RayTracingAccelerationStructure accelerationStructure;

  /// <summary>
  /// render target cache.
  /// </summary>
  private readonly Dictionary<Rect, RTHandle> _rtCache = new Dictionary<Rect, RTHandle>();

  /// <summary>
  /// result buffer.
  /// </summary>
  private ComputeBuffer _resultBuffer;

  private readonly int _accelerationStructureShaderId = Shader.PropertyToID("_AccelerationStructure");
  private readonly int _resultShaderId = Shader.PropertyToID("_Result");
  private readonly int _pixelsShaderId = Shader.PropertyToID("_Pixels");
  private readonly int _widthShaderId = Shader.PropertyToID("_Width");
  private readonly int _heightShaderId = Shader.PropertyToID("_Height");
  private readonly int _viewLeftBottomCornerShaderId = Shader.PropertyToID("_ViewLeftBottomCorner");
  private readonly int _viewRightDirShaderId = Shader.PropertyToID("_ViewRightDir");
  private readonly int _viewUpDirShaderId = Shader.PropertyToID("_ViewUpDir");
  private readonly int _viewSizeShaderId = Shader.PropertyToID("_ViewSize");
  private readonly int _targetLeftBottomCornerShaderId = Shader.PropertyToID("_TargetLeftBottomCorner");
  private readonly int _targetRightDirShaderId = Shader.PropertyToID("_TargetRightDir");
  private readonly int _targetUpDirShaderId = Shader.PropertyToID("_TargetUpDir");
  private readonly int _targetSizeShaderId = Shader.PropertyToID("_TargetSize");
  private readonly int _rayTracingStepShaderId = Shader.PropertyToID("_RayTracingStep");

  /// <summary>
  /// the bounds of the world.
  /// </summary>
  public Bounds worldBounds;

  /// <summary>
  /// the cell size.
  /// </summary>
  public Vector3 cellSize = new Vector3(10.0f, 10.0f, 10.0f);

  /// <summary>
  /// the ray tracing step size.
  /// </summary>
  public Vector2 rayTracingStep = new Vector2(0.1f, 0.1f);

  /// <summary>
  /// hold all static renderers in the scene.
  /// NOTICE: this isn't a efficient way, but we do it as a simple way for example.
  /// DO NOT use it in your production.
  /// </summary>
  public List<Renderer> renderers;

  public List<Renderer> colliders;

  /// <summary>
  /// the baked visible flags.
  /// </summary>
  [HideInInspector]
  public int[] visibleFlags;

  public int cachedCellCountX, cachedCellCountY, cachedCellCountZ;
  public int cachedTotalCellCount;

  public bool isDrawGizmosWhenSelected = true;
  public bool isDrawWorldBoundsGizmos = true;
  public bool isDrawCellGizmos = true;
  public bool isDrawDebugPlanes = true;
  public bool isDrawDebugLines = true;
  public bool isDrawDebugEye = true;

  /// <summary>
  /// planes for debug.
  /// </summary>
  public List<Plane> debugPlanes;

  /// <summary>
  /// lines for debug.
  /// </summary>
  public List<Line> debugLines;

  /// <summary>
  /// the debug eye transform.
  /// </summary>
  public Transform debugEyeTr;

  /// <summary>
  /// build acceleration structure.
  /// </summary>
  public void BuildAccelerationStructure()
  {
    accelerationStructure?.Dispose();
    accelerationStructure = new RayTracingAccelerationStructure();

    var subMeshFlagArray = new bool[32];
    var subMeshCutoffArray = new bool[32];
    for (var i = 0; i < 32; ++i)
    {
      subMeshFlagArray[i] = true;
      subMeshCutoffArray[i] = false;
    }
    foreach (var r in colliders)
    {
      accelerationStructure.AddInstance(r, subMeshFlagArray, subMeshCutoffArray);
    }
    accelerationStructure.Build();
  }

  /// <summary>
  /// alloc memory for visible flags.
  /// </summary>
  public void AllocVisibleFlags()
  {
    GetCellCount(out cachedCellCountX, out cachedCellCountY, out cachedCellCountZ);
    cachedTotalCellCount = cachedCellCountX * cachedCellCountY * cachedCellCountZ;
    var count = cachedTotalCellCount * renderers.Count;
    visibleFlags = new int[count / 32 + 1];
    for (var i = 0; i < visibleFlags.Length; ++i)
      visibleFlags[i] = 0;

    rayTracingStep.x = math.max(0.01f, rayTracingStep.x);
    rayTracingStep.y = math.max(0.01f, rayTracingStep.y);
  }

  public void ReleaseCacheResource()
  {
    foreach (var pair in _rtCache)
    {
      RTHandles.Release(pair.Value);
    }
    _rtCache.Clear();
    if (null != _resultBuffer)
    {
      _resultBuffer.Release();
      _resultBuffer = null;
    }
  }

  /// <summary>
  /// release bake resources.
  /// </summary>
  public void ReleaseBakeResource()
  {
    if (null != accelerationStructure)
    {
      accelerationStructure.Dispose();
      accelerationStructure = null;
    }

    ReleaseCacheResource();
  }

  /// <summary>
  /// return the six planes by the bounds.
  /// </summary>
  /// <param name="bounds">bounds.</param>
  /// <returns>the six planes.</returns>
  public static Plane[] GetPlanesFromBounds(ref Bounds bounds)
  {
    var planes = new Plane[6];
    // -Z
    planes[0] = new Plane
    {
      leftBottomCorner =  new Vector3(bounds.min.x, bounds.min.y, bounds.min.z),
      size = new Vector2(bounds.size.x, bounds.size.y),
      rightDir = Vector3.right,
      upDir = Vector3.up
    };
    // +Z
    planes[1] = new Plane
    {
      leftBottomCorner = new Vector3(bounds.max.x, bounds.min.y, bounds.max.z),
      size = new Vector2(bounds.size.x, bounds.size.y),
      rightDir = Vector3.left,
      upDir = Vector3.up
    };
    // -Y
    planes[2] = new Plane
    {
      leftBottomCorner = new Vector3(bounds.min.x, bounds.min.y, bounds.max.z),
      size = new Vector2(bounds.size.x, bounds.size.z),
      rightDir = Vector3.right,
      upDir = Vector3.back
    };
    // +Y
    planes[3] = new Plane
    {
      leftBottomCorner = new Vector3(bounds.min.x, bounds.max.y, bounds.min.z),
      size = new Vector2(bounds.size.x, bounds.size.z),
      rightDir = Vector3.right,
      upDir = Vector3.forward
    };
    // -X
    planes[4] = new Plane
    {
      leftBottomCorner = new Vector3(bounds.min.x, bounds.min.y, bounds.max.z),
      size = new Vector2(bounds.size.z, bounds.size.y),
      rightDir = Vector3.back,
      upDir = Vector3.up
    };
    // +X
    planes[5] = new Plane
    {
      leftBottomCorner = new Vector3(bounds.max.x, bounds.min.y, bounds.min.z),
      size = new Vector2(bounds.size.z, bounds.size.y),
      rightDir = Vector3.forward,
      upDir = Vector3.up
    };
    return planes;
  }

  /// <summary>
  /// check whether plane1 is facing plane2.
  /// </summary>
  /// <param name="plane1">the plane1.</param>
  /// <param name="plane2">the plane2.</param>
  /// <returns>the result.</returns>
  public static bool IsPlaneFacePlane(Plane plane1, Plane plane2)
  {
    var points1 = new[]
    {
      plane1.leftBottomCorner,
      plane1.rightBottomCorner,
      plane1.rightTopCorner,
      plane1.leftTopCorner
    };
    var points2 = new[]
    {
      plane2.leftBottomCorner,
      plane2.rightBottomCorner,
      plane2.rightTopCorner,
      plane2.leftTopCorner
    };
    foreach (var p1 in points1)
    {
      foreach (var p2 in points2)
      {
        var dir = (p2 - p1).normalized;
        if (Vector3.Dot(dir, plane1.normal) > 1e-5f && Vector3.Dot(-dir, plane2.normal) > 1e-5f)
          return true;
      }
    }

    return false;
  }

  /// <summary>
  /// set visible flag.
  /// </summary>
  /// <param name="index">the renderer index.</param>
  /// <param name="cellIndexX">the cell x index.</param>
  /// <param name="cellIndexY">the cell y index.</param>
  /// <param name="cellIndexZ">the cell z index.</param>
  /// <param name="isVisible">the visible flag.</param>
  public void SetVisibleFlag(int index, int cellIndexX, int cellIndexY, int cellIndexZ, bool isVisible)
  {
    var bitIndex = index * cachedTotalCellCount + cellIndexZ * cachedCellCountX * cachedCellCountY + cellIndexY * cachedCellCountX + cellIndexX;
    var intIndex = bitIndex / 32;
    var bitOffset = bitIndex % 32;

    if (isVisible)
      visibleFlags[intIndex] = visibleFlags[intIndex] | (1 << bitOffset);
    else
      visibleFlags[intIndex] = visibleFlags[intIndex] & ~(1 << bitOffset);
  }

  /// <summary>
  /// get visible flag.
  /// </summary>
  /// <param name="index">the renderer index.</param>
  /// <param name="cellIndexX">the cell x index.</param>
  /// <param name="cellIndexY">the cell y index.</param>
  /// <param name="cellIndexZ">the cell z index.</param>
  /// <returns>the visible flag.</returns>
  public bool GetVisibleFlag(int index, int cellIndexX, int cellIndexY, int cellIndexZ)
  {
    var bitIndex = index * cachedTotalCellCount + cellIndexZ * cachedCellCountX * cachedCellCountY + cellIndexY * cachedCellCountX + cellIndexX;
    var intIndex = bitIndex / 32;
    var bitOffset = bitIndex % 32;

    return (visibleFlags[intIndex] & (1 << bitOffset)) != 0;
  }

  /// <summary>
  /// Ray tracing to check whether plane1 can see plane2.
  /// </summary>
  /// <param name="plane1">the plane1.</param>
  /// <param name="plane2">the plane2.</param>
  /// <returns>the result.</returns>
  public bool RayTracingPlane2Plane(Plane plane1, Plane plane2)
  {
    for (var xOffset1 = 0.0f; xOffset1 < plane1.size.x - 1e-5f; xOffset1 += rayTracingStep.x)
    {
      for (var yOffset1 = 0.0f; yOffset1 < plane1.size.y - 1e-5f; yOffset1 += rayTracingStep.y)
      {
        var startPt = plane1.leftBottomCorner + (xOffset1 + 0.5f * rayTracingStep.x) * plane1.rightDir + (yOffset1 + 0.5f * rayTracingStep.y) * plane1.upDir;
        for (var xOffset2 = 0.0f; xOffset2 < plane2.size.x - 1e-5f; xOffset2 += rayTracingStep.x)
        {
          for (var yOffset2 = 0.0f; yOffset2 < plane2.size.y - 1e-5f; yOffset2 += rayTracingStep.y)
          {
            var endPt = plane2.leftBottomCorner + (xOffset2 + 0.5f * rayTracingStep.x) * plane2.rightDir + (yOffset2 + 0.5f * rayTracingStep.y) * plane2.upDir;
            var dir = endPt - startPt;
            if (!Physics.Raycast(startPt, dir, dir.magnitude) && !Physics.Raycast(endPt, -dir, dir.magnitude))
              return true;
          }
        }
      }
    }

    return false;
  }

  /// <summary>
  /// Ray tracing to check whether plane1 can see plane2 by GPU.
  /// </summary>
  /// <param name="plane1">the plane1.</param>
  /// <param name="plane2">the plane2.</param>
  /// <returns>the result.</returns>
  public bool RayTracingPlane2PlaneGPU(Plane plane1, Plane plane2)
  {
    var viewRect = new Rect(0F, 0F, math.ceil(plane1.size.x / rayTracingStep.x), math.ceil(plane1.size.y / rayTracingStep.x));
    var renderTarget = RequireRenderTarget(ref viewRect);
    var resultBuffer = RequireResultBuffer();

    bakeRayTraceShader.SetShaderPass("PVS");
    bakeRayTraceShader.SetAccelerationStructure(_accelerationStructureShaderId, accelerationStructure);
    bakeRayTraceShader.SetTexture(_resultShaderId, renderTarget);
    bakeRayTraceShader.SetVector(_viewLeftBottomCornerShaderId, plane1.leftBottomCorner);
    bakeRayTraceShader.SetVector(_viewRightDirShaderId, plane1.rightDir);
    bakeRayTraceShader.SetVector(_viewUpDirShaderId, plane1.upDir);
    bakeRayTraceShader.SetVector(_viewSizeShaderId, plane1.size);
    bakeRayTraceShader.SetVector(_targetLeftBottomCornerShaderId, plane2.leftBottomCorner);
    bakeRayTraceShader.SetVector(_targetRightDirShaderId, plane2.rightDir);
    bakeRayTraceShader.SetVector(_targetUpDirShaderId, plane2.upDir);
    bakeRayTraceShader.SetVector(_targetSizeShaderId, plane2.size);
    bakeRayTraceShader.SetVector(_rayTracingStepShaderId, rayTracingStep);
    bakeRayTraceShader.Dispatch("BakeRaygenShader", (int)viewRect.width, (int)viewRect.height, 1);

    var data = new byte[]
    {
      0, 0, 0, 0
    };
    resultBuffer.SetData(data);

    var kernelId = countPixelComputeShader.FindKernel("CSMain");
    countPixelComputeShader.SetTexture(kernelId, _pixelsShaderId, renderTarget);
    countPixelComputeShader.SetInt(_widthShaderId, (int)viewRect.width);
    countPixelComputeShader.SetInt(_heightShaderId, (int)viewRect.height);
    countPixelComputeShader.SetBuffer(kernelId, _resultShaderId, resultBuffer);
    countPixelComputeShader.GetKernelThreadGroupSizes(kernelId, out var grpSizeX, out var grpSizeY, out var _);
    countPixelComputeShader.Dispatch(kernelId, (int)math.ceil(viewRect.width / grpSizeX), (int)math.ceil(viewRect.height / grpSizeY), 1);

    resultBuffer.GetData(data);

    return data[0] > 0;
  }

  /// <summary>
  /// Unity OnDrawGizmosSelected.
  /// </summary>
  public void OnDrawGizmosSelected()
  {
    if (!isDrawGizmosWhenSelected || worldBounds.size.sqrMagnitude <= 1e-5f)
      return;

    DrawGizmos();
  }

  /// <summary>
  /// Unity OnDrawGizmos.
  /// </summary>
  public void OnDrawGizmos()
  {
    if (isDrawGizmosWhenSelected || worldBounds.size.sqrMagnitude <= 1e-5f)
      return;

    DrawGizmos();
  }

  /// <summary>
  /// draw gizmos.
  /// </summary>
  private void DrawGizmos()
  {
    if (isDrawCellGizmos)
    {
      Gizmos.color = Color.yellow;
      for (var x = worldBounds.min.x; x <= worldBounds.max.x + 1e-5f; x += cellSize.x)
      {
        for (var y = worldBounds.min.y; y <= worldBounds.max.y + 1e-5f; y += cellSize.y)
        {
          Gizmos.DrawLine(
            new Vector3(x, y, worldBounds.min.z),
            new Vector3(x, y, worldBounds.max.z));
        }

        for (var z = worldBounds.min.z; z <= worldBounds.max.z + 1e-5f; z += cellSize.z)
        {
          Gizmos.DrawLine(
            new Vector3(x, worldBounds.min.y, z),
            new Vector3(x, worldBounds.max.y, z));
        }
      }

      for (var y = worldBounds.min.y; y <= worldBounds.max.y + 1e-5f; y += cellSize.y)
      {
        for (var z = worldBounds.min.z; z <= worldBounds.max.z + 1e-5f; z += cellSize.z)
        {
          Gizmos.DrawLine(
            new Vector3(worldBounds.min.x, y, z),
            new Vector3(worldBounds.max.x, y, z));
        }
      }
    }

    if (isDrawWorldBoundsGizmos)
    {
      Gizmos.color = Color.green;
      Gizmos.DrawWireCube(worldBounds.center, worldBounds.size);
    }

    if (isDrawDebugPlanes)
    {
      foreach (var debugPlane in debugPlanes)
      {
        Gizmos.color = Color.red;
        Gizmos.DrawLine(debugPlane.leftBottomCorner, debugPlane.rightTopCorner);
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(debugPlane.center, debugPlane.center + debugPlane.normal);
      }
    }

    if (isDrawDebugLines)
    {
      foreach (var debugLine in debugLines)
      {
        Gizmos.color = Color.red;
        Gizmos.DrawLine(debugLine.pt1, debugLine.pt2);
      }
    }

    if (isDrawDebugEye && debugEyeTr)
    {
      var eyePos = debugEyeTr.position;
      if (worldBounds.Contains(eyePos))
      {
        var localEyePos = eyePos - worldBounds.min;
        var cellIndexX = (int)math.floor(localEyePos.x / cellSize.x);
        var cellIndexY = (int)math.floor(localEyePos.y / cellSize.y);
        var cellIndexZ = (int)math.floor(localEyePos.z / cellSize.z);
        for (var i = 0; i < renderers.Count; ++i)
        {
          if (!renderers[i].name.StartsWith("Sphere"))
            continue;

          Gizmos.color = GetVisibleFlag(i, cellIndexX, cellIndexY, cellIndexZ) ? Color.green : Color.red;
          Gizmos.DrawLine(eyePos, renderers[i].bounds.center);
//          if (GetVisibleFlag(i, cellIndexX, cellIndexY, cellIndexZ))
//          {
//            Gizmos.color = Color.green;
//            Gizmos.DrawLine(eyePos, renderers[i].bounds.center);
//          }
        }
      }
    }

    Gizmos.color = Color.white;
  }

  /// <summary>
  /// get cell count in x, y, z.
  /// </summary>
  /// <param name="cellCountX">the cell count along x axis.</param>
  /// <param name="cellCountY">the cell count along y axis.</param>
  /// <param name="cellCountZ">the cell count along z axis.</param>
  private void GetCellCount(out int cellCountX, out int cellCountY, out int cellCountZ)
  {
    cellCountX = (int)(worldBounds.size.x / cellSize.x);
    cellCountY = (int)(worldBounds.size.y / cellSize.y);
    cellCountZ = (int)(worldBounds.size.z / cellSize.z);
  }

  /// <summary>
  /// get total cell count.
  /// </summary>
  /// <returns>the total cell count.</returns>
  private int GetTotalCellCount()
  {
    GetCellCount(out var cellCountX, out var cellCountY, out var cellCountZ);
    return cellCountX * cellCountY * cellCountZ;
  }

  /// <summary>
  /// require render target for rect.
  /// </summary>
  /// <param name="rect">the rect.</param>
  /// <returns>the render target.</returns>
  private RTHandle RequireRenderTarget(ref Rect rect)
  {
    if (_rtCache.TryGetValue(rect, out var rt)) return rt;

    rt = RTHandles.Alloc(
      (int) math.ceil(rect.width),
      (int) math.ceil(rect.height),
      1,
      DepthBits.None,
      GraphicsFormat.R8G8B8A8_SRGB,
      FilterMode.Point,
      TextureWrapMode.Clamp,
      TextureDimension.Tex2D,
      true,
      false,
      false,
      false,
      1,
      0F,
      MSAASamples.None,
      false,
      false,
      RenderTextureMemoryless.None,
      $"RayTracingTarget_{rect.width}_{rect.height}");
    _rtCache.Add(rect, rt);

    return rt;
  }

  /// <summary>
  /// require result buffer.
  /// </summary>
  /// <returns>the result buffer.</returns>
  private ComputeBuffer RequireResultBuffer()
  {
    if (null != _resultBuffer)
      return _resultBuffer;
    _resultBuffer = new ComputeBuffer(1, 4, ComputeBufferType.Raw, ComputeBufferMode.Dynamic);
    return _resultBuffer;
  }
}
