using OpenTK.Graphics.OpenGL4;
using OpenTK.Wpf;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace ComicViewer;

public sealed class MpvOpenGlVideoView : GLWpfControl
{
    private const int MpvRenderUpdateFrame = 1;

    private static readonly MpvOpenGlGetProcAddressDelegate GetProcAddressCallback = GetOpenGlProcAddress;
    private static readonly IntPtr GetProcAddressCallbackPtr = Marshal.GetFunctionPointerForDelegate(GetProcAddressCallback);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MpvRenderUpdateDelegate _updateCallback;
    private readonly IntPtr _framebufferPtr;
    private readonly IntPtr _flipYPtr;
    private readonly MpvRenderParam[] _renderParams;
    private bool _isStarted;
    private bool _isDisposed;
    private bool _isRendering;
    private bool _forceRender = true;
    private int _renderRequestPending;
    private int _lastFramebuffer;
    private int _lastWidth;
    private int _lastHeight;
    private IntPtr _renderContext;

    public MpvOpenGlVideoView()
    {
        _updateCallback = OnMpvRenderUpdate;
        _framebufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlFbo>());
        _flipYPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(_flipYPtr, 1);
        _renderParams =
        [
            new MpvRenderParam(MpvRenderParamType.OpenGlFbo, _framebufferPtr),
            new MpvRenderParam(MpvRenderParamType.FlipY, _flipYPtr),
            new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero)
        ];

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Ready += OnReady;
        Render += OnRender;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Cursor = System.Windows.Input.Cursors.Arrow;
        Focusable = false;
    }

    public event Action? FrameRendered;

    public Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        StartControl();
        return _ready.Task.WaitAsync(timeout, cancellationToken);
    }

    public int InitializeRenderer(IntPtr mpv)
    {
        if (Dispatcher.CheckAccess())
        {
            return InitializeRendererCore(mpv);
        }

        return Dispatcher.Invoke(() => InitializeRendererCore(mpv));
    }

    public void DisposeRenderer()
    {
        if (Dispatcher.CheckAccess())
        {
            DisposeRendererCore();
        }
        else
        {
            Dispatcher.Invoke(DisposeRendererCore);
        }
    }

    public void RequestRender()
    {
        Interlocked.Exchange(ref _renderRequestPending, 1);
    }

    public void ResetFrameState()
    {
        if (Dispatcher.CheckAccess())
        {
            ResetFrameStateCore();
        }
        else
        {
            Dispatcher.Invoke(ResetFrameStateCore);
        }
    }

    public new void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CompositionTarget.Rendering -= OnCompositionRendering;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        Render -= OnRender;
        Ready -= OnReady;

        if (Dispatcher.CheckAccess())
        {
            DisposeRendererCore();
        }
        else
        {
            Dispatcher.Invoke(DisposeRendererCore);
        }

        Marshal.FreeHGlobal(_framebufferPtr);
        Marshal.FreeHGlobal(_flipYPtr);
        base.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
        CompositionTarget.Rendering += OnCompositionRendering;
        StartControl();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_isDisposed || Interlocked.CompareExchange(ref _renderRequestPending, 1, 1) == 0)
        {
            return;
        }

        InvalidateVisual();
    }

    private void StartControl()
    {
        if (_isStarted || _isDisposed)
        {
            return;
        }

        _isStarted = true;
        Start(new GLWpfControlSettings
        {
            MajorVersion = 3,
            MinorVersion = 3,
            RenderContinuously = false,
            UseDeviceDpi = true,
            TransparentBackground = true
        });
    }

    private void OnReady()
    {
        if (!TryMakeCurrent())
        {
            return;
        }

        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _ready.TrySetResult();
    }

    private int InitializeRendererCore(IntPtr mpv)
    {
        if (_renderContext != IntPtr.Zero)
        {
            return 0;
        }

        _forceRender = true;
        StartControl();
        if (!_ready.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("OpenGL 控件尚未准备好。");
        }

        if (!TryMakeCurrent())
        {
            throw new InvalidOperationException("OpenGL context 尚未准备好。");
        }

        var apiTypePtr = Marshal.StringToHGlobalAnsi("opengl");
        var initParams = new MpvOpenGlInitParams(GetProcAddressCallbackPtr, IntPtr.Zero);
        var initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlInitParams>());
        try
        {
            Marshal.StructureToPtr(initParams, initParamsPtr, false);
            var createParams = new[]
            {
                new MpvRenderParam(MpvRenderParamType.ApiType, apiTypePtr),
                new MpvRenderParam(MpvRenderParamType.OpenGlInitParams, initParamsPtr),
                new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero)
            };

            var result = MpvRenderNative.RenderContextCreate(out _renderContext, mpv, createParams);
            if (result < 0)
            {
                _renderContext = IntPtr.Zero;
                return result;
            }

            MpvRenderNative.RenderContextSetUpdateCallback(_renderContext, _updateCallback, IntPtr.Zero);
            RequestRender();
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(initParamsPtr);
            Marshal.FreeHGlobal(apiTypePtr);
        }
    }

    private void OnMpvRenderUpdate(IntPtr callbackContext)
    {
        RequestRender();
    }

    private void ResetFrameStateCore()
    {
        Interlocked.Exchange(ref _renderRequestPending, 1);
    }

    private void OnRender(TimeSpan delta)
    {
        if (_isDisposed || _isRendering)
        {
            return;
        }

        var requested = Interlocked.Exchange(ref _renderRequestPending, 0) != 0;
        _isRendering = true;
        try
        {
            if (!TryMakeCurrent())
            {
                return;
            }

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
            GL.Viewport(0, 0, Math.Max(1, FrameBufferWidth), Math.Max(1, FrameBufferHeight));

            if (_renderContext == IntPtr.Zero)
            {
                GL.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            var updateFlags = MpvRenderNative.RenderContextUpdate(_renderContext);
            var hasFrameUpdate = (updateFlags & MpvRenderUpdateFrame) != 0;
            var framebufferChanged = Framebuffer != _lastFramebuffer
                || FrameBufferWidth != _lastWidth
                || FrameBufferHeight != _lastHeight;
            if (!hasFrameUpdate && !requested && !_forceRender && !framebufferChanged)
            {
                return;
            }

            _lastFramebuffer = Framebuffer;
            _lastWidth = FrameBufferWidth;
            _lastHeight = FrameBufferHeight;

            var fbo = new MpvOpenGlFbo(
                Framebuffer,
                Math.Max(1, FrameBufferWidth),
                Math.Max(1, FrameBufferHeight),
                0);
            Marshal.StructureToPtr(fbo, _framebufferPtr, false);

            MpvRenderNative.RenderContextRender(_renderContext, _renderParams);
            MpvRenderNative.RenderContextReportSwap(_renderContext);
            _forceRender = false;

            if (hasFrameUpdate)
            {
                FrameRendered?.Invoke();
            }
        }
        finally
        {
            _isRendering = false;
        }
    }

    private void DisposeRendererCore()
    {
        if (_renderContext != IntPtr.Zero)
        {
            if (!TryMakeCurrent())
            {
                return;
            }

            MpvRenderNative.RenderContextSetUpdateCallback(_renderContext, null, IntPtr.Zero);
            MpvRenderNative.RenderContextFree(_renderContext);
            _renderContext = IntPtr.Zero;
            _forceRender = true;
        }
    }

    private bool TryMakeCurrent()
    {
        if (Context is null)
        {
            return false;
        }

        Context.MakeCurrent();
        return true;
    }

    private static IntPtr GetOpenGlProcAddress(IntPtr context, IntPtr name)
    {
        var procName = Marshal.PtrToStringAnsi(name);
        if (string.IsNullOrWhiteSpace(procName))
        {
            return IntPtr.Zero;
        }

        var address = WglGetProcAddress(procName);
        if (address != IntPtr.Zero && address.ToInt64() > 3)
        {
            return address;
        }

        var module = GetModuleHandle("opengl32.dll");
        return module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, procName);
    }

    [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr WglGetProcAddress(string name);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}
