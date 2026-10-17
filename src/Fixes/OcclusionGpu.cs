using UnityEngine;
using UnityEngine.Rendering;

namespace GapCloser.Fixes
{
    // the gpu side of OcclusionCulling: the pyramid's level sizes and the commands that build it and test the bounds.
    // only unity types here, so the shader project's kernel test compiles this same file
    internal sealed class OcclusionGpu
    {
        public readonly Vector4[] LevelInfo = new Vector4[16];
        public int Levels, SrcW, SrcH;
        public ComputeBuffer Pyramid;
        private readonly ComputeShader _shader;
        private readonly int _first, _next, _test;

        public OcclusionGpu(ComputeShader shader)
        {
            _shader = shader;
            _first = shader.FindKernel("FirstLevel");
            _next = shader.FindKernel("NextLevel");
            _test = shader.FindKernel("TestBounds");
        }

        // level 0 is half the depth image, every next level half the one before, down to one texel
        public void Size(int w, int h)
        {
            SrcW = w;
            SrcH = h;
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

        // unity's _ZBufferParams for a camera with these planes
        public static Vector4 ZParams(float near, float far, bool reversed)
        {
            float x = reversed ? -1f + far / near : 1f - far / near;
            float y = reversed ? 1f : far / near;
            return new Vector4(x, y, x / far, y / far);
        }

        // the pyramid from the depth image, then the test of count bounds (each grown by inflate meters). flipY 0 reads the
        // depth image with its first row at the image bottom, as the game's depth texture is (checked 17 Oct)
        public void Record(CommandBuffer cmd, Texture src, bool raw, Vector4 zParams, float near, float far, float ignoreNear,
            Matrix4x4 worldToClip, Matrix4x4 worldToCamera, ComputeBuffer bounds, int count, float margin, float marginScale,
            float inflate, int flipY, ComputeBuffer result, ComputeBuffer rect)
        {
            cmd.SetComputeTextureParam(_shader, _first, "_Src", src);
            cmd.SetComputeIntParams(_shader, "_SrcSize", src.width, src.height);
            cmd.SetComputeIntParam(_shader, "_SrcMode", raw ? 0 : 1);
            cmd.SetComputeVectorParam(_shader, "_ZParams", zParams);
            cmd.SetComputeFloatParam(_shader, "_Far", far);
            cmd.SetComputeFloatParam(_shader, "_IgnoreNear", ignoreNear);
            cmd.SetComputeVectorArrayParam(_shader, "_LevelInfo", LevelInfo);
            cmd.SetComputeIntParam(_shader, "_Levels", Levels);
            cmd.SetComputeBufferParam(_shader, _first, "_Pyramid", Pyramid);
            cmd.DispatchCompute(_shader, _first, ((int)LevelInfo[0].y + 7) / 8, ((int)LevelInfo[0].z + 7) / 8, 1);
            cmd.SetComputeBufferParam(_shader, _next, "_Pyramid", Pyramid);
            for (int level = 1; level < Levels; level++)
            {
                cmd.SetComputeIntParam(_shader, "_Level", level);
                cmd.DispatchCompute(_shader, _next, ((int)LevelInfo[level].y + 7) / 8, ((int)LevelInfo[level].z + 7) / 8, 1);
            }
            cmd.SetComputeBufferParam(_shader, _test, "_Pyramid", Pyramid);
            cmd.SetComputeBufferParam(_shader, _test, "_Bounds", bounds);
            cmd.SetComputeIntParam(_shader, "_Count", count);
            cmd.SetComputeMatrixParam(_shader, "_WorldToClip", worldToClip);
            cmd.SetComputeVectorParam(_shader, "_ViewRow2", worldToCamera.GetRow(2));
            cmd.SetComputeFloatParam(_shader, "_Near", near);
            cmd.SetComputeFloatParam(_shader, "_Margin", margin);
            cmd.SetComputeFloatParam(_shader, "_MarginScale", marginScale);
            cmd.SetComputeFloatParam(_shader, "_Inflate", inflate);
            cmd.SetComputeIntParam(_shader, "_FlipY", flipY);
            cmd.SetComputeBufferParam(_shader, _test, "_Result", result);
            cmd.SetComputeBufferParam(_shader, _test, "_Rect", rect);
            cmd.DispatchCompute(_shader, _test, (count + 63) / 64, 1, 1);
        }
    }
}
