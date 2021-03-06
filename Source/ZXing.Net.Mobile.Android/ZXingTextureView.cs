using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
using Android.Opengl;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;

using ApxLabs.FastAndroidCamera;

using Javax.Microedition.Khronos.Egl;

using ZXing.Net.Mobile.Android;

using Camera = Android.Hardware.Camera;
using Matrix = Android.Graphics.Matrix;

namespace ZXing.Mobile
{
	public static class IntEx
	{
		public static bool Between(this int i, int lower, int upper)
		{
			return lower <= i && i <= upper;
		}
	}

	public static class HandlerEx
	{
		public static void PostSafe(this Handler self, Action action)
		{
			self.Post(() =>
			{
				try
				{
					action();
				}
				catch (Exception ex)
				{
					// certain death, unless we squash
					Log.Debug(MobileBarcodeScanner.TAG, $"Squashing: {ex} to avoid certain death! Handler is: {self.GetHashCode()}");
				}
			});
		}

		public static void PostSafe(this Handler self, Func<Task> action)
		{
			self.Post(async () =>
			{
				try
				{
					await action();
				}
				catch (Exception ex)
				{
					// certain death, unless we squash
					Log.Debug(MobileBarcodeScanner.TAG, $"Squashing: {ex} to avoid certain death! Handler is: {self.GetHashCode()}");
				}
			});
		}

	}

	public static class RectFEx {
		public static void Flip(this RectF s) {
			var tmp = s.Left;
			s.Left = s.Top;
			s.Top = tmp;
			tmp = s.Right;
			s.Right = s.Bottom;
			s.Bottom = tmp;
		}
	}

	class MyOrientationEventListener : OrientationEventListener
	{
		public MyOrientationEventListener(Context context, SensorDelay delay) : base(context, delay) { }

		public event Action<int> OrientationChanged;

		public override void OnOrientationChanged(int orientation)
		{
			OrientationChanged?.Invoke(orientation);
		}
	}

	public class RingBuffer<T>
	{
		readonly T[] _buffer;
		int _tail;
		int _length;

		public RingBuffer(int capacity)
		{
			_buffer = new T[capacity];
		}

		public void Add(T item)
		{
			_buffer[_tail] = item; // will overwrite existing entry, if any
			_tail = (_tail + 1) % _buffer.Length; // roll over
			_length++;
		}

		public T this[int index]
		{
			get { return _buffer[WrapIndex(index)]; }
			set { _buffer[WrapIndex(index)] = value; }
		}

		public int Length
		{
			get { return _length; }
		}

		public int FindIndex(ref T toFind, IComparer<T> comparer = null)
		{
			comparer = comparer ?? Comparer<T>.Default;
			int idx = -1;
			for (int i = 0; i < Length; ++i)
			{
				var candidate = this[i];
				if (comparer.Compare(candidate, toFind) == 0)
				{
					idx = i;
					toFind = candidate;
					break; // item found in history ring
				}
			}
			return idx;
		}

		public void AddOrUpdate(ref T item, IComparer<T> comparer = null) 
		{
			var idx = FindIndex(ref item);
			if (idx < 0)
				Add(item);
			else
				this[idx] = item;
		}

		int Head
		{
			get { return (_tail - _length) % _buffer.Length; }
		}

		int WrapIndex(int index)
		{
			if (index < 0 || index >= _length)
				throw new IndexOutOfRangeException($"{nameof(index)} = {index}");

			return (Head + index) % _buffer.Length;
		}
	}

	public class ZXingTextureView : TextureView, IScannerView, Camera.IAutoFocusCallback, INonMarshalingPreviewCallback
	{
		Camera.CameraInfo _cameraInfo;
		Camera _camera;

		static ZXingTextureView() {
		}

		public ZXingTextureView(IntPtr javaRef, JniHandleOwnership transfer) : base(javaRef, transfer)
		{
			Init();
		}

		public ZXingTextureView(Context ctx) : base(ctx)
		{
			Init();
		}

		public ZXingTextureView(Context ctx, IAttributeSet attr) : base(ctx, attr)
		{
			Init();
		}

		public ZXingTextureView(Context ctx, IAttributeSet attr, int defStyle) : base(ctx, attr, defStyle)
		{
			Init();
		}

