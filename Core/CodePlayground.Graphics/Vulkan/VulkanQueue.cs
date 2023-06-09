﻿using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CodePlayground.Graphics.Vulkan
{
    internal struct VulkanSemaphoreInfo
    {
        public VulkanSemaphore Semaphore { get; set; }
        public SemaphoreUsage Usage { get; set; }
    }

    public sealed class VulkanCommandBuffer : ICommandList, IDisposable
    {
        public VulkanCommandBuffer(CommandPool pool, Device device, CommandQueueFlags queueUsage)
        {
            mDisposed = false;
            mRecording = false;

            mPool = pool;
            mDevice = device;
            mQueueUsage = queueUsage;

            mStagingObjects = new List<IDisposable>();
            mSemaphores = new List<VulkanSemaphoreInfo>();

            var allocInfo = VulkanUtilities.Init<CommandBufferAllocateInfo>() with
            {
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            var api = VulkanContext.API;
            api.AllocateCommandBuffers(device, allocInfo, out mBuffer).Assert();
        }

        ~VulkanCommandBuffer()
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

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                Clean();
            }

            var api = VulkanContext.API;
            api.FreeCommandBuffers(mDevice, mPool, 1, mBuffer);
        }

        public void Begin()
        {
            var beginInfo = VulkanUtilities.Init<CommandBufferBeginInfo>();
            var api = VulkanContext.API;
            api.BeginCommandBuffer(mBuffer, beginInfo).Assert();
        }

        public void End()
        {
            var api = VulkanContext.API;
            api.EndCommandBuffer(mBuffer).Assert();
        }

        void ICommandList.AddSemaphore(IDisposable semaphore, SemaphoreUsage usage)
        {
            if (semaphore is not VulkanSemaphore)
            {
                throw new ArgumentException("Must pass a Vulkan semaphore!");
            }

            AddSemaphore((VulkanSemaphore)semaphore, usage);
        }

        public void AddSemaphore(VulkanSemaphore semaphore, SemaphoreUsage usage)
        {
            mSemaphores.Add(new VulkanSemaphoreInfo
            {
                Semaphore = semaphore,
                Usage = usage
            });
        }

        public void PushStagingObject(IDisposable stagingObject)
        {
            mStagingObjects.Add(stagingObject);
        }

        internal void Reset()
        {
            var api = VulkanContext.API;
            api.ResetCommandBuffer(mBuffer, CommandBufferResetFlags.None);

            mSemaphores.Clear();
            Clean();
        }

        private void Clean()
        {
            if (!mStagingObjects.Any())
            {
                return;
            }

            foreach (var stagingObject in mStagingObjects)
            {
                stagingObject.Dispose();
            }

            mStagingObjects.Clear();
        }

        public bool IsRecording => mRecording;
        public CommandQueueFlags QueueUsage => mQueueUsage;
        public CommandBuffer Buffer => mBuffer;

        private bool mDisposed, mRecording;
        private readonly Device mDevice;
        private readonly CommandPool mPool;
        private readonly CommandBuffer mBuffer;
        private readonly CommandQueueFlags mQueueUsage;

        private readonly List<IDisposable> mStagingObjects;
        internal readonly List<VulkanSemaphoreInfo> mSemaphores;
    }

    internal struct StoredCommandBuffer
    {
        public VulkanCommandBuffer CommandBuffer { get; set; }
        public Fence Fence { get; set; }
        public bool OwnsFence { get; set; }
    }

    public struct VulkanQueueSemaphoreDependency
    {
        public Semaphore Semaphore { get; set; }
        public PipelineStageFlags DestinationStageMask { get; set; }
    }

    public struct VulkanQueueSubmitInfo
    {
        public Fence? Fence { get; set; }
        public IReadOnlyList<VulkanQueueSemaphoreDependency>? WaitSemaphores { get; set; }
        public IReadOnlyList<Semaphore>? SignalSemaphores { get; set; }
    }

    public sealed class VulkanQueue : ICommandQueue, IDisposable
    {
        internal unsafe VulkanQueue(int queueFamily, CommandQueueFlags usage, VulkanDevice device)
        {
            var api = VulkanContext.API;
            mDevice = device.Device;
            mQueue = api.GetDeviceQueue(mDevice, (uint)queueFamily, 0);

            mStoredBuffers = new Queue<StoredCommandBuffer>();
            mFences = new Queue<Fence>();

            mUsage = usage;
            mBufferCap = -1;
            mDisposed = false;

            var createInfo = VulkanUtilities.Init<CommandPoolCreateInfo>() with
            {
                Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = (uint)queueFamily
            };

            fixed (CommandPool* ptr = &mPool)
            {
                api.CreateCommandPool(mDevice, &createInfo, null, ptr).Assert();
            }
        }

        ~VulkanQueue()
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
            ClearCache();

            var api = VulkanContext.API;
            while (mFences.Count > 0)
            {
                var fence = mFences.Dequeue();
                api.DestroyFence(mDevice, fence, null);
            }

            api.DestroyCommandPool(mDevice, mPool, null);
        }

        ICommandList ICommandQueue.Release() => Release();
        public VulkanCommandBuffer Release()
        {
            if (mStoredBuffers.Count > 0)
            {
                var buffer = mStoredBuffers.Peek();
                var fence = buffer.Fence;
                var commandBuffer = buffer.CommandBuffer;

                var api = VulkanContext.API;
                if (api.GetFenceStatus(mDevice, fence) == Result.Success)
                {
                    if (buffer.OwnsFence)
                    {
                        api.ResetFences(mDevice, 1, fence);
                        mFences.Enqueue(fence);
                    }

                    commandBuffer.Reset();
                    mStoredBuffers.Dequeue();

                    return commandBuffer;
                }
                else if (mBufferCap >= 0 && mStoredBuffers.Count > mBufferCap)
                {
                    api.WaitForFences(mDevice, 1, fence, true, ulong.MaxValue).Assert();
                }
            }

            return new VulkanCommandBuffer(mPool, mDevice, mUsage);
        }

        void ICommandQueue.Submit(ICommandList commandList, bool wait)
        {
            if (commandList is not VulkanCommandBuffer)
            {
                throw new ArgumentException("Must pass a vulkan command buffer!");
            }

            var commandBuffer = (VulkanCommandBuffer)commandList;
            Submit(commandBuffer, wait: wait);
        }

        private unsafe Fence GetFence()
        {
            Fence fence;
            if (mFences.Count > 0)
            {
                fence = mFences.Dequeue();
            }
            else
            {
                var fenceInfo = VulkanUtilities.Init<FenceCreateInfo>() with
                {
                    Flags = FenceCreateFlags.None
                };

                var api = VulkanContext.API;
                api.CreateFence(mDevice, &fenceInfo, null, &fence).Assert();
            }

            return fence;
        }

        public unsafe void Submit(VulkanCommandBuffer commandBuffer, VulkanQueueSubmitInfo info = default, bool wait = false)
        {
            if (commandBuffer.IsRecording)
            {
                commandBuffer.End();
            }

            var semaphores = commandBuffer.mSemaphores;
            var waitSemaphores = semaphores.Where(info => info.Usage == SemaphoreUsage.Wait).Select(info => info.Semaphore.Semaphore);
            var signalSemaphores = semaphores.Where(info => info.Usage == SemaphoreUsage.Signal).Select(info => info.Semaphore.Semaphore);

            var waitStageArray = new PipelineStageFlags[waitSemaphores.Count()];
            Array.Fill(waitStageArray, PipelineStageFlags.AllCommandsBit);

            if (info.WaitSemaphores is not null)
            {
                waitSemaphores = waitSemaphores.Concat(info.WaitSemaphores.Select(dependency => dependency.Semaphore));
                waitStageArray = waitStageArray.Concat(info.WaitSemaphores.Select(dependency => dependency.DestinationStageMask)).ToArray();
            }

            if (info.SignalSemaphores is not null)
            {
                signalSemaphores = signalSemaphores.Concat(info.SignalSemaphores);
            }

            var buffer = commandBuffer.Buffer;
            var fence = info.Fence ?? GetFence();

            var waitSemaphoreArray = waitSemaphores.ToArray();
            var signalSemaphoreArray = signalSemaphores.ToArray();

            var api = VulkanContext.API;
            fixed (Semaphore* waitSemaphorePtr = waitSemaphoreArray)
            {
                fixed (PipelineStageFlags* waitStagePtr = waitStageArray)
                {
                    fixed (Semaphore* signalSemaphorePtr = signalSemaphoreArray)
                    {
                        var submitInfo = VulkanUtilities.Init<SubmitInfo>() with
                        {
                            WaitSemaphoreCount = (uint)waitSemaphoreArray.Length,
                            PWaitSemaphores = waitSemaphorePtr,
                            PWaitDstStageMask = waitStagePtr,
                            CommandBufferCount = 1,
                            PCommandBuffers = &buffer,
                            SignalSemaphoreCount = (uint)signalSemaphoreArray.Length,
                            PSignalSemaphores = signalSemaphorePtr
                        };

                        api.QueueSubmit(mQueue, 1, submitInfo, fence).Assert();
                    }
                }
            }

            mStoredBuffers.Enqueue(new StoredCommandBuffer
            {
                CommandBuffer = commandBuffer,
                Fence = fence,
                OwnsFence = info.Fence is null
            });

            if (wait)
            {
                api.WaitForFences(mDevice, 1, fence, true, ulong.MaxValue).Assert();
            }
        }

        public void Wait()
        {
            var api = VulkanContext.API;
            api.QueueWaitIdle(mQueue).Assert();
        }

        public unsafe void ClearCache()
        {
            Wait();

            var api = VulkanContext.API;
            while (mStoredBuffers.Count > 0)
            {
                var storedBuffer = mStoredBuffers.Dequeue();
                storedBuffer.CommandBuffer.Dispose();

                if (storedBuffer.OwnsFence)
                {
                    api.DestroyFence(mDevice, storedBuffer.Fence, null);
                }
            }
        }

        public CommandQueueFlags Usage => mUsage;
        public Queue Queue => mQueue;
        public int CommandListCap
        {
            get => mBufferCap;
            set
            {
                mBufferCap = value;
                ClearCache();
            }
        }

        private readonly Queue mQueue;
        private readonly Device mDevice;
        private readonly CommandPool mPool;

        private readonly Queue<StoredCommandBuffer> mStoredBuffers;
        private readonly Queue<Fence> mFences;

        private readonly CommandQueueFlags mUsage;
        private int mBufferCap;
        private bool mDisposed;
    }
}
