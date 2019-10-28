# 使用DXR加速PVS烘焙

Version 1.0, 2019-Oct-28
<br/>
Copyright 2019. ZHing. All rights received.

# 1. PVS
PVS全称为Potentially Visible Sets，是游戏引擎中常用的静态遮挡剔除方案。

## 1.1. 为什么不用Unity自带的Occlusion Culling？
Unity自带的Occlusion Culling在运行时消耗大量CPU时间且不支持动态加载与卸载PVS数据。

## 1.2. 有GPU加速的动态遮挡剔除技术为什么还要PVS？
纯粹依赖动态遮挡剔除无论是基于CPU的还是GPU的都存在一定的性能消耗，因此先用运行代价更小的静态遮挡剔除过滤一遍数据后再使用动态遮挡剔除将会大大减小动态遮挡剔除的性能损耗。

## 1.3. 静态遮挡剔除的缺点
1、因为是预先烘焙的，无法对场景中的动态物体起效。

2、烘焙PVS数据非常耗时。基于此问题，本文将阐述如何通过Unity 2019.3集成的DXR加速PVS数据烘焙。

# 2. PVS数据的组织

## 2.1. 空间划分
计算PVS的第一步就是将整个场景空间划分为许多的格子。

首先计算出场景的最大包围盒。

![avatar](images/1_worldbounds.png)

根据需要创建2D或者3D的空间划分。

![avatar](images/2_2dgrids.png)

![avatar](images/3_3dgrids.png)

本文后续以3D空间划分方案阐述。

这样就获得了X * Y * Z个格子。接下来收集场景中所有的静态物体，假设一共收集到N个静态物体，那么就需要分配一个X * Y * Z * N大小的空间存储每一个格子对每一个对象的可见性信息。

**注意：文本所附源码工程中直接采用直接保存场景中静态物体引用的方法快速索引静态物体，仅是为了简化Demo流程。实际项目中考虑到动态加载与卸载需要更加复杂的方法组织静态物体列表，但这已超出的本文所述范围，不做详细说明。**

## 2.2. 选取计算可见性射线的起点和终点
在计算每一个格子到每一个物体的可见性测试之前我们可以做一些优化工作。

每一个格子可以看成一个AABB，每一个物体也有自己的AABB。两个AABB是否可以互通没有阻挡，只需要在两个AABB上的六个面中能够相互“看到”的面上选点判定即可。

![avatar](images/4_planefaceplane.png)

只有当两个面上点的连线与两个面的法线的夹角都为0-90度时，两个面上的点才可能相互“看到”。作为简单判定，只判断面的四个顶点是否满足条件。
```csharp
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
```
当判定两个面上的点可能互相“看到”之后，就是在这两个面上取点然后做RayCast看是否可连通，如果任何一次RayCast连通则两个面互相可见，即两个AABB互相可见。

在两个面上选定点时，我们没有采用随机取点的方案，而是按一定的uv间隔取点。

![avatar](images/5_selectpoints.png)

为什么不采用随机选点的方案？请参看下图

![avatar](images/6_randompoints.png)

(a)为随机采样，(b)我们选择的方案，(c)分层随机采样

A方案非常不均匀，容易错过一些重要位置的采样。C方案需要引入随机扰动，增加计算量。对于PVS计算来说，B方案足够了。
```csharp
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
```

## 2.3. 烘焙
接下来就是一个接一个的计算每个格子对应每个物体的可见性，这里为了快速验证结果，只烘焙了下图中蓝色球体的PVS信息。

![avatar](images/7_cpuresult.png)

红线连线表示在*眼睛*位置不可见，绿色连线表示可见。

烘焙耗时：30.18786秒

# 3. DXR加速烘焙
由第二节的内容可知，整个烘焙过程其实就是RayCast的过程，因此非常适合使用DXR实现加速。