		Toast _toast;
		Handler _handler;
		MyOrientationEventListener _orientationEventListener;
		TaskCompletionSource<SurfaceTextureAvailableEventArgs> _surfaceAvailable = new TaskCompletionSource<SurfaceTextureAvailableEventArgs>();
		SurfaceTexture _surfaceTexture;
		void Init()
		{
			_toast = Toast.MakeText(Context, string.Empty, ToastLength.Short);

			var handlerThread = new HandlerThread("ZXingTextureView");
			handlerThread.Start();
			_handler = new Handler(handlerThread.Looper);

			// We have to handle changes to screen orientation explicitly, as we cannot rely on OnConfigurationChanges
			_orientationEventListener = new MyOrientationEventListener(Context, SensorDelay.Normal);
			_orientationEventListener.OrientationChanged += OnOrientationChanged;
			if (_orientationEventListener.CanDetectOrientation())
				_orientationEventListener.Enable();

			SurfaceTextureAvailable += (sender, e) =>
				_surfaceAvailable.SetResult(e);

			SurfaceTextureSizeChanged += (sender, e) =>
				SetSurfaceTransform(e.Surface, e.Width, e.Height);

			SurfaceTextureDestroyed += (sender, e) =>
			{
				ShutdownCamera();
				_surfaceAvailable = new TaskCompletionSource<SurfaceTextureAvailableEventArgs>();
				_surfaceTexture = null;
			};
		}

		Camera.Size PreviewSize { get; set; }

		int _lastOrientation;
		SurfaceOrientation _lastSurfaceOrientation;
		void OnOrientationChanged(int orientation)
		{
			//
			// This code should only run when UI snaps into either portrait or landscape mode.
			// At first glance we could just override OnConfigurationChanged, but unfortunately 
			// a rotation from landscape directly to reverse landscape won't fire an event 
			// (which is easily done by rotating via upside-down on many devices), because Android
			// can just reuse the existing config and handle the rotation automatically ..
			// 
			// .. except of course for camera orientation, which must handled explicitly *sigh*. 
			// Hurray Google, you sure suck at API design!
			//
			// Instead we waste some CPU by tracking orientation down to the last degree, every 200ms.
			// I have yet to come up with a better way. 
			//
			if (_camera == null)
				return;

			var o = (((orientation + 45) % 360) / 90) * 90; // snap to 0, 90, 180, or 270.
			if (o == _lastOrientation)
				return; // fast path, no change ..

			// Actual snap is delayed, so check if we are actually rotated
			var rotation = WindowManager.DefaultDisplay.Rotation;
			if (rotation == _lastSurfaceOrientation)
				return;  // .. still no change

			_lastOrientation = o;
			_lastSurfaceOrientation = rotation;

			_handler.PostSafe(() =>
			{
				_camera?.SetDisplayOrientation(CameraOrientation(WindowManager.DefaultDisplay.Rotation)); // and finally, the interesting part *sigh*
			});
		}

		bool IsPortrait { 
			get { 			
				var rotation = WindowManager.DefaultDisplay.Rotation;
				return rotation == SurfaceOrientation.Rotation0 || rotation == SurfaceOrientation.Rotation180;
			} 
		}

