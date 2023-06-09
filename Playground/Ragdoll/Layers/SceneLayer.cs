using BepuPhysics;
using BepuUtilities;
using BepuUtilities.Memory;
using CodePlayground.Graphics;
using ImGuiNET;
using Ragdoll.Components;
using Ragdoll.Shaders;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ragdoll.Layers
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    internal sealed class RegisteredComponentAttribute : Attribute
    {
        public RegisteredComponentAttribute()
        {
            DisplayName = string.Empty;
        }

        public string DisplayName { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    internal sealed class ImGuiMenuAttribute : Attribute
    {
        public ImGuiMenuAttribute(string path)
        {
            Path = path;
            Flags = ImGuiWindowFlags.None;
            Visible = true;
            Fullscreen = false;
            RenderChildren = true;
            NoPadding = false;
        }

        public string Path { get; }
        public ImGuiWindowFlags Flags { get; set; }
        public bool Visible { get; set; }
        public bool Fullscreen { get; set; }
        public bool RenderChildren { get; set; }
        public bool NoPadding { get; set; }
    }

    internal delegate void ImGuiMenuCallback(IEnumerable<ImGuiMenu> children);
    internal sealed class ImGuiMenu
    {
        public ImGuiMenu(string title, ImGuiWindowFlags flags, bool visible, bool fullscreen, bool renderChildren, bool noPadding, ImGuiMenuCallback callback)
        {
            mVisible = visible;
            mFullscreen = fullscreen;
            mRenderChildren = renderChildren;
            mNoPadding = noPadding;
            mTitle = title;
            mFlags = flags;
            mCallback = callback;
            mChildren = new List<ImGuiMenu>();
        }

        public void Render(bool enableVisibility = true)
        {
            if (!mVisible)
            {
                return;
            }

            var flags = mFlags;
            if (mFullscreen)
            {
                var viewport = ImGui.GetMainViewport();

                ImGui.SetNextWindowPos(viewport.WorkPos);
                ImGui.SetNextWindowSize(viewport.WorkSize);
                ImGui.SetNextWindowViewport(viewport.ID);

                ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

                flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize;
                flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;
            }

            if (mNoPadding)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            }

            bool visible;
            if (enableVisibility)
            {
                visible = ImGui.Begin(mTitle, ref mVisible, flags);
            }
            else
            {
                visible = ImGui.Begin(mTitle, flags);
            }

            int styleVarCount = 0;
            if (mNoPadding)
            {
                styleVarCount++;
            }

            if (mFullscreen)
            {
                styleVarCount += 2;
            }

            if (styleVarCount > 0)
            {
                ImGui.PopStyleVar(styleVarCount);
            }

            if (visible)
            {
                mCallback.Invoke(mChildren);
            }

            ImGui.End();
            if (mRenderChildren && visible)
            {
                foreach (var child in mChildren)
                {
                    child.Render();
                }
            }
        }

        public ref bool Visible => ref mVisible;
        public ref bool Fullscreen => ref mFullscreen;
        public string Title => mTitle;
        public IList<ImGuiMenu> Children => mChildren;

        private bool mVisible, mFullscreen;
        private readonly bool mRenderChildren, mNoPadding;
        private readonly string mTitle;
        private readonly ImGuiWindowFlags mFlags;
        private readonly ImGuiMenuCallback mCallback;
        private readonly List<ImGuiMenu> mChildren;
    }

    internal struct ImGuiMenuInfo
    {
        public ImGuiMenu? Menu { get; set; }
        public Dictionary<string, ImGuiMenuInfo> Children { get; set; }
    }

    internal struct ComponentTypeInfo
    {
        public Type Type { get; set; }
        public string DisplayName { get; set; }
    }

    internal struct LoadedModelInfo
    {
        public IPipeline[] Pipelines { get; set; }
        // not much else...
    }

    internal struct CameraBufferData
    {
        public Matrix4x4 ViewProjection;
    }

    internal struct PushConstantData
    {
        public Matrix4x4 Model;
        public int BoneOffset;
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    internal sealed class SceneLayer : Layer
    {
        private static IReadOnlyList<ComponentTypeInfo> sComponentTypes;
        static SceneLayer()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var types = assembly.GetTypes();

            var componentTypes = new List<ComponentTypeInfo>();
            foreach (var type in types)
            {
                var attribute = type.GetCustomAttribute<RegisteredComponentAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var displayName = attribute.DisplayName;
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = type.Name;
                }

                componentTypes.Add(new ComponentTypeInfo
                {
                    Type = type,
                    DisplayName = displayName
                });
            }

            sComponentTypes = componentTypes;
        }

        public SceneLayer()
        {
            mSelectedEntity = Scene.Null;
            mMenus = LoadMenus();
            mModelPath = mModelName = mModelError = string.Empty;
            mLoadedModelInfo = new Dictionary<int, LoadedModelInfo>();
            mFramebufferAttachments = new FramebufferAttachmentInfo[2];
            mCameraBuffer = null;
            mReflectionView = null;
        }

        private int LoadModel(string path, string name)
        {
            var app = App.Instance;
            var library = app.Renderer!.Library;
            var registry = app.ModelRegistry!;

            int modelId = registry.Load<ModelShader>(path, name, nameof(ModelShader.u_BoneBuffer));
            if (modelId < 0)
            {
                return -1;
            }

            var modelData = registry.Models[modelId];
            var materials = modelData.Model.Materials;

            var pipelines = new IPipeline[materials.Count];
            for (int i = 0; i < materials.Count; i++)
            {
                var material = materials[i];
                var pipeline = pipelines[i] = library.LoadPipeline<ModelShader>(new PipelineDescription
                {
                    RenderTarget = mRenderTarget,
                    Type = PipelineType.Graphics,
                    FrameCount = Renderer.FrameCount,
                    Specification = material.PipelineSpecification
                });

                pipeline.Bind(mCameraBuffer!, nameof(ModelShader.u_CameraBuffer));
                pipeline.Bind(modelData.BoneBuffer, nameof(ModelShader.u_BoneBuffer));
                material.Bind(pipeline, nameof(ModelShader.u_MaterialBuffer), textureType => $"u_{textureType}Map");
            }

            mLoadedModelInfo.Add(modelId, new LoadedModelInfo
            {
                Pipelines = pipelines
            });

            return modelId;
        }

        #region Menu loading

        private IReadOnlyList<ImGuiMenu> LoadMenus()
        {
            var methods = GetType().GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            var menus = new Dictionary<string, ImGuiMenuInfo>();

            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<ImGuiMenuAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var path = attribute.Path.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (path.Length == 0)
                {
                    throw new ArgumentException("Cannot pass an empty path!");
                }

                var currentContainer = menus;
                var callback = (ImGuiMenuCallback)Delegate.CreateDelegate(typeof(ImGuiMenuCallback), method.IsStatic ? null : this, method);
                var menu = new ImGuiMenu(path[^1], attribute.Flags, attribute.Visible, attribute.Fullscreen, attribute.RenderChildren, attribute.NoPadding, callback);

                for (int i = 0; i < path.Length; i++)
                {
                    var segment = path[i];
                    if (!currentContainer.TryGetValue(segment, out ImGuiMenuInfo menuInfo))
                    {
                        currentContainer[segment] = menuInfo = new ImGuiMenuInfo
                        {
                            Menu = i < path.Length - 1 ? null : menu,
                            Children = new Dictionary<string, ImGuiMenuInfo>()
                        };
                    }
                    else if (i == path.Length - 1)
                    {
                        if (menuInfo.Menu is not null)
                        {
                            throw new InvalidOperationException($"Duplicate menu title: {segment}");
                        }

                        menuInfo.Menu = menu;
                        currentContainer[segment] = menuInfo;
                    }

                    currentContainer = menuInfo.Children;
                }
            }

            var result = new List<ImGuiMenu>();
            LinkMenus(result, menus);

            return result;
        }

        private void LinkMenus(IList<ImGuiMenu> result, IReadOnlyDictionary<string, ImGuiMenuInfo> children)
        {
            foreach (var child in children.Values)
            {
                var menu = child.Menu;
                if (menu is not null)
                {
                    result.Add(menu);
                }

                LinkMenus(menu?.Children ?? result, child.Children);
            }
        }

        #endregion
        #region Layer events

        public override void OnPushed()
        {
            var app = App.Instance;
            var graphicsContext = app.GraphicsContext!;

            // for transfer operations
            var queue = graphicsContext.Device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();
            commandList.Begin();

            mReflectionView = app.Renderer!.Library.CreateReflectionView<ModelShader>();
            int bufferSize = mReflectionView.GetBufferSize(nameof(ModelShader.u_CameraBuffer));
            if (bufferSize < 0)
            {
                throw new InvalidOperationException("Failed to find camera buffer!");
            }

            mCameraBuffer = graphicsContext.CreateDeviceBuffer(DeviceBufferUsage.Uniform, bufferSize);
            mFramebufferSemaphore = graphicsContext.CreateSemaphore();
            mFramebufferRecreated = true;

            var size = app.RootWindow!.FramebufferSize;
            CreateFramebufferAttachments(size.X, size.Y, commandList);
            mFramebuffer = graphicsContext.CreateFramebuffer(new FramebufferInfo
            {
                Width = size.X,
                Height = size.Y,
                Attachments = mFramebufferAttachments
            }, out mRenderTarget);

            // test scene
            mScene = new Scene();
            {
                int modelId = LoadModel("../../../../VulkanTest/Resources/Models/sledge-hammer.fbx", "sledge-hammer");

                ulong entity = mScene.NewEntity("Model");
                mScene.AddComponent<TransformComponent>(entity).Scale = Vector3.One * 10f;
                mScene.AddComponent<RenderedModelComponent>(entity).UpdateModel(modelId, entity);

                entity = mScene.NewEntity("Camera");
                mScene.AddComponent<CameraComponent>(entity).MainCamera = true;

                var transform = mScene.AddComponent<TransformComponent>(entity);
                transform.Translation = (Vector3.UnitY - Vector3.UnitZ) * 7.5f;
                transform.Rotation.X = 45f;
            }

            commandList.AddSemaphore(mFramebufferSemaphore, SemaphoreUsage.Signal);
            commandList.End();
            queue.Submit(commandList);
        }

        private void CreateFramebufferAttachments(int width, int height, ICommandList commandList)
        {
            var graphicsContext = App.Instance.GraphicsContext!;
            var colorImage = graphicsContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(width, height),
                Usage = DeviceImageUsageFlags.Render | DeviceImageUsageFlags.ColorAttachment,
                Format = DeviceImageFormat.RGBA8_UNORM,
                MipLevels = 1
            });

            var depthImage = graphicsContext.CreateDeviceImage(new DeviceImageInfo
            {
                Size = new Size(width, height),
                Usage = DeviceImageUsageFlags.DepthStencilAttachment,
                Format = DeviceImageFormat.DepthStencil,
                MipLevels = 1
            });

            var colorLayout = colorImage.GetLayout(DeviceImageLayoutName.ShaderReadOnly);
            colorImage.TransitionLayout(commandList, colorImage.Layout, colorLayout);
            colorImage.Layout = colorLayout;

            var depthLayout = depthImage.GetLayout(DeviceImageLayoutName.DepthStencilAttachment);
            depthImage.TransitionLayout(commandList, depthImage.Layout, depthLayout);
            depthImage.Layout = depthLayout;

            mFramebufferAttachments[0] = new FramebufferAttachmentInfo
            {
                Image = colorImage,
                Type = AttachmentType.Color,
                Layout = colorImage.GetLayout(DeviceImageLayoutName.ColorAttachment)
            };

            mFramebufferAttachments[1] = new FramebufferAttachmentInfo
            {
                Image = depthImage,
                Type = AttachmentType.DepthStencil
            };

            mViewportTexture = colorImage.CreateTexture(false, null);
        }

        private void DestroyFramebuffer()
        {
            mFramebuffer?.Dispose();
            mViewportTexture?.Dispose();

            foreach (var attachment in mFramebufferAttachments)
            {
                attachment.Image.Dispose();
            }
        }

        public override void OnPopped()
        {
            mCameraBuffer?.Dispose();
            foreach (var modelData in mLoadedModelInfo.Values)
            {
                foreach (var pipeline in modelData.Pipelines)
                {
                    pipeline.Dispose();
                }
            }

            DestroyFramebuffer();
            mRenderTarget?.Dispose();
            mFramebufferSemaphore?.Dispose();

            mScene?.Dispose();
            mScene = null;
            mSelectedEntity = Scene.Null;
        }

        // this math is so fucking confusing...
        // aligned with TransformComponent.operator Matrix4x4
        private static void ComputeCameraVectors(Vector3 angle, out Vector3 direction, out Vector3 up)
        {
            var radians = angle * MathF.PI / 180f;
            // +X - tilt camera up
            // +Y - rotate view to the right
            // +Z - roll camera counterclockwise

            float pitch = -radians.X;
            float yaw = -radians.Y;
            float roll = radians.Z + MathF.PI / 2f; // we want the up vector to face +Y at 0 degrees roll

            // yaw offset of 90 degrees - we want the camera to be facing +Z at 0 degrees yaw
            float directionPitch = MathF.Sin(roll) * pitch - MathF.Cos(roll) * yaw;
            float directionYaw = MathF.Sin(roll) * yaw - MathF.Cos(roll) * pitch + MathF.PI / 2f;

            direction = Vector3.Normalize(new Vector3
            {
                X = MathF.Cos(directionYaw) * MathF.Cos(directionPitch),
                Y = MathF.Sin(directionPitch),
                Z = MathF.Sin(directionYaw) * MathF.Cos(directionPitch)
            });

            up = Vector3.Normalize(new Vector3
            {
                X = MathF.Cos(yaw) * MathF.Cos(roll),
                Y = MathF.Sin(roll),
                Z = MathF.Sin(yaw) * MathF.Cos(roll)
            });
        }

        public override void OnUpdate(double delta)
        {
            if (mScene is null)
            {
                return;
            }

            mScene.Update(delta);

            var app = App.Instance;
            var context = app.GraphicsContext;
            var registry = app.ModelRegistry;

            if (registry is not null)
            {
                foreach (ulong entity in mScene.ViewEntities(typeof(RenderedModelComponent)))
                {
                    var modelData = mScene.GetComponent<RenderedModelComponent>(entity);

                    var skeleton = modelData.Model?.Skeleton;
                    if (skeleton is null)
                    {
                        continue;
                    }

                    var boneTransforms = new Matrix4x4[skeleton.BoneCount];
                    var globalTransforms = new List<Matrix4x4>();
                    for (int i = 0; i < boneTransforms.Length; i++)
                    {
                        int parent = skeleton.GetParent(i);
                        var parentTransform = parent < 0 ? skeleton.GetParentTransform(i) : globalTransforms[parent];

                        var nodeTransform = skeleton.GetTransform(i) * modelData.BoneTransforms![i];
                        var globalTransform = parentTransform * nodeTransform;

                        var offsetMatrix = skeleton.GetOffsetMatrix(i);
                        var boneTransform = globalTransform * offsetMatrix;

                        globalTransforms.Add(globalTransform);
                        boneTransforms[i] = Matrix4x4.Transpose(boneTransform);
                    }

                    var boneBuffer = registry.Models[modelData.ID].BoneBuffer;
                    boneBuffer.CopyFromCPU(boneTransforms, modelData.BoneOffset * Marshal.SizeOf<Matrix4x4>());
                }
            }

            if (context is not null)
            {
                var camera = Scene.Null;
                foreach (ulong entity in mScene.ViewEntities(typeof(TransformComponent), typeof(CameraComponent)))
                {
                    var component = mScene.GetComponent<CameraComponent>(entity);
                    if (component.MainCamera)
                    {
                        camera = entity;
                        break;
                    }
                }

                if (camera != Scene.Null)
                {
                    var transform = mScene.GetComponent<TransformComponent>(camera);
                    var cameraData = mScene.GetComponent<CameraComponent>(camera);

                    float aspectRatio = (float)mFramebuffer!.Width / mFramebuffer!.Height;
                    float fov = cameraData.FOV * MathF.PI / 180f;
                    ComputeCameraVectors(transform.Rotation + cameraData.RotationOffset, out Vector3 direction, out Vector3 up);

                    var math = new MatrixMath(context);
                    var projection = math.Perspective(fov, aspectRatio, 0.1f, 100f);
                    var view = math.LookAt(transform.Translation, transform.Translation + direction, up);

                    mCameraBuffer?.MapStructure(mReflectionView!, nameof(ModelShader.u_CameraBuffer), new CameraBufferData
                    {
                        // GLSL matrices are column-major
                        // System.Numerics uses row-major
                        ViewProjection = Matrix4x4.Transpose(projection * view)
                    });
                }
            }
        }

        public override void OnImGuiRender()
        {
            foreach (var menu in mMenus)
            {
                menu.Render(false);
            }
        }

        public override void PreRender(Renderer renderer)
        {
            if (mScene is null)
            {
                return;
            }

            var commandList = renderer.FrameInfo.CommandList;
            if (mFramebufferRecreated)
            {
                commandList.AddSemaphore(mFramebufferSemaphore!, SemaphoreUsage.Wait);
                mFramebufferRecreated = false;
            }

            var colorAttachment = mFramebufferAttachments[0];
            var attachmentLayout = colorAttachment.Layout!;
            var renderLayout = colorAttachment.Image.Layout;

            colorAttachment.Image.TransitionLayout(commandList, renderLayout, attachmentLayout);
            renderer.BeginRender(mRenderTarget!, mFramebuffer!, new Vector4(0.2f, 0.2f, 0.2f, 1f));

            foreach (ulong id in mScene.ViewEntities(typeof(TransformComponent), typeof(RenderedModelComponent)))
            {
                var transform = mScene.GetComponent<TransformComponent>(id);
                var renderedModel = mScene.GetComponent<RenderedModelComponent>(id);

                var model = renderedModel.Model;
                if (model is null)
                {
                    continue;
                }

                var info = mLoadedModelInfo[renderedModel.ID];
                foreach (var mesh in model.Submeshes)
                {
                    var pipeline = info.Pipelines[mesh.MaterialIndex];
                    renderer.RenderMesh(model.VertexBuffer, model.IndexBuffer, pipeline,
                                        mesh.IndexOffset, mesh.IndexCount, DeviceBufferIndexType.UInt32,
                                        mapped =>
                    {
                        mReflectionView!.MapStructure(mapped, nameof(ModelShader.u_PushConstants), new PushConstantData
                        {
                            Model = Matrix4x4.Transpose(transform), // see OnUpdate
                            BoneOffset = renderedModel.BoneOffset
                        });
                    });
                }
            }

            renderer.EndRender();
            colorAttachment.Image.TransitionLayout(commandList, attachmentLayout, renderLayout);
        }

        #endregion
        #region Menus

        private void RecreateFramebuffer(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var graphicsContext = App.Instance.GraphicsContext!;
            var device = graphicsContext.Device;
            device.ClearQueues();

            var queue = device.GetQueue(CommandQueueFlags.Transfer);
            var commandList = queue.Release();
            commandList.Begin();

            DestroyFramebuffer();
            CreateFramebufferAttachments(width, height, commandList);

            mFramebuffer = graphicsContext.CreateFramebuffer(new FramebufferInfo
            {
                Width = width,
                Height = height,
                Attachments = mFramebufferAttachments
            }, mRenderTarget!);

            if (!mFramebufferRecreated)
            {
                mFramebufferRecreated = true;
                commandList.AddSemaphore(mFramebufferSemaphore!, SemaphoreUsage.Signal);
            }

            commandList.End();
            queue.Submit(commandList);
        }

        [ImGuiMenu("Dockspace/Viewport", NoPadding = true)]
        private void Viewport(IEnumerable<ImGuiMenu> children)
        {
            var imageSize = ImGui.GetContentRegionAvail();

            int width = (int)imageSize.X;
            int height = (int)imageSize.Y;
            if (width != mFramebuffer?.Width || height != mFramebuffer?.Height)
            {
                RecreateFramebuffer(width, height);
            }

            var app = App.Instance;
            var imguiLayer = app.LayerView.FindLayer<ImGuiLayer>();
            if (imguiLayer is null)
            {
                throw new InvalidOperationException("Failed to find ImGui layer!");
            }

            nint id = imguiLayer.Controller.GetTextureID(mViewportTexture!);
            ImGui.Image(id, imageSize);
        }

        [ImGuiMenu("Dockspace/Scene")]
        private void SceneMenu(IEnumerable<ImGuiMenu> children)
        {
            if (mScene is null)
            {
                ImGui.Text("No scene is associated with the scene layer");
                return;
            }

            var io = ImGui.GetIO();
            ImGui.Text($"FPS: {io.Framerate:0.###}");

            if (ImGui.Button(mScene.UpdatePhysics ? "Pause" : "Resume"))
            {
                mScene.UpdatePhysics = !mScene.UpdatePhysics;
            }

            ImGui.SameLine();
            ImGui.Text("Physics simulation");

            ImGui.DragFloat3("Gravity", ref mScene.Gravity, 0.1f);
            ImGui.DragFloat("Linear velocity damping", ref mScene.VelocityDamping.Linear, 0.01f);
            ImGui.DragFloat("Angular velocity damping", ref mScene.VelocityDamping.Angular, 0.01f);
            ImGui.Separator();

            var deletedEntities = new HashSet<ulong>();
            bool entityHovered = false;

            foreach (ulong id in mScene.Entities)
            {
                string tag = mScene.TryGetComponent(id, out TagComponent? component) ? component.Tag : $"<no tag:{id}>";

                ImGui.PushID($"entity-{id}");
                if (ImGui.Selectable(tag, mSelectedEntity == id))
                {
                    mSelectedEntity = id;
                }

                entityHovered |= ImGui.IsItemHovered();
                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("Delete entity"))
                    {
                        deletedEntities.Add(id);
                        mSelectedEntity = Scene.Null;
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            foreach (ulong id in deletedEntities)
            {
                mScene.DestroyEntity(id);
            }

            const string windowContextId = "window-context";
            if (ImGui.IsWindowHovered())
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    mSelectedEntity = Scene.Null;
                }

                // these bindings don't have right-click BeginPopupContextWindow
                if (ImGui.IsMouseDown(ImGuiMouseButton.Right) && !entityHovered)
                {
                    ImGui.OpenPopup(windowContextId);
                }
            }

            if (ImGui.BeginPopup(windowContextId))
            {
                if (ImGui.MenuItem("New entity"))
                {
                    mSelectedEntity = mScene.NewEntity();
                }

                ImGui.EndPopup();
            }
        }

        [ImGuiMenu("Dockspace/Editor")]
        private void Editor(IEnumerable<ImGuiMenu> children)
        {
            if (mSelectedEntity == Scene.Null || mScene is null)
            {
                ImGui.Text("No entity selected");
                return;
            }

            const string addComponentId = "add-component";
            if (ImGui.Button("Add component"))
            {
                ImGui.OpenPopup(addComponentId);
            }

            if (ImGui.BeginPopup(addComponentId))
            {
                foreach (var componentType in sComponentTypes)
                {
                    if (ImGui.MenuItem(componentType.DisplayName) && !mScene.HasComponent(mSelectedEntity, componentType.Type))
                    {
                        mScene.AddComponent(mSelectedEntity, componentType.Type);
                    }
                }

                ImGui.EndPopup();
            }

            var style = ImGui.GetStyle();
            var font = ImGui.GetFont();

            var removedComponents = new List<Type>();
            foreach (var component in mScene.ViewComponents(mSelectedEntity))
            {
                var type = component.GetType();
                var fullName = type.FullName ?? type.Name;
                ImGui.PushID(fullName.Replace('.', '-'));

                var attribute = type.GetCustomAttribute<RegisteredComponentAttribute>();
                var displayName = attribute?.DisplayName;

                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = type.Name;
                }

                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f));
                float lineHeight = font.FontSize + style.FramePadding.Y * 2f;

                var regionAvailable = ImGui.GetContentRegionAvail();
                float xOffset = regionAvailable.X - lineHeight / 2f;

                bool open = ImGui.TreeNodeEx(displayName, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed |
                                                          ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowItemOverlap |
                                                          ImGuiTreeNodeFlags.FramePadding);

                ImGui.PopStyleVar();
                ImGui.SameLine(xOffset);

                const string componentSettingsId = "component-settings";
                if (ImGui.Button("+", new Vector2(lineHeight)))
                {
                    ImGui.OpenPopup(componentSettingsId);
                }

                if (ImGui.BeginPopup(componentSettingsId))
                {
                    if (ImGui.MenuItem("Remove"))
                    {
                        removedComponents.Add(type);
                    }

                    ImGui.EndPopup();
                }

                if (open)
                {
                    if (!mScene.InvokeComponentEvent(component, mSelectedEntity, ComponentEventID.Edited))
                    {
                        ImGui.Text("This component is not editable");
                    }

                    ImGui.TreePop();
                }

                ImGui.PopID();
            }

            foreach (var type in removedComponents)
            {
                mScene.RemoveComponent(mSelectedEntity, type);
            }
        }

        [ImGuiMenu("Dockspace/Model registry")]
        private unsafe void ModelRegistry(IEnumerable<ImGuiMenu> children)
        {
            var registry = App.Instance.ModelRegistry;
            if (registry is null)
            {
                ImGui.Text("The model registry has not been initialized!");
                return;
            }

            string hint = Path.GetFileNameWithoutExtension(mModelPath) ?? string.Empty;
            ImGui.InputText("Model path", ref mModelPath, 256);
            ImGui.InputTextWithHint("Model name", hint, ref mModelName, 256);

            if (ImGui.Button("Load model"))
            {
                if (string.IsNullOrEmpty(mModelPath))
                {
                    mModelError = "No path provided!";
                }
                else
                {
                    try
                    {
                        string name = string.IsNullOrEmpty(mModelName) ? hint : mModelName;
                        if (LoadModel(mModelPath, name) < 0)
                        {
                            mModelError = "Failed to load model!";
                        }
                        else
                        {
                            mModelName = mModelPath = mModelError = string.Empty;
                        }
                    }
                    catch (Exception exc)
                    {
                        var type = exc.GetType();
                        mModelError = $"Exception thrown ({type.FullName ?? type.Name}): {exc.Message}";
                    }
                }
            }

            if (!string.IsNullOrEmpty(mModelError))
            {
                ImGui.TextColored(new Vector4(0.8f, 0f, 0f, 1f), mModelError);
            }

            ImGui.Separator();
            var models = registry.Models;
            foreach (var id in models.Keys)
            {
                ImGui.PushID($"model-{id}");

                var name = registry.GetFormattedName(id);
                ImGui.Selectable(name);

                if (ImGui.BeginDragDropSource())
                {
                    ImGui.Text(name);
                    ImGui.SetDragDropPayload(RenderedModelComponent.ModelDragDropID, (nint)(void*)&id, sizeof(int));
                    ImGui.EndDragDropSource();
                }

                ImGui.PopID();
            }
        }

        [ImGuiMenu("Dockspace", Fullscreen = true, NoPadding = true, Flags = ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoDocking)]
        private void Dockspace(IEnumerable<ImGuiMenu> children)
        {
            var io = ImGui.GetIO();
            if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.DockingEnable))
            {
                var id = ImGui.GetID("main-dockspace");
                ImGui.DockSpace(id, Vector2.Zero, ImGuiDockNodeFlags.None);
            }
            else
            {
                ImGui.Text("Docking has been disabled");
            }

            if (ImGui.BeginMenuBar())
            {
                if (ImGui.BeginMenu("View"))
                {
                    foreach (var child in children)
                    {
                        ImGui.MenuItem(child.Title, string.Empty, ref child.Visible);
                    }

                    ImGui.EndMenu();
                }

                ImGui.EndMenuBar();
            }
        }

        #endregion

        public Scene Scene => mScene!;

        private ulong mSelectedEntity;
        private Scene? mScene;

        private readonly IReadOnlyList<ImGuiMenu> mMenus;
        private string mModelPath, mModelName, mModelError;
        private readonly Dictionary<int, LoadedModelInfo> mLoadedModelInfo;

        private IFramebuffer? mFramebuffer;
        private IRenderTarget? mRenderTarget;
        private FramebufferAttachmentInfo[] mFramebufferAttachments;
        private ITexture? mViewportTexture;

        private IDisposable? mFramebufferSemaphore;
        private bool mFramebufferRecreated;

        private IDeviceBuffer? mCameraBuffer;
        private IReflectionView? mReflectionView;
    }
}