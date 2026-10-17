using UnityEngine;
using UnityEngine.Rendering;

namespace GapCloser.Fixes
{
    // the gpu side of SunShadows: command buffers on the sun that build a pyramid of its shadow map right after unity draws
    // it and capture the cascades while unity still holds them, and the test of piece bounds against both. only unity
    // types here, so the shader project's test compiles this same file
    internal sealed class SunShadowGpu
    {
        public readonly Vector4[] LevelInfo = new Vector4[16];
        public int Levels, MapW, MapH;
        public ComputeBuffer Pyramid, Info;
        public RenderTexture Cascades;
        public Light Light { get; private set; }
        private readonly ComputeShader _shader;
        private readonly Material _capture;
        private readonly int _info, _first, _next, _test;
        private CommandBuffer _afterMap, _beforeMask;
        private bool _reversed;

        public SunShadowGpu(ComputeShader shader, Shader capture)
        {
            _shader = shader;
            _capture = new Material(capture) { hideFlags = HideFlags.HideAndDontSave };
            _info = shader.FindKernel("ShadowInfo");
            _first = shader.FindKernel("ShadowFirstLevel");
            _next = shader.FindKernel("ShadowNextLevel");
            _test = shader.FindKernel("TestShadow");
            Cascades = new RenderTexture(4, 8, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave,
            };
            Cascades.Create();
            Info = new ComputeBuffer(1, 16);
        }

        // level 0 is half the shadow map, every next level half the one before, down to one texel
        public void Size(int w, int h)
        {
            MapW = w;
            MapH = h;
            int lw = (w + 1) / 2, lh = (h + 1) / 2, offset = 0;
            Levels = 0;
            while (Levels < LevelInfo.Length)
            {
                LevelInfo[Levels] = new Vector4(offset, lw, lh, 0);
                offset += lw * lh;
                Levels++;
                if (lw == 1 && lh == 1) break;
                lw = (lw + 1) / 2;
                lh = (lh + 1) / 2;
            }
            if (Pyramid == null || Pyramid.count < offset)
            {
                Pyramid?.Release();
                Pyramid = new ComputeBuffer(offset, 4);
            }
        }

        // records the two command buffers for the current size and adds them to the light
        public void Attach(Light light, bool reversed)
        {
            Detach();
            Light = light;
            _reversed = reversed;
            _afterMap = new CommandBuffer { name = "GapCloser sun shadow pyramid" };
            var map = new RenderTargetIdentifier(BuiltinRenderTextureType.CurrentActive);
            _afterMap.SetComputeTextureParam(_shader, _info, "_ShadowSrc", map);
            _afterMap.SetComputeBufferParam(_shader, _info, "_SInfo", Info);
            _afterMap.DispatchCompute(_shader, _info, 1, 1, 1);
            _afterMap.SetComputeIntParam(_shader, "_Reversed", reversed ? 1 : 0);
            _afterMap.SetComputeVectorArrayParam(_shader, "_SLevelInfo", LevelInfo);
            _afterMap.SetComputeIntParam(_shader, "_SLevels", Levels);
            _afterMap.SetComputeTextureParam(_shader, _first, "_ShadowSrc", map);
            _afterMap.SetComputeBufferParam(_shader, _first, "_SPyramid", Pyramid);
            _afterMap.DispatchCompute(_shader, _first, ((int)LevelInfo[0].y + 7) / 8, ((int)LevelInfo[0].z + 7) / 8, 1);
            _afterMap.SetComputeBufferParam(_shader, _next, "_SPyramid", Pyramid);
            for (int level = 1; level < Levels; level++)
            {
                _afterMap.SetComputeIntParam(_shader, "_SLevel", level);
                _afterMap.DispatchCompute(_shader, _next, ((int)LevelInfo[level].y + 7) / 8, ((int)LevelInfo[level].z + 7) / 8, 1);
            }
            light.AddCommandBuffer(LightEvent.AfterShadowMap, _afterMap);
            _beforeMask = new CommandBuffer { name = "GapCloser sun shadow cascades" };
            _beforeMask.SetRenderTarget(Cascades);
            _beforeMask.DrawProcedural(Matrix4x4.identity, _capture, 0, MeshTopology.Triangles, 3);
            light.AddCommandBuffer(LightEvent.BeforeScreenspaceMask, _beforeMask);
        }

        public void Detach()
        {
            if (Light != null)
            {
                if (_afterMap != null) Light.RemoveCommandBuffer(LightEvent.AfterShadowMap, _afterMap);
                if (_beforeMask != null) Light.RemoveCommandBuffer(LightEvent.BeforeScreenspaceMask, _beforeMask);
            }
            _afterMap?.Release();
            _beforeMask?.Release();
            _afterMap = _beforeMask = null;
            Light = null;
        }

        // the test of count bounds (each grown by inflate meters), hidden only margin meters behind the casters
        public void RecordTest(CommandBuffer cmd, ComputeBuffer bounds, int count, int cascades, float inflate, float margin, float border, ComputeBuffer result)
        {
            cmd.SetComputeTextureParam(_shader, _test, "_ShadowMat", Cascades);
            cmd.SetComputeBufferParam(_shader, _test, "_SPyramid", Pyramid);
            cmd.SetComputeVectorArrayParam(_shader, "_SLevelInfo", LevelInfo);
            cmd.SetComputeIntParam(_shader, "_SLevels", Levels);
            cmd.SetComputeIntParam(_shader, "_Reversed", _reversed ? 1 : 0);
            cmd.SetComputeIntParam(_shader, "_Cascades", cascades);
            cmd.SetComputeBufferParam(_shader, _test, "_SBounds", bounds);
            cmd.SetComputeIntParam(_shader, "_SCount", count);
            cmd.SetComputeFloatParam(_shader, "_SInflate", inflate);
            cmd.SetComputeFloatParam(_shader, "_SMargin", margin);
            cmd.SetComputeFloatParam(_shader, "_SBorder", border);
            cmd.SetComputeBufferParam(_shader, _test, "_SResult", result);
            cmd.DispatchCompute(_shader, _test, (count + 63) / 64, 1, 1);
        }

        public void Release()
        {
            Detach();
            Pyramid?.Release();
            Info?.Release();
            if (Cascades != null) Cascades.Release();
            Pyramid = Info = null;
        }
    }
}