		Rectangle _area;
		void SetSurfaceTransform(SurfaceTexture st, int width, int height)
		{
			var p = PreviewSize;
			if (p == null)
				return; // camera no ready yet, we will be called again later from SetupCamera.

			using (var metrics = new DisplayMetrics())
			{
				#region transform
				// Compensate for non-square pixels
				WindowManager.DefaultDisplay.GetMetrics(metrics);
				var aspectRatio = metrics.Xdpi / metrics.Ydpi; // close to 1, but rarely perfect 1

				// Compensate for preview streams aspect ratio
				aspectRatio *= (float)p.Height / p.Width;

				// Compensate for portrait mode
				if (IsPortrait)
					aspectRatio = 1f / aspectRatio;

				// OpenGL coordinate system goes form 0 to 1
				var transform = new Matrix();
				transform.SetScale(1f, aspectRatio * width / height); // lock on to width

				Post(() => {
					try
					{
						SetTransform(transform);
					}
					catch (ObjectDisposedException) { } // todo: What to do here?! For now we squash :-/
				}); // ensure we use the right thread when updating transform

				Log.Debug(MobileBarcodeScanner.TAG, $"Aspect ratio: {aspectRatio}, Transform: {transform}");

				#endregion

				#region area
				using (var max = new RectF(0, 0, p.Width, p.Height))
				using (var r = new RectF(max))
				{
					// Calculate area of interest within preview
					var inverse = new Matrix();
					transform.Invert(inverse);

					Log.Debug(MobileBarcodeScanner.TAG, $"Inverse: {inverse}");

					var flip = IsPortrait;
					if (flip) r.Flip();
					inverse.MapRect(r);
					if (flip) r.Flip();

					r.Intersect(max); // stream doesn't always fill the view!

					// Compensate for reverse mounted camera, like on the Nexus 5X.
					var reverse = _cameraInfo.Orientation == 270;
					if (reverse)
					{
						if (flip)
							r.OffsetTo(p.Width - r.Right, 0); // shift area right
						else
							r.Offset(0, p.Height - r.Bottom); // shift are down
					}

					_area = new Rectangle((int)r.Left, (int)r.Top, (int)r.Width(), (int)r.Height());

					Log.Debug(MobileBarcodeScanner.TAG, $"Area: {_area}");
				}
				#endregion
			}
		}

		IWindowManager _wm;
		IWindowManager WindowManager
		{
			get
			{
				_wm = _wm ?? Context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
				return _wm;
			}
		}

		bool? _hasTorch;
		public bool HasTorch
		{
			get
			{
				if (_hasTorch.HasValue)
					return _hasTorch.Value;

				var p = _camera.GetParameters();
				var supportedFlashModes = p.SupportedFlashModes;

				if (supportedFlashModes != null
					&& (supportedFlashModes.Contains(Camera.Parameters.FlashModeTorch)
					|| supportedFlashModes.Contains(Camera.Parameters.FlashModeOn)))
					_hasTorch = PermissionsHandler.CheckTorchPermissions(Context, false);

				return _hasTorch.HasValue && _hasTorch.Value;
			}
		}

		bool _isAnalyzing;
		public bool IsAnalyzing
		{
			get { return _isAnalyzing; }
		}

		bool _isTorchOn;
		public bool IsTorchOn
		{
			get { return _isTorchOn; }
		}

		MobileBarcodeScanningOptions _scanningOptions;
		IBarcodeReaderGeneric<FastJavaByteArray> _barcodeReader;
		public MobileBarcodeScanningOptions ScanningOptions
		{
			get { return _scanningOptions; }
			set
			{
				_scanningOptions = value;
				_delay = TimeSpan.FromMilliseconds(value.DelayBetweenContinuousScans).Ticks;
				_barcodeReader = CreateBarcodeReader(value);
			}
		}

		bool _useContinuousFocus;
		bool _autoFocusRunning;
		public void AutoFocus()
		{
			_handler.PostSafe(() =>
			{
				var camera = _camera;
				if (camera == null || _autoFocusRunning || _useContinuousFocus)
					return; // Allow camera to complete autofocus cycle, before trying again!

				_autoFocusRunning = true;
				camera.AutoFocus(this);
			});
		}

		public void AutoFocus(int x, int y)
		{
			// todo: Needs some slightly serious math to map back to camera coordinates. 
			// The method used in ZXingSurfaceView is simply wrong.
			AutoFocus();
		}

		public void OnAutoFocus(bool focus, Camera camera)
		{
			_autoFocusRunning = false;

		    if (!(focus || _useContinuousFocus))
		    {
		        _handler.PostDelayed(AutoFocus, 1000);
		    }
		}


		public void PauseAnalysis()
		{
			_isAnalyzing = false;
		}

		public void ResumeAnalysis()
		{
			_isAnalyzing = true;
		}

		Action<Result> _callback;
		public void StartScanning(Action<Result> scanResultCallback, MobileBarcodeScanningOptions options = null)
		{
			_callback = scanResultCallback;
			ScanningOptions = options ?? MobileBarcodeScanningOptions.Default;

			_handler.PostSafe(SetupCamera);

			ResumeAnalysis();
		}

