﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Xenko.Graphics;
using SiliconStudio.Xenko.Rendering.Images;
using Buffer = SiliconStudio.Xenko.Graphics.Buffer;

namespace SiliconStudio.Xenko.Rendering.ComputeEffect.LambertianPrefiltering
{
    /// <summary>
    /// Performs Lambertian pre-filtering in the form of Spherical Harmonics.
    /// </summary>
    public class LambertianPrefilteringSHNoCompute : ImageEffect
    {
        private int harmonicalOrder;

        private readonly ImageEffectShader firstPassEffect;
        private readonly ImageEffectShader secondPassEffect;

        /// <summary>
        /// Gets or sets the level of precision desired when calculating the spherical harmonics.
        /// </summary>
        public int HarmonicOrder
        {
            get { return harmonicalOrder; }
            set
            {
                harmonicalOrder = Math.Max(1, Math.Min(5, value));

                firstPassEffect.Parameters.Set(SphericalHarmonicsParameters.HarmonicsOrder, harmonicalOrder);
            }
        }

        /// <summary>
        /// Gets the computed spherical harmonics corresponding to the pre-filtered lambertian.
        /// </summary>
        public SphericalHarmonics PrefilteredLambertianSH { get; private set; }

        /// <summary>
        /// Gets or sets the input radiance map to pre-filter.
        /// </summary>
        public Texture RadianceMap { get; set; }

        public LambertianPrefilteringSHNoCompute(RenderContext context)
            : base(context, "LambertianPrefilteringSHNoCompute")
        {
            firstPassEffect = new ImageEffectShader("LambertianPrefilteringSHNoComputeEffectPass1");
            secondPassEffect = new ImageEffectShader("LambertianPrefilteringSHNoComputePass2");

            HarmonicOrder = 3;
        }

        protected override void DrawCore(RenderContext context)
        {
            var inputTexture = RadianceMap;
            if (inputTexture == null)
                return;

            // Gets and checks the input texture
            if (inputTexture.Dimension != TextureDimension.TextureCube)
                throw new NotSupportedException("Only texture cube are currently supported as input of 'LambertianPrefilteringSH' effect.");

            var faceCount = inputTexture.Dimension == TextureDimension.TextureCube ? 6 : 1;
            var coefficientsCount = harmonicalOrder * harmonicalOrder;

            firstPassEffect.Parameters.Set(SphericalHarmonicsParameters.HarmonicsOrder, harmonicalOrder);
            firstPassEffect.Parameters.Set(LambertianPrefilteringSHNoComputePass1Keys.RadianceMap, inputTexture);

            // Create a tree of power-of-two textures for summing up coefficients
            var intermediateSize = new Int2(MathUtil.NextPowerOfTwo(inputTexture.Width), MathUtil.NextPowerOfTwo(inputTexture.Height));
            var intermediateTextures = new List<Texture>();

            intermediateTextures.Add(NewScopedRenderTarget2D(intermediateSize.X, intermediateSize.Y, PixelFormat.R32G32B32A32_Float));
            while (intermediateSize.X > 1 || intermediateSize.Y > 1)
            {
                if (intermediateSize.X > 1)
                    intermediateSize.X >>= 1;
                if (intermediateSize.Y > 1)
                    intermediateSize.Y >>= 1;

                intermediateTextures.Add(NewScopedRenderTarget2D(intermediateSize.X, intermediateSize.Y, PixelFormat.R32G32B32A32_Float));
            }

            // Create a staging texture for each coefficient
            var stagingTextures = new Texture[coefficientsCount];
            var stagingDescription = intermediateTextures[intermediateTextures.Count - 1].Description.ToStagingDescription();
            for (var c = 0; c < coefficientsCount; c++)
                stagingTextures[c] = Texture.New(GraphicsDevice, stagingDescription);

            // Calculate one coefficient at a time
            for (var c = 0; c < coefficientsCount; c++)
            {
                // Project the radiance on the SH basis and sum up the results for all faces
                firstPassEffect.Parameters.Set(LambertianPrefilteringSHNoComputePass1Keys.CoefficientIndex, c);
                firstPassEffect.Parameters.Set(LambertianPrefilteringSHNoComputePass1Keys.RadianceMap, RadianceMap);
                firstPassEffect.SetOutput(intermediateTextures[0]);
                firstPassEffect.Draw(context);

                // Recursive summation
                for (var i = 1; i < intermediateTextures.Count; i++)
                {
                    secondPassEffect.SetInput(intermediateTextures[i - 1]);
                    secondPassEffect.SetOutput(intermediateTextures[i]);
                    secondPassEffect.Draw(context);
                }

                GraphicsDevice.Copy(intermediateTextures[intermediateTextures.Count - 1], stagingTextures[c]);
            }

            // Create and initialize result SH
            PrefilteredLambertianSH = new SphericalHarmonics(HarmonicOrder);

            // Read back coefficients and store it in the SH
            for (var c = 0; c < coefficientsCount; c++)
            {
                var value = stagingTextures[c].GetData<Vector4>()[0];
                PrefilteredLambertianSH.Coefficients[c] = 4 * MathUtil.Pi / value.W * new Color3(value.X, value.Y, value.Z);
            }
        }
    }
}