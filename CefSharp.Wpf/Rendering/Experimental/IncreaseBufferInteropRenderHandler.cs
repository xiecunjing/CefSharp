// Copyright © 2018 The CefSharp Authors. All rights reserved.
//
// Use of this source code is governed by a BSD-style license that can be found in the LICENSE file.

using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rect = CefSharp.Structs.Rect;

namespace CefSharp.Wpf.Rendering.Experimental
{
    /// <summary>
    /// IncreaseBufferInteropRenderHandler - creates/updates an InteropBitmap
    /// Uses a MemoryMappedFile for double buffering, only ever creates a new buffer
    /// when the size increases
    /// </summary>
    /// <seealso cref="CefSharp.Wpf.IRenderHandler" />
    public class IncreaseBufferInteropRenderHandler : IRenderHandler
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);

        /// <summary>
        /// The pixel format
        /// </summary>
        private static readonly PixelFormat PixelFormat = PixelFormats.Pbgra32;
        private static int BytesPerPixel = PixelFormat.BitsPerPixel / 8;

        private object lockObject = new object();

        private Size viewSize;
        private Size popupSize;
        private DispatcherPriority dispatcherPriority;

        private MemoryMappedFile viewMemoryMappedFile;
        private MemoryMappedFile popupMemoryMappedFile;
        private MemoryMappedViewAccessor viewMemoryMappedViewAccessor;
        private MemoryMappedViewAccessor popupMemoryMappedViewAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="InteropBitmapRenderHandler"/> class.
        /// </summary>
        /// <param name="dispatcherPriority">priority at which the bitmap will be updated on the UI thread</param>
        public IncreaseBufferInteropRenderHandler(DispatcherPriority dispatcherPriority = DispatcherPriority.Render)
        {
            this.dispatcherPriority = dispatcherPriority;
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            ReleaseMemoryMappedView(ref popupMemoryMappedFile, ref popupMemoryMappedViewAccessor);
            ReleaseMemoryMappedView(ref viewMemoryMappedFile, ref viewMemoryMappedViewAccessor);
        }

        /// <summary>
        /// Called when an element has been rendered to the shared texture handle.
        /// This method is only called when <see cref="IWindowInfo.SharedTextureEnabled"/> is set to true
        /// </summary>
        /// <param name="isPopup">indicates whether the element is the view or the popup widget.</param>
        /// <param name="dirtyRect">contains the set of rectangles in pixel coordinates that need to be repainted</param>
        /// <param name="sharedHandle">is the handle for a D3D11 Texture2D that can be accessed via ID3D11Device using the OpenSharedResource method.</param>
        void IRenderHandler.OnAcceleratedPaint(bool isPopup, Rect dirtyRect, IntPtr sharedHandle)
        {
            //NOT USED
        }

        void IRenderHandler.OnPaint(bool isPopup, Rect dirtyRect, IntPtr buffer, int width, int height, Image image)
        {
            if (isPopup)
            {
                CreateOrUpdateBitmap(isPopup, dirtyRect, buffer, width, height, image, ref popupSize, ref popupMemoryMappedFile, ref popupMemoryMappedViewAccessor);
            }
            else
            {
                CreateOrUpdateBitmap(isPopup, dirtyRect, buffer, width, height, image, ref viewSize, ref viewMemoryMappedFile, ref viewMemoryMappedViewAccessor);
            }
        }

        private void CreateOrUpdateBitmap(bool isPopup, Rect dirtyRect, IntPtr buffer, int width, int height, Image image, ref Size currentSize, ref MemoryMappedFile mappedFile, ref MemoryMappedViewAccessor viewAccessor)
        {
            if (image.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            var createNewBitmap = false;

            lock (lockObject)
            {
                int pixels = width * height;
                int numberOfBytes = pixels * BytesPerPixel;

                createNewBitmap = mappedFile == null || currentSize.Height != height || currentSize.Width != width;

                if (createNewBitmap)
                {
                    //If the MemoryMappedFile is smaller than we need then create a larger one
                    //If it's larger then we need then rather than going through the costly expense of
                    //allocating a new one we'll just use the old one and only access the number of bytes we require.
                    if (viewAccessor == null || viewAccessor.Capacity < numberOfBytes)
                    {
                        ReleaseMemoryMappedView(ref mappedFile, ref viewAccessor);

                        mappedFile = MemoryMappedFile.CreateNew(null, numberOfBytes, MemoryMappedFileAccess.ReadWrite);

                        viewAccessor = mappedFile.CreateViewAccessor();
                    }

                    currentSize.Height = height;
                    currentSize.Width = width;
                }

                //TODO: Performance analysis to determine which is the fastest memory copy function
                //NativeMethodWrapper.CopyMemoryUsingHandle(viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle(), buffer, numberOfBytes);
                CopyMemory(viewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle(), buffer, (uint)numberOfBytes);

                //Take a reference to the backBufferHandle, once we're on the UI thread we need to check if it's still valid
                var backBufferHandle = mappedFile.SafeMemoryMappedFileHandle;

                image.Dispatcher.BeginInvoke((Action)(() =>
                {
                    lock (lockObject)
                    {
                        if (backBufferHandle.IsClosed || backBufferHandle.IsInvalid)
                        {
                            return;
                        }

                        if (createNewBitmap)
                        {
                            if (image.Source != null)
                            {
                                image.Source = null;
                                GC.Collect(1);
                            }

                            var stride = width * BytesPerPixel;
                            var bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(backBufferHandle.DangerousGetHandle(), width, height, PixelFormat, stride, 0);
                            image.Source = bitmap;
                        }
                        else
                        {
                            if (image.Source != null)
                            {
                                var sourceRect = new Int32Rect(dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height);
                                var bitmap = (InteropBitmap)image.Source;
                                bitmap.Invalidate(sourceRect);
                            }
                        }
                    }
                }), dispatcherPriority);
            }
        }

        private void ReleaseMemoryMappedView(ref MemoryMappedFile mappedFile, ref MemoryMappedViewAccessor stream)
        {
            if (stream != null)
            {
                stream.Dispose();
                stream = null;
            }

            if (mappedFile != null)
            {
                mappedFile.Dispose();
                mappedFile = null;
            }
        }
    }
}