		void OpenCamera()
		{
			if (_camera != null)
				return;

			PermissionsHandler.CheckCameraPermissions(Context);

			if (Build.VERSION.SdkInt >= BuildVersionCodes.Gingerbread) // Choose among multiple cameras from Gingerbread forward
			{
				int max = Camera.NumberOfCameras;
				Log.Debug(MobileBarcodeScanner.TAG, $"Found {max} cameras");
				var requestedFacing = CameraFacing.Back; // default to back facing camera, ..
				if (ScanningOptions.UseFrontCameraIfAvailable.HasValue && ScanningOptions.UseFrontCameraIfAvailable.Value)
					requestedFacing = CameraFacing.Front; // .. but use front facing if available and requested

				var info = new Camera.CameraInfo();
				int idx = 0;
				do
				{
					Camera.GetCameraInfo(idx++, info); // once again Android sucks!
				}
				while (info.Facing != requestedFacing && idx < max);
				--idx;

				Log.Debug(MobileBarcodeScanner.TAG, $"Opening {info.Facing} facing camera: {idx}...");
				_cameraInfo = info;
				_camera = Camera.Open(idx);
			}
			else {
				_camera = Camera.Open();
			}

			_camera.Lock();
		}

		async Task SetupCamera()
		{
			OpenCamera();

			var p = _camera.GetParameters();
			p.PreviewFormat = ImageFormatType.Nv21; // YCrCb format (all Android devices must support this)

			// First try continuous video, then auto focus, then fixed
			var supportedFocusModes = p.SupportedFocusModes;
			if (supportedFocusModes.Contains(Camera.Parameters.FocusModeContinuousVideo))
				p.FocusMode = Camera.Parameters.FocusModeContinuousVideo;
			else if (supportedFocusModes.Contains(Camera.Parameters.FocusModeAuto))
				p.FocusMode = Camera.Parameters.FocusModeAuto;
			else if (supportedFocusModes.Contains(Camera.Parameters.FocusModeFixed))
				p.FocusMode = Camera.Parameters.FocusModeFixed;

			// Check if we can support requested resolution ..
			var availableResolutions = p.SupportedPreviewSizes.Select(s => new CameraResolution { Width = s.Width, Height = s.Height }).ToList();
			var resolution = ScanningOptions.GetResolution(availableResolutions);

			// .. If not, let's try and find a suitable one
			resolution = resolution ?? availableResolutions.OrderBy(r => r.Width).FirstOrDefault(r => r.Width.Between(640, 1280) && r.Height.Between(640, 960));

			// Hopefully a resolution was selected at some point
			if (resolution != null)
				p.SetPreviewSize(resolution.Width, resolution.Height);

			_camera.SetParameters(p);

			SetupTorch(_isTorchOn);

			p = _camera.GetParameters(); // refresh!

			_useContinuousFocus = p.FocusMode == Camera.Parameters.FocusModeContinuousVideo;
			PreviewSize = p.PreviewSize; // get actual preview size (may differ from requested size)
			var bitsPerPixel = ImageFormat.GetBitsPerPixel(p.PreviewFormat);

			Log.Debug(MobileBarcodeScanner.TAG, $"Preview size {PreviewSize.Width}x{PreviewSize.Height} with {bitsPerPixel} bits per pixel");

			var surfaceInfo = await _surfaceAvailable.Task;
			_surfaceTexture = surfaceInfo.Surface;

			SetSurfaceTransform(surfaceInfo.Surface, surfaceInfo.Width, surfaceInfo.Height);

			_camera.SetDisplayOrientation(CameraOrientation(WindowManager.DefaultDisplay.Rotation));
			_camera.SetPreviewTexture(surfaceInfo.Surface);
			_camera.StartPreview();

			int bufferSize = (PreviewSize.Width * PreviewSize.Height * bitsPerPixel) / 8;
			using (var buffer = new FastJavaByteArray(bufferSize))
				_camera.AddCallbackBuffer(buffer);

			_camera.SetNonMarshalingPreviewCallback(this);

			// Docs suggest if Auto or Macro modes, we should invoke AutoFocus at least once
			_autoFocusRunning = false;
			if (!_useContinuousFocus)
				AutoFocus();
		}