## 3.1. RayTrace Shader
```glsl
#pragma max_recursion_depth 1

RaytracingAccelerationStructure _AccelerationStructure;

RWTexture2D<float4> _Result;

float3 _ViewLeftBottomCorner;
float3 _ViewRightDir;
float3 _ViewUpDir;
float2 _ViewSize;

float3 _TargetLeftBottomCorner;
float3 _TargetRightDir;
float3 _TargetUpDir;
float2 _TargetSize;

float2 _RayTracingStep;

struct RayIntersection
{
  float missing;
};

[shader("raygeneration")]
void BakeRaygenShader()
{
  uint2 dispatchIdx = DispatchRaysIndex().xy;
  float2 uv = (dispatchIdx + 0.5f) / DispatchRaysDimensions().xy;

  RayDesc rayDescriptor;
  rayDescriptor.Origin = _ViewLeftBottomCorner + uv.x * _ViewSize.x * _ViewRightDir + uv.y * _ViewSize.y * _ViewUpDir;
  for (float x = 0.5f * _RayTracingStep.x; x < _TargetSize.x; x += _RayTracingStep.x)
  {
    for (float y = 0.5f * _RayTracingStep.y; y < _TargetSize.y; y += _RayTracingStep.y)
    {
      float3 dir = (_TargetLeftBottomCorner + x * _TargetRightDir + y * _TargetUpDir) - rayDescriptor.Origin;
      rayDescriptor.Direction = normalize(dir);
      rayDescriptor.TMin = 1e-5f;
      rayDescriptor.TMax = length(dir);

      RayIntersection rayIntersection;
      rayIntersection.missing = 0.0f;

      TraceRay(_AccelerationStructure, RAY_FLAG_NONE, 0xFF, 0, 1, 0, rayDescriptor, rayIntersection);
      if (rayIntersection.missing > 0.0f)
      {
        _Result[dispatchIdx] = float4(1, 1, 1, 1);
        return;
      }
    }
  }

  _Result[dispatchIdx] = float4(0, 0, 0, 0);
}

[shader("miss")]
void MissShader(inout RayIntersection rayIntersection : SV_RayPayload)
{
  rayIntersection.missing = 1.0f;
}
```

关于RayTrace Shader的使用可以参看微软官方文档、Unity文档或本人写的另外两篇介绍DXR Ray Tracing的“[GPU Ray Tracing in One Weekend by Unity 2019.3](https://zhuanlan.zhihu.com/p/88366613)”和“[GPU Ray Tracing: Rest of Your Life by Unity 2019.3](https://zhuanlan.zhihu.com/p/88619055)”

**_ViewLeftBottomCorner**, **_ViewRightDir**, **_ViewUpDir**, **_ViewSize**为做面对面是否“可见”检测时起始面的参数。

**_TargetLeftBottomCorner**, **_TargetRightDir**, **_TargetUpDir**, **_TargetSize**为目标面的参数。

**BakeRaygenShader**中就是遍历发射光线做检测，如果光线碰到任何物体，*missing*值为0，如果没有碰到任何物体则执行**MissShader** *missing*值被设置为1。

关于此Shader如何被调用的参看C#代码。

## 3.2. C#代码
```csharp
var viewRect = new Rect(0F, 0F, math.ceil(plane1.size.x / rayTracingStep.x), math.ceil(plane1.size.y / rayTracingStep.x));
var renderTarget = RequireRenderTarget(ref viewRect);

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
```
首先计算出起始面需要发射的光线数*viewRect*大小为N * M。然后通过*RequireRenderTarget*函数获取指定大小的RenderTarget，函数实现参看源码工程。

最后调用**Dispatch**进行N * M的Ray Tracing。

## 3.3. 获取检测结果
在经过3.2节的Ray Tracing之后得到的是一张N*M的黑白RenderTarget，黑色表示被阻挡，白色表示没有阻挡。只要存在任何一个像素为白色即两个面互相可见，即两个AABB互相可见。

这里使用Compute Shader来完成此计算。
```glsl
#pragma kernel CSMain

Texture2D<float> _Pixels;
RWByteAddressBuffer _Result;
uint _Width;
uint _Height;

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
  if (id.x >= _Width || id.y >= _Height)
    return;

  int tmp;
  _Result.InterlockedMax(0, _Pixels[id.xy].r * 255, tmp);
}
```
代码非常简单，不做过多解释，调用C#代码如下。
```csharp
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
```
代码非常简单，不做过多解释。最终 *data[0]* 如果大于0则表示可见。

## 3.4. 烘焙结果
结果同CPU烘焙结果一致。但耗时大大缩短。

CPU烘焙耗时：30.18786秒
GPU烘焙耗时：5.335464s秒

# 4. 使用Unity DXR烘焙的问题
截至本文发布时，Unity集成DXR的版本为2019.3 Beta8。在使用RayTrace Shader存在非常严重的问题，如果一帧内调用Dispatch过多将会导致编辑器**崩溃**！！！

因此为了得到如下烘焙结果，

![avatar](images/8_final.png)

必须进行分批烘焙，每次最多烘焙3个目标。。。（为了烘焙此结果蛋都碎了一地）

分批烘焙代码参阅源代码中的**PVSBakerAvoidCrashWindow**类。

期待将来Unity能够修正此问题。在Unity修正此问题之前DXR加速烘焙并不能够真正使用到项目中。

# 5. 工程源代码
[https://github.com/zhing2006/Bake-PVS-By-GPU-Ray-Tracing](https://github.com/zhing2006/Bake-PVS-By-GPU-Ray-Tracing)