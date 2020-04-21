﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Microsoft.MixedReality.WebRTC.Unity
{
    /// <summary>
    /// Custom video source capturing the Unity scene content as rendered by a given camera,
    /// and sending it as a video track through the selected peer connection.
    /// </summary>
    public class SceneVideoSource : CustomVideoSource<Argb32VideoFrameStorage>
    {
        /// <summary>
        /// Camera used to capture the scene content, whose rendering is used as
        /// video content for the track.
        /// </summary>
        /// <remarks>
        /// If the project uses Multi-Pass stereoscopic rendering, then this camera needs to
        /// render to a single eye to produce a single video frame. Generally this means that
        /// this needs to be a separate Unity camera from the one used for XR rendering, which
        /// is generally rendering to both eyes.
        ///
        /// If the project uses Single-Pass Instanced stereoscopic rendering, then Unity 2019.1+
        /// is required to make this component work, due to the fact earlier versions of Unity
        /// are missing some command buffer API calls to be able to efficiently access the camera
        /// backbuffer in this mode. For Unity 2018.3 users who cannot upgrade, use Single-Pass
        /// (non-instanced) instead.
        /// </remarks>
        [Header("Camera")]
        [Tooltip("Camera used to capture the scene content sent by the track.")]
        [CaptureCamera]
        public Camera SourceCamera;

        /// <summary>
        /// Camera event indicating the point in time during the Unity frame rendering
        /// when the camera rendering is to be captured.
        /// 
        /// This defaults to <see xref="CameraEvent.AfterEverything"/>, which is a reasonable
        /// default to capture the entire scene rendering, but can be customized to achieve
        /// other effects like capturing only a part of the scene.
        /// </summary>
        [Tooltip("Camera event when to insert the scene capture at.")]
        public CameraEvent CameraEvent = CameraEvent.AfterEverything;

        /// <summary>
        /// Command buffer attached to the camera to capture its rendered content from the GPU
        /// and transfer it to the CPU for dispatching to WebRTC.
        /// </summary>
        private CommandBuffer _commandBuffer;

        /// <summary>
        /// Read-back texture where the content of the camera backbuffer is copied before being
        /// transferred from GPU to CPU. The size of the texture is <see cref="_frameSize"/>.
        /// </summary>
        private RenderTexture _readBackTex;

        /// <summary>
        /// Cached width, in pixels, of the readback texture and video frame produced.
        /// </summary>
        private int _readBackWidth;

        /// <summary>
        /// Cached height, in pixels, of the readback texture and video frame produced.
        /// </summary>
        private int _readBackHeight;

        protected new void OnEnable()
        {
            // If a headset is active then do not capture scene stream.
            // Note: this was failing on Oculus Quest, but it looks like a solution exists,
            // see error message from the Quest:
            //
            // "NotSupportedException: Capturing scene content in single - pass instanced stereo
            // rendering requires blitting from the Texture2DArray render target of the camera,
            // which is not supported before Unity 2019.1. To use this feature, either upgrade
            // your project to Unity 2019.1 + or use single-pass non - instanced stereo rendering
            // (XRSettings.stereoRenderingMode = SinglePass)."

            if (XRDevice.isPresent)
            {
                return;
            }

            // If no camera provided, attempt to fallback to main camera
            if (SourceCamera == null)
            {
                var mainCameraGameObject = GameObject.FindGameObjectWithTag("MainCamera");
                if (mainCameraGameObject != null)
                {
                    SourceCamera = mainCameraGameObject.GetComponent<Camera>();
                }
            }
            if (SourceCamera == null)
            {
                throw new NullReferenceException("Empty source camera for SceneVideoSource, and could not find MainCamera as fallback.");
            }

            CreateCommandBuffer();
            SourceCamera.AddCommandBuffer(CameraEvent, _commandBuffer);

            base.OnEnable();
        }

        protected new void OnDisable()
        {
            base.OnDisable();

            if (_commandBuffer != null)
            {
                // The camera sometimes goes away before this component.
                if (SourceCamera != null)
                {
                    SourceCamera.RemoveCommandBuffer(CameraEvent, _commandBuffer);
                }

                _commandBuffer.Dispose();
                _commandBuffer = null;
            }
        }

        /// <summary>
        /// Create the command buffer reading the scene content from the source camera back into CPU memory
        /// and delivering it via the <see cref="OnSceneFrameReady(AsyncGPUReadbackRequest)"/> callback to
        /// the underlying WebRTC track.
        /// </summary>
        private void CreateCommandBuffer()
        {
            if (_commandBuffer != null)
            {
                throw new InvalidOperationException("Command buffer already initialized.");
            }

            // By default, use the camera's render target texture size
            _readBackWidth = SourceCamera.scaledPixelWidth;
            _readBackHeight = SourceCamera.scaledPixelHeight;

            // Offset and scale into source render target.
            Vector2 srcScale = Vector2.one;
            Vector2 srcOffset = Vector2.zero;
            RenderTextureFormat srcFormat = RenderTextureFormat.ARGB32;

            // Handle stereoscopic rendering for VR/AR.
            // See https://unity3d.com/how-to/XR-graphics-development-tips for details.
            if (SourceCamera.stereoEnabled)
            {
                // Readback size is the size of the texture for a single eye.
                // The readback will occur on the left eye (chosen arbitrarily).
                _readBackWidth = XRSettings.eyeTextureWidth;
                _readBackHeight = XRSettings.eyeTextureHeight;
                srcFormat = XRSettings.eyeTextureDesc.colorFormat;

                if (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass)
                {
                    // Multi-pass is similar to non-stereo, nothing to do.

                    // Ensure camera is not rendering to both eyes in multi-pass stereo, otherwise the command buffer
                    // is executed twice (once per eye) and will produce twice as many frames, which leads to stuttering
                    // when playing back the video stream resulting from combining those frames.
                    if (SourceCamera.stereoTargetEye == StereoTargetEyeMask.Both)
                    {
                        throw new InvalidOperationException("SourceCamera has stereoscopic rendering enabled to both eyes" +
                            " with multi-pass rendering (XRSettings.stereoRenderingMode = MultiPass). This is not supported" +
                            " with SceneVideoSource, as this would produce one image per eye. Either set XRSettings." +
                            "stereoRenderingMode to single-pass (instanced or not), or use multi-pass with a camera rendering" +
                            " to a single eye (Camera.stereoTargetEye != Both).");
                    }
                }
                else if (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePass)
                {
                    // Single-pass (non-instanced) stereo use "wide-buffer" packing.
                    // Left eye corresponds to the left half of the buffer.
                    srcScale.x = 0.5f;
                }
                else if ((XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced)
                    || (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview)) // same as instanced (OpenGL)
                {
                    // Single-pass instanced stereo use texture array packing.
                    // Left eye corresponds to the first array slice.
#if !UNITY_2019_1_OR_NEWER
                    // https://unity3d.com/unity/alpha/2019.1.0a13
                    // "Graphics: Graphics.Blit and CommandBuffer.Blit methods now support blitting to and from texture arrays."
                    throw new NotSupportedException("Capturing scene content in single-pass instanced stereo rendering requires" +
                        " blitting from the Texture2DArray render target of the camera, which is not supported before Unity 2019.1." +
                        " To use this feature, either upgrade your project to Unity 2019.1+ or use single-pass non-instanced stereo" +
                        " rendering (XRSettings.stereoRenderingMode = SinglePass).");
#endif
                }
            }

            _readBackTex = new RenderTexture(_readBackWidth, _readBackHeight, 0, srcFormat, RenderTextureReadWrite.Linear);

            _commandBuffer = new CommandBuffer();
            _commandBuffer.name = "SceneVideoSource";

            // Explicitly set the render target to instruct the GPU to discard previous content.
            // https://docs.unity3d.com/ScriptReference/Rendering.CommandBuffer.Blit.html recommends this.
            //< TODO - This doesn't work
            //_commandBuffer.SetRenderTarget(_readBackTex, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);

            // Copy camera target to readback texture
            _commandBuffer.BeginSample("Blit");
#if UNITY_2019_1_OR_NEWER
            int srcSliceIndex = 0; // left eye
            int dstSliceIndex = 0;
            _commandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, /*BuiltinRenderTextureType.CurrentActive*/_readBackTex,
                srcScale, srcOffset, srcSliceIndex, dstSliceIndex);
#else
            _commandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, /*BuiltinRenderTextureType.CurrentActive*/_readBackTex, srcScale, srcOffset);
#endif
            _commandBuffer.EndSample("Blit");

            // Copy readback texture to RAM asynchronously, invoking the given callback once done
            _commandBuffer.BeginSample("Readback");
            _commandBuffer.RequestAsyncReadback(_readBackTex, 0, TextureFormat.BGRA32, OnSceneFrameReady);
            _commandBuffer.EndSample("Readback");
        }

        /// <summary>
        /// Callback invoked by the base class when the WebRTC track requires a new frame.
        /// </summary>
        /// <param name="request">The frame request to serve.</param>
        protected override void OnFrameRequested(in FrameRequest request)
        {
            // Try to dequeue a frame from the internal frame queue
            if (_frameQueue.TryDequeue(out Argb32VideoFrameStorage storage))
            {
                var frame = new Argb32VideoFrame
                {
                    width = storage.Width,
                    height = storage.Height,
                    stride = (int)storage.Width * 4
                };
                unsafe
                {
                    fixed (void* ptr = storage.Buffer)
                    {
                        // Complete the request with a view over the frame buffer (no allocation)
                        // while the buffer is pinned into memory. The native implementation will
                        // make a copy into a native memory buffer if necessary before returning.
                        frame.data = new IntPtr(ptr);
                        request.CompleteRequest(frame);
                    }
                }

                // Put the allocated buffer back in the pool for reuse
                _frameQueue.RecycleStorage(storage);
            }
        }

        /// <summary>
        /// Callback invoked by the command buffer when the scene frame GPU readback has completed
        /// and the frame is available in CPU memory.
        /// </summary>
        /// <param name="request">The completed and possibly failed GPU readback request.</param>
        private void OnSceneFrameReady(AsyncGPUReadbackRequest request)
        {
            // Read back the data from GPU, if available
            if (request.hasError)
            {
                return;
            }
            NativeArray<byte> rawData = request.GetData<byte>();
            Debug.Assert(rawData.Length >= _readBackWidth * _readBackHeight * 4);
            unsafe
            {
                byte* ptr = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(rawData);

                // Enqueue a frame in the internal frame queue. This will make a copy
                // of the frame into a pooled buffer owned by the frame queue.
                var frame = new Argb32VideoFrame
                {
                    data = (IntPtr)ptr,
                    stride = _readBackWidth * 4,
                    width = (uint)_readBackWidth,
                    height = (uint)_readBackHeight
                };
                _frameQueue.Enqueue(frame);
            }
        }
    }
}