		public int CameraOrientation(SurfaceOrientation rotation)
		{
			int degrees = 0;
			switch (rotation)
			{
				case SurfaceOrientation.Rotation0:
					degrees = 0;
					break;
				case SurfaceOrientation.Rotation90:
					degrees = 90;
					break;
				case SurfaceOrientation.Rotation180:
					degrees = 180;
					break;
				case SurfaceOrientation.Rotation270:
					degrees = 270;
					break;
			}

			// Handle front facing camera
			if (_cameraInfo.Facing == CameraFacing.Front)
				return (360 - ((_cameraInfo.Orientation + degrees) % 360)) % 360;  // compensate for mirror

			return (_cameraInfo.Orientation - degrees + 360) % 360;
		}

		void ShutdownCamera()
		{
			_handler.Post(() =>
			{
				if (_camera == null)
					return;

				var camera = _camera;
				_camera = null;

				try
				{
					camera.StopPreview();
					camera.SetNonMarshalingPreviewCallback(null);
					ClearSurface(_surfaceTexture);
				}
				catch (Exception e)
				{
					Log.Error(MobileBarcodeScanner.TAG, e.ToString());
				}
				finally
				{
					camera.Release();
				}
			});
		}

		void ClearSurface(SurfaceTexture texture)
		{
			if (texture == null)
				return;

			var egl = (IEGL10)EGLContext.EGL;
			var display = egl.EglGetDisplay(EGL10.EglDefaultDisplay);
			egl.EglInitialize(display, null);

			int[] attribList = {
				EGL10.EglRedSize, 8,
				EGL10.EglGreenSize, 8,
				EGL10.EglBlueSize, 8,
				EGL10.EglAlphaSize, 8,
				EGL10.EglRenderableType, EGL10.EglWindowBit,
				EGL10.EglNone, 0, // placeholder for recordable [@-3]
				EGL10.EglNone
			};

			var configs = new EGLConfig[1];
			int[] numConfigs = new int[1];
			egl.EglChooseConfig(display, attribList, configs, configs.Length, numConfigs);
			var config = configs[0];
			var context = egl.EglCreateContext(display, config, EGL10.EglNoContext, new int[] { 12440, 2, EGL10.EglNone });

			var eglSurface = egl.EglCreateWindowSurface(display, config, texture, new int[] { EGL10.EglNone });

			egl.EglMakeCurrent(display, eglSurface, eglSurface, context);
			GLES20.GlClearColor(0, 0, 0, 1); // black, no opacity
			GLES20.GlClear(GLES20.GlColorBufferBit);
			egl.EglSwapBuffers(display, eglSurface);
			egl.EglDestroySurface(display, eglSurface);
			egl.EglMakeCurrent(display, EGL10.EglNoSurface, EGL10.EglNoSurface, EGL10.EglNoContext);
			egl.EglDestroyContext(display, context);
			egl.EglTerminate(display);
		}

		public void StopScanning()
		{
			PauseAnalysis();
			ShutdownCamera();
		}

		public void Torch(bool on)
		{
			if (!Context.PackageManager.HasSystemFeature(PackageManager.FeatureCameraFlash))
			{
				Log.Info(MobileBarcodeScanner.TAG, "Flash not supported on this device");
				return;
			}

			Net.Mobile.Android.PermissionsHandler.CheckTorchPermissions(Context);

			_isTorchOn = on;
			if (_camera != null) // already running
				SetupTorch(on);
		}

		public void ToggleTorch()
		{
			Torch(!_isTorchOn);
		}

		void SetupTorch(bool on)
		{
			var p = _camera.GetParameters();
			var supportedFlashModes = p.SupportedFlashModes ?? Enumerable.Empty<string>();

			string flashMode = null;

			if (on)
			{
				if (supportedFlashModes.Contains(Camera.Parameters.FlashModeTorch))
					flashMode = Camera.Parameters.FlashModeTorch;
				else if (supportedFlashModes.Contains(Camera.Parameters.FlashModeOn))
					flashMode = Camera.Parameters.FlashModeOn;
			}
			else
			{
				if (supportedFlashModes.Contains(Camera.Parameters.FlashModeOff))
					flashMode = Camera.Parameters.FlashModeOff;
			}

			if (!string.IsNullOrEmpty(flashMode))
			{
				p.FlashMode = flashMode;
				_camera.SetParameters(p);
			}
		}

