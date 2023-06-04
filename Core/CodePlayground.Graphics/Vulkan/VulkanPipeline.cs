﻿using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanPipelineDescriptorSet
    {
        public DescriptorSetLayout Layout;
        public DescriptorSet[] Sets;
    }

    internal struct DescriptorSetPipelineResources
    {
        public Dictionary<int, BindingPipelineResources> Bindings { get; set; }
    }

    internal struct BindingPipelineResources
    {
        public Dictionary<int, IBindableVulkanResource> BoundResources { get; set; }
    }

    public interface IBindableVulkanResource
    {
        public ulong ID { get; }

        public void Bind(DescriptorSet[] sets, int set, int binding, int index, VulkanPipeline pipeline);
        public void Unbind(int set, int binding, int index, VulkanPipeline pipeline);
    }

    public sealed class VulkanPipeline : IPipeline
    {
        private static readonly IReadOnlyDictionary<ShaderTypeClass, IReadOnlyDictionary<int, Format>> sAttributeFormats;
        private static readonly Dictionary<ulong, VulkanPipeline> sPipelines;
        private static ulong sCurrentID;

        static VulkanPipeline()
        {
            sCurrentID = 0;
            sPipelines = new Dictionary<ulong, VulkanPipeline>();

            sAttributeFormats = new Dictionary<ShaderTypeClass, IReadOnlyDictionary<int, Format>>
            {
                [ShaderTypeClass.Float] = new Dictionary<int, Format>
                {
                    [1] = Format.R32Sfloat,
                    [2] = Format.R32G32Sfloat,
                    [3] = Format.R32G32B32Sfloat,
                    [4] = Format.R32G32B32A32Sfloat
                },
                [ShaderTypeClass.SInt] = new Dictionary<int, Format>
                {
                    [1] = Format.R32Sint,
                    [2] = Format.R32G32Sint,
                    [3] = Format.R32G32B32Sint,
                    [4] = Format.R32G32B32A32Sint
                },
                [ShaderTypeClass.UInt] = new Dictionary<int, Format>
                {
                    [1] = Format.R32Uint,
                    [2] = Format.R32G32Uint,
                    [3] = Format.R32G32B32Uint,
                    [4] = Format.R32G32B32A32Uint
                }
            };
        }

        public static ulong GenerateID() => sCurrentID++;
        public static VulkanPipeline? LookupID(ulong id)
        {
            if (sPipelines.TryGetValue(id, out VulkanPipeline? pipeline))
            {
                return pipeline;
            }
            else
            {
                return null;
            }
        }

        public unsafe VulkanPipeline(VulkanContext context, PipelineDescription description)
        {
            mDesc = description;
            mReflectionData = new Dictionary<ShaderStage, ShaderReflectionResult>();
            mBoundResources = new Dictionary<int, DescriptorSetPipelineResources>();

            mDescriptorSets = new Dictionary<int, VulkanPipelineDescriptorSet>();
            mDevice = context.Device;
            mCompiler = context.CreateCompiler();

            mLoaded = false;
            mDisposed = false;

            const uint maxSets = 50; // placeholder value
            const uint descriptorTypeCount = 10; // see VkDescriptorType

            var poolSizes = new DescriptorPoolSize[descriptorTypeCount];
            for (uint i = 0; i < descriptorTypeCount; i++)
            {
                poolSizes[i] = new DescriptorPoolSize
                {
                    Type = (DescriptorType)i,
                    DescriptorCount = maxSets * 16 // just to be safe
                };
            }

            fixed (DescriptorPoolSize* poolSizePtr = poolSizes)
            {
                var createInfo = VulkanUtilities.Init<DescriptorPoolCreateInfo>() with
                {
                    Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                    MaxSets = maxSets,
                    PoolSizeCount = descriptorTypeCount,
                    PPoolSizes = poolSizePtr
                };

                fixed (DescriptorPool* pool = &mDescriptorPool)
                {
                    var api = VulkanContext.API;
                    api.CreateDescriptorPool(mDevice.Device, &createInfo, null, pool).Assert();
                }
            }
        }

        ~VulkanPipeline()
        {
            if (!mDisposed)
            {
                Dispose(false);
                mDisposed = true;
            }
        }

        public void Dispose()
        {
            if (mDisposed)
            {
                return;
            }

            Dispose(true);
            mDisposed = true;
        }

        private unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (int set in mBoundResources.Keys)
                {
                    var setData = mBoundResources[set];
                    foreach (int binding in setData.Bindings.Keys)
                    {
                        var bindingData = setData.Bindings[binding];
                        foreach (int index in bindingData.BoundResources.Keys)
                        {
                            var resource = bindingData.BoundResources[index];
                            resource.Unbind(set, binding, index, this);
                        }
                    }
                }
            }

            Cleanup();

            var api = VulkanContext.API;
            api.DestroyDescriptorPool(mDevice.Device, mDescriptorPool, null);
        }

        private unsafe void Cleanup()
        {
            if (!mLoaded)
            {
                return;
            }

            var api = VulkanContext.API;
            api.DestroyPipeline(mDevice.Device, mPipeline, null);
            api.DestroyPipelineLayout(mDevice.Device, mLayout, null);

            foreach (int set in mDescriptorSets.Keys)
            {
                var setData = mDescriptorSets[set];

                api.FreeDescriptorSets(mDevice.Device, mDescriptorPool, setData.Sets).Assert();
                api.DestroyDescriptorSetLayout(mDevice.Device, setData.Layout, null);
            }

            mLoaded = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssertLoaded()
        {
            if (!mLoaded)
            {
                throw new InvalidOperationException("This pipeline has not loaded a shader!");
            }
        }

        void IPipeline.Bind(ICommandList commandList, int frame)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a Vulkan command buffer to bind to!");
            }

            Bind((VulkanCommandBuffer)commandList, frame);
        }

        public void Bind(VulkanCommandBuffer commandBuffer, int frame)
        {
            AssertLoaded();

            var api = VulkanContext.API;
            var buffer = commandBuffer.Buffer;
            var bindPoint = mDesc.Type switch
            {
                PipelineType.Graphics => PipelineBindPoint.Graphics,
                PipelineType.Compute => PipelineBindPoint.Compute,
                _ => throw new InvalidOperationException($"Invalid pipeline type: {mDesc.Type}")
            };

            api.CmdBindPipeline(buffer, bindPoint, mPipeline);
            foreach (int set in mDescriptorSets.Keys)
            {
                var setData = mDescriptorSets[set];
                api.CmdBindDescriptorSets(buffer, bindPoint, mLayout, (uint)set, 1, setData.Sets[frame], 0, 0);
            }
        }

        bool IPipeline.Bind(IDeviceBuffer buffer, string name, int index) => BindObject(buffer, name, index);
        bool IPipeline.Bind(ITexture texture, string name, int index) => BindObject(texture, name, index);

        private bool BindObject(object passedObject, string name, int index)
        {
            if (passedObject is IBindableVulkanResource resource)
            {
                return Bind(resource, name, index);
            }
            else
            {
                throw new ArgumentException("Must pass a bindable Vulkan resource!");
            }
        }

        public bool Bind(IBindableVulkanResource resource, string name, int index)
        {
            AssertLoaded();
            if (!FindResource(name, out ShaderStage stage, out int set, out int binding))
            {
                return false;
            }

            Bind(resource, set, binding, index);
            return true;
        }

        private void UnbindResource(int set, int binding, int index, ulong? newId)
        {
            if (!mBoundResources.TryGetValue(set, out DescriptorSetPipelineResources setData))
            {
                return;
            }

            if (!setData.Bindings.TryGetValue(binding, out BindingPipelineResources bindingData))
            {
                return;
            }

            if (!bindingData.BoundResources.TryGetValue(index, out IBindableVulkanResource? resource) || resource is null)
            {
                return;
            }

            if (resource.ID == newId)
            {
                return;
            }

            resource.Unbind(set, binding, index, this);
        }

        public void Bind(IBindableVulkanResource resource, int set, int binding, int index)
        {
            AssertLoaded();
            UnbindResource(set, binding, index, resource.ID);

            var sets = mDescriptorSets[set].Sets;
            resource.Bind(sets, set, binding, index, this);
        }

        public bool FindResource(string name, out ShaderStage stage, out int set, out int binding)
        {
            foreach (var currentStage in mReflectionData.Keys)
            {
                var data = mReflectionData[currentStage];
                foreach (int currentSet in data.Resources.Keys)
                {
                    var setResources = data.Resources[currentSet];
                    foreach (int currentBinding in setResources.Keys)
                    {
                        var currentResource = setResources[currentBinding];
                        if (currentResource.Name == name)
                        {
                            stage = currentStage;
                            set = currentSet;
                            binding = currentBinding;

                            return true;
                        }
                    }
                }
            }

            stage = ShaderStage.Vertex;
            set = binding = -1;
            return false;
        }

        public void Load(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            Cleanup();

            var filteredStages = shaders.Keys.Where(stage => mDesc.Type switch
            {
                PipelineType.Graphics => stage != ShaderStage.Compute,
                PipelineType.Compute => stage == ShaderStage.Compute,
                _ => throw new ArgumentException($"Invalid pipeline type: {mDesc.Type}")
            });

            if (!filteredStages.Any())
            {
                throw new ArgumentException($"No applicable shaders provided for a {mDesc.Type.ToString().ToLower()} pipeline!");
            }

            var filteredShaders = new Dictionary<ShaderStage, IShader>();
            foreach (var stage in filteredStages)
            {
                filteredShaders.Add(stage, shaders[stage]);
            }

            Reflect(filteredShaders);
            CreateDescriptorSets();
            CreatePipelineLayout();

            switch (mDesc.Type)
            {
                case PipelineType.Graphics:
                    CreateGraphicsPipeline(filteredShaders);
                    break;
                case PipelineType.Compute:
                    CreateComputePipeline(filteredShaders[ShaderStage.Compute]);
                    break;
            }

            mLoaded = true;
        }

        private void Reflect(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            mReflectionData.Clear();
            foreach (var stage in shaders.Keys)
            {
                var shader = shaders[stage];
                var data = mCompiler.Reflect(shader.Bytecode);
                mReflectionData.Add(stage, data);
            }
        }

        private unsafe void CreateDescriptorSets()
        {
            var layoutBindings = new Dictionary<int, List<DescriptorSetLayoutBinding>>();
            foreach (var stage in mReflectionData.Keys)
            {
                var stageReflectionData = mReflectionData[stage];
                foreach (int set in stageReflectionData.Resources.Keys)
                {
                    if (!layoutBindings.ContainsKey(set))
                    {
                        layoutBindings.Add(set, new List<DescriptorSetLayoutBinding>());
                    }

                    var bindingList = layoutBindings[set];
                    var setBindings = stageReflectionData.Resources[set];

                    foreach (int binding in setBindings.Keys)
                    {
                        var resource = setBindings[binding];
                        var typeData = stageReflectionData.Types[resource.Type];

                        uint descriptorCount = 1;
                        if (typeData.ArrayDimensions is not null)
                        {
                            foreach (var dimension in typeData.ArrayDimensions)
                            {
                                descriptorCount *= (uint)dimension;
                            }
                        }

                        var bindingData = VulkanUtilities.Init<DescriptorSetLayoutBinding>() with
                        {
                            Binding = (uint)binding,
                            DescriptorType = resource.ResourceType switch
                            {
                                ShaderResourceTypeFlags.Image | ShaderResourceTypeFlags.Sampler => DescriptorType.CombinedImageSampler,
                                ShaderResourceTypeFlags.Image => DescriptorType.SampledImage,
                                ShaderResourceTypeFlags.Sampler => DescriptorType.Sampler,
                                ShaderResourceTypeFlags.UniformBuffer => DescriptorType.UniformBuffer,
                                ShaderResourceTypeFlags.StorageBuffer => DescriptorType.StorageBuffer,
                                _ => throw new InvalidOperationException($"Flag combination not implemented: {resource.ResourceType}")
                            },
                            DescriptorCount = descriptorCount,
                            StageFlags = ShaderStageFlags.All
                        };

                        int existingIndex = bindingList.FindIndex(desc => desc.Binding == binding);
                        if (existingIndex < 0)
                        {
                            bindingList.Add(bindingData);
                        }
                        else if (bindingList[existingIndex].DescriptorType != bindingData.DescriptorType)
                        {
                            throw new InvalidOperationException($"Duplicate resource in set {set} binding {binding}!");
                        }
                    }
                }
            }

            mDescriptorSets.Clear();
            foreach (int set in layoutBindings.Keys)
            {
                var api = VulkanContext.API;
                var setData = new VulkanPipelineDescriptorSet
                {
                    Sets = new DescriptorSet[mDesc.FrameCount]
                };

                var bindings = layoutBindings[set].ToArray();
                fixed (DescriptorSetLayoutBinding* bindingPtr = bindings)
                {
                    var createInfo = VulkanUtilities.Init<DescriptorSetLayoutCreateInfo>() with
                    {
                        Flags = DescriptorSetLayoutCreateFlags.None,
                        BindingCount = (uint)bindings.Length,
                        PBindings = bindingPtr
                    };

                    api.CreateDescriptorSetLayout(mDevice.Device, &createInfo, null, &setData.Layout).Assert();
                }

                var layouts = new DescriptorSetLayout[mDesc.FrameCount];
                Array.Fill(layouts, setData.Layout);

                fixed (DescriptorSetLayout* layoutPtr = layouts)
                {
                    var allocInfo = VulkanUtilities.Init<DescriptorSetAllocateInfo>() with
                    {
                        DescriptorPool = mDescriptorPool,
                        DescriptorSetCount = (uint)mDesc.FrameCount,
                        PSetLayouts = layoutPtr
                    };

                    fixed (DescriptorSet* setPtr = setData.Sets)
                    {
                        api.AllocateDescriptorSets(mDevice.Device, &allocInfo, setPtr).Assert();
                    }
                }

                mDescriptorSets.Add(set, setData);
            }
        }

        private unsafe void CreatePipelineLayout()
        {
            var setIndices = mDescriptorSets.Keys.ToList();
            setIndices.Sort((a, b) => -a.CompareTo(b));

            var setLayouts = new DescriptorSetLayout[setIndices.Count > 0 ? (setIndices[0] + 1) : 0];
            foreach (var set in mDescriptorSets.Keys)
            {
                setLayouts[set] = mDescriptorSets[set].Layout;
            }

            fixed (DescriptorSetLayout* layoutPtr = setLayouts)
            {
                var createInfo = VulkanUtilities.Init<PipelineLayoutCreateInfo>() with
                {
                    Flags = PipelineLayoutCreateFlags.None,
                    SetLayoutCount = (uint)setLayouts.Length,
                    PSetLayouts = layoutPtr,
                    PushConstantRangeCount = 0,
                    PPushConstantRanges = null // currently not implemented
                };

                fixed (PipelineLayout* layout = &mLayout)
                {
                    var api = VulkanContext.API;
                    api.CreatePipelineLayout(mDevice.Device, &createInfo, null, layout).Assert();
                }
            }
        }

        private static Format GetAttributeFormat(ReflectedShaderType attributeType)
        {
            if (attributeType.Columns != 1 || (attributeType.ArrayDimensions?.Any() ?? false))
            {
                throw new InvalidOperationException("Every vertex attribute must be a single vector!");
            }

            if (attributeType.Size != 4)
            {
                throw new InvalidOperationException("Vector columns must be 32-bit integers or floats!");
            }

            if (!sAttributeFormats.ContainsKey(attributeType.Class))
            {
                throw new InvalidOperationException($"Unsupported vector type: {attributeType.Class}");
            }

            var formats = sAttributeFormats[attributeType.Class];
            if (!formats.ContainsKey(attributeType.Rows))
            {
                throw new InvalidOperationException($"Invalid vector size: {attributeType.Rows}");
            }

            return formats[attributeType.Rows];
        }

        private unsafe void CreateGraphicsPipeline(IReadOnlyDictionary<ShaderStage, IShader> shaders)
        {
            if (!shaders.ContainsKey(ShaderStage.Vertex) || !shaders.ContainsKey(ShaderStage.Fragment))
            {
                throw new ArgumentException("A graphics pipeline must consist of both a vertex & fragment shader!");
            }

            if (mDesc.RenderTarget is not VulkanRenderPass)
            {
                throw new ArgumentException("Must pass a Vulkan render pass!");
            }

            var stages = shaders.Keys.ToList();
            var shaderStageInfo = new PipelineShaderStageCreateInfo[stages.Count];

            using var marshal = new StringMarshal();
            for (int i = 0; i < stages.Count; i++)
            {
                var stage = stages[i];
                if (shaders[stage] is VulkanShader shader)
                {
                    shaderStageInfo[i] = VulkanUtilities.Init<PipelineShaderStageCreateInfo>() with
                    {
                        Flags = PipelineShaderStageCreateFlags.None,
                        Stage = stage switch
                        {
                            ShaderStage.Vertex => ShaderStageFlags.VertexBit,
                            ShaderStage.Fragment => ShaderStageFlags.FragmentBit,
                            ShaderStage.Geometry => ShaderStageFlags.GeometryBit,
                            _ => throw new ArgumentException($"Unsupported graphics shader stage: {stage}")
                        },
                        Module = shader.Module,
                        PName = marshal.MarshalString(shader.Entrypoint)
                    };
                }
                else
                {
                    throw new ArgumentException($"The passed {stage.ToString().ToLower()} shader is not a Vulkan shader!");
                }
            }

            var vertexReflectionData = mReflectionData[ShaderStage.Vertex];
            var inputs = vertexReflectionData.StageIO.Where(field => field.Direction == StageIODirection.In).ToList();
            inputs.Sort((a, b) => a.Location.CompareTo(b.Location));

            int vertexSize = 0;
            var attributes = new VertexInputAttributeDescription[inputs.Count];

            for (int i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var type = vertexReflectionData.Types[input.Type];

                attributes[i] = VulkanUtilities.Init<VertexInputAttributeDescription>() with
                {
                    Location = (uint)input.Location,
                    Binding = 0,
                    Format = GetAttributeFormat(type),
                    Offset = (uint)vertexSize
                };

                vertexSize += type.TotalSize;
            }

            var spec = mDesc.Specification;
            var frontFace = spec?.FrontFace ?? PipelineFrontFace.Clockwise;
            var blendMode = spec?.BlendMode ?? PipelineBlendMode.None;
            var enableDepthTesting = spec?.EnableDepthTesting ?? false;
            bool disableCulling = spec?.DisableCulling ?? false;

            var dynamicStates = new DynamicState[]
            {
                DynamicState.Viewport,
                DynamicState.Scissor
            };

            fixed (DynamicState* dynamicStatePtr = dynamicStates)
            {
                fixed (VertexInputAttributeDescription* attributePtr = attributes)
                {
                    fixed (PipelineShaderStageCreateInfo* stagePtr = shaderStageInfo)
                    {
                        var binding = VulkanUtilities.Init<VertexInputBindingDescription>() with
                        {
                            Binding = 0,
                            Stride = (uint)vertexSize,
                            InputRate = VertexInputRate.Vertex
                        };

                        var vertexInput = VulkanUtilities.Init<PipelineVertexInputStateCreateInfo>() with
                        {
                            VertexBindingDescriptionCount = 1,
                            PVertexBindingDescriptions = &binding,
                            VertexAttributeDescriptionCount = (uint)attributes.Length,
                            PVertexAttributeDescriptions = attributePtr
                        };

                        var inputAssembly = VulkanUtilities.Init<PipelineInputAssemblyStateCreateInfo>() with
                        {
                            Topology = PrimitiveTopology.TriangleList,
                            PrimitiveRestartEnable = false
                        };

                        var viewportState = VulkanUtilities.Init<PipelineViewportStateCreateInfo>() with
                        {
                            ViewportCount = 1,
                            PViewports = null,
                            ScissorCount = 1,
                            PScissors = null
                        };

                        var rasterization = VulkanUtilities.Init<PipelineRasterizationStateCreateInfo>() with
                        {
                            DepthClampEnable = false,
                            RasterizerDiscardEnable = false,
                            PolygonMode = PolygonMode.Fill,
                            CullMode = disableCulling ? CullModeFlags.None : CullModeFlags.BackBit,
                            FrontFace = frontFace switch
                            {
                                PipelineFrontFace.Clockwise => FrontFace.Clockwise,
                                PipelineFrontFace.CounterClockwise => FrontFace.CounterClockwise,
                                _ => throw new ArgumentException($"Invalid front face: {frontFace}")
                            },
                            DepthBiasEnable = false,
                            LineWidth = 1f
                        };

                        var multisampling = VulkanUtilities.Init<PipelineMultisampleStateCreateInfo>() with
                        {
                            RasterizationSamples = SampleCountFlags.Count1Bit,
                            SampleShadingEnable = false,
                            MinSampleShading = 1f,
                            PSampleMask = null,
                            AlphaToCoverageEnable = false,
                            AlphaToOneEnable = false
                        };

                        var depthStencil = VulkanUtilities.Init<PipelineDepthStencilStateCreateInfo>() with
                        {
                            DepthBoundsTestEnable = false,
                            StencilTestEnable = false,

                            DepthTestEnable = enableDepthTesting,
                            DepthWriteEnable = enableDepthTesting,
                            DepthCompareOp = CompareOp.Less,
                        };

                        var colorBlendAttachment = VulkanUtilities.Init<PipelineColorBlendAttachmentState>() with
                        {
                            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                        };

                        if (blendMode != PipelineBlendMode.None)
                        {
                            colorBlendAttachment.BlendEnable = true;
                            colorBlendAttachment.ColorBlendOp = BlendOp.Add;
                            colorBlendAttachment.AlphaBlendOp = BlendOp.Add;

                            switch (blendMode)
                            {
                                case PipelineBlendMode.SourceAlphaOneMinusSourceAlpha:
                                    colorBlendAttachment.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                                    colorBlendAttachment.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;

                                    colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.SrcAlpha;
                                    colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                                    break;
                                case PipelineBlendMode.OneZero:
                                    colorBlendAttachment.SrcColorBlendFactor = BlendFactor.One;
                                    colorBlendAttachment.DstColorBlendFactor = BlendFactor.Zero;

                                    colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
                                    colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.Zero;
                                    break;
                                case PipelineBlendMode.ZeroSourceColor:
                                    colorBlendAttachment.SrcColorBlendFactor = BlendFactor.Zero;
                                    colorBlendAttachment.DstColorBlendFactor = BlendFactor.SrcColor;

                                    colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.Zero;
                                    colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.SrcColor;
                                    break;
                                default:
                                    throw new ArgumentException("Unsupported blend mode!");
                            }
                        }

                        var colorBlending = VulkanUtilities.Init<PipelineColorBlendStateCreateInfo>() with
                        {
                            LogicOpEnable = false,
                            AttachmentCount = 1,
                            PAttachments = &colorBlendAttachment
                        };

                        var dynamicState = VulkanUtilities.Init<PipelineDynamicStateCreateInfo>() with
                        {
                            DynamicStateCount = (uint)dynamicStates.Length,
                            PDynamicStates = dynamicStatePtr
                        };

                        var createInfo = VulkanUtilities.Init<GraphicsPipelineCreateInfo>() with
                        {
                            Flags = PipelineCreateFlags.None,
                            StageCount = (uint)stages.Count,
                            PStages = stagePtr,
                            PVertexInputState = &vertexInput,
                            PInputAssemblyState = &inputAssembly,
                            PTessellationState = null,
                            PViewportState = &viewportState,
                            PRasterizationState = &rasterization,
                            PMultisampleState = &multisampling,
                            PDepthStencilState = &depthStencil,
                            PColorBlendState = &colorBlending,
                            PDynamicState = &dynamicState,
                            Layout = mLayout,
                            RenderPass = ((VulkanRenderPass)mDesc.RenderTarget).RenderPass,
                            Subpass = 0,
                            BasePipelineHandle = default,
                            BasePipelineIndex = 0
                        };

                        fixed (Pipeline* pipeline = &mPipeline)
                        {
                            var api = VulkanContext.API;
                            api.CreateGraphicsPipelines(mDevice.Device, default, 1, &createInfo, null, pipeline).Assert();
                        }
                    }
                }
            }
        }

        private unsafe void CreateComputePipeline(IShader shader)
        {
            using var marshal = new StringMarshal();
            if (shader is VulkanShader vulkanShader)
            {
                var createInfo = VulkanUtilities.Init<ComputePipelineCreateInfo>() with
                {
                    Flags = PipelineCreateFlags.None,
                    Stage = VulkanUtilities.Init<PipelineShaderStageCreateInfo>() with
                    {
                        Flags = PipelineShaderStageCreateFlags.None,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = vulkanShader.Module,
                        PName = marshal.MarshalString(vulkanShader.Entrypoint)
                    },
                    Layout = mLayout,
                    BasePipelineHandle = default,
                    BasePipelineIndex = 0
                };

                fixed (Pipeline* pipeline = &mPipeline)
                {
                    var api = VulkanContext.API;
                    api.CreateComputePipelines(mDevice.Device, default, 1, &createInfo, null, pipeline).Assert();
                }
            }
            else
            {
                throw new ArgumentException("The passed compute shader is not a Vulkan shader!");
            }
        }

        public bool ResourceExists(string resource)
        {
            return FindResource(resource, out ShaderStage stage, out int set, out int binding);
        }

        public int GetBufferSize(string resource)
        {
            if (!FindResource(resource, out ShaderStage stage, out int set, out int binding))
            {
                return -1;
            }

            return GetBufferSize(stage, set, binding);
        }

        public int GetBufferSize(ShaderStage stage, int set, int binding)
        {
            var reflectionData = mReflectionData[stage];
            var resource = reflectionData.Resources[set][binding];
            var typeData = reflectionData.Types[resource.Type];

            if (typeData.Class != ShaderTypeClass.Struct)
            {
                return -1;
            }

            return typeData.Size;
        }

        public int GetBufferOffset(string resource, string expression)
        {
            if (!FindResource(resource, out ShaderStage stage, out int set, out int binding))
            {
                return -1;
            }

            return GetBufferOffset(stage, set, binding, expression);
        }

        public int GetBufferOffset(ShaderStage stage, int set, int binding, string expression)
        {
            var reflectionData = mReflectionData[stage];
            var resource = reflectionData.Resources[set][binding];
            var typeData = reflectionData.Types[resource.Type];

            if (typeData.Class != ShaderTypeClass.Struct)
            {
                return -1;
            }

            return GetTypeOffset(stage, resource.Type, expression);
        }

        private int GetTypeOffset(ShaderStage stage, int type, string expression)
        {
            const char beginIndexCharacter = '[';
            const char endIndexCharacter = ']';

            var reflectionData = mReflectionData[stage];
            var typeData = reflectionData.Types[type];
            var fields = typeData.Fields!;

            if (typeData.Class != ShaderTypeClass.Struct)
            {
                return -1;
            }

            int memberOperator = expression.IndexOf('.');
            string fieldExpression = memberOperator < 0 ? expression : expression[0..memberOperator];

            int beginIndexOperator = fieldExpression.IndexOf(beginIndexCharacter);
            string fieldName = beginIndexOperator < 0 ? fieldExpression : fieldExpression[0..beginIndexOperator];

            if (string.IsNullOrEmpty(fieldName))
            {
                throw new ArgumentException("No field name given!");
            }

            if (!fields.ContainsKey(fieldName))
            {
                return -1;
            }

            var field = fields[fieldName];
            int offset = field.Offset;

            if (beginIndexOperator >= 0)
            {
                var fieldType = reflectionData.Types[field.Type];
                if (fieldType.ArrayDimensions is null || !fieldType.ArrayDimensions.Any())
                {
                    throw new InvalidOperationException("Cannot index a non-array field!");
                }

                int stride = field.Stride;
                if (stride <= 0)
                {
                    throw new InvalidOperationException("SPIRV-Cross did not provide a stride value!");
                }

                var indexStrings = new List<string>();
                for (int i = beginIndexOperator; i < fieldExpression.Length; i++)
                {
                    char character = fieldExpression[i];
                    if (character == beginIndexCharacter)
                    {
                        indexStrings.Add(beginIndexCharacter.ToString());
                    }
                    else
                    {
                        var currentString = indexStrings[^1];
                        if (currentString[^1] == endIndexCharacter)
                        {
                            throw new InvalidOperationException("Malformed index operator!");
                        }

                        indexStrings[^1] = currentString + character;
                    }
                }

                for (int i = 0; i < indexStrings.Count; i++)
                {
                    var indexString = indexStrings[i];
                    if (indexString[^1] != endIndexCharacter)
                    {
                        throw new InvalidOperationException("Malformed index operator!");
                    }

                    int index = int.Parse(indexString[1..^1]);
                    int dimensionIndex = fieldType.ArrayDimensions.Count - (i + 1);
                    int dimensionStride = stride;

                    if (index < 0 || index >= fieldType.ArrayDimensions[dimensionIndex])
                    {
                        throw new IndexOutOfRangeException();
                    }

                    for (int j = 0; j < dimensionIndex; j++)
                    {
                        dimensionStride *= fieldType.ArrayDimensions[j];
                    }

                    offset += index * dimensionStride;
                }
            }

            if (memberOperator >= 0)
            {
                offset += GetTypeOffset(stage, field.Type, expression[(memberOperator + 1)..]);
            }

            return offset;
        }

        public PipelineDescription Description => mDesc;

        private readonly PipelineDescription mDesc;
        private readonly Dictionary<ShaderStage, ShaderReflectionResult> mReflectionData;

        private Pipeline mPipeline;
        private PipelineLayout mLayout;

        private readonly Dictionary<int, DescriptorSetPipelineResources> mBoundResources;
        private readonly Dictionary<int, VulkanPipelineDescriptorSet> mDescriptorSets;
        private readonly DescriptorPool mDescriptorPool;

        private readonly VulkanDevice mDevice;
        private readonly IShaderCompiler mCompiler;
        private bool mLoaded, mDisposed;
    }
}
