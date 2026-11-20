using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using AlphaMode = Vortice.DXGI.AlphaMode;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace PCVitals;

/// <summary>
/// A window's GPU drawing surface: a Direct3D 11 device, a premultiplied-alpha swap chain shown through
/// DirectComposition (so the window can be see-through without a CPU-side layered bitmap), and a Direct2D
/// context that draws into it. Used from one thread only: the panel's render thread.
/// </summary>
sealed class GpuSurface : IDisposable
{
    readonly IntPtr _hwnd;
    ID3D11Device? _d3d;
    IDXGIDevice? _dxgi;
    IDXGISwapChain1? _swap;
    ID2D1Device? _d2dDevice;
    IDCompositionDevice? _dcomp;
    IDCompositionTarget? _target;
    IDCompositionVisual? _visual;
    ID2D1Bitmap1? _backBuffer;

    public static readonly ID2D1Factory1 Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded);
    public static readonly Vortice.DirectWrite.IDWriteFactory Text = Vortice.DirectWrite.DWrite.DWriteCreateFactory<Vortice.DirectWrite.IDWriteFactory>();

    public ID2D1DeviceContext Context { get; private set; } = null!;
    /// <summary>The same context, for pre-tessellated geometry (Windows 8.1+).</summary>
    public ID2D1DeviceContext1 Context1 { get; private set; } = null!;
    public Size Size { get; private set; }

    public GpuSurface(IntPtr hwnd, Size size)
    {
        _hwnd = hwnd;
        Create(size);
    }

    static readonly FeatureLevel[] Levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };

    /// <summary>Whether this PC can draw panels on the GPU (a Direct3D device with Direct2D support).</summary>
    public static bool Probe()
    {
        try
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, Levels, out ID3D11Device? device).CheckError();
            device?.Dispose();
            return device != null;
        }
        catch (Exception ex)
        {
            Program.Log("GPU panel renderer unavailable: " + ex.Message);
            return false;
        }
    }

    void Create(Size size)
    {
        Size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        // The default adapter: on a desktop with an iGPU and a graphics card, the integrated one measured worse on
        // both processor time and memory (its frames are copied across to the card driving the displays).
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, Levels, out _d3d).CheckError();
        _dxgi = _d3d!.QueryInterface<IDXGIDevice>();
        using (var adapter = _dxgi.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)Size.Width, Height = (uint)Size.Height, Format = Format.B8G8R8A8_UNorm,
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied, Scaling = Scaling.Stretch,
            };
            _swap = factory.CreateSwapChainForComposition(_dxgi, desc);
        }
        _d2dDevice = Factory.CreateDevice(_dxgi);
        Context = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        Context1 = Context.QueryInterface<ID2D1DeviceContext1>();
        Context.TextAntialiasMode = TextAntialiasMode.Grayscale; // ClearType needs an opaque background
        Context.AntialiasMode = AntialiasMode.PerPrimitive;
        BindBackBuffer();

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgi);
        _dcomp.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
        _dcomp.CreateVisual(out _visual).CheckError();
        _visual!.SetContent(_swap).CheckError();
        _target!.SetRoot(_visual).CheckError();
        _dcomp.Commit().CheckError();
    }

    void BindBackBuffer()
    {
        using var surface = _swap!.GetBuffer<IDXGISurface>(0);
        _backBuffer = Context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        Context.Target = _backBuffer;
    }

    public void Resize(Size size)
    {
        size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (size == Size) return;
        Context.Target = null;
        _backBuffer?.Dispose();
        _swap!.ResizeBuffers(0, (uint)size.Width, (uint)size.Height, Format.Unknown, SwapChainFlags.None).CheckError();
        Size = size;
        BindBackBuffer();
    }

    /// <summary>Shows what was drawn. False when the GPU was lost (driver update, sleep): make a new surface.</summary>
    public bool Present()
    {
        var hr = _swap!.Present(0, PresentFlags.None);
        return hr.Success || hr.Code == (int)0x087A0001; // DXGI_STATUS_OCCLUDED
    }

    /// <summary>A GDI+ bitmap (premultiplied 32-bit) as a Direct2D bitmap on this device.</summary>
    public ID2D1Bitmap1 Upload(Bitmap bmp)
    {
        var rect = new Rectangle(Point.Empty, bmp.Size);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            return Context.CreateBitmap(new Vortice.Mathematics.SizeI(bmp.Width, bmp.Height), data.Scan0, (uint)data.Stride,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        if (Context != null) Context.Target = null;
        _backBuffer?.Dispose();
        Context1?.Dispose();
        Context?.Dispose();
        _d2dDevice?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _swap?.Dispose();
        _dxgi?.Dispose();
        _d3d?.Dispose();
    }
}