		IBarcodeReaderGeneric<FastJavaByteArray> CreateBarcodeReader(MobileBarcodeScanningOptions options)
		{
			var barcodeReader = new BarcodeReaderGeneric<FastJavaByteArray>();

			if (options.TryHarder.HasValue)
				barcodeReader.Options.TryHarder = options.TryHarder.Value;

			if (options.PureBarcode.HasValue)
				barcodeReader.Options.PureBarcode = options.PureBarcode.Value;

			if (!string.IsNullOrEmpty(options.CharacterSet))
				barcodeReader.Options.CharacterSet = options.CharacterSet;

			if (options.TryInverted.HasValue)
				barcodeReader.TryInverted = options.TryInverted.Value;

			if (options.AutoRotate.HasValue)
				barcodeReader.AutoRotate = options.AutoRotate.Value;

			if (options.PossibleFormats?.Any() ?? false)
			{
				barcodeReader.Options.PossibleFormats = new List<BarcodeFormat>();

				foreach (var pf in options.PossibleFormats)
					barcodeReader.Options.PossibleFormats.Add(pf);
			}

			return barcodeReader;
		}

		public void RotateCounterClockwise(byte[] source, ref byte[] target, int width, int height)
		{
			if (source.Length != (target?.Length ?? -1))
				target = new byte[source.Length];

			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
					target[x * height + height - y - 1] = source[x + y * width];
		}

		const int maxHistory = 10; // a bit arbitrary :-/
		struct LastResult
		{
			public long Timestamp;
			public Result Result;
		};

		readonly RingBuffer<LastResult> _ring = new RingBuffer<LastResult>(maxHistory);
		readonly IComparer<LastResult> _resultComparer = Comparer<LastResult>.Create((x, y) => x.Result.Text.CompareTo(y.Result.Text));
		long _delay;

		byte[] _matrix;
		byte[] _rotatedMatrix;

		async public void OnPreviewFrame(IntPtr data, Camera camera)
		{
			System.Diagnostics.Stopwatch sw = null;
			using (var buffer = new FastJavaByteArray(data)) // avoids marshalling
			{ 
				try
				{
#if DEBUG
					sw = new Stopwatch();
					sw.Start();
#endif
					if (!_isAnalyzing)
						return;

					var isPortrait = IsPortrait;

					var result = await Task.Run(() =>
					{
						LuminanceSource luminanceSource;
						var fast = new FastJavaByteArrayYUVLuminanceSource(buffer, PreviewSize.Width, PreviewSize.Height, _area.Left, _area.Top, _area.Width, _area.Height);
						if (isPortrait)
						{
							fast.CopyMatrix(ref _matrix);
							RotateCounterClockwise(_matrix, ref _rotatedMatrix, _area.Width, _area.Height);
							luminanceSource = new PlanarYUVLuminanceSource(_rotatedMatrix, _area.Height, _area.Width, 0, 0, _area.Height, _area.Width, false);
						}
						else
							luminanceSource = fast;

						return _barcodeReader.Decode(luminanceSource);
					});

					if (result != null)
					{
						var now = Stopwatch.GetTimestamp();
						var lastResult = new LastResult { Result = result };
						int idx = _ring.FindIndex(ref lastResult, _resultComparer);
						if (idx < 0 || lastResult.Timestamp + _delay < now)
						{
							_callback(result);

							lastResult.Timestamp = now; // update timestamp
							if (idx < 0)
								_ring.Add(lastResult);
							else
								_ring[idx] = lastResult;
						}
					}
					else if (!_useContinuousFocus)
						AutoFocus();
				}
				catch (Exception ex)
				{
					// It is better to just skip a frame :-) ..
					Log.Warn(MobileBarcodeScanner.TAG, ex.ToString());
				}
				finally
				{
					camera.AddCallbackBuffer(buffer); // IMPORTANT!

#if DEBUG
					sw.Stop();
					try
					{
						Post(() =>
						{
							_toast.SetText(string.Format("{0}ms", sw.ElapsedMilliseconds));
							_toast.Show();
						});
					}
					catch { } // squash
#endif
				}
			}
		}
	}
}
