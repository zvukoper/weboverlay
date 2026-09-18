using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace WebOverlay
{
    /// <summary>
    /// Keeps the clickability toggle globally available even when the active
    /// overlay is disabled/click-through and therefore cannot receive normal
    /// keyboard events.
    /// </summary>
    internal static class ClickabilityHotkeyFix
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 0x5A51;
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        private static Timer? _timer;
        private static bool _failureLogged;
        private static IntPtr _registeredHandle;
        private static string _registeredBinding = "";
        private static readonly HotkeyMessageFilter MessageFilter = new();

        [ModuleInitializer]
        internal static void Initialize()
        {
            Application.Idle += OnFirstIdle;
        }

        private static void OnFirstIdle(object? sender, EventArgs e)
        {
            Application.Idle -= OnFirstIdle;

            try
            {
                Application.AddMessageFilter(MessageFilter);
                Application.ApplicationExit += (_, _) => Unregister();

                EnsureRegistered();

                // Только пока хоткей не принадлежит этому процессу: повторная
                // регистрация уже занятого сочетания падала бы вечно и засоряла
                // лог (было ~4 попытки в секунду).
                if (!Program.ClickableHotkeyRegistered)
                {
                    _timer = new Timer { Interval = 250 };
                    _timer.Tick += (_, _) => EnsureRegistered();
                    _timer.Start();
                }
            }
            catch (Exception ex)
            {
                Program.Log($"ClickabilityHotkeyFix startup error: {ex.Message}");
            }
        }

        private static void EnsureRegistered()
        {
            try
            {
                if (Program.ClickableHotkeyRegistered)
                {
                    Stop();
                    return;
                }

                var window = WindowManager.Windows.FirstOrDefault(w =>
                    w != null && !w.IsDisposed && w.IsHandleCreated);
                if (window == null)
                    return;

                string binding = LoadBinding();
                if (_registeredHandle == window.Handle &&
                    string.Equals(_registeredBinding, binding, StringComparison.OrdinalIgnoreCase))
                    return;

                Unregister();

                var kb = KeyBinding.Parse(binding);
                if (!kb.TryGetNative(out int modifiers, out int vk))
                    return;

                bool ok = RegisterHotKey(window.Handle, HOTKEY_ID, modifiers, vk);
                if (ok)
                {
                    _registeredHandle = window.Handle;
                    _registeredBinding = binding;
                    Program.Log($"ClickabilityHotkeyFix: registered id={HOTKEY_ID} binding={binding}");
                    Stop();
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
                    {
                        // Сочетание уже принадлежит Program (тот же Ctrl+Shift+Alt+U).
                        // Повторять бессмысленно: обработчик Program его уже получает.
                        Program.ClickableHotkeyRegistered = true;
                        Program.Log($"ClickabilityHotkeyFix: binding={binding} уже зарегистрирован в Program, повторные попытки прекращены");
                        Stop();
                    }
                    else if (_failureLogged)
                    {
                        Stop();
                    }
                    else
                    {
                        _failureLogged = true;
                        Program.Log($"ClickabilityHotkeyFix: RegisterHotKey failed binding={binding} error={error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"ClickabilityHotkeyFix registration error: {ex.Message}");
            }
        }

        private static void Stop()
        {
            var timer = _timer;
            _timer = null;
            if (timer == null)
                return;

            timer.Stop();
            timer.Dispose();
        }

        private static string LoadBinding()
        {
            const string fallback = "Ctrl+Shift+Alt+U";

            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WebOverlay",
                    "config.json");

                if (!File.Exists(path))
                    return fallback;

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options);
                return !string.IsNullOrWhiteSpace(cfg?.ToggleClickable)
                    ? cfg.ToggleClickable
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static void Unregister()
        {
            if (_registeredHandle != IntPtr.Zero)
            {
                try { UnregisterHotKey(_registeredHandle, HOTKEY_ID); }
                catch { }
            }

            _registeredHandle = IntPtr.Zero;
            _registeredBinding = "";
        }

        private sealed class HotkeyMessageFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_HOTKEY || m.WParam.ToInt64() != HOTKEY_ID)
                    return false;

                try
                {
                    WindowManager.ToggleClickableActive();
                }
                catch (Exception ex)
                {
                    Program.Log($"ClickabilityHotkeyFix toggle error: {ex.Message}");
                }

                return true;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
