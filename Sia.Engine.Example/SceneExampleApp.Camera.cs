using Sia.GLFW;
using Sia.Input;
using Sia.Math;

namespace Sia.Engine.Example;

internal sealed partial class SceneExampleApp
{
    private readonly (float3 Eye, float3 Target)? _initialCamera;
    private float3? _cameraEye;
    private float _cameraYaw;
    private float _cameraPitch;
#if !BROWSER
    private MousePosition? _cameraDrag;
    private readonly HashSet<Key> _cameraPressed = [];
#endif

    private unsafe void UpdateFreeCamera(float deltaTime, ref float3 eye, ref float3 target)
    {
#if BROWSER
        var focused = _cameraFocused;
#else
        var focused = GlfwUnsafe.GetWindowAttrib((WindowHandle*)_window.Handle, WindowAttribute.Focused) != 0;
#endif
#if !BROWSER
        bool Down(Key key) => focused && (_cameraPressed.Contains(key) || Glfw.GetKey(_window, key) != InputAction.Release);
#endif
        if (_cameraEye is null) {
            if (_initialCamera is { } initial) { (eye, target) = initial; }
            _cameraEye = eye;
            var direction = math.normalize(target - eye);
            _cameraYaw = MathF.Atan2(direction.x, -direction.z);
            _cameraPitch = MathF.Asin(System.Math.Clamp(direction.y, -1, 1));
#if !BROWSER
            _cameraDrag = null;
#endif
        }
#if BROWSER
        if (focused) {
            _cameraYaw += _browserLook.x * .003f + _browserTurn.x * deltaTime * 1.5f;
            _cameraPitch += _browserLook.y * .003f + _browserTurn.y * deltaTime * 1.5f;
        }
#else
        var cursor = Glfw.GetCursorPosition(_window);
        if (focused && Glfw.GetMouseButton(_window, MouseButton.Right) != InputAction.Release) {
            if (_cameraDrag is { } previous) {
                _cameraYaw += (float)(cursor.X - previous.X) * .003f;
                _cameraPitch -= (float)(cursor.Y - previous.Y) * .003f;
            }
            _cameraDrag = cursor;
        } else { _cameraDrag = null; }
        _cameraYaw += ((Down(Key.Right) ? 1 : 0) - (Down(Key.Left) ? 1 : 0)) * deltaTime * 1.5f;
        _cameraPitch += ((Down(Key.Up) ? 1 : 0) - (Down(Key.Down) ? 1 : 0)) * deltaTime * 1.5f;
#endif
        _cameraPitch = System.Math.Clamp(_cameraPitch, -1.55f, 1.55f);
        _cameraYaw = MathF.IEEERemainder(_cameraYaw, MathF.Tau);
        var forward = new float3(MathF.Sin(_cameraYaw) * MathF.Cos(_cameraPitch), MathF.Sin(_cameraPitch),
            -MathF.Cos(_cameraYaw) * MathF.Cos(_cameraPitch));
        var right = new float3(MathF.Cos(_cameraYaw), 0, MathF.Sin(_cameraYaw));
#if BROWSER
        var move = focused ? forward * _browserMove.z + right * _browserMove.x + new float3(0, _browserMove.y, 0) : default;
        var speed = _browserSpeed;
#else
        var move = forward * ((Down(Key.W) ? 1 : 0) - (Down(Key.S) ? 1 : 0))
            + right * ((Down(Key.D) ? 1 : 0) - (Down(Key.A) ? 1 : 0))
            + new float3(0, (Down(Key.E) ? 1 : 0) - (Down(Key.Q) ? 1 : 0), 0);
        var speed = Down(Key.C) ? 1f
            : Down(Key.LeftShift) || Down(Key.RightShift) ? 16f : 4f;
#endif
        eye = _cameraEye.Value;
#if BROWSER
        if (math.lengthsq(move) > 1) { move = math.normalize(move); }
        eye += move * speed * deltaTime;
#else
        if (math.lengthsq(move) > 0) { eye += math.normalize(move) * speed * deltaTime; }
#endif
        _cameraEye = eye;
        target = eye + forward;
#if !BROWSER
        _cameraPressed.Clear();
#endif
#if BROWSER
        if (_compareLodRequested) {
            _compareLodRequested = false;
            _browserComparePose = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{eye.x:F6},{eye.y:F6},{eye.z:F6},{target.x:F6},{target.y:F6},{target.z:F6}");
        }
#endif
    }
}
