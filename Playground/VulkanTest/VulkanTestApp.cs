﻿using CodePlayground;
using CodePlayground.Graphics;
using CodePlayground.Graphics.Vulkan;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using VulkanTest.Shaders;

[assembly: LoadedApplication(typeof(VulkanTest.VulkanTestApp))]

namespace VulkanTest
{
    [ApplicationTitle("Vulkan Test")]
    [ApplicationGraphicsAPI(AppGraphicsAPI.Vulkan)]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    public class VulkanTestApp : GraphicsApplication
    {
        public VulkanTestApp()
        {
            Utilities.BindHandlers(this, this);
        }

        [EventHandler(nameof(Load))]
        private unsafe void OnLoad()
        {
            CreateGraphicsContext<VulkanContext>();

            var swapchain = GraphicsContext!.Swapchain;
            swapchain.VSync = true; // enable vsync

            mShaderLibrary = new ShaderLibrary(this);
            mPipeline = mShaderLibrary.LoadPipeline<TestShader>(new PipelineDescription
            {
                RenderTarget = swapchain.RenderTarget,
                Type = PipelineType.Graphics,
                FrameCount = swapchain.FrameCount
            });
        }

        protected override void OnContextCreation(IGraphicsContext context)
        {
            if (context is VulkanContext vulkanContext)
            {
                vulkanContext.DebugMessage += DebugMessageCallback;
            }
        }

        private unsafe static void DebugMessageCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type, DebugUtilsMessengerCallbackDataEXT data)
        {
            var severityNames = new Dictionary<DebugUtilsMessageSeverityFlagsEXT, string>
            {
                [DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt] = "Verbose",
                [DebugUtilsMessageSeverityFlagsEXT.InfoBitExt] = "Info",
                [DebugUtilsMessageSeverityFlagsEXT.WarningBitExt] = "Warning",
                [DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt] = "Error"
            };

            string severityName = string.Empty;
            foreach (var severityFlag in severityNames.Keys)
            {
                if (severity.HasFlag(severityFlag))
                {
                    severityName = severityNames[severityFlag];
                    break;
                }
            }

            string message = Marshal.PtrToStringAnsi((nint)data.PMessage) ?? string.Empty;
            Console.WriteLine($"Vulkan validation layer: [{severityName}] {message}");

            if (Debugger.IsAttached && severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt))
            {
                Debugger.Break();
            }
        }

        [EventHandler(nameof(Closing))]
        private void OnClose()
        {
            mPipeline?.Dispose();
            mShaderLibrary?.Dispose();
            GraphicsContext?.Dispose();
        }

        [EventHandler(nameof(Render))]
        private void OnRender(FrameRenderInfo renderInfo)
        {
            if (renderInfo.CommandList is null || renderInfo.RenderTarget is null || renderInfo.Framebuffer is null)
            {
                return;
            }

            var clearColor = new Vector4D<float>(1f, 0f, 0f, 1f);
            renderInfo.RenderTarget.BeginRender(renderInfo.CommandList, renderInfo.Framebuffer, clearColor);

            mPipeline!.Bind(renderInfo.CommandList, renderInfo.CurrentFrame);
            // todo: render

            renderInfo.RenderTarget.EndRender(renderInfo.CommandList);
        }

        private ShaderLibrary? mShaderLibrary;
        private IPipeline? mPipeline;
    }
}
